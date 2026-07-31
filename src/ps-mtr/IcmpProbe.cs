using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PSAdminTools.Mtr
{
    internal readonly struct ProbeResult
    {
        public ProbeResult(bool replied, string? address, double roundTripMs, bool isDestination)
        {
            Replied = replied;
            Address = address;
            RoundTripMs = roundTripMs;
            IsDestination = isDestination;
        }

        /// <summary>True if anything answered - either the target itself or an intermediate router.</summary>
        public bool Replied { get; }

        public string? Address { get; }
        public double RoundTripMs { get; }

        /// <summary>True only when the reply came from the trace target (end of the path).</summary>
        public bool IsDestination { get; }

        public static ProbeResult Timeout() => new ProbeResult(false, null, 0d, false);
    }

    /// <summary>
    /// Sends a single TTL-limited ICMP echo and reports which host answered.
    ///
    /// Two Windows paths:
    ///   - No -Interface: System.Net.NetworkInformation.Ping with PingOptions.Ttl.
    ///   - With -Interface: IcmpSendEcho2Ex from iphlpapi.dll, which accepts an explicit source
    ///     address. Unlike a raw socket, this does NOT require Administrator.
    ///
    /// On timing: every probe runs synchronously on its own dedicated thread. That matters.
    /// Using "await SendPingAsync" instead queues the continuation onto the thread pool, so with
    /// a full cycle of probes in flight the stopwatch stopped long after the reply arrived and
    /// every hop reported the same inflated figure. A synchronous send stops the stopwatch on the
    /// same thread the moment the call returns, and a dedicated thread avoids the pool's
    /// thread-injection throttle delaying the later TTLs.
    ///
    /// PingReply.RoundtripTime is preferred where available, but Windows only populates it when
    /// Status is Success - for TtlExpired replies (every intermediate hop) it is zero, so the
    /// stopwatch value is used there.
    /// </summary>
    internal static class IcmpProbe
    {
        private static readonly byte[] Payload = new byte[32];

        // --- IP_STATUS codes returned by IcmpSendEcho2Ex ---
        private const uint IpSuccess = 0;
        private const uint IpDestNetUnreachable = 11002;
        private const uint IpDestHostUnreachable = 11003;
        private const uint IpTtlExpiredTransit = 11013;

        public static Task<ProbeResult> ProbeAsync(
            IPAddress target,
            int ttl,
            int timeoutMs,
            IPAddress? sourceAddress)
        {
            return Task.Factory.StartNew(
                () => sourceAddress != null
                    ? ProbeBound(target, ttl, timeoutMs, sourceAddress)
                    : ProbeUnbound(target, ttl, timeoutMs),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        private static ProbeResult ProbeUnbound(IPAddress target, int ttl, int timeoutMs)
        {
            using (var ping = new Ping())
            {
                var options = new PingOptions(ttl, true);
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    PingReply reply = ping.Send(target, timeoutMs, Payload, options);
                    stopwatch.Stop();

                    double elapsed = reply.RoundtripTime > 0
                        ? reply.RoundtripTime
                        : stopwatch.Elapsed.TotalMilliseconds;

                    if (reply.Status == IPStatus.Success)
                    {
                        string address = reply.Address.ToString();
                        return new ProbeResult(true, address, elapsed, IsSameAddress(reply.Address, target));
                    }

                    if (reply.Status == IPStatus.TtlExpired ||
                        reply.Status == IPStatus.DestinationHostUnreachable ||
                        reply.Status == IPStatus.DestinationNetworkUnreachable)
                    {
                        if (reply.Address != null && !reply.Address.Equals(IPAddress.Any))
                        {
                            return new ProbeResult(true, reply.Address.ToString(), elapsed, false);
                        }
                    }

                    return ProbeResult.Timeout();
                }
                catch (PingException)
                {
                    return ProbeResult.Timeout();
                }
                catch (SocketException)
                {
                    return ProbeResult.Timeout();
                }
            }
        }

        private static ProbeResult ProbeBound(IPAddress target, int ttl, int timeoutMs, IPAddress sourceAddress)
        {
            IntPtr handle = IcmpCreateFile();
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return ProbeResult.Timeout();
            }

            IntPtr requestBuffer = IntPtr.Zero;
            IntPtr replyBuffer = IntPtr.Zero;
            IntPtr optionsBuffer = IntPtr.Zero;

            try
            {
                int replySize = Marshal.SizeOf(typeof(IcmpEchoReply)) + Payload.Length + 8;
                replyBuffer = Marshal.AllocHGlobal(replySize);

                requestBuffer = Marshal.AllocHGlobal(Payload.Length);
                Marshal.Copy(Payload, 0, requestBuffer, Payload.Length);

                var requestOptions = new IpOptionInformation
                {
                    Ttl = (byte)ttl,
                    Tos = 0,
                    Flags = 0,
                    OptionsSize = 0,
                    OptionsData = IntPtr.Zero
                };
                optionsBuffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(IpOptionInformation)));
                Marshal.StructureToPtr(requestOptions, optionsBuffer, false);

                uint sourceIp = ToIpAddr(sourceAddress);
                uint destinationIp = ToIpAddr(target);

                var stopwatch = Stopwatch.StartNew();
                uint replyCount = IcmpSendEcho2Ex(
                    handle,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    sourceIp,
                    destinationIp,
                    requestBuffer,
                    (ushort)Payload.Length,
                    optionsBuffer,
                    replyBuffer,
                    (uint)replySize,
                    (uint)timeoutMs);
                stopwatch.Stop();

                if (replyCount == 0)
                {
                    return ProbeResult.Timeout();
                }

                var reply = (IcmpEchoReply)Marshal.PtrToStructure(replyBuffer, typeof(IcmpEchoReply))!;

                // Same reasoning as the unbound path: the API only fills RoundTripTime for a
                // successful echo, so fall back to the measured elapsed time for TTL-expired hops.
                double elapsed = reply.RoundTripTime > 0
                    ? reply.RoundTripTime
                    : stopwatch.Elapsed.TotalMilliseconds;

                if (reply.Status == IpSuccess)
                {
                    var address = new IPAddress(BitConverter.GetBytes(reply.Address));
                    return new ProbeResult(true, address.ToString(), elapsed, IsSameAddress(address, target));
                }

                if (reply.Status == IpTtlExpiredTransit ||
                    reply.Status == IpDestHostUnreachable ||
                    reply.Status == IpDestNetUnreachable)
                {
                    if (reply.Address != 0)
                    {
                        var address = new IPAddress(BitConverter.GetBytes(reply.Address));
                        return new ProbeResult(true, address.ToString(), elapsed, false);
                    }
                }

                return ProbeResult.Timeout();
            }
            catch (Exception)
            {
                return ProbeResult.Timeout();
            }
            finally
            {
                if (requestBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(requestBuffer); }
                if (replyBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(replyBuffer); }
                if (optionsBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(optionsBuffer); }
                IcmpCloseHandle(handle);
            }
        }

        private static bool IsSameAddress(IPAddress a, IPAddress b)
        {
            return a != null && b != null && a.Equals(b);
        }

        private static uint ToIpAddr(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length != 4)
            {
                throw new NotSupportedException("Only IPv4 addresses are supported.");
            }
            return BitConverter.ToUInt32(bytes, 0);
        }

        /// <summary>
        /// Resolves an interface name (or description/ID) to its first IPv4 address, for -Interface.
        /// </summary>
        public static IPAddress? GetInterfaceAddress(string interfaceName)
        {
            NetworkInterface? nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n =>
                    string.Equals(n.Name, interfaceName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n.Description, interfaceName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n.Id, interfaceName, StringComparison.OrdinalIgnoreCase));

            if (nic == null)
            {
                return null;
            }

            return nic.GetIPProperties().UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(u => u.Address)
                .FirstOrDefault();
        }

        /// <summary>
        /// Determines which local IPv4 address the OS routing table would use to reach the given
        /// target. Connecting a UDP socket performs the route lookup and populates LocalEndPoint
        /// without putting a single packet on the wire.
        /// </summary>
        public static IPAddress? GetSourceAddressForTarget(IPAddress target)
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(target, 65530);
                    return (socket.LocalEndPoint as IPEndPoint)?.Address;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Finds the interface that owns a given local address, returning its friendly name and
        /// IPv4 interface index. Returns nulls when no match is found.
        /// </summary>
        public static (string? Name, int? Index) GetInterfaceInfo(IPAddress localAddress)
        {
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    IPInterfaceProperties properties = nic.GetIPProperties();

                    bool owns = properties.UnicastAddresses
                        .Any(u => u.Address.Equals(localAddress));

                    if (!owns)
                    {
                        continue;
                    }

                    int? index = null;
                    try
                    {
                        index = properties.GetIPv4Properties()?.Index;
                    }
                    catch (NetworkInformationException)
                    {
                        // Adapter has no IPv4 properties - leave the index blank.
                    }

                    return (nic.Name, index);
                }
            }
            catch (Exception)
            {
                // Fall through to the empty result below.
            }

            return (null, null);
        }

        public static string[] GetInterfaceNames()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.GetIPProperties().UnicastAddresses
                    .Any(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
                .Select(n => n.Name)
                .ToArray();
        }

        // --- P/Invoke declarations ---

        [DllImport("Iphlpapi.dll", SetLastError = true)]
        private static extern IntPtr IcmpCreateFile();

        [DllImport("Iphlpapi.dll", SetLastError = true)]
        private static extern bool IcmpCloseHandle(IntPtr handle);

        [DllImport("Iphlpapi.dll", SetLastError = true)]
        private static extern uint IcmpSendEcho2Ex(
            IntPtr icmpHandle,
            IntPtr @event,
            IntPtr apcRoutine,
            IntPtr apcContext,
            uint sourceAddress,
            uint destinationAddress,
            IntPtr requestData,
            ushort requestSize,
            IntPtr requestOptions,
            IntPtr replyBuffer,
            uint replySize,
            uint timeout);

        [StructLayout(LayoutKind.Sequential)]
        private struct IpOptionInformation
        {
            public byte Ttl;
            public byte Tos;
            public byte Flags;
            public byte OptionsSize;
            public IntPtr OptionsData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IcmpEchoReply
        {
            public uint Address;
            public uint Status;
            public uint RoundTripTime;
            public ushort DataSize;
            public ushort Reserved;
            public IntPtr Data;
            public IpOptionInformation Options;
        }
    }
}
