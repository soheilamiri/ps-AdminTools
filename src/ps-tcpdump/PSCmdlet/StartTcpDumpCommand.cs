using System.Management.Automation;
using PsTcpDump.Core;
using PsTcpDump.Helpers;

namespace PsTcpDump.PSCmdlet;

[Cmdlet(VerbsLifecycle.Start, "TcpDump")]
public class StartTcpDumpCommand : System.Management.Automation.PSCmdlet
{
    [Parameter(Position = 0, HelpMessage = "Network interface name")]
    public string? Interface { get; set; }

    [Parameter(HelpMessage = "Source IP address filter")]
    public string? SourceIp { get; set; }

    [Parameter(HelpMessage = "Destination IP address filter")]
    public string? DestinationIp { get; set; }

    [Parameter(HelpMessage = "Port filter (matches src or dst)")]
    public int? Port { get; set; }

    [Parameter(HelpMessage = "Save capture to .pcap file path")]
    public string? OutFile { get; set; }

    protected override void BeginProcessing()
    {
        // ── Npcap check ───────────────────────────────────────────────────
        if (!NpcapHelper.IsInstalled())
        {
            ThrowTerminatingError(new ErrorRecord(
                new Exception("Npcap is not installed. Download from https://npcap.com"),
                "NpcapNotFound",
                ErrorCategory.NotInstalled,
                null));
            return;
        }

        // ── Build filter options ───────────────────────────────────────────
        var filterOptions = BuildFilterOptions();

        // ── Setup pcap writer ──────────────────────────────────────────────
        PcapWriter? pcapWriter = null;
        if (!string.IsNullOrEmpty(OutFile))
        {
            var dir = Path.GetDirectoryName(OutFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            pcapWriter = new PcapWriter(OutFile);
            WriteHost($"  Saving to : {OutFile}", ConsoleColor.Cyan);
        }

        // ── Start engine ───────────────────────────────────────────────────
        var engine = new CaptureEngine(filterOptions);

        engine.ErrorOccurred += (err) =>
            WriteWarning($"Capture error: {err}");

        engine.PacketCaptured += (pkt) =>
        {
            pcapWriter?.WritePacket(pkt);

            var srcPort = pkt.SourcePort.HasValue ? $":{pkt.SourcePort}" : "";
            var dstPort = pkt.DestinationPort.HasValue ? $":{pkt.DestinationPort}" : "";

            var line = $"  {pkt.Timestamp:HH:mm:ss.fff}   {pkt.Protocol,-6} {pkt.SourceIp,-15} {srcPort,-8} {pkt.DestinationIp,-15} {dstPort,-8} {pkt.Info}";
            WriteHost(line, GetProtocolColor(pkt.Protocol));
        };

        WriteHost($"  Interface : {filterOptions.InterfaceDescription}", ConsoleColor.Cyan);
        WriteHost($"  Filter    : {(string.IsNullOrEmpty(filterOptions.BuildBpfFilter()) ? "none (all traffic)" : filterOptions.BuildBpfFilter())}", ConsoleColor.Cyan);
        WriteHost($"  Press Ctrl+C to stop...", ConsoleColor.Green);
        WriteHost("");

        try
        {
            engine.Start();

            // Keep running until Ctrl+C
            while (true)
            {
                if (Stopping)
                {
                    engine.Stop();
                    pcapWriter?.Dispose();
                    WriteHost($"\n  Captured : {engine.TotalPackets:N0} packets ({engine.TotalBytes:N0} bytes)", ConsoleColor.Yellow);
                    if (!string.IsNullOrEmpty(OutFile))
                        WriteHost($"  Saved to : {OutFile}", ConsoleColor.Cyan);
                    break;
                }
                Thread.Sleep(100);
            }
        }
        catch (Exception ex)
        {
            pcapWriter?.Dispose();
            ThrowTerminatingError(new ErrorRecord(ex, "CaptureError", ErrorCategory.OperationStopped, null));
        }
    }

    private FilterOptions BuildFilterOptions()
    {
        var devices = SharpPcap.CaptureDeviceList.Instance;

        if (devices.Count == 0)
            throw new Exception("No network interfaces found.");

        // Match by name or description if provided
        SharpPcap.ICaptureDevice? selected = null;
        if (!string.IsNullOrEmpty(Interface))
        {
            selected = devices.FirstOrDefault(d =>
                d.Name.Contains(Interface, StringComparison.OrdinalIgnoreCase) ||
                d.Description.Contains(Interface, StringComparison.OrdinalIgnoreCase));

            if (selected == null)
                throw new Exception($"Interface '{Interface}' not found.");
        }
        else
        {
            // Show list and prompt
            WriteHost("\n  Available interfaces:", ConsoleColor.Cyan);
            for (int i = 0; i < devices.Count; i++)
                WriteHost($"  [{i + 1}] {devices[i].Description}");

            WriteHost("\n  Use -Interface to specify one, or pipe number: ", ConsoleColor.White);
            var input = Console.ReadLine()?.Trim();
            if (int.TryParse(input, out int idx) && idx >= 1 && idx <= devices.Count)
                selected = devices[idx - 1];
            else
                selected = devices[0];
        }

        return new FilterOptions
        {
            InterfaceName = selected.Name,
            InterfaceDescription = string.IsNullOrWhiteSpace(selected.Description)
                ? selected.Name : selected.Description,
            SourceIp = SourceIp,
            DestinationIp = DestinationIp,
            LocalPort = Port
        };
    }

    private void WriteHost(string message, ConsoleColor? color = null)
    {
        if (color.HasValue)
        {
            var colorParam = new System.Collections.Hashtable { ["ForegroundColor"] = color.Value.ToString() };
            InvokeCommand.InvokeScript($"Write-Host '{message.Replace("'", "''")}' -ForegroundColor {color.Value}");
        }
        else
        {
            InvokeCommand.InvokeScript($"Write-Host '{message.Replace("'", "''")}'");
        }
    }

    private static ConsoleColor GetProtocolColor(string protocol) => protocol.ToUpper() switch
    {
        "TCP"    => ConsoleColor.Green,
        "UDP"    => ConsoleColor.Yellow,
        "ICMP"   => ConsoleColor.Cyan,
        "ICMPV6" => ConsoleColor.Cyan,
        "ARP"    => ConsoleColor.Magenta,
        "OTHER"  => ConsoleColor.DarkGray,
        _        => ConsoleColor.White
    };
}