using System;
using System.Management.Automation;

namespace PSAdminTools.NtpCheck
{
    /// <summary>
    /// Sets the NTP server list for the local machine, auto-detecting OS and NTP service
    /// (W32Time on Windows; chrony, systemd-timesyncd, or ntpd on Linux). Replaces any
    /// existing configured servers entirely. Requires Administrator/root privileges.
    /// </summary>
    [Cmdlet(VerbsCommon.Set, "NtpConf")]
    [OutputType(typeof(NtpConfigInfo))]
    public class SetNtpConfCommand : PSCmdlet
    {
        /// <summary>
        /// One or more NTP server hostnames/IPs to configure, comma-separated.
        /// Replaces any previously configured servers.
        /// </summary>
        [Parameter(Mandatory = true, Position = 0)]
        [ValidateCount(1, 10)]
        public string[] Server { get; set; } = Array.Empty<string>();

        protected override void ProcessRecord()
        {
            var (success, message, serviceUsed) = NtpConfigWriter.SetServers(Server);

            if (!success)
            {
                WriteError(new ErrorRecord(
                    new InvalidOperationException(message),
                    "SetNtpConfFailed",
                    ErrorCategory.PermissionDenied,
                    serviceUsed));
                return;
            }

            Host.UI.WriteLine(message);

            // Give the time service a moment to attempt initial sync before checking status.
            System.Threading.Thread.Sleep(2000);

            var config = NtpConfigReader.GetConfig();

            if (config.Stratum == 0 ||
                string.IsNullOrEmpty(config.NtpReference) ||
                config.NtpReference.TrimEnd().EndsWith("()"))
            {
                WriteWarning(
                    "NTP server configured and service restarted, but synchronization has not " +
                    "completed yet. This is normal and can take a few seconds up to a couple of " +
                    "minutes depending on network conditions. Run Get-NtpConf again shortly to confirm sync status.");
            }

            // Return the refreshed config so the change can be verified immediately.
            WriteObject(config);
        }
    }
}
