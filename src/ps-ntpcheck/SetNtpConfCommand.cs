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

            // Return the refreshed config so the change can be verified immediately.
            WriteObject(NtpConfigReader.GetConfig());
        }
    }
}
