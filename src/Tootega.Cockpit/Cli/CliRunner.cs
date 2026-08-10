using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    internal struct CliResult
    {
        public int Code;
        public string Output;
        public string Error;

        public bool Ok => Code == 0;

        /// <summary>
        /// The last few lines of whatever the command said, trimmed for display. Errors
        /// usually end with the useful part, and the whole stream would be unreadable in a
        /// notification.
        /// </summary>
        public string Message(int maxLines = 3, int maxChars = 240)
        {
            var text = (string.IsNullOrWhiteSpace(Error) ? Output : Error) ?? string.Empty;
            var lines = text.Trim().Split('\n');
            var tail = lines.Length <= maxLines ? lines : SubArray(lines, lines.Length - maxLines);
            var joined = string.Join(" ", tail).Trim();
            if (joined.Length > maxChars) joined = joined.Substring(0, maxChars);
            return joined.Length > 0 ? joined : "exit " + Code;
        }

        private static string[] SubArray(string[] source, int start)
        {
            var result = new string[source.Length - start];
            Array.Copy(source, start, result, 0, result.Length);
            return result;
        }
    }

    /// <summary>
    /// Runs a one-shot `claude` subcommand and collects its output.
    ///
    /// Distinct from CliProcessManager, which owns a long-lived streaming session. These are
    /// short commands (plugin management, mcp list) whose whole result is their stdout, and
    /// they must never throw: the panel that called shows what it got.
    /// </summary>
    internal static class CliRunner
    {
        public static async Task<CliResult> RunAsync(string executable, string[] args, int timeoutMs = 90_000)
        {
            try
            {
                var info = ProcessLauncher.Build(executable, args, null);
                info.RedirectStandardInput = false;

                using (var process = Process.Start(info))
                {
                    if (process == null) return new CliResult { Code = -1, Error = "the process did not start" };

                    // Both pipes are read before waiting, or a command that writes more than
                    // the buffer holds would block forever.
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();

                    var reading = Task.WhenAll(stdout, stderr);
                    var finished = await Task.WhenAny(reading, Task.Delay(timeoutMs)).ConfigureAwait(false);

                    if (finished != reading)
                    {
                        ProcessLauncher.KillTree(process);
                        // Whatever arrived before the timeout is not worth racing for; the
                        // caller only needs to know the command did not finish.
                        return new CliResult { Code = -1, Output = string.Empty, Error = "timeout" };
                    }

                    process.WaitForExit(2000);

                    return new CliResult
                    {
                        Code = process.ExitCode,
                        Output = await stdout.ConfigureAwait(false),
                        Error = await stderr.ConfigureAwait(false),
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Debug("cli run failed (" + string.Join(" ", args) + "): " + ex.Message);
                return new CliResult { Code = -1, Error = ex.Message };
            }
        }
    }
}
