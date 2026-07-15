using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PSAdminTools.NtpCheck
{
    /// <summary>
    /// Writes a new NTP server list to the OS-native time-sync configuration and restarts
    /// the relevant service. Requires Administrator (Windows) or root (Linux) privileges.
    /// </summary>
    internal static class NtpConfigWriter
    {
        public static (bool Success, string Message, string ServiceUsed) SetServers(string[] servers)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return SetWindows(servers);
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return SetLinux(servers);
            }
            return (false, "Set-NtpConf is not supported on this platform.", "Unsupported");
        }

        // ── Windows (W32Time) ──────────────────────────────────────────────

        private static bool IsWindowsAdmin()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private static (bool, string, string) SetWindows(string[] servers)
        {
            if (!IsWindowsAdmin())
            {
                return (false, "Access denied: Set-NtpConf must be run from an elevated (Administrator) PowerShell session.", "W32Time");
            }

            string peerList = string.Join(",", servers);
            var (cfgOk, _, cfgErr) = ProcessRunner.Run(
                "w32tm",
                $"/config /manualpeerlist:\"{peerList}\" /syncfromflags:manual /reliable:YES /update",
                5000);

            if (!cfgOk)
            {
                return (false, $"Failed to update W32Time configuration: {cfgErr}", "W32Time");
            }

            // Restart so the new peer list takes effect. Ignore failure on stop - it may already be stopped.
            ProcessRunner.Run("net", "stop w32time", 5000);
            var (startOk, _, startErr) = ProcessRunner.Run("net", "start w32time", 5000);
            if (!startOk)
            {
                return (false, $"Configuration updated, but failed to restart the W32Time service: {startErr}", "W32Time");
            }

            // Best-effort immediate resync; not fatal if it fails.
            ProcessRunner.Run("w32tm", "/resync /force", 5000);

            return (true, $"W32Time configured with {servers.Length} server(s) and restarted.", "W32Time");
        }

        // ── Linux (chrony / systemd-timesyncd / ntpd) ──────────────────────

        private static bool IsLinuxRoot()
        {
            var (ok, stdOut, _) = ProcessRunner.Run("id", "-u");
            return ok && stdOut.Trim() == "0";
        }

        private static string DetectLinuxService()
        {
            var (chronyFound, _, _) = ProcessRunner.Run("which", "chronyc");
            if (chronyFound)
            {
                return "chrony";
            }

            if (File.Exists("/etc/systemd/timesyncd.conf"))
            {
                return "systemd-timesyncd";
            }

            var (ntpdFound, _, _) = ProcessRunner.Run("which", "ntpd");
            if (ntpdFound)
            {
                return "ntpd";
            }

            return "Unknown";
        }

        private static (bool, string, string) SetLinux(string[] servers)
        {
            if (!IsLinuxRoot())
            {
                return (false, "Access denied: Set-NtpConf must be run as root (e.g. sudo pwsh).", "Unknown");
            }

            string detected = DetectLinuxService();
            switch (detected)
            {
                case "chrony":
                    return SetChrony(servers);
                case "systemd-timesyncd":
                    return SetTimesyncd(servers);
                case "ntpd":
                    return SetNtpd(servers);
                default:
                    return (false, "No supported NTP service found. Install chrony, systemd-timesyncd, or ntpd first.", "Unknown");
            }
        }

        private static (bool, string, string) SetChrony(string[] servers)
        {
            string path = File.Exists("/etc/chrony/chrony.conf") ? "/etc/chrony/chrony.conf" : "/etc/chrony.conf";

            try
            {
                List<string> lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();

                lines = lines.Where(l =>
                {
                    string t = l.TrimStart();
                    return !(t.StartsWith("server ", StringComparison.OrdinalIgnoreCase) ||
                             t.StartsWith("pool ", StringComparison.OrdinalIgnoreCase));
                }).ToList();

                lines.AddRange(servers.Select(s => $"server {s} iburst"));
                File.WriteAllLines(path, lines);
            }
            catch (Exception ex)
            {
                return (false, $"Failed to write {path}: {ex.Message}", "chrony");
            }

            var (restartOk, _, restartErr) = ProcessRunner.Run("systemctl", "restart chronyd");
            if (!restartOk)
            {
                (restartOk, _, restartErr) = ProcessRunner.Run("systemctl", "restart chrony");
            }
            if (!restartOk)
            {
                return (false, $"Config written to {path}, but failed to restart chrony: {restartErr}", "chrony");
            }

            ProcessRunner.Run("chronyc", "makestep"); // best-effort immediate step

            return (true, $"chrony configured with {servers.Length} server(s) in {path} and service restarted.", "chrony");
        }

        private static (bool, string, string) SetTimesyncd(string[] servers)
        {
            const string path = "/etc/systemd/timesyncd.conf";
            string ntpLine = "NTP=" + string.Join(" ", servers);

            try
            {
                List<string> lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();

                int timeSectionIndex = lines.FindIndex(l => l.Trim().Equals("[Time]", StringComparison.OrdinalIgnoreCase));
                if (timeSectionIndex < 0)
                {
                    lines.Add("[Time]");
                    lines.Add(ntpLine);
                }
                else
                {
                    int ntpLineIndex = -1;
                    for (int i = timeSectionIndex + 1; i < lines.Count; i++)
                    {
                        string trimmed = lines[i].Trim();
                        if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                        {
                            break; // reached the next section
                        }
                        if (trimmed.StartsWith("NTP=", StringComparison.OrdinalIgnoreCase))
                        {
                            ntpLineIndex = i;
                            break;
                        }
                    }

                    if (ntpLineIndex >= 0)
                    {
                        lines[ntpLineIndex] = ntpLine;
                    }
                    else
                    {
                        lines.Insert(timeSectionIndex + 1, ntpLine);
                    }
                }

                File.WriteAllLines(path, lines);
            }
            catch (Exception ex)
            {
                return (false, $"Failed to write {path}: {ex.Message}", "systemd-timesyncd");
            }

            var (restartOk, _, restartErr) = ProcessRunner.Run("systemctl", "restart systemd-timesyncd");
            if (!restartOk)
            {
                return (false, $"Config written to {path}, but failed to restart systemd-timesyncd: {restartErr}", "systemd-timesyncd");
            }

            return (true, $"systemd-timesyncd configured with {servers.Length} server(s) in {path} and service restarted.", "systemd-timesyncd");
        }

        private static (bool, string, string) SetNtpd(string[] servers)
        {
            const string path = "/etc/ntp.conf";

            try
            {
                List<string> lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
                lines = lines.Where(l => !l.TrimStart().StartsWith("server ", StringComparison.OrdinalIgnoreCase)).ToList();
                lines.AddRange(servers.Select(s => $"server {s} iburst"));
                File.WriteAllLines(path, lines);
            }
            catch (Exception ex)
            {
                return (false, $"Failed to write {path}: {ex.Message}", "ntpd");
            }

            var (restartOk, _, restartErr) = ProcessRunner.Run("systemctl", "restart ntpd");
            if (!restartOk)
            {
                (restartOk, _, restartErr) = ProcessRunner.Run("systemctl", "restart ntp");
            }
            if (!restartOk)
            {
                return (false, $"Config written to {path}, but failed to restart ntpd: {restartErr}", "ntpd");
            }

            return (true, $"ntpd configured with {servers.Length} server(s) in {path} and service restarted.", "ntpd");
        }
    }
}
