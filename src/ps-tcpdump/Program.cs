using PsTcpDump.Helpers;
using PsTcpDump.UI;
using PsTcpDump.Core;

// ── Npcap check ───────────────────────────────────────────────────────────
if (!NpcapHelper.IsInstalled())
{
    NpcapHelper.PrintErrorAndExit();
    return;
}

// ── Setup Wizard ──────────────────────────────────────────────────────────
var filterOptions = SetupWizard.Run();

// ── Pcap output file ───────────────────────────────────────────────────────
var (savePcap, pcapPath) = SetupWizard.PromptSaveOption();
var pcapWriter = savePcap ? new PcapWriter(pcapPath!) : null;

// ── Capture Engine ─────────────────────────────────────────────────────────
var engine = new CaptureEngine(filterOptions);

// ── Print header ───────────────────────────────────────────────────────────
Console.Clear();
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine($"  Interface : {filterOptions.InterfaceDescription}");
Console.WriteLine($"  Filter    : {(string.IsNullOrEmpty(filterOptions.BuildBpfFilter()) ? "none (all traffic)" : filterOptions.BuildBpfFilter())}");
if (savePcap && pcapPath != null)
    Console.WriteLine($"  Saving to : {pcapPath}");
else
    Console.WriteLine($"  Saving to : disabled");
Console.ResetColor();
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine($"  {"Time",-15} {"Proto",-6} {"Src IP",-15} {"SrcPort",-8} {"Dst IP",-15} {"DstPort",-8} Info");
Console.WriteLine($"  {"────",-15} {"─────",-6} {"──────",-15} {"───────",-8} {"──────",-15} {"───────",-8} ────");
Console.ResetColor();

// ── Wire up packet output ──────────────────────────────────────────────────
engine.PacketCaptured += (pkt) =>
{
    pcapWriter?.WritePacket(pkt);

    var srcPort = pkt.SourcePort.HasValue ? $":{pkt.SourcePort}" : "";
    var dstPort = pkt.DestinationPort.HasValue ? $":{pkt.DestinationPort}" : "";

    Console.ForegroundColor = GetProtocolColor(pkt.Protocol);
    Console.WriteLine($"  {pkt.Timestamp:HH:mm:ss.fff}   {pkt.Protocol,-6} {pkt.SourceIp,-15} {srcPort,-8} {pkt.DestinationIp,-15} {dstPort,-8} {pkt.Info}");
    Console.ResetColor();
};

engine.ErrorOccurred += (err) =>
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"  ERROR: {err}");
    Console.ResetColor();
};

// ── Ctrl+C handler ─────────────────────────────────────────────────────────
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    engine.Stop();
    pcapWriter?.Dispose();

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("  ──────────────────────────────────────────────────────");
    Console.ResetColor();
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"  Captured : {engine.TotalPackets:N0} packets ({engine.TotalBytes:N0} bytes)");
    Console.WriteLine($"  Rate     : {engine.GetPacketsPerSecond():F1} packets/sec");
    if (savePcap && pcapPath != null)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  Saved to : {pcapPath}");
    }
    Console.ResetColor();
    Environment.Exit(0);
};

// ── Start capture ──────────────────────────────────────────────────────────
try
{
    engine.Start();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("  [Capturing... press Ctrl+C to stop]");
    Console.ResetColor();
    Console.WriteLine();
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"  Failed to start capture: {ex.Message}");
    Console.ResetColor();
    return;
}

// ── Keep alive ─────────────────────────────────────────────────────────────
while (true) Thread.Sleep(100);

// ── Protocol color helper ──────────────────────────────────────────────────
static ConsoleColor GetProtocolColor(string protocol) => protocol.ToUpper() switch
{
    "TCP"    => ConsoleColor.Green,
    "UDP"    => ConsoleColor.Yellow,
    "ICMP"   => ConsoleColor.Cyan,
    "ICMPV6" => ConsoleColor.Cyan,
    "ARP"    => ConsoleColor.Magenta,
    "OTHER"  => ConsoleColor.DarkGray,
    _        => ConsoleColor.White
};
