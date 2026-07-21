using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace PSAdminTools.SslCheck
{
    internal sealed class SslInfoException : Exception
    {
        public SslInfoException(string message) : base(message) { }
        public SslInfoException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Connects to a remote host over TLS and inspects the certificate it presents.
    /// Uses SslStream/TcpClient directly - fully cross-platform under .NET, no external
    /// dependencies or OS-specific APIs required.
    /// </summary>
    internal static class SslInfoReader
    {
        /// <summary>
        /// Strips scheme/path/query from a URL-ish input and extracts an embedded port if present,
        /// e.g. "https://yahoo.com:8443/path" -> ("yahoo.com", 8443).
        /// </summary>
        public static (string Host, int? Port) ParseHost(string url)
        {
            string host = url.Trim();

            int schemeIdx = host.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx >= 0)
            {
                host = host.Substring(schemeIdx + 3);
            }

            int slashIdx = host.IndexOf('/');
            if (slashIdx >= 0)
            {
                host = host.Substring(0, slashIdx);
            }

            int colonIdx = host.IndexOf(':');
            int? parsedPort = null;
            if (colonIdx >= 0)
            {
                string portText = host.Substring(colonIdx + 1);
                if (int.TryParse(portText, out int p))
                {
                    parsedPort = p;
                }
                host = host.Substring(0, colonIdx);
            }

            return (host, parsedPort);
        }

        public static SslCertInfo GetCertInfo(string host, int port, int timeoutMs = 5000)
        {
            try
            {
                using (var tcpClient = new TcpClient())
                {
                    var connectTask = tcpClient.ConnectAsync(host, port);
                    if (!connectTask.Wait(timeoutMs))
                    {
                        throw new SslInfoException($"Connection to {host}:{port} timed out after {timeoutMs}ms.");
                    }

                    using (var sslStream = new SslStream(
                        tcpClient.GetStream(),
                        leaveInnerStreamOpen: false,
                        userCertificateValidationCallback: (sender, certificate, chain, errors) => true))
                        // Deliberately accept any certificate here - this cmdlet's job is to *inspect*
                        // the certificate (including expired/self-signed ones), not to validate trust.
                    {
                        var authTask = sslStream.AuthenticateAsClientAsync(host);
                        if (!authTask.Wait(timeoutMs))
                        {
                            throw new SslInfoException($"TLS handshake with {host}:{port} timed out after {timeoutMs}ms.");
                        }

                        X509Certificate? remoteCert = sslStream.RemoteCertificate;
                        if (remoteCert == null)
                        {
                            throw new SslInfoException($"{host}:{port} did not present a certificate.");
                        }

                        using (var cert2 = new X509Certificate2(remoteCert))
                        {
                            DateTime notAfterUtc = cert2.NotAfter.ToUniversalTime();
                            DateTime notBeforeUtc = cert2.NotBefore.ToUniversalTime();
                            int remainingDays = (int)Math.Floor((notAfterUtc - DateTime.UtcNow).TotalDays);

                            return new SslCertInfo
                            {
                                Host = host,
                                Port = port,
                                Subject = cert2.Subject,
                                Issuer = cert2.Issuer,
                                NotBefore = notBeforeUtc,
                                NotAfter = notAfterUtc,
                                RemainingDays = remainingDays,
                                Thumbprint = cert2.Thumbprint
                            };
                        }
                    }
                }
            }
            catch (SslInfoException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SslInfoException($"Failed to retrieve certificate from {host}:{port} - {ex.Message}", ex);
            }
        }
    }
}
