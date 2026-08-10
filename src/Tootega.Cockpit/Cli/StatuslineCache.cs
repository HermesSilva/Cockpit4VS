using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    /// <summary>Session flags the statusline payload carries.</summary>
    internal sealed class StatuslineSession
    {
        public bool? FastMode { get; set; }
        public string ModelId { get; set; }
        /// <summary>The CLI's own label, so it never has to be guessed from the id.</summary>
        public string ModelDisplay { get; set; }
        public string Effort { get; set; }
        public string OutputStyle { get; set; }
        /// <summary>
        /// interactive | attached | unattended — reported since CLI 2.1.221. Only shown when
        /// the payload carries it; an older CLI simply has no field.
        /// </summary>
        public string Kind { get; set; }

        public bool IsEmpty =>
            !FastMode.HasValue && ModelId == null && ModelDisplay == null &&
            Effort == null && OutputStyle == null && Kind == null;
    }

    internal sealed class RealLimits
    {
        /// <summary>The current session window.</summary>
        public LimitWindow FiveHour { get; set; }
        /// <summary>The weekly all-models window.</summary>
        public LimitWindow SevenDay { get; set; }
        /// <summary>Per-model weekly windows, when the payload has them.</summary>
        public List<ScopedBucket> WeeklyScoped { get; set; }
        public StatuslineSession Session { get; set; }
        /// <summary>Age of the cache. Absent when the payload carries no timestamp.</summary>
        public long? AgeMs { get; set; }
    }

    /// <summary>
    /// Reads the cache the statusline wrapper writes and extracts the account's real limits.
    /// Port of src/cli/StatuslineCache.ts.
    ///
    /// The parser is deliberately generous about field names. This payload is the CLI's
    /// statusline contract, which has changed shape more than once: the current format is a
    /// `limits[]` array keyed by kind, earlier ones used fixed fields, and percentages have
    /// arrived both as fractions and as 0..100. Accepting all of them is what keeps the meters
    /// working across CLI upgrades instead of going blank on one.
    /// </summary>
    internal static class StatuslineCache
    {
        /// <summary>Where the wrapper writes. Also referenced by the installer.</summary>
        public static string CacheFile => Path.Combine(ClaudeHome.Root, ".tootega-usage.json");

        /// <summary>Returns null when there is no readable cache.</summary>
        public static RealLimits Read(string file = null)
        {
            var raw = FileStore.ReadAllTextOrNull(file ?? CacheFile);
            if (raw == null) return null;

            try
            {
                using (var document = JsonDocument.Parse(raw))
                {
                    return Parse(document.RootElement);
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        internal static RealLimits Parse(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) return null;

            var result = new RealLimits
            {
                AgeMs = CacheAge(root),
                Session = ParseSession(root),
            };

            if (!root.TryGetProperty("rate_limits", out var rateLimits) ||
                rateLimits.ValueKind != JsonValueKind.Object)
            {
                // Session flags alone are still worth returning: they feed the account panel.
                return result;
            }

            ParseKinds(rateLimits, result);

            if (result.FiveHour == null)
                result.FiveHour = ParseWindow(FirstProperty(rateLimits, "five_hour", "fiveHour", "5h"));

            if (result.SevenDay == null)
                result.SevenDay = ParseWindow(FirstProperty(rateLimits, "seven_day", "sevenDay", "7d", "weekly"));

            if (result.WeeklyScoped == null)
                result.WeeklyScoped = LegacyScoped(rateLimits);

            return result;
        }

        /// <summary>
        /// Current format: `limits[]` where each entry has a kind
        /// (session | weekly_all | weekly_scoped) and, for scoped ones, the display name of
        /// the scope. The label comes from the server rather than being invented here, so a new
        /// scope appears correctly named without a code change.
        /// </summary>
        private static void ParseKinds(JsonElement rateLimits, RealLimits result)
        {
            if (!rateLimits.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array) return;

            var scoped = new List<ScopedBucket>();

            foreach (var limit in limits.EnumerateArray())
            {
                var window = ParseWindow(limit);
                if (window == null) continue;

                switch (ReadString(limit, "kind"))
                {
                    case "session":
                        result.FiveHour = window;
                        break;

                    case "weekly_all":
                        result.SevenDay = window;
                        break;

                    case "weekly_scoped":
                        var label = ReadScopeLabel(limit);
                        // Without a label the bucket cannot be presented meaningfully, so it is
                        // dropped rather than shown as "unknown".
                        if (label == null) break;
                        scoped.Add(new ScopedBucket
                        {
                            Label = label,
                            UsedPct = window.UsedPct,
                            ResetsAt = window.ResetsAt,
                        });
                        break;
                }
            }

            if (scoped.Count > 0) result.WeeklyScoped = scoped;
        }

        private static string ReadScopeLabel(JsonElement limit)
        {
            if (!limit.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object) return null;
            if (!scope.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object) return null;
            return ReadString(model, "display_name");
        }

        /// <summary>Legacy format: weekly per-model windows in fixed fields.</summary>
        private static List<ScopedBucket> LegacyScoped(JsonElement rateLimits)
        {
            var scoped = new List<ScopedBucket>();

            var families = new[]
            {
                new { Label = "Opus", Keys = new[] { "seven_day_opus", "sevenDayOpus", "weekly_opus", "opus" } },
                new { Label = "Sonnet", Keys = new[] { "seven_day_sonnet", "sevenDaySonnet", "weekly_sonnet", "sonnet" } },
            };

            foreach (var family in families)
            {
                foreach (var key in family.Keys)
                {
                    var window = ParseWindow(FirstProperty(rateLimits, key));
                    if (window == null) continue;
                    scoped.Add(new ScopedBucket
                    {
                        Label = family.Label,
                        UsedPct = window.UsedPct,
                        ResetsAt = window.ResetsAt,
                    });
                    break;
                }
            }

            return scoped.Count > 0 ? scoped : null;
        }

        private static StatuslineSession ParseSession(JsonElement root)
        {
            var session = new StatuslineSession();

            if (root.TryGetProperty("fast_mode", out var fastMode))
            {
                if (fastMode.ValueKind == JsonValueKind.True) session.FastMode = true;
                else if (fastMode.ValueKind == JsonValueKind.False) session.FastMode = false;
            }

            if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
            {
                session.ModelId = ReadString(model, "id");
                session.ModelDisplay = ReadString(model, "display_name");
            }

            if (root.TryGetProperty("effort", out var effort) && effort.ValueKind == JsonValueKind.Object)
                session.Effort = ReadString(effort, "level");

            if (root.TryGetProperty("output_style", out var style) && style.ValueKind == JsonValueKind.Object)
                session.OutputStyle = ReadString(style, "name");

            session.Kind = ReadString(root, "session_kind");
            if (session.Kind == null && root.TryGetProperty("session", out var nested) &&
                nested.ValueKind == JsonValueKind.Object)
            {
                session.Kind = ReadString(nested, "kind");
            }

            return session.IsEmpty ? null : session;
        }

        /// <summary>Cache age from the ISO `ts` field. Absent when missing or unparseable.</summary>
        private static long? CacheAge(JsonElement root)
        {
            var ts = ReadString(root, "ts");
            if (ts == null) return null;
            if (!DateTimeOffset.TryParse(ts, out var written)) return null;
            return Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - written.ToUnixTimeMilliseconds());
        }

        /// <summary>
        /// One window. The percentage is accepted under any of the names this payload has used,
        /// and normalized: a value above 1.5 must have arrived as 0..100, since a real fraction
        /// never exceeds 1. Without a percentage there is no window to show.
        /// </summary>
        internal static LimitWindow ParseWindow(JsonElement? element)
        {
            if (element?.ValueKind != JsonValueKind.Object) return null;
            var window = element.Value;

            var percent = FirstNumber(window,
                // The official statusline field first.
                "used_percentage", "usedPercentage", "used_pct", "usedPct",
                "utilization", "percent", "pct", "used_percent", "usage");
            if (!percent.HasValue) return null;

            var value = percent.Value;
            if (value > 1.5) value /= 100.0;

            return new LimitWindow
            {
                UsedPct = Math.Max(0, Math.Min(1, value)),
                ResetsAt = FirstTimestamp(window, "resets_at", "reset_at", "resetsAt", "reset"),
            };
        }

        private static JsonElement? FirstProperty(JsonElement parent, params string[] names)
        {
            foreach (var name in names)
            {
                if (parent.TryGetProperty(name, out var value)) return value;
            }
            return null;
        }

        private static double? FirstNumber(JsonElement parent, params string[] names)
        {
            foreach (var name in names)
            {
                if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) continue;
                if (value.TryGetDouble(out var number) && !double.IsNaN(number)) return number;
            }
            return null;
        }

        /// <summary>
        /// A reset time, as a string or as an epoch. The epoch may be in seconds or
        /// milliseconds; the threshold tells them apart.
        /// </summary>
        private static string FirstTimestamp(JsonElement parent, params string[] names)
        {
            foreach (var name in names)
            {
                if (!parent.TryGetProperty(name, out var value)) continue;

                if (value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrEmpty(text)) return text;
                }
                else if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && number > 0)
                {
                    var milliseconds = number > 1e12 ? number : number * 1000;
                    try
                    {
                        return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds).UtcDateTime.ToString("o");
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // Out of range: treated as no reset time rather than a wrong one.
                    }
                }
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
    }
}
