using SharpPcap;
using PsTcpDump.Core;
using System.Net;
using System.Diagnostics;

namespace PsTcpDump.UI;

public static class SetupWizard
{
    public static FilterOptions Run()
    {
        Console.Clear();
        PrintBanner();

        var options = new FilterOptions();

        // ── Step 1: Select interface ───────────────────────────────────────
        var devices = CaptureDeviceList.Instance;

        if (devices.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  No network interfaces found. Is Npcap installed correctly?");
            Console.ResetColor();
            Environment.Exit(1);
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ┌─────────────────────────────────────────────────────┐");
        Console.WriteLine("  │            SELECT NETWORK INTERFACE                 │");
        Console.WriteLine("  └─────────────────────────────────────────────────────┘");
        Console.ResetColor();
        Console.WriteLine();

        for (int i = 0; i < devices.Count; i++)
        {
            var dev = devices[i];
            var desc = string.IsNullOrWhiteSpace(dev.Description) ? $"Interface {i + 1}" : dev.Description;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"  [{i + 1}] ");
            Console.ResetColor();
            Console.WriteLine(desc);
        }

        Console.WriteLine();
        int selected = PromptInt("  Select interface number", 1, devices.Count);
        var selectedDevice = devices[selected - 1];
        options.InterfaceName = selectedDevice.Name;
        options.InterfaceDescription = string.IsNullOrWhiteSpace(selectedDevice.Description)
            ? selectedDevice.Name
            : selectedDevice.Description;

        // ── Step 2: Optional filters ───────────────────────────────────────
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ┌─────────────────────────────────────────────────────┐");
        Console.WriteLine("  │               OPTIONAL FILTERS                      │");
        Console.WriteLine("  └─────────────────────────────────────────────────────┘");
        Console.ResetColor();
        Console.WriteLine();

        options.SourceIp = PromptOptionalIp("  Source IP address      (Enter to skip)");
        options.DestinationIp = PromptOptionalIp("  Destination IP address (Enter to skip)");
        options.LocalPort = PromptOptionalPort("  Port (src or dst)      (Enter to skip)");

        // ── Summary ────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  ┌─────────────────────────────────────────────────────┐");
        Console.WriteLine("  │                CAPTURE SUMMARY                      │");
        Console.WriteLine("  └─────────────────────────────────────────────────────┘");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"  Interface : {options.InterfaceDescription}");
        Console.WriteLine($"  Source IP : {options.SourceIp ?? "any"}");
        Console.WriteLine($"  Dest IP   : {options.DestinationIp ?? "any"}");
        Console.WriteLine($"  Port      : {(options.LocalPort.HasValue ? $"{options.LocalPort} (src or dst)" : "any")}");

        var bpf = options.BuildBpfFilter();
        if (!string.IsNullOrEmpty(bpf))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  BPF Filter: {bpf}");
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  Press any key to continue");
        Console.ResetColor();
        Console.Write("...");
        Console.ReadKey(true);

        return options;
    }

    public static (bool save, string? path) PromptSaveOption()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ┌─────────────────────────────────────────────────────┐");
        Console.WriteLine("  │                 SAVE CAPTURE FILE                   │");
        Console.WriteLine("  └─────────────────────────────────────────────────────┘");
        Console.ResetColor();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.White;
        Console.Write("  Save capture to .pcap file? [y/N]: ");
        Console.ResetColor();

        var answer = Console.ReadLine()?.Trim().ToLower();
        if (answer != "y" && answer != "yes")
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  Skipping file save.");
            Console.ResetColor();
            return (false, null);
        }

        // ── Ask for path ───────────────────────────────────────────────────
        var defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ps-tcpdump");
        var defaultName = $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.pcap";

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  Default folder : {defaultDir}");
        Console.WriteLine($"  Default name   : {defaultName}");
        Console.ResetColor();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.White;
        Console.Write("  Save folder (Enter for default): ");
        Console.ResetColor();
        var folderInput = Console.ReadLine()?.Trim();
        var folder = string.IsNullOrEmpty(folderInput) ? defaultDir : folderInput;

        while (true)
        {
            try { Directory.CreateDirectory(folder); break; }
            catch
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  Invalid folder. Try again or press Enter for default.");
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("  Save folder (Enter for default): ");
                Console.ResetColor();
                folderInput = Console.ReadLine()?.Trim();
                folder = string.IsNullOrEmpty(folderInput) ? defaultDir : folderInput;
            }
        }

        Console.ForegroundColor = ConsoleColor.White;
        Console.Write("  File name   (Enter for default): ");
        Console.ResetColor();
        var nameInput = Console.ReadLine()?.Trim();
        var fileName = string.IsNullOrEmpty(nameInput) ? defaultName : nameInput;

        if (!fileName.EndsWith(".pcap", StringComparison.OrdinalIgnoreCase))
            fileName += ".pcap";

        var fullPath = Path.Combine(folder, fileName);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Output file  : {fullPath}");
        Console.ResetColor();

        return (true, fullPath);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine();
        Console.WriteLine(@"  ██████╗ ███████╗    ████████╗ ██████╗██████╗ ██╗   ██╗███╗   ███╗██████╗ ");
        Console.WriteLine(@"  ██╔══██╗██╔════╝       ██╔══╝██╔════╝██╔══██╗██║   ██║████╗ ████║██╔══██╗");
        Console.WriteLine(@"  ██████╔╝███████╗       ██║   ██║     ██████╔╝██║   ██║██╔████╔██║██████╔╝");
        Console.WriteLine(@"  ██╔═══╝ ╚════██║       ██║   ██║     ██╔═══╝ ██║   ██║██║╚██╔╝██║██╔═══╝ ");
        Console.WriteLine(@"  ██║     ███████║       ██║   ╚██████╗██║     ╚██████╔╝██║ ╚═╝ ██║██║     ");
        Console.WriteLine(@"  ╚═╝     ╚══════╝       ╚═╝    ╚═════╝╚═╝      ╚═════╝ ╚═╝     ╚═╝╚═╝     ");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("                         Packet Capture Tool for Windows");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static int PromptInt(string prompt, int min, int max)
    {
        while (true)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"{prompt} [{min}-{max}]: ");
            Console.ResetColor();
            var input = Console.ReadLine()?.Trim();
            if (int.TryParse(input, out int val) && val >= min && val <= max)
                return val;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  Invalid input. Enter a number between {min} and {max}.");
            Console.ResetColor();
        }
    }

    private static string? PromptOptionalIp(string prompt)
    {
        while (true)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"{prompt}: ");
            Console.ResetColor();
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input)) return null;
            if (IPAddress.TryParse(input, out _)) return input;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  Invalid IP address. Try again or press Enter to skip.");
            Console.ResetColor();
        }
    }

    private static int? PromptOptionalPort(string prompt)
    {
        while (true)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"{prompt}: ");
            Console.ResetColor();
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input)) return null;
            if (int.TryParse(input, out int port) && port >= 1 && port <= 65535)
                return port;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  Invalid port. Enter a number between 1 and 65535.");
            Console.ResetColor();
        }
    }
}