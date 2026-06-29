using Microsoft.Win32;

namespace PsTcpDump.Helpers;

public static class NpcapHelper
{
    public static bool IsInstalled()
    {
        // Check Npcap in registry (64-bit and 32-bit)
        string[] keys =
        [
            @"SOFTWARE\Npcap",
            @"SOFTWARE\WOW6432Node\Npcap"
        ];

        foreach (var key in keys)
        {
            using var reg = Registry.LocalMachine.OpenSubKey(key);
            if (reg != null) return true;
        }

        // Fallback: check if npcap DLL exists
        string dllPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "Npcap", "wpcap.dll");

        return File.Exists(dllPath);
    }

    public static void PrintErrorAndExit()
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine();
        Console.WriteLine("  ╔══════════════════════════════════════════════════╗");
        Console.WriteLine("  ║           NPCAP NOT INSTALLED                   ║");
        Console.WriteLine("  ╠══════════════════════════════════════════════════╣");
        Console.WriteLine("  ║  ps-tcpdump requires Npcap to capture packets.  ║");
        Console.WriteLine("  ║                                                  ║");
        Console.WriteLine("  ║  Download it from: https://npcap.com            ║");
        Console.WriteLine("  ║  Install with: 'WinPcap API-compatible mode'    ║");
        Console.WriteLine("  ╚══════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("  Press any key to exit...");
        Console.ReadKey(true);
        Environment.Exit(1);
    }
}