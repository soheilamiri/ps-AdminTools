using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Threading;

namespace PSAdminTools.NtpCheck
{
    /// <summary>
    /// Result object for a single remote comparison. This is the only thing Test-Time writes
    /// to the pipeline for a given remote - there is no separate printed report. When the
    /// command's output isn't consumed (no assignment, no member access), PowerShell's default
    /// table formatting displays these objects automatically. When you access a property, e.g.
    /// (Test-Time -Remote host).OffsetSeconds, only that value is returned - nothing else prints,
    /// because nothing is ever written outside the pipeline.
    /// </summary>
    public sealed class TestTimeResult
    {
        public string Remote { get; set; } = string.Empty;

        /// <summary>Drift in whole seconds between Source and Remote. Null if the query to Remote failed.</summary>
        public int? OffsetSeconds { get; set; }

        public bool WithinTolerance { get; set; }

        /// <summary>
        /// Human-readable status with embedded ANSI color (green True / orange False / red ERROR)
        /// for terminals that render ANSI escape sequences, such as PowerShell 7's default console
        /// table formatting. Selecting this property directly (e.g. .Status) returns the raw string
        /// including the escape codes.
        /// </summary>
        public string Status { get; set; } = string.Empty;
    }

    /// <summary>
    /// Compares the time reported by a "source" (local clock, by default, or an NTP server)
    /// against the time reported by one or more "remote" NTP servers, and flags any that drift
    /// beyond -MaxOffset seconds. Returns one TestTimeResult object per remote per attempt -
    /// no separate printed report; PowerShell's own table formatting handles display.
    /// </summary>
    [Cmdlet(VerbsDiagnostic.Test, "Time")]
    [OutputType(typeof(bool))]
    [OutputType(typeof(TestTimeResult))]
    public class TestTimeCommand : PSCmdlet
    {
        private const int MaxRemoteServers = 5;
        private const int RetryDelayMs = 2000;

        private const string AnsiGreen = "\u001b[32m";
        private const string AnsiOrange = "\u001b[38;5;208m";
        private const string AnsiRed = "\u001b[31m";
        private const string AnsiReset = "\u001b[0m";

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
        /// Maximum allowed offset, in seconds, between Source and each Remote before it's
        /// flagged as not within tolerance.
        /// </summary>
        [Parameter]
        [ValidateRange(1, int.MaxValue)]
        public int MaxOffset { get; set; } = 60;

        /// <summary>
        /// Number of times to repeat the full comparison, with a fixed 2-second pause between
        /// attempts. Each attempt returns its own set of result objects.
        /// </summary>
        [Parameter]
        [ValidateRange(1, 10)]
        public int Retry { get; set; } = 1;

        /// <summary>
        /// When present, returns only $true/$false per remote server per attempt
        /// (true = within MaxOffset), for use in scripts/pipelines. Legacy behavior,
        /// kept for backward compatibility.
        /// </summary>
        [Parameter]
        public SwitchParameter Output { get; set; }

        private sealed class RemoteResult
        {
            public string Label = string.Empty;
            public bool Success;
            public double OffsetSeconds;
            public bool WithinTolerance;
            public string? ErrorMessage;
            public string ShortReason = "Error";
        }

        private static string ClassifyError(Exception ex)
        {
            string message = ex.Message;
            if (message.IndexOf("Unable to resolve", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "DNS resolution failed";
            }
            if (message.IndexOf("incomplete", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Invalid response from NTP server";
            }
            // Covers connection-refused, timed-out, unreachable, etc.
            return "No response from NTP server (timeout)";
        }

        protected override void ProcessRecord()
        {
            bool isSourceLocal = string.IsNullOrWhiteSpace(Source);

            for (int attempt = 1; attempt <= Retry; attempt++)
            {
                RunSingleComparison(isSourceLocal);

                if (attempt < Retry)
                {
                    Thread.Sleep(RetryDelayMs);
                }
            }
        }

        private void RunSingleComparison(bool isSourceLocal)
        {
            // Resolve source time once per attempt - shared across all remotes.
            DateTime sourceTime;
            try
            {
                sourceTime = isSourceLocal ? DateTime.Now : NtpClient.GetNetworkTime(Source!);
            }
            catch (NtpQueryException ex)
            {
                WriteError(new ErrorRecord(ex, "SourceNtpQueryFailed", ErrorCategory.ResourceUnavailable, Source));

                foreach (string remote in Remote)
                {
                    if (Output.IsPresent)
                    {
                        WriteObject(false);
                    }
                    else
                    {
                        WriteObject(new TestTimeResult
                        {
                            Remote = remote,
                            OffsetSeconds = null,
                            WithinTolerance = false,
                            Status = $"{AnsiRed}ERROR{AnsiReset}"
                        });
                    }
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
                    result.OffsetSeconds = offsetSeconds;
                    result.WithinTolerance = offsetSeconds <= MaxOffset;
                }
                catch (NtpQueryException ex)
                {
                    result.Success = false;
                    result.ErrorMessage = ex.Message;
                    result.ShortReason = ClassifyError(ex);
                }

                results.Add(result);
            }

            foreach (var result in results)
            {
                if (!result.Success)
                {
                    WriteError(new ErrorRecord(
                        new NtpQueryException(result.ErrorMessage ?? result.ShortReason),
                        "RemoteNtpQueryFailed",
                        ErrorCategory.ResourceUnavailable,
                        result.Label));
                }

                if (Output.IsPresent)
                {
                    WriteObject(result.Success && result.WithinTolerance);
                }
                else
                {
                    WriteObject(ToTestTimeResult(result));
                }
            }
        }

        private static TestTimeResult ToTestTimeResult(RemoteResult result)
        {
            string status = !result.Success
                ? $"{AnsiRed}ERROR{AnsiReset}"
                : result.WithinTolerance
                    ? $"{AnsiGreen}True{AnsiReset}"
                    : $"{AnsiOrange}False{AnsiReset}";

            return new TestTimeResult
            {
                Remote = result.Label,
                OffsetSeconds = result.Success ? (int?)Math.Round(result.OffsetSeconds) : null,
                WithinTolerance = result.Success && result.WithinTolerance,
                Status = status
            };
        }
    }
}