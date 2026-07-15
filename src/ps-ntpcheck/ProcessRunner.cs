using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace PSAdminTools.NtpCheck
{
    /// <summary>
    /// Runs an external command and captures its output. Used to shell out to the
    /// platform-native NTP status tools (w32tm, chronyc, timedatectl, ntpq), since
    /// none of these have a portable .NET API equivalent.
    /// </summary>
    internal static class ProcessRunner
    {
        public static (bool Success, string StdOut, string StdErr) Run(string fileName, string arguments, int timeoutMs = 3000)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        return (false, string.Empty, "Failed to start process.");
                    }

                    // Read both streams concurrently to avoid a deadlock if either fills its buffer.
                    Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> stdErrTask = process.StandardError.ReadToEndAsync();

                    bool exited = process.WaitForExit(timeoutMs);
                    if (!exited)
                    {
                        try { process.Kill(); } catch { /* best effort */ }
                        return (false, string.Empty, $"'{fileName}' timed out after {timeoutMs}ms.");
                    }

                    Task.WaitAll(stdOutTask, stdErrTask);
                    return (process.ExitCode == 0, stdOutTask.Result, stdErrTask.Result);
                }
            }
            catch (Exception ex)
            {
                // Covers "file not found" (command not installed) and similar failures.
                return (false, string.Empty, ex.Message);
            }
        }
    }
}
