using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Tootega.Cockpit.Cli;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// The statusline payload is the CLI's contract and it has changed shape more than once:
    /// a `limits[]` array today, fixed fields before that, percentages as fractions and as
    /// 0..100. Accepting all of them is what keeps the meters working across a CLI upgrade
    /// instead of going blank on one, so each historical shape gets a test.
    /// </summary>
    public class StatuslineTests
    {
        private static Tootega.Cockpit.Cli.RealLimits Parse(string json)
        {
            using (var document = JsonDocument.Parse(json))
            {
                return StatuslineCache.Parse(document.RootElement);
            }
        }

        // --- Current format: limits[] keyed by kind ---

        [Fact]
        public void ReadsTheCurrentLimitsArray()
        {
            var limits = Parse(
                "{\"ts\":\"2026-08-10T10:00:00Z\",\"rate_limits\":{\"limits\":[" +
                "{\"kind\":\"session\",\"used_percentage\":42,\"resets_at\":\"2026-08-10T15:00:00Z\"}," +
                "{\"kind\":\"weekly_all\",\"used_percentage\":70}," +
                "{\"kind\":\"weekly_scoped\",\"used_percentage\":15,\"scope\":{\"model\":{\"display_name\":\"Opus\"}}}]}}");

            Assert.Equal(0.42, limits.FiveHour.UsedPct.Value, 6);
            Assert.Equal("2026-08-10T15:00:00Z", limits.FiveHour.ResetsAt);
            Assert.Equal(0.70, limits.SevenDay.UsedPct.Value, 6);

            var scoped = limits.WeeklyScoped.Single();
            // The label comes from the server, so a new scope is named correctly with no code change.
            Assert.Equal("Opus", scoped.Label);
            Assert.Equal(0.15, scoped.UsedPct.Value, 6);
        }

        [Fact]
        public void DropsAScopedWindowWithNoLabel()
        {
            // Without a label it cannot be presented meaningfully, and "unknown" would be noise.
            var limits = Parse("{\"rate_limits\":{\"limits\":[{\"kind\":\"weekly_scoped\",\"used_percentage\":15}]}}");

            Assert.Null(limits.WeeklyScoped);
        }

        [Fact]
        public void IgnoresAnUnknownKind()
        {
            var limits = Parse("{\"rate_limits\":{\"limits\":[{\"kind\":\"something_new\",\"used_percentage\":50}]}}");

            Assert.Null(limits.FiveHour);
            Assert.Null(limits.SevenDay);
        }

        // --- Legacy shapes ---

        [Fact]
        public void ReadsLegacyFixedFields()
        {
            var limits = Parse(
                "{\"rate_limits\":{\"five_hour\":{\"used_pct\":0.25}," +
                "\"seven_day\":{\"used_pct\":0.5}," +
                "\"seven_day_opus\":{\"used_pct\":0.1}," +
                "\"seven_day_sonnet\":{\"used_pct\":0.2}}}");

            Assert.Equal(0.25, limits.FiveHour.UsedPct.Value, 6);
            Assert.Equal(0.5, limits.SevenDay.UsedPct.Value, 6);
            Assert.Equal(new[] { "Opus", "Sonnet" }, limits.WeeklyScoped.Select(s => s.Label));
        }

        [Theory]
        [InlineData("fiveHour")]
        [InlineData("5h")]
        public void AcceptsAlternativeSessionWindowNames(string key)
        {
            var limits = Parse("{\"rate_limits\":{\"" + key + "\":{\"utilization\":0.33}}}");

            Assert.Equal(0.33, limits.FiveHour.UsedPct.Value, 6);
        }

        [Theory]
        [InlineData("sevenDay")]
        [InlineData("7d")]
        [InlineData("weekly")]
        public void AcceptsAlternativeWeeklyWindowNames(string key)
        {
            var limits = Parse("{\"rate_limits\":{\"" + key + "\":{\"pct\":0.6}}}");

            Assert.Equal(0.6, limits.SevenDay.UsedPct.Value, 6);
        }

        // --- Percentage normalization ---

        [Theory]
        [InlineData(42, 0.42)]     // arrived as 0..100
        [InlineData(0.42, 0.42)]   // arrived as a fraction
        [InlineData(100, 1.0)]
        [InlineData(1, 1.0)]       // ambiguous, but 1 is a legitimate full fraction
        [InlineData(150, 1.0)]     // clamped: over budget is still "full"
        [InlineData(-5, 0.0)]
        public void NormalisesAndClampsThePercentage(double input, double expected)
        {
            // A value above 1.5 must have come as 0..100, since a real fraction never exceeds 1.
            var limits = Parse("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":" +
                               input.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}");

            Assert.Equal(expected, limits.FiveHour.UsedPct.Value, 6);
        }

        [Fact]
        public void AWindowWithoutAPercentageIsNotAWindow()
        {
            var limits = Parse("{\"rate_limits\":{\"five_hour\":{\"resets_at\":\"2026-08-10T15:00:00Z\"}}}");

            Assert.Null(limits.FiveHour);
        }

        // --- Reset timestamps ---

        [Fact]
        public void AcceptsAnEpochResetInSecondsOrMilliseconds()
        {
            var seconds = Parse("{\"rate_limits\":{\"five_hour\":{\"used_pct\":0.1,\"reset\":1786000000}}}");
            var milliseconds = Parse("{\"rate_limits\":{\"five_hour\":{\"used_pct\":0.1,\"reset\":1786000000000}}}");

            Assert.Equal(seconds.FiveHour.ResetsAt, milliseconds.FiveHour.ResetsAt);
            Assert.Contains("2026", seconds.FiveHour.ResetsAt);
        }

        [Fact]
        public void AnOutOfRangeEpochYieldsNoResetRatherThanAWrongOne()
        {
            var limits = Parse("{\"rate_limits\":{\"five_hour\":{\"used_pct\":0.1,\"reset\":999999999999999}}}");

            Assert.Null(limits.FiveHour.ResetsAt);
        }

        // --- Session flags ---

        [Fact]
        public void ReadsSessionFlags()
        {
            var limits = Parse(
                "{\"fast_mode\":true,\"model\":{\"id\":\"claude-opus-5\",\"display_name\":\"Claude Opus 5\"}," +
                "\"effort\":{\"level\":\"high\"},\"output_style\":{\"name\":\"concise\"}," +
                "\"session_kind\":\"interactive\"}");

            Assert.True(limits.Session.FastMode);
            Assert.Equal("claude-opus-5", limits.Session.ModelId);
            Assert.Equal("Claude Opus 5", limits.Session.ModelDisplay);
            Assert.Equal("high", limits.Session.Effort);
            Assert.Equal("concise", limits.Session.OutputStyle);
            Assert.Equal("interactive", limits.Session.Kind);
        }

        [Fact]
        public void AcceptsTheNestedSessionKind()
        {
            var limits = Parse("{\"session\":{\"kind\":\"attached\"}}");

            Assert.Equal("attached", limits.Session.Kind);
        }

        [Fact]
        public void NoFlagsMeansNoSessionBlock()
        {
            Assert.Null(Parse("{\"ts\":\"2026-08-10T10:00:00Z\"}").Session);
        }

        [Fact]
        public void SessionFlagsSurviveAPayloadWithoutRateLimits()
        {
            // They feed the account panel, so they are worth returning on their own.
            var limits = Parse("{\"fast_mode\":false}");

            Assert.NotNull(limits.Session);
            Assert.False(limits.Session.FastMode);
            Assert.Null(limits.FiveHour);
        }

        // --- Cache age ---

        [Fact]
        public void ComputesTheCacheAge()
        {
            // Age is what lets the UI dim a stale reading instead of presenting it as current.
            var written = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("o");

            var limits = Parse("{\"ts\":\"" + written + "\",\"fast_mode\":true}");

            Assert.True(limits.AgeMs >= 4 * 60 * 1000, "age was " + limits.AgeMs);
        }

        [Theory]
        [InlineData("{\"fast_mode\":true}")]
        [InlineData("{\"ts\":\"not a date\",\"fast_mode\":true}")]
        public void MissingOrInvalidTimestampMeansUnknownAge(string json)
        {
            Assert.Null(Parse(json).AgeMs);
        }

        [Fact]
        public void MissingCacheFileReadsAsNoData()
        {
            var missing = Path.Combine(Path.GetTempPath(), "cockpit-no-such-" + Guid.NewGuid().ToString("N") + ".json");

            Assert.Null(StatuslineCache.Read(missing));
        }

        [Fact]
        public void CorruptCacheFileReadsAsNoData()
        {
            var file = Path.Combine(Path.GetTempPath(), "cockpit-bad-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(file, "{ half written", new UTF8Encoding(false));
            try
            {
                Assert.Null(StatuslineCache.Read(file));
            }
            finally
            {
                File.Delete(file);
            }
        }

        // --- Installer reversibility ---

        [Fact]
        public void RecoversTheOriginalStatuslineFromTheWrapperCommand()
        {
            // This is what keeps the operation reversible when our own state is empty — a
            // wrapper installed by another machine or an older version.
            const string original = "powershell -File \"C:\\my\\statusline.ps1\" | more";
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(original));
            var command = "powershell -NoProfile -File \"...statusline-wrapper.ps1\" -Original \"" + encoded + "\"";

            Assert.Equal(original, StatuslineInstaller.DecodeOriginal(command));
        }

        [Fact]
        public void AnEmptyOriginalDecodesToNothing()
        {
            // There was no statusline before ours, so disabling must remove the block rather
            // than restore an empty command.
            Assert.Null(StatuslineInstaller.DecodeOriginal("... -Original \"\""));
        }

        [Theory]
        [InlineData("powershell -File wrapper.ps1")]
        [InlineData("... -Original \"not base64 !!\"")]
        [InlineData(null)]
        public void UnrecoverableCommandsDecodeToNothing(string command)
        {
            Assert.Null(StatuslineInstaller.DecodeOriginal(command));
        }
    }
}
