using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PSAdminTools.Mtr
{
    /// <summary>
    /// Live traceroute with per-hop packet-loss and latency statistics, in the style of the
    /// Linux "mtr" tool. The table redraws in place each cycle; Ctrl+C stops it.
    ///
    /// On Linux (and macOS) this delegates to the installed mtr binary, because .NET's Ping
    /// class falls back to invoking /bin/ping when unprivileged, and that path does not reliably
    /// expose the intermediate hop address from a TTL-expired reply - which is the one thing a
    /// traceroute depends on.
    ///
    /// On Windows the trace is implemented directly: all TTLs in a cycle are probed in parallel
    /// for speed, which is what makes this far quicker than tracert (tracert probes strictly
    /// sequentially with a 4-second timeout per probe).
    /// </summary>
    [Cmdlet(VerbsLifecycle.Start, "Mtr")]
    public class StartMtrCommand : PSCmdlet
    {
        private volatile bool _stopping;
        private Process? _delegatedProcess;

        private readonly ConcurrentDictionary<string, string> _dnsCache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Hostname or IP address to trace to.</summary>
        [Parameter(Mandatory = true, Position = 0)]
        [ValidateNotNullOrEmpty]
        public string Target { get; set; } = string.Empty;

        /// <summary>Skip reverse-DNS lookups and show IP addresses only (mtr's -n).</summary>
        [Parameter]
        public SwitchParameter NoDns { get; set; }

        /// <summary>Send probes from a specific network interface (mtr's -I).</summary>
        [Parameter]
        public string? Interface { get; set; }

        /// <summary>Maximum number of hops to probe.</summary>
        [Parameter]
        [ValidateRange(1, 255)]
        public int MaxHops { get; set; } = 30;

        /// <summary>Seconds to wait between cycles.</summary>
        [Parameter]
        [ValidateRange(1, 60)]
        public int Interval { get; set; } = 1;

        /// <summary>Per-probe timeout in milliseconds.</summary>
        [Parameter]
        [ValidateRange(100, 10000)]
        public int Timeout { get; set; } = 1000;

        /// <summary>
        /// How many probes may be in flight at once.
        ///
        /// Probing every TTL simultaneously looked fastest but was self-defeating: the Windows
        /// ICMP path serialises the sends, so each one blocked for hundreds of milliseconds and
        /// hops timed out against a 1000 ms limit even when the target answered in 0 ms. A
        /// bounded window removes that contention while staying far quicker than tracert, which
        /// probes strictly one hop at a time with a four-second timeout per probe.
        /// </summary>
        private const int MaxConcurrentProbes = 8;

        /// <summary>
        /// How many consecutive silent hops end the search, mirroring mtr's MAX_UNKNOWN_HOSTS.
        /// Five routers in a row returning nothing means the path is either dead or filtered from
        /// that point on, so probing the remaining TTLs yields no further information - it just
        /// fills the screen with identical "???" rows.
        /// </summary>
        private const int MaxUnknownHosts = 5;

        /// <summary>
        /// Pause between cycles while the frontier is still expanding. The TTL grows one hop per
        /// cycle, as in mtr, so waiting the full -Interval during discovery would make a twelve
        /// hop path take twelve seconds to appear. Once the path settles, -Interval applies.
        /// </summary>
        private const int DiscoveryIntervalMs = 250;

        private async Task<ProbeResult> ProbeWithLimitAsync(
            SemaphoreSlim gate,
            IPAddress target,
            int ttl,
            IPAddress? sourceAddress)
        {
            // Wait for a slot BEFORE starting the probe, so a dedicated thread is only created
            // once there is capacity to run it.
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await IcmpProbe.ProbeAsync(target, ttl, Timeout, sourceAddress).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        protected override void StopProcessing()
        {
            // Ctrl+C arrives here (not via Console.CancelKeyPress) for a binary cmdlet.
            _stopping = true;

            try
            {
                if (_delegatedProcess != null && !_delegatedProcess.HasExited)
                {
                    _delegatedProcess.Kill();
                }
            }
            catch (Exception)
            {
                // Process already gone - nothing to clean up.
            }
        }

        protected override void ProcessRecord()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                RunDelegatedToMtr();
                return;
            }

            RunWindowsTrace();
        }

        // ---------------------------------------------------------------- Linux / macOS

        private void RunDelegatedToMtr()
        {
            string? mtrPath = FindMtrBinary();
            if (mtrPath == null)
            {
                WriteError(new ErrorRecord(
                    new InvalidOperationException(
                        "The 'mtr' binary was not found. Install it (for example: sudo apt install mtr-tiny, " +
                        "or sudo dnf install mtr) and try again."),
                    "MtrNotInstalled",
                    ErrorCategory.NotInstalled,
                    "mtr"));
                return;
            }

            var args = new List<string>();
            if (NoDns.IsPresent) { args.Add("-n"); }
            if (!string.IsNullOrWhiteSpace(Interface))
            {
                args.Add("-I");
                args.Add(Interface!);
            }
            args.Add(Target);

            string arguments = string.Join(" ", args);
            WriteVerbose($"Delegating to: {mtrPath} {arguments}");

            var psi = new ProcessStartInfo
            {
                FileName = mtrPath,
                Arguments = arguments,
                // Deliberately NOT redirecting: mtr draws its own curses interface and needs the
                // real terminal. Redirecting would break the display entirely.
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            try
            {
                _delegatedProcess = Process.Start(psi);
                if (_delegatedProcess == null)
                {
                    WriteError(new ErrorRecord(
                        new InvalidOperationException("Failed to start the mtr process."),
                        "MtrStartFailed",
                        ErrorCategory.NotSpecified,
                        mtrPath));
                    return;
                }

                while (!_delegatedProcess.HasExited)
                {
                    if (_stopping) { break; }
                    _delegatedProcess.WaitForExit(200);
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "MtrDelegationFailed", ErrorCategory.NotSpecified, mtrPath));
            }
        }

        private static string? FindMtrBinary()
        {
            foreach (string candidate in new[] { "/usr/sbin/mtr", "/usr/bin/mtr", "/sbin/mtr", "/usr/local/bin/mtr" })
            {
                if (System.IO.File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // Fall back to PATH lookup.
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = "mtr",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null) { return null; }
                    string output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(2000);
                    return string.IsNullOrEmpty(output) ? null : output;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- Windows

        private void RunWindowsTrace()
        {
            IPAddress? target = ResolveTarget(Target);
            if (target == null)
            {
                WriteError(new ErrorRecord(
                    new InvalidOperationException($"Could not resolve '{Target}' to an IPv4 address."),
                    "TargetResolutionFailed",
                    ErrorCategory.InvalidArgument,
                    Target));
                return;
            }

            IPAddress? sourceAddress = null;
            if (!string.IsNullOrWhiteSpace(Interface))
            {
                sourceAddress = IcmpProbe.GetInterfaceAddress(Interface!);
                if (sourceAddress == null)
                {
                    string available = string.Join(", ", IcmpProbe.GetInterfaceNames());
                    WriteError(new ErrorRecord(
                        new InvalidOperationException(
                            $"No IPv4 address found for interface '{Interface}'. Available interfaces: {available}"),
                        "InterfaceNotFound",
                        ErrorCategory.InvalidArgument,
                        Interface));
                    return;
                }
                WriteVerbose($"Binding probes to {Interface} ({sourceAddress}).");
            }

            // Even when no -Interface was given, report which address the routing table picks,
            // so the header always shows where probes actually originate.
            IPAddress? reportedSource = sourceAddress ?? IcmpProbe.GetSourceAddressForTarget(target);
            string? interfaceName = null;
            int? interfaceIndex = null;
            IPAddress? gateway = null;

            if (reportedSource != null)
            {
                (interfaceName, interfaceIndex) = IcmpProbe.GetInterfaceInfo(reportedSource);
                gateway = IcmpProbe.GetGatewayForAddress(reportedSource);
            }

            var context = new TraceContext
            {
                TargetAddress = target,
                SourceAddress = reportedSource,
                InterfaceName = interfaceName,
                InterfaceIndex = interfaceIndex,
                ExplicitlyBound = sourceAddress != null
            };

            var hops = new Dictionary<int, HopStats>();
            var renderer = new MtrRenderer(Host.UI);
            int? destinationTtl = null;
            int? unreachableTtl = null;
            string? unreachableAddress = null;
            int cycle = 0;

            // The furthest TTL probed so far. mtr starts at hop 1 and extends the frontier one
            // hop at a time rather than sweeping every TTL up front, so the display only ever
            // shows hops that have actually been looked at.
            int frontier = 1;

            // Pre-flight a single TTL=1 probe. A host on the same subnet answers at TTL 1 - no
            // router decrements the TTL for direct delivery - so without this the first cycle
            // sweeps all 30 TTLs to discover a path that is one hop long. Statistics from this
            // probe are discarded; the first real cycle re-probes and records properly.
            try
            {
                ProbeResult preflight = IcmpProbe
                    .ProbeAsync(target, 1, Timeout, sourceAddress)
                    .GetAwaiter().GetResult();

                if (preflight.IsDestination)
                {
                    destinationTtl = 1;
                    WriteVerbose("Target answered at TTL 1 - it is directly reachable.");
                }
            }
            catch (Exception ex)
            {
                WriteVerbose($"TTL 1 pre-flight failed: {ex.Message}");
            }

            while (!_stopping)
            {
                cycle++;
                int highestTtl = destinationTtl ?? unreachableTtl ?? frontier;
                ProbeResult[] results;

                var tasks = new List<Task<ProbeResult>>(highestTtl);
                using (var gate = new SemaphoreSlim(MaxConcurrentProbes))
                {
                    for (int ttl = 1; ttl <= highestTtl; ttl++)
                    {
                        tasks.Add(ProbeWithLimitAsync(gate, target, ttl, sourceAddress));
                    }

                    ProbeResult[] gathered;
                    try
                    {
                        gathered = Task.WhenAll(tasks).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        WriteVerbose($"Cycle {cycle} failed: {ex.Message}");
                        break;
                    }

                    results = gathered;
                }

                for (int i = 0; i < results.Length; i++)
                {
                    int ttl = i + 1;
                    ProbeResult result = results[i];

                    if (!hops.TryGetValue(ttl, out HopStats? stats))
                    {
                        stats = new HopStats(ttl);
                        hops[ttl] = stats;
                    }

                    if (result.Replied && result.Address != null)
                    {
                        if (result.Unreachable)
                        {
                            // Not a hop on the path. Remember the earliest TTL that produced it so
                            // the trace can stop there, and count this TTL as a timeout - because
                            // no router at this position actually identified itself.
                            if (unreachableTtl == null || ttl < unreachableTtl.Value)
                            {
                                unreachableTtl = ttl;
                                unreachableAddress = result.Address;
                                WriteVerbose(
                                    $"Path ends at hop {ttl}: {unreachableAddress} reports {target} " +
                                    "as unreachable.");
                            }

                            stats.RecordTimeout();
                            continue;
                        }

                        stats.RecordReply(result.Address, result.RoundTripMs, result.IsDestination);

                        if (result.IsDestination && (destinationTtl == null || ttl < destinationTtl.Value))
                        {
                            destinationTtl = ttl;
                        }

                        if (!NoDns.IsPresent)
                        {
                            QueueDnsLookup(stats, result.Address);
                        }
                    }
                    else
                    {
                        stats.RecordTimeout();
                    }
                }

                bool anyReply = hops.Values.Any(h => !h.IsUnknown);

                // Hop 1 is the one hop identifiable without a reply. Only label it when the
                // target is genuinely beyond the gateway: if the target is on-link the
                // pre-flight sets destinationTtl to 1, and hop 1 is the target itself, not the
                // gateway - labelling it then would be wrong.
                if (gateway != null && destinationTtl != 1 && hops.TryGetValue(1, out HopStats? firstHop))
                {
                    firstHop.FallbackAddress = gateway.ToString();
                }

                // Once the path terminus is known, discard anything recorded beyond it during the
                // first cycle - those TTLs were probed before the terminus was discovered.
                int? pathEnd = destinationTtl ?? unreachableTtl;
                if (pathEnd.HasValue)
                {
                    foreach (int ttl in hops.Keys.Where(k => k > pathEnd.Value).ToList())
                    {
                        hops.Remove(ttl);
                    }
                }

                int displayLimit = pathEnd ?? frontier;

                List<HopStats> visible = Enumerable.Range(1, displayLimit)
                    .Where(hops.ContainsKey)
                    .Select(ttl => hops[ttl])
                    .ToList();

                // Decide whether to look one hop further. Two conditions stop the search, both
                // taken from mtr: reaching the path terminus, or hitting MaxUnknownHosts silent
                // hops in a row.
                int trailingUnknown = TrailingUnknownCount(hops, frontier);
                bool searchExhausted = trailingUnknown >= MaxUnknownHosts;
                bool stillDiscovering = pathEnd == null && !searchExhausted && frontier < MaxHops;

                if (stillDiscovering)
                {
                    frontier++;
                }

                string? warning = null;
                if (destinationTtl == null && unreachableTtl == null && !anyReply && searchExhausted)
                {
                    string via = context.SourceAddress != null
                        ? $"{context.SourceAddress} on {context.InterfaceName ?? "unknown interface"}"
                        : "the selected source address";

                    warning =
                        $"Gave up after {frontier} silent hops. Probes are leaving from {via}. " +
                        "Common causes: a local firewall blocking outbound ICMP, that interface " +
                        "having no route to the target, or the network being down. " +
                        $"Try: ping {target}   /   Test-NetConnection {target}";
                }

                renderer.Render(context, cycle, visible, NoDns.IsPresent, warning);

                // Move quickly while the frontier is still growing; settle to -Interval once the
                // path is established, so the statistics accumulate at the requested rate.
                int pauseMs = stillDiscovering ? DiscoveryIntervalMs : Interval * 1000;
                if (!SleepInterruptibly(pauseMs))
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Counts consecutive silent hops ending at the frontier. A hop that has answered at any
        /// point resets the run, so one flaky router mid-path does not end the search early.
        /// </summary>
        private static int TrailingUnknownCount(Dictionary<int, HopStats> hops, int frontier)
        {
            int count = 0;

            for (int ttl = frontier; ttl >= 1; ttl--)
            {
                if (hops.TryGetValue(ttl, out HopStats? stats) && !stats.IsUnknown)
                {
                    break;
                }
                count++;
            }

            return count;
        }

        private void QueueDnsLookup(HopStats stats, string address)
        {
            if (stats.HostName != null)
            {
                return;
            }

            if (_dnsCache.TryGetValue(address, out string? cached))
            {
                stats.HostName = cached;
                return;
            }

            // Resolve off the render loop - a slow or unreachable resolver must never stall the
            // live table. The name simply appears on a later cycle once it comes back.
            Task.Run(() =>
            {
                try
                {
                    IPHostEntry entry = Dns.GetHostEntry(address);

                    // Forward-confirm the result. Windows does not always throw when an address
                    // has no PTR record - it can hand back an unrelated name, typically the local
                    // machine's, which then appears as the label for a remote hop. Only trust the
                    // name if the entry actually maps back to the address that was looked up.
                    bool confirmed = !string.IsNullOrEmpty(entry.HostName)
                        && entry.AddressList != null
                        && entry.AddressList.Any(a =>
                            string.Equals(a.ToString(), address, StringComparison.OrdinalIgnoreCase));

                    if (confirmed)
                    {
                        _dnsCache[address] = entry.HostName;
                        stats.HostName = entry.HostName;
                    }
                    else
                    {
                        _dnsCache[address] = address;
                    }
                }
                catch (Exception)
                {
                    // No PTR record - keep showing the raw address.
                    _dnsCache[address] = address;
                }
            });
        }

        private bool SleepInterruptibly(int totalMs)
        {
            const int slice = 100;
            int elapsed = 0;

            while (elapsed < totalMs)
            {
                if (_stopping) { return false; }
                Thread.Sleep(slice);
                elapsed += slice;
            }

            return !_stopping;
        }

        private static IPAddress? ResolveTarget(string target)
        {
            if (IPAddress.TryParse(target, out IPAddress? parsed))
            {
                return parsed.AddressFamily == AddressFamily.InterNetwork ? parsed : null;
            }

            try
            {
                return Dns.GetHostAddresses(target)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
