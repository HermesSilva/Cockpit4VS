using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    internal sealed class PriceInfo
    {
        /// <summary>USD per 1M input tokens.</summary>
        public double InMTok { get; set; }
        /// <summary>USD per 1M output tokens.</summary>
        public double OutMTok { get; set; }
    }

    /// <summary>
    /// Model prices, read from Anthropic's public pricing docs. Port of src/cli/ModelPricing.ts.
    ///
    /// There is NO price endpoint in the API — the price exists only in the documentation. So
    /// the pricing page markdown is fetched once a day and its "Model pricing" table parsed.
    /// It is an unauthenticated read of a public document with no token spend, and it is not
    /// part of the agent loop.
    ///
    /// The cache is what makes it safe: a fetch failure keeps the previous prices, even stale,
    /// rather than blanking the price column in the UI.
    /// </summary>
    internal sealed class ModelPricing
    {
        private const string PricingUrl = "https://platform.claude.com/docs/en/about-claude/pricing.md";
        private const string CacheFileName = "model-pricing.json";
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

        private static readonly Regex NamePattern =
            new Regex(@"^Claude\s+([A-Za-z]+)\s+(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MarkdownLink =
            new Regex(@"\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);
        private static readonly Regex UsdPattern =
            new Regex(@"\$\s*([\d.]+)", RegexOptions.Compiled);
        private static readonly Regex SeparatorRow =
            new Regex(@"^-+$", RegexOptions.Compiled);

        private sealed class PricingCache
        {
            [JsonPropertyName("fetchedAt")] public long FetchedAt { get; set; }
            [JsonPropertyName("models")] public Dictionary<string, PriceInfo> Models { get; set; }
        }

        private readonly string _cacheFile;

        public ModelPricing(string cacheDirectory = null)
        {
            _cacheFile = Path.Combine(cacheDirectory ?? ClaudeHome.CockpitDir, CacheFileName);
        }

        /// <summary>
        /// The price map, from the on-disk cache when fresh, otherwise fetched and stored.
        /// Never throws: on failure it returns the previous cache, or an empty map.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, PriceInfo>> EnsureAsync()
        {
            var cached = ReadCache();
            if (cached != null &&
                DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(cached.FetchedAt) < MaxAge)
            {
                return cached.Models;
            }

            var markdown = await AnthropicHttp
                .GetAsync(PricingUrl, null, 8000, "text/markdown, text/plain, */*")
                .ConfigureAwait(false);

            var fresh = markdown != null ? ParseMarkdown(markdown) : new Dictionary<string, PriceInfo>();
            if (fresh.Count > 0)
            {
                WriteCache(new PricingCache
                {
                    FetchedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Models = fresh,
                });
                return fresh;
            }

            // Keep whatever we have, even stale: a missing price column reads as "free",
            // which is worse than a slightly old number.
            return cached?.Models ?? new Dictionary<string, PriceInfo>();
        }

        /// <summary>
        /// "Claude Opus 4.8" becomes "claude-opus-4-8". Null when the pattern does not match,
        /// which is how footnote and heading rows are filtered out.
        /// </summary>
        public static string NameToId(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            // Strips markdown links and any trailing prose, e.g. "… starting September 2026".
            var stripped = MarkdownLink.Replace(name, string.Empty).Trim();
            var match = NamePattern.Match(stripped);
            if (!match.Success) return null;

            var family = match.Groups[1].Value.ToLowerInvariant();
            var version = match.Groups[2].Value.Replace('.', '-');
            return "claude-" + family + "-" + version;
        }

        /// <summary>Parses the docs markdown into id to price.</summary>
        public static Dictionary<string, PriceInfo> ParseMarkdown(string markdown)
        {
            var prices = new Dictionary<string, PriceInfo>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(markdown)) return prices;

            foreach (var line in ModelPricingSection(markdown).Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("|", StringComparison.Ordinal)) continue;

                // ['', Model, Base Input, 5m writes, 1h writes, Cache hits, Output, '']
                var cells = trimmed.Split('|').Select(c => c.Trim()).ToArray();
                if (cells.Length < 7) continue;

                var name = cells[1];
                if (string.IsNullOrEmpty(name)) continue;
                if (SeparatorRow.IsMatch(name)) continue;
                if (string.Equals(name, "model", StringComparison.OrdinalIgnoreCase)) continue;

                var id = NameToId(name);
                // First occurrence wins: a model can appear twice, and the introductory price
                // listed first is the one in force.
                if (id == null || prices.ContainsKey(id)) continue;

                var input = ParseUsd(cells[2]);
                // Output is the last cell before the trailing empty one.
                var output = ParseUsd(cells[cells.Length - 2]);
                if (!input.HasValue || !output.HasValue) continue;

                prices[id] = new PriceInfo { InMTok = input.Value, OutMTok = output.Value };
            }

            return prices;
        }

        /// <summary>Narrows the document to the "Model pricing" section, if it has one.</summary>
        private static string ModelPricingSection(string markdown)
        {
            var start = Regex.Match(markdown, @"^##\s+Model pricing", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!start.Success) return markdown;

            var section = markdown.Substring(start.Index);
            var next = Regex.Match(section, @"^##\s+(?!Model pricing)",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            return next.Success && next.Index > 0 ? section.Substring(0, next.Index) : section;
        }

        /// <summary>First dollar amount in a cell: "$6.25 / MTok" becomes 6.25.</summary>
        private static double? ParseUsd(string cell)
        {
            if (string.IsNullOrEmpty(cell)) return null;
            var match = UsdPattern.Match(cell);
            if (!match.Success) return null;

            return double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : (double?)null;
        }

        private PricingCache ReadCache()
        {
            var raw = FileStore.ReadAllTextOrNull(_cacheFile);
            if (raw == null) return null;

            var parsed = Json.TryDeserialize<PricingCache>(raw);
            return parsed?.Models != null && parsed.FetchedAt > 0 ? parsed : null;
        }

        private void WriteCache(PricingCache cache)
        {
            FileStore.WriteAtomic(_cacheFile, Json.Serialize(cache));
        }
    }
}
