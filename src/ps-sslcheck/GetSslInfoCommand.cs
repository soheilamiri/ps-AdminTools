using System.Management.Automation;

namespace PSAdminTools.SslCheck
{
    /// <summary>
    /// Connects to a remote host over TLS and returns the certificate it presents,
    /// including how many days remain until it expires.
    /// </summary>
    [Cmdlet(VerbsCommon.Get, "SslInfo")]
    [OutputType(typeof(SslCertInfo))]
    public class GetSslInfoCommand : PSCmdlet
    {
        /// <summary>Remote host to check. Scheme/path/query are ignored if present; an embedded port (host:port) is honored unless -Port is explicitly given.</summary>
        [Parameter(Mandatory = true, Position = 0)]
        public string Url { get; set; } = string.Empty;

        /// <summary>TCP port to connect on. Defaults to 443, or an embedded port from -Url if present.</summary>
        [Parameter]
        [ValidateRange(1, 65535)]
        public int Port { get; set; } = 443;

        protected override void ProcessRecord()
        {
            (string host, int? urlPort) = SslInfoReader.ParseHost(Url);

            int effectivePort = MyInvocation.BoundParameters.ContainsKey(nameof(Port))
                ? Port
                : (urlPort ?? 443);

            SslCertInfo info;
            try
            {
                info = SslInfoReader.GetCertInfo(host, effectivePort);
            }
            catch (SslInfoException ex)
            {
                WriteError(new ErrorRecord(ex, "SslInfoQueryFailed", ErrorCategory.ResourceUnavailable, host));
                return;
            }

            WriteObject(info);
        }
    }
}

