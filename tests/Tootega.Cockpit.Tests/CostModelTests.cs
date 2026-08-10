using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Stats;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// The context limit drives the meter the whole panel is built around, and getting it
    /// wrong is invisible: a 1M session shown as 200K just looks like the user is nearly out
    /// of context. These tests pin the precedence between the four sources of that number.
    /// </summary>
    public class CostModelTests
    {
        public CostModelTests()
        {
            // Discovery state is process-wide, so each test starts from a clean slate.
            CostModel.ResetDiscoveredContexts();
        }

        // --- Pricing ---

        [Fact]
        public void PricesPerFamily()
        {
            Assert.Equal(5, CostModel.PriceFor("claude-opus-5").Input);
            Assert.Equal(3, CostModel.PriceFor("claude-sonnet-5").Input);
            Assert.Equal(1, CostModel.PriceFor("claude-haiku-4-5").Input);
            Assert.Equal(10, CostModel.PriceFor("claude-fable-5").Input);
        }

        [Fact]
        public void UnknownModelsFallBackToTheExpensiveDefault()
        {
            // Under-reporting cost is the worse error for a transparency panel, so an
            // unrecognised model is priced high rather than free.
            Assert.Equal(5, CostModel.PriceFor("something-new").Input);
            Assert.Equal(5, CostModel.PriceFor(null).Input);
        }

        [Fact]
        public void EstimatesCostFromEveryTokenCategory()
        {
            var usage = new Usage
            {
                InputTokens = 1_000_000,
                OutputTokens = 1_000_000,
                CacheCreationInputTokens = 1_000_000,
                CacheReadInputTokens = 1_000_000,
            };

            // opus: 5 + 25 + 6.25 + 0.5 per million of each.
            Assert.Equal(36.75, CostModel.EstimateCost(usage, "claude-opus-5"), 6);
        }

        [Fact]
        public void CacheReadIsFarCheaperThanInput()
        {
            // This ratio is what the savings figure is built on.
            var asInput = CostModel.EstimateCost(new Usage { InputTokens = 1_000_000 }, "claude-opus-5");
            var asCacheRead = CostModel.EstimateCost(new Usage { CacheReadInputTokens = 1_000_000 }, "claude-opus-5");

            Assert.True(asCacheRead < asInput / 5, asCacheRead + " should be far below " + asInput);
        }

        [Fact]
        public void MalformedUsageCostsNothingRatherThanNaN()
        {
            Assert.Equal(0, CostModel.EstimateCost(new Usage(), "claude-opus-5"));
            Assert.Equal(0, CostModel.EstimateCost(new Usage { InputTokens = -50 }, "claude-opus-5"));
            Assert.Equal(0, CostModel.EstimateCost(null, "claude-opus-5"));
        }

        // --- Context limit ---

        [Fact]
        public void NoModelMeansTheConservativeLimit()
        {
            Assert.Equal(200_000, CostModel.DeriveContextLimit(null));
            Assert.Equal(200_000, CostModel.DeriveContextLimit(""));
        }

        [Fact]
        public void TheOneMSuffixMeansOneMillion()
        {
            Assert.Equal(1_000_000, CostModel.DeriveContextLimit("claude-sonnet-4-5[1m]"));
            Assert.Equal(1_000_000, CostModel.DeriveContextLimit("claude-sonnet-4-5[1M]"));
        }

        [Fact]
        public void ADiscoveredContextWinsOverGuessing()
        {
            // /v1/models is the only real source; a pattern match is a fallback.
            CostModel.RegisterModelContext("some-model", 500_000);

            Assert.Equal(500_000, CostModel.DeriveContextLimit("some-model"));
        }

        [Fact]
        public void DiscoveryIsMatchedIgnoringTheOneMSuffix()
        {
            CostModel.RegisterModelContext("claude-opus-9", 700_000);

            Assert.Equal(700_000, CostModel.DeriveContextLimit("claude-opus-9"));
        }

        [Fact]
        public void TheClaude5FamilyIsOneMillionBeforeDiscoveryAnswers()
        {
            // These models are natively 1M and carry no [1m] suffix; without this fallback the
            // meter would show 200K for the whole session until discovery replied.
            Assert.Equal(1_000_000, CostModel.DeriveContextLimit("claude-fable-5"));
            Assert.Equal(1_000_000, CostModel.DeriveContextLimit("claude-sonnet-5"));
            Assert.Equal(1_000_000, CostModel.DeriveContextLimit("claude-opus-5"));
        }

        [Fact]
        public void OlderModelsStayAtTwoHundredThousand()
        {
            Assert.Equal(200_000, CostModel.DeriveContextLimit("claude-haiku-4-5"));
            Assert.Equal(200_000, CostModel.DeriveContextLimit("claude-sonnet-4-6"));
        }

        [Fact]
        public void DisablingOneMCapsEverything()
        {
            // With CLAUDE_CODE_DISABLE_1M_CONTEXT set, 200K is where the CLI auto-compacts,
            // whatever the model's own window says.
            CostModel.RegisterModelContext("big-model", 900_000);
            CostModel.SetOneMContextDisabled(true);

            Assert.Equal(200_000, CostModel.DeriveContextLimit("claude-sonnet-4-5[1m]"));
            Assert.Equal(200_000, CostModel.DeriveContextLimit("claude-fable-5"));
            Assert.Equal(200_000, CostModel.DeriveContextLimit("big-model"));
        }

        [Fact]
        public void IgnoresInvalidDiscoveredContexts()
        {
            CostModel.RegisterModelContext("claude-haiku-4-5", 0);
            CostModel.RegisterModelContext("claude-haiku-4-5", -1);
            CostModel.RegisterModelContext("claude-haiku-4-5", null);

            Assert.Equal(200_000, CostModel.DeriveContextLimit("claude-haiku-4-5"));
        }

        // --- Model id normalization ---

        [Theory]
        [InlineData("claude-sonnet-4-5[1m][1m]", "claude-sonnet-4-5[1m]")]
        [InlineData("claude-sonnet-4-5[1M][1m]", "claude-sonnet-4-5[1M]")]
        [InlineData("claude-sonnet-4-5[1m]", "claude-sonnet-4-5[1m]")]
        [InlineData("claude-opus-5", "claude-opus-5")]
        [InlineData(null, null)]
        public void CollapsesRepeatedOneMSuffixes(string input, string expected)
        {
            // A resumed old session can carry the duplicated id, which would both display
            // wrong and confuse the price lookup.
            Assert.Equal(expected, CostModel.NormalizeModel(input));
        }
    }
}
