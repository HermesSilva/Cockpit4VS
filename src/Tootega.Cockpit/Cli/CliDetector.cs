using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    internal sealed class CliDetection
    {
        public string Path { get; set; }
        public bool Ok { get; set; }
        public string Version { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Finds a working engine binary. Port of the static detect/resolve pair in
    /// src/cli/CliProcessManager.ts.
    /// </summary>
    internal static class CliDetector
    {
        private const int VersionTimeoutMs = 8000;

        /// <summary>
        /// Runs `--version` and reports what came back.
        ///
        /// Asynchronous deliberately: this spawns a process and waits up to eight seconds
        /// for it, which on the UI thread would freeze the IDE. A missing CLI is exactly the
        /// case that hits the full timeout, so the slow path is the likely one.
        /// </summary>
        public static async Task<CliDetection> DetectAsync(string executable)
        {
            if (string.IsNullOrWhiteSpace(executable))
                return new CliDetection { Path = executable, Ok = false, Error = "no path configured" };

            try
            {
                var info = ProcessLauncher.Build(executable, new[] { "--version" }, null);
                using (var process = Process.Start(info))
                {
                    if (process == null)
                        return new CliDetection { Path = executable, Ok = false, Error = "could not start the process" };

                    // Start reading before waiting: a process writing more than the pipe
                    // buffer holds would block forever otherwise.
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();

                    var reading = Task.WhenAll(stdout, stderr);
                    var finished = await Task.WhenAny(reading, Task.Delay(VersionTimeoutMs)).ConfigureAwait(false);
                    if (finished != reading)
                    {
                        ProcessLauncher.KillTree(process);
                        return new CliDetection
                        {
                            Path = executable,
                            Ok = false,
                            Error = "timed out after " + VersionTimeoutMs + " ms",
                        };
                    }

                    var output = (await stdout.ConfigureAwait(false) ?? string.Empty).Trim();
                    var errors = (await stderr.ConfigureAwait(false) ?? string.Empty).Trim();

                    // Both pipes are closed, so the process is done; this only reaps it.
                    process.WaitForExit(1000);

                    if (process.ExitCode == 0)
                    {
                        // Some builds print the version on stderr.
                        var version = !string.IsNullOrEmpty(output) ? output : errors;
                        return new CliDetection { Path = executable, Ok = true, Version = version };
                    }

                    return new CliDetection
                    {
                        Path = executable,
                        Ok = false,
                        Error = !string.IsNullOrEmpty(errors) ? errors : "exit " + process.ExitCode,
                    };
                }
            }
            catch (Exception ex)
            {
                return new CliDetection { Path = executable, Ok = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Resolves a working engine: tries the configured path (which covers the PATH) and,
        /// failing that, probes the native installer locations. Returns the first that
        /// answers `--version`, or the last failure so the UI can say what went wrong.
        /// </summary>
        public static async Task<CliDetection> ResolveAsync(string configured, string engine = EngineIds.Claude)
        {
            var candidates = new List<string> { configured };

            // ~/.local/bin is where the Claude native installer puts things, and it is not
            // always on the PATH on Windows. Probing it for the Tootega agent would only
            // report the wrong binary as found.
            if (engine != EngineIds.Tootega) candidates.AddRange(NativeCandidates());

            CliDetection last = new CliDetection { Path = configured, Ok = false };
            foreach (var candidate in candidates)
            {
                // A non-existent absolute path is skipped without spending a spawn.
                if (candidate != configured && !SafeExists(candidate)) continue;

                var detection = await DetectAsync(candidate).ConfigureAwait(false);
                if (detection.Ok)
                {
                    Log.Debug("resolved engine at " + candidate + " (" + detection.Version + ")");
                    return detection;
                }
                last = detection;
            }

            return last;
        }

        private static IEnumerable<string> NativeCandidates()
        {
            string home;
            try
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            catch
            {
                yield break;
            }

            if (string.IsNullOrEmpty(home)) yield break;
            var bin = System.IO.Path.Combine(home, ".local", "bin");

            if (ProcessLauncher.IsWindows)
            {
                yield return System.IO.Path.Combine(bin, "claude.exe");
                yield return System.IO.Path.Combine(bin, "claude.cmd");
                yield return System.IO.Path.Combine(bin, "claude");
            }
            else
            {
                yield return System.IO.Path.Combine(bin, "claude");
            }
        }

        private static bool SafeExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }
    }
}
