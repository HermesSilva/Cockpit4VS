using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Tootega.Cockpit.Cli;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// Integration checks against the Claude CLI actually installed on this machine.
    ///
    /// Unit tests can prove the argument list is right; only this can prove the process
    /// actually launches. On Windows that path goes through `cmd.exe /s /c` because `claude`
    /// is a .cmd shim, and every mistake there (quoting, redirection, encoding) surfaces as
    /// "the CLI is missing" rather than "we launched it wrong".
    ///
    /// Skipped, not failed, when no CLI is present: a contributor without Claude Code
    /// installed should still get a green suite.
    /// </summary>
    public class CliDetectorLiveTests
    {
        private static async Task<bool> CliAvailableAsync()
        {
            return (await CliDetector.DetectAsync("claude")).Ok;
        }

        [SkippableFact]
        public async Task DetectsTheInstalledCli()
        {
            Skip.IfNot(await CliAvailableAsync(), "Claude Code CLI is not on the PATH.");

            var detection = await CliDetector.DetectAsync("claude");

            Assert.True(detection.Ok, detection.Error);
            // `claude --version` prints something like "2.1.226 (Claude Code)".
            Assert.Matches(new Regex(@"\d+\.\d+\.\d+"), detection.Version);
            Assert.NotNull(CliVersion.ParseSemver(detection.Version));
        }

        [SkippableFact]
        public async Task ResolvePrefersTheConfiguredPath()
        {
            Skip.IfNot(await CliAvailableAsync(), "Claude Code CLI is not on the PATH.");

            var resolved = await CliDetector.ResolveAsync("claude");

            Assert.True(resolved.Ok, resolved.Error);
            Assert.Equal("claude", resolved.Path);
        }

        [Fact]
        public async Task ReportsFailureForAMissingBinary()
        {
            // The text matters more than the outcome: the UI shows it, so it has to be an
            // explanation rather than an empty string.
            var detection = await CliDetector.DetectAsync("cockpit-no-such-binary-xyz");

            Assert.False(detection.Ok);
            Assert.False(string.IsNullOrWhiteSpace(detection.Error));
        }

        [Fact]
        public async Task ReportsFailureForAnEmptyPath()
        {
            var detection = await CliDetector.DetectAsync("   ");

            Assert.False(detection.Ok);
            Assert.False(string.IsNullOrWhiteSpace(detection.Error));
        }

        [SkippableFact]
        public async Task ResolveFallsBackWhenTheConfiguredPathIsWrong()
        {
            Skip.IfNot(await CliAvailableAsync(), "Claude Code CLI is not on the PATH.");

            // A bogus configured path must not be the end of it: the native installer puts
            // claude in ~/.local/bin, which is not always on the PATH on Windows. Whatever
            // the outcome, the report names a path and carries a reason when it failed.
            var resolved = await CliDetector.ResolveAsync(@"C:\definitely\not\here\claude.exe");

            Assert.False(string.IsNullOrEmpty(resolved.Path));
            if (!resolved.Ok) Assert.False(string.IsNullOrWhiteSpace(resolved.Error));
        }

        [SkippableFact]
        public async Task LaunchesThroughAPathContainingSpaces()
        {
            Skip.IfNot(ProcessLauncher.IsWindows, "Windows-only: exercises the cmd.exe /s /c route.");

            // cmd.exe's quoting rules are the reason ProcessLauncher exists. Rather than
            // asserting a specific machine layout, this proves the launcher survives a
            // quoted path with a space instead of truncating it at the space — which would
            // surface as a cryptic "not recognized" naming only the first word.
            var detection = await CliDetector.DetectAsync(@"C:\Program Files\cockpit nope\claude.cmd");

            Assert.False(detection.Ok);
            Assert.DoesNotContain("Program", detection.Error ?? string.Empty);
        }
    }
}
