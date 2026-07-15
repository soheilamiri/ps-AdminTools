using System;

namespace PSAdminTools.NtpCheck
{
    /// <summary>
    /// Structured result returned by Get-NtpConfig.
    /// </summary>
    public sealed class NtpConfigInfo
    {
        public string ComputerName { get; set; } = string.Empty;
        public DateTime CurrentTime { get; set; }
        public string TimeZoneId { get; set; } = string.Empty;
        public string TimeZoneDisplayName { get; set; } = string.Empty;

        /// <summary>Which NTP mechanism was detected: W32Time, chrony, systemd-timesyncd, ntpd, or Unknown.</summary>
        public string NtpService { get; set; } = "Unknown";

        /// <summary>The currently configured/active NTP reference (server address, hostname, or reference ID).</summary>
        public string NtpReference { get; set; } = string.Empty;

        /// <summary>NTP stratum of the reference, when the underlying tool reports one.</summary>
        public int? Stratum { get; set; }
    }
}
