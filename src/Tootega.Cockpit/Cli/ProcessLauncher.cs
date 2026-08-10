using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Tootega.Cockpit.Cli
{
    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> for launching the engine.
    ///
    /// On Windows `claude` is normally a .cmd shim, which CreateProcess cannot execute
    /// directly — and PATH lookup does not consider PATHEXT either. The Node port solved
    /// this with `shell: true`; the equivalent here is to route through `cmd.exe /s /c`.
    /// That has a consequence the rest of the code has to know about: the process we own is
    /// the cmd.exe, not the engine, so ending it takes `taskkill /T` rather than killing a
    /// single pid.
    ///
    /// Outside Windows the binary is executed directly.
    /// </summary>
    internal static class ProcessLauncher
    {
        public static bool IsWindows =>
            Environment.OSVersion.Platform == PlatformID.Win32NT;

        /// <summary>True when the launched process is a shell wrapping the real engine.</summary>
        public static bool UsesShell => IsWindows;

        public static ProcessStartInfo Build(string executable, IEnumerable<string> args, string workingDirectory)
        {
            var commandLine = CliArguments.ToCommandLine(Prepend(executable, args));

            ProcessStartInfo info;
            if (IsWindows)
            {
                info = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    // /s tells cmd to strip only the outer quotes and take the rest
                    // literally, which is the one form that survives paths with spaces.
                    Arguments = "/s /c \"" + commandLine + "\"",
                };
            }
            else
            {
                info = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = CliArguments.ToCommandLine(args),
                };
            }

            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            // The CLI emits UTF-8; without this the console code page mangles any
            // non-ASCII text in the conversation.
            info.StandardOutputEncoding = new UTF8Encoding(false);
            info.StandardErrorEncoding = new UTF8Encoding(false);

            if (!string.IsNullOrEmpty(workingDirectory)) info.WorkingDirectory = workingDirectory;

            return info;
        }

        private static IEnumerable<string> Prepend(string first, IEnumerable<string> rest)
        {
            yield return first;
            foreach (var item in rest) yield return item;
        }

        /// <summary>
        /// Ends the process and everything it started.
        ///
        /// On Windows the pid we hold is cmd.exe; killing only it would orphan the engine
        /// (and any subagents) still running, so the whole tree goes via taskkill /T.
        /// </summary>
        public static void KillTree(Process process)
        {
            if (process == null) return;
            try
            {
                if (process.HasExited) return;
            }
            catch
            {
                return;
            }

            try
            {
                if (IsWindows)
                {
                    var taskkill = new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/pid " + process.Id + " /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using (var killer = Process.Start(taskkill))
                    {
                        killer?.WaitForExit(5000);
                    }
                }
                else
                {
                    process.Kill();
                }
            }
            catch (Exception ex)
            {
                Util.Log.Debug("KillTree failed: " + ex.Message);
            }
        }
    }
}
