using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace T6_Workbench
{
    /// <summary>
    /// Starts OAT without a shell and captures output and cancellation.
    /// </summary>
    public sealed class ToolRunner
    {
        private readonly Action<string> _log;

        public ToolRunner(Action<string> log = null)
        {
            _log = log;
        }
        // Only the child receives Redacted settings; inherited settings are always removed.
        /// <summary>
        /// Run an operation asynchronously, capture tool output and honor cancellation.
        /// </summary>
        public async Task<ToolResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, string capture, CancellationToken cancellation = default, string redactedAuthTemplate = null)
        {
            var start = new ProcessStartInfo(Helpers.IsPathFullyQualified(executable) ? Path.GetFullPath(executable) : executable)
            {
                WorkingDirectory = Path.GetFullPath(workingDirectory),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            var args = arguments.ToArray();
            // Framework has Arguments rather than ArgumentList. Quote each value individually;
            // never concatenate a shell command or expand paths through cmd.exe/PowerShell.
            start.Arguments = string.Join(" ", args.Select(Helpers.QuoteArgument));
            foreach (var key in new[] { "OAT_REDACTED_MODE", "OAT_REDACTED_SKIP_SIGNATURE", "OAT_REDACTED_CAPTURE_FILE", "OAT_REDACTED_WRITE", "OAT_REDACTED_AUTH_TEMPLATE_FILE" })
            {
                start.EnvironmentVariables.Remove(key);
            }
            if (capture != null)
            {
                CaptureValidator.Validate(capture);
                start.EnvironmentVariables["OAT_REDACTED_MODE"] = "1";
                start.EnvironmentVariables["OAT_REDACTED_SKIP_SIGNATURE"] = "1";
                start.EnvironmentVariables["OAT_REDACTED_CAPTURE_FILE"] = Path.GetFullPath(capture);
            }
            if (redactedAuthTemplate != null)
            {
                if (capture == null || !Workspace.IsRedacted(redactedAuthTemplate))
                    throw new InvalidDataException("Redacted writer needs an original Redacted FF and CaptureValidator.");
                start.EnvironmentVariables["OAT_REDACTED_WRITE"] = "1";
                start.EnvironmentVariables["OAT_REDACTED_AUTH_TEMPLATE_FILE"] = Path.GetFullPath(redactedAuthTemplate);
            }
            _log?.Invoke($"> {Path.GetFileName(executable)} " + string.Join(" ", args.Select(a => '"' + a + '"')));
            using (var process = new Process { StartInfo = start })
            {
                var lines = new StringBuilder();
                var sync = new object();
                if (!process.Start())
                    throw new InvalidOperationException(EnglishText.Text("Could not start OAT."));
                async Task Drain(StreamReader reader)
                {
                    while ((await reader.ReadLineAsync()) is string line)
                    {
                        lock (sync)
                            lines.AppendLine(line);
                        _log?.Invoke(line);
                    }
                }
                var stdout = Drain(process.StandardOutput);
                var stderr = Drain(process.StandardError);
                using (var registration = cancellation.Register(() =>
                {
                    // OAT executables run directly. Cancel only the process started by this operation.
                    try
                    {
                        if (!process.HasExited)
                            process.Kill();
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }))
                {            // Drain both pipes before disposing, including when cancellation kills the child.
                    await Helpers.WaitForExitAsync(process);
                    await Task.WhenAll(stdout, stderr);
                    cancellation.ThrowIfCancellationRequested();
                    return new ToolResult(process.ExitCode, lines.ToString());
                }
            }
        }
    }
}
