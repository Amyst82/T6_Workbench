using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace T6_Workbench
{
    /// <summary>
    /// Captured output and exit status returned by an OAT process.
    /// </summary>
    public sealed class ToolResult
    {
        public int ExitCode { get; set; }

        public string Output { get; set; }

        public ToolResult(int ExitCode, string Output)
        {
            this.ExitCode = ExitCode;
            this.Output = Output;
        }

        public string[] Errors => Output.Split('\n').Select(line => line.Trim()) .Where(line => Regex.IsMatch(line, @"^(ERROR:|FATAL:)", RegexOptions.IgnoreCase)).ToArray();

        /// <summary>
        /// Throw an actionable exception if OAT reported a failure.
        /// </summary>
        public void RequireSuccess()
        {
            if (ExitCode != 0 || Errors.Length != 0)
            {
                throw new InvalidOperationException(EnglishText.Format("OAT exited with code {0}:\n", ExitCode) + (Errors.Length > 0 ? string.Join("\n", Errors) : Output));
            }
        }
    }
}
