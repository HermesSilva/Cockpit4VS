using System.Text.Json;

namespace Tootega.Cockpit.Cli
{
    /// <summary>
    /// Parses the answer to the get_context_usage control request. Port of
    /// src/cli/ContextUsage.ts.
    ///
    /// The real payload, abridged (CLI 2.1.217):
    ///   { categories: [{ name: "Skills", tokens: 1928 }, …],
    ///     skills: { totalSkills: 14, includedSkills: 14, tokens: 1928,
    ///               skillFrontmatter: [{ name, source, tokens }, …] } }
    ///
    /// Version-tolerant: any missing or unexpectedly shaped field becomes absent, and nothing
    /// throws. If the CLI changes the payload the skills panel goes empty — which is the right
    /// failure, because the alternative is a broken UI over a working conversation.
    /// </summary>
    internal static class ContextUsage
    {
        /// <summary>
        /// Returns null when the payload carries nothing recognisable, so the caller simply
        /// does not update anything rather than clearing good data.
        /// </summary>
        public static ContextUsageInfo Parse(JsonElement? payload)
        {
            if (payload?.ValueKind != JsonValueKind.Object) return null;
            var root = payload.Value;

            var skills = root.TryGetProperty("skills", out var skillsElement) &&
                         skillsElement.ValueKind == JsonValueKind.Object
                ? skillsElement
                : (JsonElement?)null;

            var frontmatter = skills.HasValue &&
                              skills.Value.TryGetProperty("skillFrontmatter", out var fm) &&
                              fm.ValueKind == JsonValueKind.Array
                ? fm
                : (JsonElement?)null;

            // The listing total is on the skills block in newer CLIs and in the categories
            // array in older ones; either is accepted.
            var listingTokens = skills.HasValue ? ReadCount(skills.Value, "tokens") : null;
            if (!listingTokens.HasValue) listingTokens = CategoryTokens(root, "Skills");

            if (!frontmatter.HasValue && !listingTokens.HasValue) return null;

            var info = new ContextUsageInfo { ListingTokens = listingTokens };

            if (frontmatter.HasValue)
            {
                foreach (var entry in frontmatter.Value.EnumerateArray())
                {
                    var name = ReadString(entry, "name");
                    // An entry with no name cannot be shown or attributed, so it is dropped.
                    if (string.IsNullOrEmpty(name)) continue;

                    info.Skills.Add(new ContextUsageSkill
                    {
                        Name = name,
                        Source = ReadString(entry, "source"),
                        Tokens = ReadCount(entry, "tokens"),
                    });
                }
            }

            if (skills.HasValue)
            {
                info.TotalSkills = (int?)ReadCount(skills.Value, "totalSkills");
                info.IncludedSkills = (int?)ReadCount(skills.Value, "includedSkills");
            }

            // Falling back to the row count keeps the header honest when the engine reports the
            // list but not its size.
            if (!info.IncludedSkills.HasValue && frontmatter.HasValue) info.IncludedSkills = info.Skills.Count;

            return info;
        }

        /// <summary>Tokens of the named entry in `categories[]`.</summary>
        private static long? CategoryTokens(JsonElement root, string label)
        {
            if (!root.TryGetProperty("categories", out var categories) ||
                categories.ValueKind != JsonValueKind.Array) return null;

            foreach (var category in categories.EnumerateArray())
            {
                if (category.ValueKind != JsonValueKind.Object) continue;
                if (ReadString(category, "name") != label) continue;
                return ReadCount(category, "tokens");
            }

            return null;
        }

        private static string ReadString(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>A non-negative count, rounded. Anything else is treated as absent.</summary>
        private static long? ReadCount(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
            if (!value.TryGetDouble(out var number) || double.IsNaN(number) || number < 0) return null;
            return (long)System.Math.Round(number);
        }
    }
}
