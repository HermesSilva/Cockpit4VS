using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Tootega.Cockpit.Cli
{
    /// <summary>
    /// Builds the engine command line. Kept apart from process management so the argument
    /// list — the part that is easy to get subtly wrong and impossible to notice — can be
    /// asserted in tests without spawning anything.
    /// </summary>
    internal static class CliArguments
    {
        /// <summary>Short BCP47 code to language name, for the prompt instruction.</summary>
        private static readonly Dictionary<string, string> LanguageNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pt"] = "Brazilian Portuguese (pt-BR)",
                ["en"] = "international English",
                ["es"] = "Spanish",
                ["fr"] = "French",
                ["de"] = "German",
                ["it"] = "Italian",
            };

        public static string AskLanguagePrompt(string code)
        {
            var name = LanguageNames.TryGetValue(code ?? string.Empty, out var known) ? known : code;
            return "When you use the AskUserQuestion tool, write every question, header text, and " +
                   "option label/description in " + name + ". This language rule applies ONLY to " +
                   "AskUserQuestion content, not to your other replies.";
        }

        /// <summary>
        /// The text to append to the system prompt: the AskUserQuestion language rule plus
        /// the user's own text, merged.
        ///
        /// They are merged rather than passed as two flags because repeating
        /// --append-system-prompt does not accumulate — the last one wins — so two flags
        /// would mean one silently erasing the other.
        /// </summary>
        public static string AppendedSystemPrompt(CliOptions options)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(options.AskLanguage))
                parts.Add(AskLanguagePrompt(options.AskLanguage));
            if (!string.IsNullOrWhiteSpace(options.ExtraSystemPrompt))
                parts.Add(options.ExtraSystemPrompt);

            return parts.Count == 0 ? null : string.Join("\n\n", parts);
        }

        /// <summary>
        /// Arguments for the Claude Code CLI.
        /// </summary>
        /// <param name="promptFile">
        /// Path of the file holding <see cref="AppendedSystemPrompt"/>, or null to pass it
        /// inline. A file is used whenever the user's own text is involved: measured on
        /// Windows, a multi-line inline argument containing |, $ or a backtick is mangled by
        /// cmd.exe and reaches the model empty.
        /// </param>
        /// <param name="settingsFile">Path of the temporary --settings file, or null.</param>
        public static List<string> ForClaude(CliOptions options, string promptFile, string settingsFile)
        {
            var args = new List<string>
            {
                "-p",
                "--output-format", "stream-json",
                "--input-format", "stream-json",
                "--include-partial-messages",
                // Routes permission decisions through the control protocol. With this
                // sentinel the CLI emits a can_use_tool control_request instead of denying
                // silently in headless mode. AskUserQuestion also arrives this way.
                "--permission-prompt-tool", "stdio",
                "--verbose",
            };

            if (!string.IsNullOrWhiteSpace(options.Model)) { args.Add("--model"); args.Add(options.Model); }
            if (!string.IsNullOrWhiteSpace(options.Effort)) { args.Add("--effort"); args.Add(options.Effort); }

            if (!string.IsNullOrWhiteSpace(options.PermissionMode) && options.PermissionMode != "default")
            {
                args.Add("--permission-mode");
                args.Add(options.PermissionMode);
            }

            if (options.DisallowedTools != null && options.DisallowedTools.Count > 0)
            {
                args.Add("--disallowedTools");
                args.Add(string.Join(",", options.DisallowedTools));
            }

            if (options.ForwardSubagentText) args.Add("--forward-subagent-text");

            if (!string.IsNullOrWhiteSpace(options.ResumeSessionId))
            {
                args.Add("--resume");
                args.Add(options.ResumeSessionId);
            }

            var appended = AppendedSystemPrompt(options);
            if (!string.IsNullOrEmpty(appended))
            {
                if (!string.IsNullOrEmpty(promptFile))
                {
                    args.Add("--append-system-prompt-file");
                    args.Add(promptFile);
                }
                else
                {
                    args.Add("--append-system-prompt");
                    args.Add(appended);
                }
            }

            // --settings MERGES with the user's settings rather than replacing them, and
            // accepts a path or inline JSON — but inline JSON does not survive the Windows
            // shell, so a temp file is always used.
            if (!string.IsNullOrEmpty(settingsFile))
            {
                args.Add("--settings");
                args.Add(settingsFile);
            }

            return args;
        }

        /// <summary>
        /// Arguments for the Tootega agent.
        ///
        /// The shared part is the process contract itself; everything else the Claude CLI
        /// takes is either meaningless here (no account, no model catalogue, no MCP) or
        /// already covered by the agent's defaults. The agent tolerates unknown flags, but
        /// sending flags it ignores would only make a bug report harder to read.
        /// </summary>
        public static List<string> ForTootega(CliOptions options)
        {
            var args = new List<string>
            {
                "-p",
                "--output-format", "stream-json",
                "--input-format", "stream-json",
                "--include-partial-messages",
                "--permission-prompt-tool", "stdio",
                "--verbose",
                "--cwd", options.Cwd,
            };

            if (!string.IsNullOrWhiteSpace(options.Server)) { args.Add("--server"); args.Add(options.Server); }

            // The agent exits on its own when idle — one immortal process per tab becomes a
            // dozen of them. --resume is what makes that safe: the conversation is on disk,
            // and the next message brings it back.
            if (!string.IsNullOrWhiteSpace(options.ResumeSessionId))
            {
                args.Add("--resume");
                args.Add(options.ResumeSessionId);
            }

            // The agent has no plan mode; the closest thing to "do not touch anything" is
            // restricting it to read-only tools.
            if (options.PermissionMode == "plan") args.Add("--no-tools");
            // bypassPermissions is the CLI's name for "stop asking".
            if (options.PermissionMode == "bypassPermissions") args.Add("--yes");

            return args;
        }

        public static List<string> For(CliOptions options, string promptFile = null, string settingsFile = null)
        {
            return options.Engine == EngineIds.Tootega
                ? ForTootega(options)
                : ForClaude(options, promptFile, settingsFile);
        }

        /// <summary>
        /// Joins arguments into a single command line.
        ///
        /// .NET Framework has no ProcessStartInfo.ArgumentList, so the quoting that Windows
        /// would otherwise do for us has to be done here, following the CommandLineToArgvW
        /// rules. Getting this wrong shows up as a path silently truncated at the first
        /// space, which is why it is one function with tests rather than inline formatting.
        /// </summary>
        public static string ToCommandLine(IEnumerable<string> args)
        {
            return string.Join(" ", args.Select(Quote));
        }

        public static string Quote(string argument)
        {
            if (argument == null) return "\"\"";
            // Unquoted is fine only when there is nothing for the parser to misread.
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\v' }) < 0)
                return argument;

            var sb = new StringBuilder(argument.Length + 8);
            sb.Append('"');
            for (var i = 0; i < argument.Length; i++)
            {
                var backslashes = 0;
                while (i < argument.Length && argument[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i == argument.Length)
                {
                    // Trailing backslashes must be doubled, or they would escape the
                    // closing quote.
                    sb.Append('\\', backslashes * 2);
                    break;
                }

                if (argument[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(argument[i]);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
