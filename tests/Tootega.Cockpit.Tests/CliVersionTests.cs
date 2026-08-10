using Tootega.Cockpit.Cli;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    public class CliVersionTests
    {
        [Theory]
        [InlineData("2.1.226 (Claude Code)", "2.1.226")]
        [InlineData("2.1.226", "2.1.226")]
        [InlineData("v10.0.1-beta.3", "10.0.1")]
        [InlineData("no digits here", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void ParsesSemverOutOfCliOutput(string input, string expected)
        {
            Assert.Equal(expected, CliVersion.ParseSemver(input));
        }

        [Theory]
        [InlineData("2.1.225", "2.1.226", true)]
        [InlineData("2.0.999", "2.1.0", true)]
        [InlineData("1.9.9", "2.0.0", true)]
        [InlineData("2.1.226", "2.1.226", false)]
        [InlineData("2.1.227", "2.1.226", false)]  // ahead of the registry (a local build)
        [InlineData("2.2.0", "2.1.226", false)]
        public void ComparesVersionsComponentwise(string installed, string latest, bool outdated)
        {
            Assert.Equal(outdated, CliVersion.IsOutdated(installed, latest));
        }

        [Theory]
        [InlineData(null, "2.1.226")]
        [InlineData("2.1.226", null)]
        [InlineData("unknown", "2.1.226")]
        [InlineData(null, null)]
        public void UnknownVersionsAreNeverReportedAsOutdated(string installed, string latest)
        {
            // Showing a spurious "update available" is worse than showing nothing, so
            // missing data resolves to false rather than to a guess.
            Assert.False(CliVersion.IsOutdated(installed, latest));
        }

        [Fact]
        public void ComparesTheRealCliFormat()
        {
            // What `claude --version` actually prints, against a registry-style version.
            Assert.True(CliVersion.IsOutdated("2.1.200 (Claude Code)", "2.1.226"));
            Assert.False(CliVersion.IsOutdated("2.1.226 (Claude Code)", "2.1.226"));
        }
    }
}
