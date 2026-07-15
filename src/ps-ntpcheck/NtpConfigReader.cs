using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace PSAdminTools.NtpCheck
{
    /// <summary>
    /// Reads the current time, time zone, and active NTP reference for the local machine,
    /// branching on OS to use the appropriate native tool.
    /// </summary>
    internal static class NtpConfigReader
    {
        public static NtpConfigInfo GetConfig()
        {
            var info = new NtpConfigInfo
            {
                ComputerName = Environment.MachineName,
                CurrentTime = DateTime.Now,
                TimeZoneId = TimeZoneInfo.Local.Id,
                TimeZoneDisplayName = TimeZoneInfo.Local.DisplayName
            };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                PopulateWindows(info);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                PopulateLinux(info);
            }
            else
            {
                info.NtpService = "Unsupported";
                info.NtpReference = "NTP config lookup is not implemented for this platform.";
            }

            return info;
        }

        private static void PopulateWindows(NtpConfigInfo info)
        {
            var (success, stdOut, _) = ProcessRunner.Run("w32tm", "/query /status");
            if (success && !string.IsNullOrWhiteSpace(stdOut))
            {
                info.NtpService = "W32Time";
                info.NtpReference = ExtractLineValue(stdOut, "Source:") ?? "Unknown";

                string? stratumText = ExtractLineValue(stdOut, "Stratum:");
                if (stratumText != null)
                {
                    string digits = new string(stratumText.TakeWhile(char.IsDigit).ToArray());
                    if (int.TryParse(digits, out int stratum))
                    {
                        info.Stratum = stratum;
                    }
                }
                return;
            }

            // W32Time service may not be running - fall back to its configured server in the registry.
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters"))
                {
                    string? configuredServer = key?.GetValue("NtpServer") as string;
                    if (!string.IsNullOrWhiteSpace(configuredServer))
                    {
                        info.NtpService = "W32Time (configured; service not responding)";
                        info.NtpReference = configuredServer!;
                        return;
                    }
                }
            }
            catch
            {
                // Fall through to Unknown below.
            }

            info.NtpService = "Unknown";
            info.NtpReference = "Not available - w32tm query failed and no configured server was found.";
        }

        private static void PopulateLinux(NtpConfigInfo info)
        {
            // 1. chrony - most common on modern RHEL/Ubuntu
            var (chronySuccess, chronyOut, _) = ProcessRunner.Run("chronyc", "tracking");
            if (chronySuccess && !string.IsNullOrWhiteSpace(chronyOut))
            {
                info.NtpService = "chrony";
                info.NtpReference = ExtractLineValue(chronyOut, "Reference ID") ?? "Unknown";

                string? stratumText = ExtractLineValue(chronyOut, "Stratum");
                if (stratumText != null && int.TryParse(stratumText.Trim(), out int stratum))
                {
                    info.Stratum = stratum;
                }
                return;
            }

            // 2. systemd-timesyncd - newer systemd (>= 245) exposes 'timedatectl timesync-status'
            var (syncSuccess, syncOut, _) = ProcessRunner.Run("timedatectl", "timesync-status");
            if (syncSuccess && !string.IsNullOrWhiteSpace(syncOut))
            {
                info.NtpService = "systemd-timesyncd";
                info.NtpReference = ExtractLineValue(syncOut, "Server:") ?? "Unknown";
                return;
            }

            // 2b. Older systemd fallback
            var (syncSuccess2, syncOut2, _) = ProcessRunner.Run("timedatectl", "show-timesync --property=ServerName --value");
            if (syncSuccess2 && !string.IsNullOrWhiteSpace(syncOut2))
            {
                info.NtpService = "systemd-timesyncd";
                info.NtpReference = syncOut2.Trim();
                return;
            }

            // 3. Classic ntpd
            var (ntpqSuccess, ntpqOut, _) = ProcessRunner.Run("ntpq", "-pn");
            if (ntpqSuccess && !string.IsNullOrWhiteSpace(ntpqOut))
            {
                info.NtpService = "ntpd";
                string? peerLine = ntpqOut
                    .Split('\n')
                    .FirstOrDefault(l => l.TrimStart().StartsWith("*", StringComparison.Ordinal));

                if (peerLine != null)
                {
                    string[] parts = peerLine.TrimStart('*', ' ')
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    info.NtpReference = parts.Length > 0 ? parts[0] : "Unknown";
                }
                else
                {
                    info.NtpReference = "No synchronized peer found.";
                }
                return;
            }

            info.NtpService = "Unknown";
            info.NtpReference = "No supported NTP service detected (tried chrony, systemd-timesyncd, ntpd).";
        }

        private static string? ExtractLineValue(string text, string label)
        {
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                int idx = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string rest = line.Substring(idx + label.Length);
                    return rest.TrimStart(':', '=', ' ', '\t').Trim();
                }
            }
            return null;
        }
    }
}
