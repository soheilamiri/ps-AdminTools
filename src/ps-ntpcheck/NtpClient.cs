using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace PSAdminTools.NtpCheck
{
    /// <summary>
    /// Minimal SNTP (RFC 4330) client implemented over a raw UDP socket.
    /// Works identically on Windows, Linux, and macOS under .NET 10 -
    /// no platform-specific APIs or external NuGet packages required.
    /// </summary>
    internal static class NtpClient
    {
        private const int NtpPort = 123;
        private const int NtpPacketLength = 48;
        private const byte NtpClientRequestHeader = 0x1B; // LI=0, VN=3, Mode=3 (client)
        private static readonly DateTime NtpEpoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Queries the given NTP server and returns its current time (converted to local time).
        /// Throws on DNS failure, timeout, or an unreachable/non-responding server.
        /// </summary>
        public static DateTime GetNetworkTime(string ntpServer, int timeoutMs = 3000)
        {
            if (string.IsNullOrWhiteSpace(ntpServer))
            {
                throw new ArgumentException("NTP server address must not be empty.", nameof(ntpServer));
            }

            IPAddress[] addresses;
            try
            {
                addresses = Dns.GetHostAddresses(ntpServer);
            }
            catch (Exception ex)
            {
                throw new NtpQueryException($"Unable to resolve '{ntpServer}': {ex.Message}", ex);
            }

            IPAddress? targetAddress = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                                        ?? addresses.FirstOrDefault();

            if (targetAddress == null)
            {
                throw new NtpQueryException($"Could not resolve any IP address for '{ntpServer}'.");
            }

            var requestData = new byte[NtpPacketLength];
            requestData[0] = NtpClientRequestHeader;

            var endpoint = new IPEndPoint(targetAddress, NtpPort);

            using (var socket = new Socket(targetAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.ReceiveTimeout = timeoutMs;
                socket.SendTimeout = timeoutMs;

                try
                {
                    socket.Connect(endpoint);
                    socket.Send(requestData);

                    var responseData = new byte[NtpPacketLength];
                    int received = socket.Receive(responseData);

                    if (received < NtpPacketLength)
                    {
                        throw new NtpQueryException($"'{ntpServer}' returned an incomplete NTP response.");
                    }

                    return ParseNtpResponse(responseData);
                }
                catch (SocketException ex)
                {
                    throw new NtpQueryException($"NTP query to '{ntpServer}' ({targetAddress}) failed or timed out: {ex.Message}", ex);
                }
            }
        }

        private static DateTime ParseNtpResponse(byte[] ntpData)
        {
            // Bytes 40-43: Transmit Timestamp seconds (since 1900-01-01, big-endian)
            // Bytes 44-47: Transmit Timestamp fraction (big-endian)
            const int transmitTimestampOffset = 40;

            uint intPart = SwapEndianness(BitConverter.ToUInt32(ntpData, transmitTimestampOffset));
            uint fracPart = SwapEndianness(BitConverter.ToUInt32(ntpData, transmitTimestampOffset + 4));

            double milliseconds = (intPart * 1000.0) + (fracPart * 1000.0 / 0x100000000L);

            return NtpEpoch.AddMilliseconds(milliseconds).ToLocalTime();
        }

        private static uint SwapEndianness(uint x)
        {
            return ((x & 0x000000ff) << 24) +
                   ((x & 0x0000ff00) << 8) +
                   ((x & 0x00ff0000) >> 8) +
                   ((x & 0xff000000) >> 24);
        }
    }

    internal sealed class NtpQueryException : Exception
    {
        public NtpQueryException(string message) : base(message) { }
        public NtpQueryException(string message, Exception innerException) : base(message, innerException) { }
    }
}
