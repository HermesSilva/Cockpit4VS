using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    /// <summary>The machine facts the template can describe, detected once per host process.</summary>
    internal sealed class ShellEnvironment
    {
        /// <summary>Default shell name, with its version when known.</summary>
        public string DefaultShell { get; set; }
        public string PsVersion { get; set; }
        public bool GitBash { get; set; }
        /// <summary>Default WSL distribution, when one exists.</summary>
        public string Wsl { get; set; }
        public string WinPathStyle { get; set; }
    }

    /// <summary>
    /// Expands the user's extra system-prompt text before it reaches the CLI. Port of
    /// src/cli/SystemPromptTemplate.ts.
    ///
    /// The text is a TEMPLATE with ${name} placeholders, and the expansion describes the REAL
    /// machine — nothing is asserted without checking. Three rules, in order:
    ///
    ///   1. a resolved placeholder is substituted;
    ///   2. a placeholder whose dependency does NOT exist here removes its WHOLE LINE. A table
    ///      row describing a shell the machine does not have is worse than no row at all,
    ///      because it actively misleads the agent into writing commands that cannot run;
    ///   3. an unknown placeholder is left verbatim. Inventing a value would be worse than
    ///      showing the user their own typo.
    /// </summary>
    internal static class SystemPromptTemplate
    {
        private static readonly Regex Placeholder = new Regex(@"\$\{([A-Za-z][\w]*)\}", RegexOptions.Compiled);
        private static readonly Regex DriveLetterPath = new Regex(@"^([A-Za-z]):[\\/](.*)$", RegexOptions.Compiled);
        private static readonly Regex BlankRun = new Regex(@"\n{3,}", RegexOptions.Compiled);

        private static readonly object Gate = new object();
        private static ShellEnvironment _cached;

        /// <summary>Test seam: forces the environment to be detected again.</summary>
        internal static void ResetEnvironmentCache()
        {
            lock (Gate) _cached = null;
        }

        public static ShellEnvironment DetectEnvironment()
        {
            lock (Gate)
            {
                if (_cached != null) return _cached;
            }

            var detected = Detect();

            lock (Gate)
            {
                _cached = detected;
                return _cached;
            }
        }

        private static ShellEnvironment Detect()
        {
            if (!ProcessLauncher.IsWindows)
            {
                var shell = Environment.GetEnvironmentVariable("SHELL");
                return new ShellEnvironment
                {
                    DefaultShell = string.IsNullOrEmpty(shell) ? "bash" : Path.GetFileName(shell),
                    GitBash = true,
                    WinPathStyle = "POSIX (/home/...)",
                };
            }

            // pwsh 7+ when installed, otherwise the Windows PowerShell that ships with the OS.
            var pwsh = Probe("pwsh", "-NoProfile", "-Command", "$PSVersionTable.PSVersion.ToString()");
            var ps51 = pwsh != null
                ? null
                : Probe("powershell", "-NoProfile", "-Command", "$PSVersionTable.PSVersion.ToString()");

            // `wsl -l -q` lists something only when a distribution is installed; WSL with no
            // distribution is of no use to the agent.
            var wslList = Probe("wsl", "-l", "-q");
            var wsl = wslList?
                .Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0);

            var environment = new ShellEnvironment
            {
                DefaultShell = pwsh != null ? "PowerShell " + pwsh
                    : ps51 != null ? "Windows PowerShell " + ps51
                    : "cmd.exe",
                PsVersion = pwsh ?? ps51,
                GitBash = FindGitBash(),
                Wsl = wsl,
                WinPathStyle = @"Windows (C:\...)",
            };

            Log.Debug("prompt env: shell=" + environment.DefaultShell +
                      " gitBash=" + environment.GitBash +
                      " wsl=" + (environment.Wsl ?? "none"));
            return environment;
        }

        /// <summary>
        /// Runs a short command and returns its stdout, or null when it fails or is absent.
        ///
        /// Synchronous, and called only from background work: detection runs several
        /// subprocesses and its result is cached for the life of the host process. stderr is
        /// drained on its own callback rather than read after stdout, because a chatty probe
        /// could otherwise fill that pipe and deadlock the read.
        /// </summary>
        private static string Probe(string executable, params string[] args)
        {
            try
            {
                var info = ProcessLauncher.Build(executable, args, null);
                info.RedirectStandardInput = false;

                using (var process = Process.Start(info))
                {
                    if (process == null) return null;

                    process.ErrorDataReceived += (s, e) => { };
                    process.BeginErrorReadLine();

                    var output = process.StandardOutput.ReadToEnd();

                    if (!process.WaitForExit(4000))
                    {
                        ProcessLauncher.KillTree(process);
                        return null;
                    }

                    // WSL writes UTF-16, which leaves embedded nulls once decoded as UTF-8.
                    var text = (output ?? string.Empty).Replace("\0", string.Empty).Trim();
                    return text.Length > 0 ? text : null;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Whether Git Bash really exists here, rather than probably exists.</summary>
        private static bool FindGitBash()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files",
                             "Git", "bin", "bash.exe"),
                Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)",
                             "Git", "bin", "bash.exe"),
                Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty,
                             "Programs", "Git", "bin", "bash.exe"),
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return true;
                }
                catch
                {
                    // An unreadable path counts as absent.
                }
            }

            return Probe("bash", "--version") != null;
        }

        /// <summary>The workspace path as Git Bash sees it: D:\a\b becomes /d/a/b.</summary>
        internal static string ToGitBashPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var match = DriveLetterPath.Match(path);
            if (!match.Success) return path.Replace('\\', '/');
            return "/" + match.Groups[1].Value.ToLowerInvariant() + "/" + match.Groups[2].Value.Replace('\\', '/');
        }

        /// <summary>The workspace path inside WSL: D:\a\b becomes /mnt/d/a/b.</summary>
        internal static string ToWslPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var match = DriveLetterPath.Match(path);
            if (!match.Success) return path.Replace('\\', '/');
            return "/mnt/" + match.Groups[1].Value.ToLowerInvariant() + "/" + match.Groups[2].Value.Replace('\\', '/');
        }

        /// <summary>
        /// The supported placeholders, resolved against the real machine. A null value means
        /// "this machine does not have it", which removes the line that mentions it.
        /// </summary>
        public static Dictionary<string, string> BuildVars(string cwd, ShellEnvironment environment = null)
        {
            var env = environment ?? DetectEnvironment();

            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["defaultShell"] = env.DefaultShell,
                ["psVersion"] = env.PsVersion,
                ["winPathStyle"] = env.WinPathStyle,
                ["projectPathWin"] = cwd,
                ["projectPathGitBash"] = env.GitBash ? ToGitBashPath(cwd) : null,
                ["projectPathWsl"] = !string.IsNullOrEmpty(env.Wsl) ? ToWslPath(cwd) : null,
                // A whole table row: with no WSL, the placeholder takes the row with it.
                ["wslRow"] = !string.IsNullOrEmpty(env.Wsl)
                    ? "| WSL (" + env.Wsl + ") | Linux real | " + ToWslPath(cwd) + " | /tmp (inside WSL) | ok |"
                    : null,
                ["os"] = Environment.OSVersion.VersionString,
                ["tempDir"] = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            };
        }

        /// <summary>Expands the template against the given variables.</summary>
        public static string Expand(string text, IDictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var kept = new List<string>();

            foreach (var line in text.Split('\n'))
            {
                var drop = false;

                var expanded = Placeholder.Replace(line, match =>
                {
                    var name = match.Groups[1].Value;

                    // Unknown: left verbatim rather than blanked or invented.
                    if (!vars.TryGetValue(name, out var value)) return match.Value;

                    if (value == null)
                    {
                        drop = true;
                        return string.Empty;
                    }

                    return value;
                });

                if (!drop) kept.Add(expanded);
            }

            // A removed line in the middle of a block must not leave a hole behind.
            return BlankRun.Replace(string.Join("\n", kept), "\n\n").Trim();
        }

        /// <summary>
        /// The final text for --append-system-prompt. Null when there is nothing to inject:
        /// switched off, empty, or every line dropped during validation.
        /// </summary>
        public static string Build(string text, string cwd)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var expanded = Expand(text, BuildVars(cwd));
            return string.IsNullOrEmpty(expanded) ? null : expanded;
        }
    }
}
