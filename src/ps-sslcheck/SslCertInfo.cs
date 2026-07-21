using System;

namespace PSAdminTools.SslCheck
{
    /// <summary>
    /// Certificate details returned by Get-SslInfo for a given host/port.
    /// </summary>
    public sealed class SslCertInfo
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Subject { get; set; } = string.Empty;
        public string Issuer { get; set; } = string.Empty;
        public DateTime NotBefore { get; set; }
        public DateTime NotAfter { get; set; }

        /// <summary>Days until expiration. Negative if the certificate has already expired.</summary>
        public int RemainingDays { get; set; }

        public string Thumbprint { get; set; } = string.Empty;
    }
}
