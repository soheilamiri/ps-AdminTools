using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Threading;

namespace PSAdminTools.NtpCheck
{
    /// <summary>
    /// Compares the time reported by a "source" (local clock, by default, or an NTP server)
    /// against the time reported by one or more "remote" NTP servers, and warns if the offset
    /// between source and any remote exceeds -MaxOffset seconds.
    /// </summary>
    [Cmdlet(VerbsDiagnostic.Test, "Time")]
    [OutputType(typeof(bool))]
    public class TestTimeCommand : PSCmdlet
    {
        private const int MaxRemoteServers = 5;
        private const int RetryDelayMs = 2000;

        /// <summary>
        /// Source to compare. If omitted, the local machine's own clock is used.
        /// If set to a hostname/IP, that address is queried as an NTP server instead.
        /// </summary>
        [Parameter(Position = 0)]
        public string? Source { get; set; }

        /// <summary>
        /// One or more remote NTP servers to query and compare against Source.
        /// Comma-separated, up to 5 servers. Mandatory.
        /// </summary>
        [Parameter(Mandatory = true, Position = 1)]
        [ValidateCount(1, MaxRemoteServers)]
        public string[] Remote { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Maximum allowed offset, in seconds, between Source and each Remote before a warning is raised.
        /// </summary>
        [Parameter]
        [ValidateRange(1, int.MaxValue)]
        public int MaxOffset { get; set; } = 60;

        /// <summary>
        /// Number of times to repeat the full comparison, with a fixed 2-second pause between
        /// attempts. Each attempt reports its own result.
        /// </summary>
        [Parameter]
        [ValidateRange(1, 10)]
        public int Retry { get; set; } = 1;

        /// <summary>
        /// When present, suppresses the formatted report and returns only $true/$false
        /// per remote server per attempt (true = within MaxOffset), for use in scripts/pipelines.
        /// </summary>
        [Parameter]
        public SwitchParameter Output { get; set; }

        private sealed class RemoteResult
        {
            public string Label = string.Empty;
            public bool Success;
            public DateTime Time;
            public double OffsetSeconds;
            public bool WithinTolerance;
            public string? ErrorMessage;
            public string TimelineReason = "Error";
            public string ShortReason = "Error";
        }

        private static (string Timeline, string Result) ClassifyError(Exception ex)
        {
            string message = ex.Message;
            if (message.IndexOf("Unable to resolve", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ("DNS failed", "DNS resolution failed");
            }
            if (message.IndexOf("incomplete", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ("Invalid response", "Invalid response from NTP server");
            }
            // Covers connection-refused, timed-out, unreachable, etc.
            return ("No response", "No response from NTP server (timeout)");
        }

        protected override void ProcessRecord()
        {
            bool isSourceLocal = string.IsNullOrWhiteSpace(Source);
            string sourceLabel = isSourceLocal ? Environment.MachineName : Source!;

            for (int attempt = 1; attempt <= Retry; attempt++)
            {
                if (Retry > 1 && !Output.IsPresent)
                {
                    Host.UI.WriteLine();
                    Host.UI.WriteLine($"--- Attempt {attempt} of {Retry} ---");
                }

                RunSingleComparison(isSourceLocal, sourceLabel);

                if (attempt < Retry)
                {
                    Thread.Sleep(RetryDelayMs);
                }
            }
        }

        private void RunSingleComparison(bool isSourceLocal, string sourceLabel)
        {
            // Resolve source time once per attempt - shared across all remotes.
            DateTime sourceTime;
            try
            {
                sourceTime = isSourceLocal ? DateTime.Now : NtpClient.GetNetworkTime(Source!);
            }
            catch (NtpQueryException ex)
            {
                if (Output.IsPresent)
                {
                    foreach (var _ in Remote) { WriteObject(false); }
                }
                else
                {
                    WriteError(new ErrorRecord(ex, "SourceNtpQueryFailed", ErrorCategory.ResourceUnavailable, Source));
                }
                return;
            }

            // Query every remote against that same source time.
            var results = new List<RemoteResult>();
            foreach (string remote in Remote)
            {
                var result = new RemoteResult { Label = remote };
                try
                {
                    DateTime remoteTime = NtpClient.GetNetworkTime(remote);
                    double offsetSeconds = Math.Abs((sourceTime - remoteTime).TotalSeconds);

                    result.Success = true;
                    result.Time = remoteTime;
                    result.OffsetSeconds = offsetSeconds;
                    result.WithinTolerance = offsetSeconds <= MaxOffset;
                }
                catch (NtpQueryException ex)
                {
                    result.Success = false;
                    result.ErrorMessage = ex.Message;
                    (result.TimelineReason, result.ShortReason) = ClassifyError(ex);
                }

                results.Add(result);
            }

            if (Output.IsPresent)
            {
                foreach (var result in results)
                {
                    WriteObject(result.Success && result.WithinTolerance);
                }
                return;
            }

            PrintReport(sourceLabel, sourceTime, results);
        }

        private void PrintReport(string sourceLabel, DateTime sourceTime, List<RemoteResult> results)
        {
            // Column width covers source label and every remote label so the
            // timestamps line up exactly underneath one another.
            int labelWidth = Math.Max(
                sourceLabel.Length,
                results.Count > 0 ? results.Max(r => r.Label.Length) : 0);

            Host.UI.WriteLine($"{sourceLabel.PadRight(labelWidth)} : {sourceTime:yyyy-MM-dd HH:mm:ss.fff}");

            foreach (var result in results)
            {
                string valueText = result.Success
                    ? $"{result.Time:yyyy-MM-dd HH:mm:ss.fff}"
                    : $"ERROR - {result.TimelineReason}";

                Host.UI.WriteLine($"{result.Label.PadRight(labelWidth)} : {valueText}");
            }

            Host.UI.WriteLine();

            foreach (var result in results)
            {
                if (!result.Success)
                {
                    Host.UI.WriteErrorLine($"Result for {result.Label}: ERROR - {result.ShortReason}.");
                    WriteVerbose($"{result.Label}: {result.ErrorMessage}");
                }
                else if (result.WithinTolerance)
                {
                    Host.UI.WriteLine($"Result for {result.Label}: OK - Source is within MaxOffset with value of {result.OffsetSeconds:F0} in second.");
                }
                else
                {
                    Host.UI.WriteWarningLine($"Result for {result.Label}: WARNING - Source has exceeded  MaxOffset with value of {result.OffsetSeconds:F0} in second.");
                }
            }
        }
    }
}