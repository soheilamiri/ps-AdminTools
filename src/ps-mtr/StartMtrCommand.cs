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

            var hops = new Dictionary<int, HopStats>();
            var renderer = new MtrRenderer(Host.UI);
            int? destinationTtl = null;
            int cycle = 0;

            while (!_stopping)
            {
                cycle++;
                int highestTtl = destinationTtl ?? MaxHops;

                var tasks = new List<Task<ProbeResult>>(highestTtl);
                for (int ttl = 1; ttl <= highestTtl; ttl++)
                {
                    tasks.Add(IcmpProbe.ProbeAsync(target, ttl, Timeout, sourceAddress));
                }

                ProbeResult[] results;
                try
                {
                    results = Task.WhenAll(tasks).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    WriteVerbose($"Cycle {cycle} failed: {ex.Message}");
                    break;
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

                int displayLimit = destinationTtl ?? HighestRespondingTtl(hops, MaxHops);
                List<HopStats> visible = Enumerable.Range(1, displayLimit)
                    .Where(hops.ContainsKey)
                    .Select(ttl => hops[ttl])
                    .ToList();

                renderer.Render(target.ToString(), cycle, visible, NoDns.IsPresent);

                if (!SleepInterruptibly(Interval * 1000))
                {
                    break;
                }
            }
        }

        private static int HighestRespondingTtl(Dictionary<int, HopStats> hops, int maxHops)
        {
            int highest = 1;
            foreach (KeyValuePair<int, HopStats> entry in hops)
            {
                if (!entry.Value.IsUnknown && entry.Key > highest)
                {
                    highest = entry.Key;
                }
            }
            return Math.Min(highest, maxHops);
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
                    if (!string.IsNullOrEmpty(entry.HostName))
                    {
                        _dnsCache[address] = entry.HostName;
                        stats.HostName = entry.HostName;
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
