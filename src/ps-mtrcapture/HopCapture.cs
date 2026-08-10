using System.Collections.Concurrent;
using System.Net;
using Microsoft.Win32;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace PSAdminTools.MtrCapture;

/// <summary>
/// Identifies traceroute hops for TCP-mode probes by capturing the ICMP Time Exceeded messages
/// routers send back.
///
/// This exists because a TCP socket cannot see those messages. When a router discards a probe
/// whose TTL hit zero, it replies with ICMP addressed to the IP layer - a failed connect()
/// reports only that it failed, never which router was responsible. Reading them normally needs
/// a raw socket, which Windows restricts to Administrator; capturing through Npcap avoids that,
/// and Npcap is already a dependency of Start-BwMon and Start-TcpDump.
///
/// Matching works on the quoted headers: a Time Exceeded message carries the original IP header
/// plus the first 8 bytes of the original TCP header, which include the source port. Giving each
/// TTL its own local port therefore identifies which hop a given reply belongs to, and the
/// router's identity is simply the ICMP packet's own source address.
///
/// Every public member takes and returns primitives, because MtrCheck.dll (netstandard2.0)
/// invokes this by reflection and cannot reference the types directly.
/// </summary>
public sealed class HopCapture : IDisposable
{
    private ICaptureDevice? _device;
    private volatile bool _running;

    // Keyed by the local source port of the probe that provoked the reply.
    private readonly ConcurrentDictionary<int, HopReply> _replies = new();

    private readonly struct HopReply
    {
        public HopReply(string address, long arrivalTicks)
        {
            Address = address;
            ArrivalTicks = arrivalTicks;
        }

        public string Address { get; }
        public long ArrivalTicks { get; }
    }

    /// <summary>
    /// True when Npcap is installed. Mirrors the check ps-tcpdump uses, so both tools agree on
    /// what "available" means.
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            foreach (string key in new[] { @"SOFTWARE\Npcap", @"SOFTWARE\WOW6432Node\Npcap" })
            {
                using RegistryKey? reg = Registry.LocalMachine.OpenSubKey(key);
                if (reg != null)
                {
                    return true;
                }
            }

            string dllPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "Npcap", "wpcap.dll");

            return File.Exists(dllPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens the interface owning the given source address and begins capturing ICMP.
    /// Returns false rather than throwing if Npcap is absent, the device cannot be found, or the
    /// driver refuses access - the caller then falls back to unidentified hops.
    /// </summary>
    public bool Start(string sourceAddress)
    {
        try
        {
            if (!IsAvailable())
            {
                return false;
            }

            _device = FindDevice(sourceAddress);
            if (_device == null)
            {
                return false;
            }

            // 1000 ms read timeout matches ps-tcpdump. Promiscuous is not strictly required -
            // these replies are addressed to us - but it keeps behaviour consistent with the
            // other capture tool in this module.
            _device.Open(DeviceModes.Promiscuous, 1000);

            // Only ICMP is of interest; TCP replies from the destination are detected by the
            // connecting socket itself, not here.
            _device.Filter = "icmp";

            _device.OnPacketArrival += OnPacketArrival;
            _running = true;
            _device.StartCapture();

            return true;
        }
        catch (Exception)
        {
            Stop();
            return false;
        }
    }

    private static ICaptureDevice? FindDevice(string sourceAddress)
    {
        if (!IPAddress.TryParse(sourceAddress, out IPAddress? wanted))
        {
            return null;
        }

        foreach (ICaptureDevice device in CaptureDeviceList.Instance)
        {
            if (device is not LibPcapLiveDevice live)
            {
                continue;
            }

            try
            {
                foreach (PcapAddress address in live.Addresses)
                {
                    IPAddress? candidate = address?.Addr?.ipAddress;
                    if (candidate != null && candidate.Equals(wanted))
                    {
                        return device;
                    }
                }
            }
            catch (Exception)
            {
                // Some adapters expose no usable address list - skip and keep looking.
            }
        }

        return null;
    }

    private void OnPacketArrival(object sender, PacketCapture capture)
    {
        if (!_running)
        {
            return;
        }

        try
        {
            RawCapture raw = capture.GetPacket();
            Packet packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);

            IcmpV4Packet? icmp = packet.Extract<IcmpV4Packet>();
            if (icmp == null)
            {
                return;
            }

            // TypeCode packs type in the high byte and code in the low byte. Comparing the type
            // numerically avoids depending on enum member names, which differ across
            // PacketDotNet versions. 11 = Time Exceeded.
            int icmpType = ((ushort)icmp.TypeCode) >> 8;
            if (icmpType != 11)
            {
                return;
            }

            IPv4Packet? outerIp = packet.Extract<IPv4Packet>();
            if (outerIp == null)
            {
                return;
            }

            int quotedPort = ReadQuotedSourcePort(icmp.PayloadData);
            if (quotedPort <= 0)
            {
                return;
            }

            // Use the timestamp the driver recorded when the packet actually arrived, NOT
            // DateTime.UtcNow. Npcap delivers packets to this handler in batches, so stamping
            // here gave every reply in a batch the same time - six different routers all
            // reporting an identical latency to one decimal place.
            long arrivalTicks;
            try
            {
                arrivalTicks = raw.Timeval.Date.ToUniversalTime().Ticks;
            }
            catch (Exception)
            {
                arrivalTicks = DateTime.UtcNow.Ticks;
            }

            _replies[quotedPort] = new HopReply(outerIp.SourceAddress.ToString(), arrivalTicks);
        }
        catch (Exception)
        {
            // A malformed or truncated capture must never disturb the trace.
        }
    }

    /// <summary>
    /// Extracts the source port from the headers quoted inside a Time Exceeded message. The
    /// payload holds the original IP header followed by at least the first 8 bytes of the
    /// original TCP header, whose first two bytes are the source port.
    /// </summary>
    private static int ReadQuotedSourcePort(byte[]? payload)
    {
        if (payload == null || payload.Length < 20)
        {
            return -1;
        }

        // Low nibble of the first byte is the IP header length in 32-bit words.
        int headerLength = (payload[0] & 0x0F) * 4;
        if (headerLength < 20 || payload.Length < headerLength + 2)
        {
            return -1;
        }

        return (payload[headerLength] << 8) | payload[headerLength + 1];
    }

    /// <summary>
    /// Returns and consumes the reply recorded for a probe's local port, formatted as
    /// "address|arrivalTicks", or null when no router has answered for it. The string form keeps
    /// the reflection boundary to primitives; the caller computes round-trip time by comparing
    /// the arrival ticks against its own send time.
    /// </summary>
    public string? TakeHop(int localPort)
    {
        if (_replies.TryRemove(localPort, out HopReply reply))
        {
            return $"{reply.Address}|{reply.ArrivalTicks}";
        }

        return null;
    }

    public void Stop()
    {
        _running = false;

        try
        {
            if (_device != null)
            {
                _device.OnPacketArrival -= OnPacketArrival;
                _device.StopCapture();
                _device.Close();
            }
        }
        catch (Exception)
        {
            // Shutdown races are not worth reporting.
        }
        finally
        {
            _device = null;
        }
    }

    public void Dispose() => Stop();
}
