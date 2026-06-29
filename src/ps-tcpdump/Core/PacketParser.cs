using PacketDotNet;
using System.Net;

namespace PsTcpDump.Core;

public class ParsedPacket
{
    public DateTime Timestamp { get; set; }
    public string Protocol { get; set; } = "UNKNOWN";
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
                Length = raw.Data.Length,
                RawBytes = raw.Data.ToArray()
            };

            // ── IP layer ───────────────────────────────────────────────────
            var ip = packet.Extract<IPPacket>();
            if (ip != null)
            {
                parsed.SourceIp = ip.SourceAddress.ToString();
                parsed.DestinationIp = ip.DestinationAddress.ToString();

                // ── TCP ────────────────────────────────────────────────────
                var tcp = packet.Extract<TcpPacket>();
                if (tcp != null)
                {
                    parsed.Protocol = "TCP";
                    parsed.SourcePort = tcp.SourcePort;
                    parsed.DestinationPort = tcp.DestinationPort;
                    parsed.Info = BuildTcpInfo(tcp);
                    return parsed;
                }

                // ── UDP ────────────────────────────────────────────────────
                var udp = packet.Extract<UdpPacket>();
                if (udp != null)
                {
                    parsed.Protocol = "UDP";
                    parsed.SourcePort = udp.SourcePort;
                    parsed.DestinationPort = udp.DestinationPort;
                    parsed.Info = $"Len={udp.Length}";
                    return parsed;
                }

                // ── ICMP ───────────────────────────────────────────────────
                var icmp = packet.Extract<IcmpV4Packet>();
                if (icmp != null)
                {
                    parsed.Protocol = "ICMP";
                    parsed.Info = $"Type={icmp.TypeCode}";
                    return parsed;
                }

                var icmp6 = packet.Extract<IcmpV6Packet>();
                if (icmp6 != null)
                {
                    parsed.Protocol = "ICMPv6";
                    parsed.Info = $"Type={icmp6.Type}";
                    return parsed;
                }

                parsed.Protocol = ip.Version == IPVersion.IPv6 ? "IPv6" : "IPv4";
                parsed.Info = $"Proto={ip.Protocol}";
                return parsed;
            }

            // ── ARP ────────────────────────────────────────────────────────
            var arp = packet.Extract<ArpPacket>();
            if (arp != null)
            {
                parsed.Protocol = "ARP";
                parsed.SourceIp = arp.SenderProtocolAddress.ToString();
                parsed.DestinationIp = arp.TargetProtocolAddress.ToString();
                parsed.Info = $"Who has {arp.TargetProtocolAddress}?";
                return parsed;
            }

            parsed.Protocol = "OTHER";
            parsed.Info = $"LinkLayer={raw.LinkLayerType}";
            return parsed;
        }
        catch
        {
            return null; // skip malformed packets
        }
    }

    private static string BuildTcpInfo(TcpPacket tcp)
    {
        var flags = new List<string>();
        if (tcp.Synchronize) flags.Add("SYN");
        if (tcp.Acknowledgment) flags.Add("ACK");
        if (tcp.Finished) flags.Add("FIN");
        if (tcp.Reset) flags.Add("RST");
        if (tcp.Push) flags.Add("PSH");
        if (tcp.Urgent) flags.Add("URG");

        var flagStr = flags.Count > 0 ? $"[{string.Join(",", flags)}]" : "";
        return $"{flagStr} Seq={tcp.SequenceNumber} Len={tcp.PayloadData?.Length ?? 0}";
    }
}