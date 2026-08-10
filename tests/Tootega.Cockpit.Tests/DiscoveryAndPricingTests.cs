using System.Linq;
using System.Text.Json;
using Tootega.Cockpit.Cli;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// Discovery and pricing both read someone else's format — an API response and a
    /// documentation page. So the tests are about tolerance: an unusable entry is dropped, and
    /// a shape we do not recognise degrades to "no data" rather than to a wrong number.
    /// </summary>
    public class DiscoveryAndPricingTests
    {
        // --- Model discovery ---

        [Fact]
        public void ParsesTheModelCatalogue()
        {
            var models = ModelDiscovery.Parse(
                "{\"data\":[{\"id\":\"claude-opus-5\",\"display_name\":\"Claude Opus 5\"," +
                "\"max_input_tokens\":1000000,\"created_at\":\"2026-05-01T00:00:00Z\"}]}");

            var model = models.Single();
            Assert.Equal("claude-opus-5", model.Id);
            Assert.Equal("Claude Opus 5", model.DisplayName);
            Assert.Equal(1_000_000, model.ContextTokens);
        }

        [Fact]
        public void DropsEntriesWithoutAnId()
        {
            var models = ModelDiscovery.Parse(
                "{\"data\":[{\"display_name\":\"No id\"},{\"id\":\"\"},{\"id\":\"good\"}]}");

            Assert.Equal("good", models.Single().Id);
        }

        [Fact]
        public void MetadataIsOptional()
        {
            // An older account may not expose max_input_tokens at all, and absent must not be
            // read as small.
            var model = ModelDiscovery.Parse("{\"data\":[{\"id\":\"claude-x\"}]}").Single();

            Assert.Null(model.ContextTokens);
            Assert.Null(model.DisplayName);
            Assert.Null(model.CreatedAt);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("{}")]
        [InlineData("{\"data\":\"not an array\"}")]
        public void UnusableBodiesYieldNoModels(string body)
        {
            Assert.Empty(ModelDiscovery.Parse(body));
        }

        [Fact]
        public void PickerOrdersNewestFirstAndTagsOneMillionWindows()
        {
            // The [1m] suffix is what makes the CLI open the 1M window where it is not the
            // default, and it is a no-op on the natively-1M ones — so the rule comes from the
            // reported window instead of a table that would go stale.
            var models = ModelDiscovery.Parse(
                "{\"data\":[" +
                "{\"id\":\"old-small\",\"created_at\":\"2025-01-01\",\"max_input_tokens\":200000}," +
                "{\"id\":\"new-big\",\"created_at\":\"2026-06-01\",\"max_input_tokens\":1000000}]}");

            var ids = ModelDiscovery.PickerIds(models);

            Assert.Equal(new[] { "new-big[1m]", "old-small" }, ids);
        }

        [Fact]
        public void ModelsWithNoWindowAreNotTagged()
        {
            var models = ModelDiscovery.Parse("{\"data\":[{\"id\":\"unknown-window\"}]}");

            Assert.Equal("unknown-window", ModelDiscovery.PickerIds(models).Single());
        }

        // --- Pricing ---

        [Theory]
        [InlineData("Claude Opus 4.8", "claude-opus-4-8")]
        [InlineData("Claude Sonnet 5", "claude-sonnet-5")]
        [InlineData("Claude Haiku 4.5", "claude-haiku-4-5")]
        [InlineData("Claude Opus 4.8 [docs](https://x) starting September 1, 2026", "claude-opus-4-8")]
        public void NormalisesDocumentNamesToApiIds(string name, string expected)
        {
            Assert.Equal(expected, ModelPricing.NameToId(name));
        }

        [Theory]
        [InlineData("Model")]
        [InlineData("---")]
        [InlineData("Some Other Product 1.0")]
        [InlineData("")]
        [InlineData(null)]
        public void RejectsRowsThatAreNotModels(string name)
        {
            // Returning null is how heading, separator and footnote rows are filtered out.
            Assert.Null(ModelPricing.NameToId(name));
        }

        [Fact]
        public void ParsesThePricingTable()
        {
            const string markdown = @"# Pricing

## Model pricing

| Model | Base Input Tokens | 5m Cache Writes | 1h Cache Writes | Cache Hits & Refreshes | Output Tokens |
| --- | --- | --- | --- | --- | --- |
| Claude Opus 4.8 | $5 / MTok | $6.25 / MTok | $10 / MTok | $0.50 / MTok | $25 / MTok |
| Claude Haiku 4.5 | $1 / MTok | $1.25 / MTok | $2 / MTok | $0.10 / MTok | $5 / MTok |

## Something else

| Claude Fake 9 | $999 / MTok | x | x | x | $999 / MTok |
";

            var prices = ModelPricing.ParseMarkdown(markdown);

            Assert.Equal(5, prices["claude-opus-4-8"].InMTok);
            Assert.Equal(25, prices["claude-opus-4-8"].OutMTok);
            Assert.Equal(1, prices["claude-haiku-4-5"].InMTok);
            Assert.Equal(5, prices["claude-haiku-4-5"].OutMTok);
            // The section boundary is respected, so a table further down is not absorbed.
            Assert.False(prices.ContainsKey("claude-fake-9"));
        }

        [Fact]
        public void FirstOccurrenceOfAModelWins()
        {
            // A model can be listed twice; the introductory price listed first is in force.
            const string markdown = @"## Model pricing

| Model | Base Input | a | b | c | Output |
| Claude Opus 5 | $3 / MTok | x | x | x | $15 / MTok |
| Claude Opus 5 | $9 / MTok | x | x | x | $45 / MTok |
";

            Assert.Equal(3, ModelPricing.ParseMarkdown(markdown)["claude-opus-5"].InMTok);
        }

        [Fact]
        public void SkipsRowsWithoutParseablePrices()
        {
            const string markdown = @"## Model pricing

| Model | Base Input | a | b | c | Output |
| Claude Opus 5 | contact sales | x | x | x | contact sales |
| Claude Sonnet 5 | $3 / MTok | x | x | x | $15 / MTok |
";

            var prices = ModelPricing.ParseMarkdown(markdown);

            Assert.False(prices.ContainsKey("claude-opus-5"));
            Assert.True(prices.ContainsKey("claude-sonnet-5"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("no tables at all")]
        [InlineData(null)]
        public void UnusableMarkdownYieldsNoPrices(string markdown)
        {
            Assert.Empty(ModelPricing.ParseMarkdown(markdown));
        }

        // --- get_context_usage ---

        private static JsonElement Payload(string json)
        {
            using (var document = JsonDocument.Parse(json))
            {
                return document.RootElement.Clone();
            }
        }

        [Fact]
        public void ParsesSkillMetadataAndTotals()
        {
            // The real shape, abridged, as measured on CLI 2.1.217.
            var info = ContextUsage.Parse(Payload(
                "{\"categories\":[{\"name\":\"Skills\",\"tokens\":1928}]," +
                "\"skills\":{\"totalSkills\":14,\"includedSkills\":11,\"tokens\":1928," +
                "\"skillFrontmatter\":[{\"name\":\"caveman\",\"source\":\"userSettings\",\"tokens\":134}," +
                "{\"name\":\"dataviz\",\"source\":\"built-in\",\"tokens\":300}]}}"));

            Assert.Equal(1928, info.ListingTokens);
            Assert.Equal(14, info.TotalSkills);
            Assert.Equal(11, info.IncludedSkills);
            Assert.Equal(2, info.Skills.Count);
            Assert.Equal("userSettings", info.Skills[0].Source);
            Assert.Equal(134, info.Skills[0].Tokens);
        }

        [Fact]
        public void FallsBackToTheCategoriesArrayForTheListingTotal()
        {
            var info = ContextUsage.Parse(Payload(
                "{\"categories\":[{\"name\":\"Messages\",\"tokens\":50},{\"name\":\"Skills\",\"tokens\":900}]," +
                "\"skills\":{\"skillFrontmatter\":[{\"name\":\"a\"}]}}"));

            Assert.Equal(900, info.ListingTokens);
        }

        [Fact]
        public void CountsTheListedRowsWhenTheEngineDoesNotSayHowMany()
        {
            var info = ContextUsage.Parse(Payload(
                "{\"skills\":{\"tokens\":10,\"skillFrontmatter\":[{\"name\":\"a\"},{\"name\":\"b\"}]}}"));

            Assert.Equal(2, info.IncludedSkills);
        }

        [Fact]
        public void DropsSkillsWithoutAName()
        {
            var info = ContextUsage.Parse(Payload(
                "{\"skills\":{\"tokens\":10,\"skillFrontmatter\":[{\"tokens\":5},{\"name\":\"real\"}]}}"));

            Assert.Equal("real", info.Skills.Single().Name);
        }

        [Fact]
        public void UnrecognisedPayloadsReturnNullSoNothingIsCleared()
        {
            // Returning null means the caller does not update — better than blanking good data
            // because a CLI upgrade changed the shape.
            Assert.Null(ContextUsage.Parse(Payload("{}")));
            Assert.Null(ContextUsage.Parse(Payload("{\"skills\":{}}")));
            Assert.Null(ContextUsage.Parse(Payload("[]")));
            Assert.Null(ContextUsage.Parse(null));
        }

        [Fact]
        public void NegativeOrNonNumericCountsAreTreatedAsAbsent()
        {
            var info = ContextUsage.Parse(Payload(
                "{\"skills\":{\"tokens\":100,\"totalSkills\":-3,\"includedSkills\":\"many\"," +
                "\"skillFrontmatter\":[{\"name\":\"a\",\"tokens\":-1}]}}"));

            Assert.Null(info.TotalSkills);
            Assert.Null(info.Skills.Single().Tokens);
            // includedSkills was unusable, so it falls back to the row count.
            Assert.Equal(1, info.IncludedSkills);
        }
    }
}
