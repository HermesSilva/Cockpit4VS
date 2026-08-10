using System.Collections.Generic;
using System.Linq;
using Tootega.Cockpit.Cli;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// The command line is the entire contract with the engine, and a wrong flag fails in a
    /// way that looks like a model problem rather than an argument problem. These tests pin
    /// the flags the conversation depends on.
    /// </summary>
    public class CliArgumentsTests
    {
        private static CliOptions Claude() => new CliOptions
        {
            ExecutablePath = "claude",
            Cwd = @"C:\work",
            Engine = EngineIds.Claude,
        };

        /// <summary>Index of the value following a flag, or -1 when the flag is absent.</summary>
        private static string ValueAfter(IReadOnlyList<string> args, string flag)
        {
            var index = args.ToList().IndexOf(flag);
            return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
        }

        [Fact]
        public void AlwaysRequestsBidirectionalStreamJson()
        {
            // These six are what make the whole UI possible: without stream-json in both
            // directions there is no event stream and no way to answer a permission prompt.
            var args = CliArguments.ForClaude(Claude(), null, null);

            Assert.Contains("-p", args);
            Assert.Equal("stream-json", ValueAfter(args, "--output-format"));
            Assert.Equal("stream-json", ValueAfter(args, "--input-format"));
            Assert.Contains("--include-partial-messages", args);
            Assert.Equal("stdio", ValueAfter(args, "--permission-prompt-tool"));
            Assert.Contains("--verbose", args);
        }

        [Fact]
        public void OmitsModelAndEffortWhenNotSet()
        {
            // Passing no --model is how "the CLI decides" is expressed; sending an empty
            // value would override it with nonsense.
            var args = CliArguments.ForClaude(Claude(), null, null);

            Assert.DoesNotContain("--model", args);
            Assert.DoesNotContain("--effort", args);
        }

        [Fact]
        public void PassesModelEffortAndResume()
        {
            var options = Claude();
            options.Model = "claude-opus-5";
            options.Effort = "high";
            options.ResumeSessionId = "sess-1";

            var args = CliArguments.ForClaude(options, null, null);

            Assert.Equal("claude-opus-5", ValueAfter(args, "--model"));
            Assert.Equal("high", ValueAfter(args, "--effort"));
            Assert.Equal("sess-1", ValueAfter(args, "--resume"));
        }

        [Fact]
        public void OmitsDefaultPermissionMode()
        {
            // 'default' is our label for "say nothing", not a value the CLI knows.
            var options = Claude();
            options.PermissionMode = "default";

            Assert.DoesNotContain("--permission-mode", CliArguments.ForClaude(options, null, null));
        }

        [Fact]
        public void PassesNonDefaultPermissionMode()
        {
            var options = Claude();
            options.PermissionMode = "acceptEdits";

            Assert.Equal("acceptEdits", ValueAfter(CliArguments.ForClaude(options, null, null), "--permission-mode"));
        }

        [Fact]
        public void JoinsDisallowedToolsWithCommas()
        {
            var options = Claude();
            options.DisallowedTools = new List<string> { "Task", "Workflow" };

            Assert.Equal("Task,Workflow", ValueAfter(CliArguments.ForClaude(options, null, null), "--disallowedTools"));
        }

        [Fact]
        public void OmitsDisallowedToolsWhenEmpty()
        {
            var options = Claude();
            options.DisallowedTools = new List<string>();

            Assert.DoesNotContain("--disallowedTools", CliArguments.ForClaude(options, null, null));
        }

        [Fact]
        public void ForwardsSubagentTextOnlyWhenAsked()
        {
            Assert.DoesNotContain("--forward-subagent-text", CliArguments.ForClaude(Claude(), null, null));

            var options = Claude();
            options.ForwardSubagentText = true;
            Assert.Contains("--forward-subagent-text", CliArguments.ForClaude(options, null, null));
        }

        // --- Appended system prompt ---

        [Fact]
        public void MergesAskLanguageAndUserPromptIntoOnePayload()
        {
            // Repeating --append-system-prompt does not accumulate: the last one wins. Two
            // flags would mean one silently erasing the other.
            var options = Claude();
            options.AskLanguage = "pt";
            options.ExtraSystemPrompt = "always use tabs";

            var appended = CliArguments.AppendedSystemPrompt(options);

            Assert.Contains("Brazilian Portuguese", appended);
            Assert.Contains("always use tabs", appended);
        }

        [Fact]
        public void AppendedPromptIsNullWhenNeitherIsSet()
        {
            Assert.Null(CliArguments.AppendedSystemPrompt(Claude()));
        }

        [Fact]
        public void AskLanguageAloneGoesInline()
        {
            var options = Claude();
            options.AskLanguage = "en";

            var args = CliArguments.ForClaude(options, null, null);

            Assert.Contains("international English", ValueAfter(args, "--append-system-prompt"));
            Assert.DoesNotContain("--append-system-prompt-file", args);
        }

        [Fact]
        public void UserPromptGoesThroughFile()
        {
            // Measured on Windows: a multi-line inline argument containing |, $ or a
            // backtick is mangled by cmd.exe and reaches the model empty.
            var options = Claude();
            options.ExtraSystemPrompt = "line one\nline two with $env:TEMP and `backtick`";

            var args = CliArguments.ForClaude(options, @"C:\temp\prompt.txt", null);

            Assert.Equal(@"C:\temp\prompt.txt", ValueAfter(args, "--append-system-prompt-file"));
            Assert.DoesNotContain("--append-system-prompt", args);
        }

        [Fact]
        public void UnknownAskLanguageFallsBackToItsCode()
        {
            // Better to name the code than to silently drop the instruction.
            Assert.Contains("ja", CliArguments.AskLanguagePrompt("ja"));
        }

        [Fact]
        public void PassesSettingsFileWhenPresent()
        {
            var args = CliArguments.ForClaude(Claude(), null, @"C:\temp\settings.json");

            Assert.Equal(@"C:\temp\settings.json", ValueAfter(args, "--settings"));
        }

        // --- Tootega engine ---

        [Fact]
        public void TootegaKeepsTheSameProcessContract()
        {
            var options = new CliOptions { ExecutablePath = "agent.exe", Cwd = @"C:\work", Engine = EngineIds.Tootega };

            var args = CliArguments.ForTootega(options);

            Assert.Equal("stream-json", ValueAfter(args, "--output-format"));
            Assert.Equal("stream-json", ValueAfter(args, "--input-format"));
            Assert.Equal(@"C:\work", ValueAfter(args, "--cwd"));
        }

        [Fact]
        public void TootegaMapsPlanModeToReadOnlyTools()
        {
            // The agent has no plan mode; the nearest thing is denying it tools.
            var options = new CliOptions { Engine = EngineIds.Tootega, Cwd = "/w", PermissionMode = "plan" };

            Assert.Contains("--no-tools", CliArguments.ForTootega(options));
        }

        [Fact]
        public void TootegaMapsBypassToYes()
        {
            var options = new CliOptions { Engine = EngineIds.Tootega, Cwd = "/w", PermissionMode = "bypassPermissions" };

            Assert.Contains("--yes", CliArguments.ForTootega(options));
        }

        [Fact]
        public void TootegaOmitsClaudeOnlyFlags()
        {
            // Sending flags the agent ignores would only make a bug report harder to read.
            var options = new CliOptions
            {
                Engine = EngineIds.Tootega,
                Cwd = "/w",
                Model = "local",
                Effort = "high",
            };

            var args = CliArguments.ForTootega(options);

            Assert.DoesNotContain("--model", args);
            Assert.DoesNotContain("--effort", args);
            Assert.DoesNotContain("--settings", args);
        }

        [Fact]
        public void ForDispatchesOnEngine()
        {
            var tootega = new CliOptions { Engine = EngineIds.Tootega, Cwd = "/w" };
            Assert.Contains("--cwd", CliArguments.For(tootega));

            Assert.DoesNotContain("--cwd", CliArguments.For(Claude()));
        }

        // --- Quoting ---
        // .NET Framework has no ArgumentList, so the quoting Windows would do for us is
        // done by hand. Getting it wrong truncates a path at its first space.

        [Theory]
        [InlineData("simple", "simple")]
        [InlineData("", "\"\"")]
        [InlineData("has space", "\"has space\"")]
        [InlineData("with\ttab", "\"with\ttab\"")]
        [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
        // No space means no quotes, and outside quotes a trailing backslash is harmless.
        [InlineData(@"C:\path\", @"C:\path\")]
        // Inside quotes it is not: an odd number of backslashes would escape the closing
        // quote and swallow the next argument.
        [InlineData(@"C:\Program Files\x\", @"""C:\Program Files\x\\""")]
        public void QuotesArgumentsForCommandLineToArgv(string input, string expected)
        {
            Assert.Equal(expected, CliArguments.Quote(input));
        }

        [Fact]
        public void CommandLineJoinsQuotedArguments()
        {
            var line = CliArguments.ToCommandLine(new[] { @"C:\Program Files\claude.cmd", "--model", "claude-opus-5" });

            Assert.Equal("\"C:\\Program Files\\claude.cmd\" --model claude-opus-5", line);
        }
    }
}
