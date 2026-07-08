using PacketDotNet;
using System.Net;

namespace PsTcpDump.Core;

public class ParsedPacket
{
    public DateTime Timestamp { get; set; }
    public string Protocol { get; set; } = "UNKNOWN";
    public string SrcMac { get; set; } = "-";
    public string DstMac { get; set; } = "-";
    public string SourceIp { get; set; } = "-";
    public string DestinationIp { get; set; } = "-";
    public int? SourcePort { get; set; }
    public int? DestinationPort { get; set; }
    public int Length { get; set; }
    public string Info { get; set; } = string.Empty;
    public byte[] RawBytes { get; set; } = [];
}

public static class PacketParser
{
    public static ParsedPacket? Parse(SharpPcap.PacketCapture capture)
    {
        try
        {
            var raw = capture.GetPacket();
            var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);

            var parsed = new ParsedPacket
            {
                Timestamp = raw.Timeval.Date,
                Length    = raw.Data.Length,
                RawBytes  = raw.Data.ToArray()
            };

            // ── Ethernet layer — extract MACs ──────────────────────────────
            var eth = packet.Extract<EthernetPacket>();
            if (eth != null)
            {
                parsed.SrcMac = eth.SourceHardwareAddress.ToString().ToLower()
                    .Replace("-", ":");
                parsed.DstMac = eth.DestinationHardwareAddress.ToString().ToLower()
                    .Replace("-", ":");
            }

            // ── IP layer ───────────────────────────────────────────────────
            var ip = packet.Extract<IPPacket>();
            if (ip != null)
            {
                parsed.SourceIp      = ip.SourceAddress.ToString();
                parsed.DestinationIp = ip.DestinationAddress.ToString();

                // ── TCP ───────────────────────────────────────────────────
                var tcp = packet.Extract<TcpPacket>();
                if (tcp != null)
                {
                    parsed.Protocol        = "TCP";
                    parsed.SourcePort      = tcp.SourcePort;
                    parsed.DestinationPort = tcp.DestinationPort;
                    parsed.Info            = BuildTcpInfo(tcp);
                    return parsed;
                }

                // ── UDP ───────────────────────────────────────────────────
                var udp = packet.Extract<UdpPacket>();
                if (udp != null)
                {
                    parsed.Protocol        = "UDP";
                    parsed.SourcePort      = udp.SourcePort;
                    parsed.DestinationPort = udp.DestinationPort;
                    parsed.Info            = $"Len={udp.Length}";
                    return parsed;
                }
// ── ICMPv4 ───────────────────────────────────────────────
var icmp = packet.Extract<IcmpV4Packet>();
if (icmp != null)
{
    parsed.Protocol = "ICMP";
    parsed.Info = icmp.TypeCode switch
    {
        IcmpV4TypeCode.EchoRequest                              => $"Echo Request  id={icmp.Id} seq={icmp.Sequence}",
        IcmpV4TypeCode.EchoReply                               => $"Echo Reply    id={icmp.Id} seq={icmp.Sequence}",
        IcmpV4TypeCode.TimeExceeded                            => "Time Exceeded (TTL expired in transit)",
        IcmpV4TypeCode.UnreachableNet                          => "Dest Unreachable (Network)",
        IcmpV4TypeCode.UnreachableHost                         => "Dest Unreachable (Host)",
        IcmpV4TypeCode.UnreachablePort                         => "Dest Unreachable (Port)",
        IcmpV4TypeCode.UnreachableProtocol                     => "Dest Unreachable (Protocol)",
        IcmpV4TypeCode.UnreachableFragmentationNeeded          => "Dest Unreachable (Fragmentation Needed)",
        IcmpV4TypeCode.UnreachableCommunicationProhibited      => "Dest Unreachable (Comm Prohibited)",
        IcmpV4TypeCode.RedirectHost                            => "Redirect (Host)",
        IcmpV4TypeCode.RedirectNetwork                         => "Redirect (Network)",
        IcmpV4TypeCode.RouterAdvertisement                     => "Router Advertisement",
        IcmpV4TypeCode.RouterSelection                         => "Router Solicitation",
        IcmpV4TypeCode.Timestamp                               => "Timestamp Request",
        IcmpV4TypeCode.TimestampReply                          => "Timestamp Reply",
        _                                                      => $"Type={icmp.TypeCode}"
    };
    return parsed;
}
                // ── ICMPv6 ───────────────────────────────────────────────
                var icmp6 = packet.Extract<IcmpV6Packet>();
                if (icmp6 != null)
                {
                    parsed.Protocol = "ICMPv6";
                    parsed.Info = icmp6.Type switch
                    {
                        IcmpV6Type.EchoRequest           => "Echo Request",
                        IcmpV6Type.EchoReply             => "Echo Reply",
                        IcmpV6Type.RouterSolicitation    => "Router Solicitation",
                        IcmpV6Type.RouterAdvertisement   => "Router Advertisement",
                        IcmpV6Type.NeighborSolicitation  => "Neighbor Solicitation",
                        IcmpV6Type.NeighborAdvertisement => "Neighbor Advertisement",
                        _                                => $"Type={icmp6.Type}"
                    };
                    return parsed;
                }

                parsed.Protocol = ip.Version == IPVersion.IPv6 ? "IPv6" : "IPv4";
                parsed.Info     = $"Proto={ip.Protocol}";
                return parsed;
            } // ── end IP block ─────────────────────────────────────────────

            // ── ARP ────────────────────────────────────────────────────────
            var arp = packet.Extract<ArpPacket>();
            if (arp != null)
            {
                parsed.Protocol      = "ARP";
                parsed.SourceIp      = arp.SenderProtocolAddress.ToString();
                parsed.DestinationIp = arp.TargetProtocolAddress.ToString();

                var senderMac = arp.SenderHardwareAddress.ToString().ToLower().Replace("-", ":");
                var targetMac = arp.TargetHardwareAddress.ToString().ToLower().Replace("-", ":");

                parsed.Info = arp.Operation switch
                {
                    ArpOperation.Request  =>
                        $"Who has {arp.TargetProtocolAddress}? Tell {arp.SenderProtocolAddress} ({senderMac})",
                    ArpOperation.Response =>
                        $"{arp.SenderProtocolAddress} is at {senderMac} → {arp.TargetProtocolAddress} ({targetMac})",
                    _ => $"Operation={arp.Operation}"
                };

                return parsed;
            }

            // ── Other ──────────────────────────────────────────────────────
            parsed.Protocol = "OTHER";
            parsed.Info     = $"LinkLayer={raw.LinkLayerType}";
            return parsed;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildTcpInfo(TcpPacket tcp)
    {
        var flags = new List<string>();
        if (tcp.Synchronize)    flags.Add("SYN");
        if (tcp.Acknowledgment) flags.Add("ACK");
        if (tcp.Finished)       flags.Add("FIN");
        if (tcp.Reset)          flags.Add("RST");
        if (tcp.Push)           flags.Add("PSH");
        if (tcp.Urgent)         flags.Add("URG");

        var flagStr = flags.Count > 0 ? $"[{string.Join(",", flags)}]" : "";
        return $"{flagStr} Seq={tcp.SequenceNumber} Len={tcp.PayloadData?.Length ?? 0}";
    }
}