using System.Management.Automation;

namespace PSAdminTools.NtpCheck
{
    /// <summary>
    /// Returns the local machine's current time, time zone, and active NTP reference
    /// as a structured object. Auto-detects the underlying NTP mechanism: W32Time on
    /// Windows; chrony, systemd-timesyncd, or ntpd (in that order) on Linux.
    /// </summary>
    [Cmdlet(VerbsCommon.Get, "NtpConf")]
    [OutputType(typeof(NtpConfigInfo))]
    public class GetNtpConfCommand : PSCmdlet
    {
        protected override void ProcessRecord()
        {
            NtpConfigInfo info = NtpConfigReader.GetConfig();
            WriteObject(info);
        }
    }
}
