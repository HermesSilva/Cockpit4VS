using System.Text.Json;

namespace Tootega.Cockpit.Stats
{
    /// <summary>
    /// Identity of an assistant response, used to deduplicate its `usage`. Port of
    /// src/stats/usageKey.ts.
    ///
    /// One assistant response becomes SEVERAL lines in the transcript — one for the text
    /// block, one per tool_use — and every one of them repeats the SAME usage object.
    /// Summing line by line inflates the total: measured at roughly 59% too much over seven
    /// days. Counting once per response is what makes the 7-day figures match reality.
    ///
    /// The duplication is always within one file, across consecutive lines of the same
    /// response, so a single set of seen keys per file is enough.
    /// </summary>
    internal static class UsageKey
    {
        /// <summary>
        /// Returns the dedup key, or null meaning "do not deduplicate — count this line".
        ///
        /// A line with no message id gets counted rather than dropped: under-counting a real
        /// response is worse than double-counting one we cannot identify.
        /// </summary>
        public static string For(JsonElement entry)
        {
            if (entry.ValueKind != JsonValueKind.Object) return null;

            if (!entry.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                return null;

            if (!message.TryGetProperty("id", out var idProperty) || idProperty.ValueKind != JsonValueKind.String)
                return null;

            var id = idProperty.GetString();
            if (string.IsNullOrEmpty(id)) return null;

            var requestId = entry.TryGetProperty("requestId", out var request) && request.ValueKind == JsonValueKind.String
                ? request.GetString()
                : string.Empty;

            return id + ":" + requestId;
        }
    }
}
