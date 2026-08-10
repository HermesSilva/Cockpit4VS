using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    internal sealed class ApiUsage
    {
        /// <summary>kind: session</summary>
        public LimitWindow FiveHour { get; set; }
        /// <summary>kind: weekly_all</summary>
        public LimitWindow SevenDay { get; set; }
        /// <summary>kind: weekly_scoped — one per scoped model.</summary>
        public List<ScopedBucket> WeeklyScoped { get; set; }
        /// <summary>Above zero when this is a reused reading, because the live fetch failed.</summary>
        public long? AgeMs { get; set; }
    }

    /// <summary>
    /// The account's REAL usage, from the OAuth endpoint the CLI's own /usage uses. Port of
    /// src/cli/UsageApi.ts.
    ///
    /// GET /api/oauth/usage is read-only and spends no tokens, so it fits the clean-utility
    /// exception. The token is read from the credentials file and never written or logged; the
    /// server is the authority on whether it is still valid.
    ///
    /// Resilience matters more than freshness here. A single timeout used to drop the whole
    /// panel back to the local dollar estimate, which is a far worse answer than a real
    /// percentage read a few minutes ago. So a transient failure retries once and then falls
    /// back to the last good reading while it is still meaningful — and says how old it is,
    /// rather than passing it off as current.
    /// </summary>
    internal sealed class UsageApi
    {
        private const string Endpoint = AnthropicHttp.Host + "/api/oauth/usage";

        private static readonly TimeSpan PositiveTtl = TimeSpan.FromSeconds(30);
        /// <summary>Short, so a blip does not stick around for half a minute.</summary>
        private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(5);
        /// <summary>How long a good reading stays worth showing while the API is unreachable.</summary>
        private static readonly TimeSpan StaleOk = TimeSpan.FromMinutes(15);
        private const int RetryDelayMs = 700;

        private readonly object _gate = new object();

        private DateTime _cachedAt = DateTime.MinValue;
        private ApiUsage _cached;
        private DateTime _lastGoodAt = DateTime.MinValue;
        private ApiUsage _lastGood;
        private string _lastError;

        /// <summary>Why the last live fetch failed and how old the reading in hand is.</summary>
        public (string LastError, long? LastGoodAgeMs) Diagnostics()
        {
            lock (_gate)
            {
                return (_lastError, _lastGood != null
                    ? (long)(DateTime.UtcNow - _lastGoodAt).TotalMilliseconds
                    : (long?)null);
            }
        }

        /// <summary>
        /// Fetches the real usage. Cached for 30 seconds; pass <paramref name="force"/> when the
        /// user explicitly asked (the Usage button) so they get a fresh reading.
        ///
        /// Returns null only when there is no usable real data at all — never as the first
        /// response to a hiccup.
        /// </summary>
        public async Task<ApiUsage> FetchAsync(bool force = false)
        {
            lock (_gate)
            {
                if (!force && _cachedAt > DateTime.MinValue)
                {
                    var age = DateTime.UtcNow - _cachedAt;
                    var ttl = _cached != null ? PositiveTtl : NegativeTtl;
                    if (age < ttl) return _cached ?? StaleFallback();
                }
            }

            var token = ClaudeHome.ReadOauthToken();
            if (string.IsNullOrEmpty(token))
            {
                lock (_gate)
                {
                    // Named without quoting any content: this is a credentials file.
                    _lastError = "no OAuth accessToken in ~/.claude/.credentials.json";
                    _cachedAt = DateTime.UtcNow;
                    _cached = null;
                    Log.Debug("usage-api: " + _lastError);
                    return StaleFallback();
                }
            }

            var credentials = new ApiCredentials { AuthToken = token };
            string failure = "not attempted";

            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0) await Task.Delay(RetryDelayMs).ConfigureAwait(false);

                var (status, body, error) = await AnthropicHttp
                    .GetWithStatusAsync(Endpoint, credentials)
                    .ConfigureAwait(false);

                if (status >= 200 && status < 300)
                {
                    var parsed = Parse(body);
                    if (parsed != null)
                    {
                        lock (_gate)
                        {
                            _lastError = null;
                            _lastGood = parsed;
                            _lastGoodAt = DateTime.UtcNow;
                            _cached = parsed;
                            _cachedAt = DateTime.UtcNow;
                        }
                        return parsed;
                    }

                    // A malformed body will not fix itself on a retry.
                    failure = "unparseable response";
                    break;
                }

                failure = error ?? ("HTTP " + status);
                // A 401 means the token expired or was revoked; the CLI refreshes it, and
                // retrying now would only fail again. Only throttling and server errors are
                // worth a second attempt.
                var transient = status == 0 || status == 429 || status >= 500;
                if (!transient) break;

                Log.Debug("usage-api: attempt " + (attempt + 1) + " failed (" + failure + ")");
            }

            lock (_gate)
            {
                _lastError = failure;
                _cached = null;
                _cachedAt = DateTime.UtcNow;
                Log.Debug("usage-api: giving up (" + failure + ")");
                return StaleFallback();
            }
        }

        /// <summary>Caller must hold <see cref="_gate"/>.</summary>
        private ApiUsage StaleFallback()
        {
            if (_lastGood == null) return null;

            var age = DateTime.UtcNow - _lastGoodAt;
            if (age > StaleOk) return null;

            // Same numbers, but carrying their age so the UI can dim them instead of
            // presenting a ten-minute-old reading as current.
            return new ApiUsage
            {
                FiveHour = _lastGood.FiveHour,
                SevenDay = _lastGood.SevenDay,
                WeeklyScoped = _lastGood.WeeklyScoped,
                AgeMs = (long)age.TotalMilliseconds,
            };
        }

        /// <summary>
        /// Extracts the windows from the payload.
        ///
        /// Current format: a `limits[]` array with kind = session | weekly_all | weekly_scoped,
        /// the scoped one naming its model through scope.model.display_name. The legacy
        /// top-level fields are still read as a fallback, since an account can be served by an
        /// older shape.
        /// </summary>
        public static ApiUsage Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using (var document = JsonDocument.Parse(json))
                {
                    return Parse(document.RootElement);
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        internal static ApiUsage Parse(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) return null;

            var usage = new ApiUsage();
            var scoped = new List<ScopedBucket>();

            if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
            {
                foreach (var limit in limits.EnumerateArray())
                {
                    var window = ParseWindow(limit);
                    if (window == null) continue;

                    switch (ReadString(limit, "kind"))
                    {
                        case "session":
                            usage.FiveHour = window;
                            break;
                        case "weekly_all":
                            usage.SevenDay = window;
                            break;
                        case "weekly_scoped":
                            var label = ReadScopeLabel(limit);
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
            }

            if (usage.FiveHour == null && root.TryGetProperty("five_hour", out var fiveHour))
                usage.FiveHour = ParseWindow(fiveHour);

            if (usage.SevenDay == null && root.TryGetProperty("seven_day", out var sevenDay))
                usage.SevenDay = ParseWindow(sevenDay);

            if (scoped.Count == 0)
            {
                // Legacy: per-model weekly windows in fixed top-level fields.
                AddLegacyScoped(root, scoped, "Opus", "seven_day_opus");
                AddLegacyScoped(root, scoped, "Sonnet", "seven_day_sonnet");
            }

            if (scoped.Count > 0) usage.WeeklyScoped = scoped;
            return usage;
        }

        private static void AddLegacyScoped(JsonElement root, List<ScopedBucket> scoped, string label, string key)
        {
            if (!root.TryGetProperty(key, out var element)) return;
            var window = ParseWindow(element);
            if (window == null) return;

            scoped.Add(new ScopedBucket { Label = label, UsedPct = window.UsedPct, ResetsAt = window.ResetsAt });
        }

        /// <summary>
        /// One API window. Here the percentage always arrives as 0..100 (unlike the statusline
        /// payload, which varies), so it is divided rather than sniffed.
        /// </summary>
        private static LimitWindow ParseWindow(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;

            double? percent = null;
            if (element.TryGetProperty("utilization", out var utilization) &&
                utilization.ValueKind == JsonValueKind.Number &&
                utilization.TryGetDouble(out var fromUtilization))
            {
                percent = fromUtilization;
            }
            else if (element.TryGetProperty("percent", out var percentProperty) &&
                     percentProperty.ValueKind == JsonValueKind.Number &&
                     percentProperty.TryGetDouble(out var fromPercent))
            {
                percent = fromPercent;
            }

            if (!percent.HasValue || double.IsNaN(percent.Value)) return null;

            return new LimitWindow
            {
                UsedPct = Math.Max(0, Math.Min(1, percent.Value / 100.0)),
                ResetsAt = ReadString(element, "resets_at"),
            };
        }

        private static string ReadScopeLabel(JsonElement limit)
        {
            if (!limit.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object) return null;
            if (!scope.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object) return null;
            return ReadString(model, "display_name");
        }

        private static string ReadString(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>Test seam: drops every cached state.</summary>
        internal void ResetCache()
        {
            lock (_gate)
            {
                _cached = null;
                _cachedAt = DateTime.MinValue;
                _lastGood = null;
                _lastGoodAt = DateTime.MinValue;
                _lastError = null;
            }
        }
    }
}
