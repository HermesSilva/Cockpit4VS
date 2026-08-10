using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Session;
using Tootega.Cockpit.Stats;
using Tootega.Cockpit.Util;
using CockpitSession = Tootega.Cockpit.Session.Session;

namespace Tootega.Cockpit.Host
{
    /// <summary>
    /// What the account has left, and where that figure came from.
    ///
    /// Three sources, in a deliberate order of trust:
    ///
    /// The OAuth API is the same source the CLI's own /usage reads, so its percentages match
    /// exactly. The statusline cache is real too, but only while it is fresh — a stale
    /// rate_limits payload would show yesterday's percentage as today's. Local accounting is
    /// the last resort and carries no percentage at all, because this machine cannot see the
    /// user's other devices or claude.ai; it reports accumulated cost and tokens instead of
    /// implying it knows the limit.
    ///
    /// The distinction is carried through to the UI rather than smoothed over: a real
    /// percentage and a local estimate are different claims, and presenting the second as the
    /// first is the failure that makes a usage panel worse than none.
    /// </summary>
    internal sealed class UsageMonitor : IDisposable
    {
        /// <summary>
        /// Beyond this the statusline cache is not trusted. It is written when Claude renders
        /// the statusline, which may not have happened for hours.
        /// </summary>
        private const long CacheMaxAgeMs = 15 * 60 * 1000;

        private const int RefreshIntervalMs = 120_000;

        private readonly UsageApi _api = new UsageApi();
        private readonly UsageAggregator _local = new UsageAggregator();
        private readonly DailyTokensCounter _tokens = new DailyTokensCounter();
        private readonly OtelReceiver _otel = new OtelReceiver();

        private readonly StateStore _state;
        private readonly Func<string> _claudePath;
        private readonly Func<IEnumerable<KeyValuePair<string, CockpitSession>>> _sessions;
        private readonly Action<HostMessage, string> _post;

        private LimitsBlock _limits;
        private string _limitsSource = "estimate";
        private string _usageSource = "estimate";
        private List<ScopedBucket> _scoped;

        private Timer _timer;
        private bool _disposed;

        public UsageMonitor(StateStore state, Func<string> claudePath,
                            Func<IEnumerable<KeyValuePair<string, CockpitSession>>> sessions,
                            Action<HostMessage, string> post)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _claudePath = claudePath ?? throw new ArgumentNullException(nameof(claudePath));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _post = post ?? throw new ArgumentNullException(nameof(post));
        }

        /// <summary>
        /// The telemetry receiver, off unless the user asked for it.
        ///
        /// Opt-in because it opens a local socket, and because the CLI sends it conversation
        /// content unless told otherwise — which is why the receiver pins the two prompt-logging
        /// switches off and discards the log stream entirely.
        /// </summary>
        public void StartTelemetry()
        {
            try
            {
                _otel.Start();
                Log.Info("Telemetry receiver listening on " + _otel.Endpoint + ".");
            }
            catch (Exception ex)
            {
                Log.Error("otel: the receiver could not start", ex);
            }
        }

        /// <summary>Starts the periodic refresh, once.</summary>
        public void Start()
        {
            if (_timer != null) return;

            _timer = new Timer(_ => Refresh(false), null, 0, RefreshIntervalMs);
        }

        private void Refresh(bool force)
        {
            // The timer's thread is not the place for an HTTP call chain to be awaited; failures
            // are logged inside, and a missed refresh costs nothing — the next one is two
            // minutes away.
            _ = RefreshAsync(force);
        }

        public async Task RefreshAsync(bool force)
        {
            var before = _usageSource;

            try
            {
                await ResolveAsync(force);

                // Losing the real percentage is worth a line: it is the difference between the
                // panel showing the account's own figure and showing a local guess.
                if (before != _usageSource)
                {
                    var why = _api.Diagnostics().LastError;
                    Log.Debug("usage source: " + before + " -> " + _usageSource +
                              (string.IsNullOrEmpty(why) ? string.Empty : " (" + why + ")"));
                }

                ApplyToSessions();
            }
            catch (Exception ex)
            {
                Log.Debug("usage: refresh failed: " + ex.Message);
            }
        }

        private async Task ResolveAsync(bool force)
        {
            var api = await _api.FetchAsync(force);
            var cached = StatuslineCache.Read();

            // The local scan only runs when the two real sources came up short, because it reads
            // every transcript on the machine.
            var local = Usable(api) || Fresh(cached)
                ? null
                : await _local.ComputeAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var chosen = Select(api, cached, local);

            _limits = chosen.Limits;
            _scoped = chosen.Scoped;
            _limitsSource = chosen.LimitsSource;
            _usageSource = chosen.UsageSource;
        }

        /// <summary>The window a source has to be within to be believed.</summary>
        internal static bool Fresh(RealLimits cached)
        {
            if (cached == null) return false;
            if (cached.FiveHour == null && cached.SevenDay == null) return false;

            // No timestamp at all is treated as fresh: the payload predates the field, and
            // discarding a real reading over a missing timestamp would lose the percentage.
            return cached.AgeMs == null || cached.AgeMs < CacheMaxAgeMs;
        }

        internal static bool Usable(ApiUsage api)
        {
            return api != null && (api.FiveHour != null || api.SevenDay != null);
        }

        /// <summary>
        /// Picks the source, in the order of trust described on the class.
        ///
        /// Pure and internal so the choice can be tested without a network, a token or a
        /// machine full of transcripts — the ordering is the whole point of this class, and it
        /// is invisible when wrong.
        /// </summary>
        internal static (LimitsBlock Limits, List<ScopedBucket> Scoped, string LimitsSource, string UsageSource)
            Select(ApiUsage api, RealLimits cached, LocalUsage local)
        {
            if (Usable(api))
            {
                return (new LimitsBlock { FiveHour = api.FiveHour, SevenDay = api.SevenDay },
                        api.WeeklyScoped, "real", "api");
            }

            if (Fresh(cached))
            {
                return (new LimitsBlock { FiveHour = cached.FiveHour, SevenDay = cached.SevenDay },
                        cached.WeeklyScoped, "real", "statusline");
            }

            if (local == null) return (null, null, "estimate", "estimate");

            // No percentage on purpose: there is no limit to compare against, and inventing one
            // would be the least honest thing this class could do.
            return (new LimitsBlock
            {
                FiveHour = new LimitWindow { Usd = local.FiveHourUsd, Tokens = local.FiveHourTokens },
                SevenDay = new LimitWindow { Usd = local.SevenDayUsd, Tokens = local.SevenDayTokens },
            }, null, "estimate", "estimate");
        }

        /// <summary>
        /// Pushes the limits into every open conversation's statistics.
        ///
        /// Each session shows them in its own context panel, so they all have to be told —
        /// otherwise the tab the user is not looking at keeps yesterday's figure and contradicts
        /// the one they are.
        /// </summary>
        private void ApplyToSessions()
        {
            foreach (var entry in _sessions())
            {
                entry.Value.ApplyLimits(_limits, _limitsSource);
                _post(HostMessages.Stats(entry.Value.Snapshot()), entry.Key);
            }
        }

        /// <summary>
        /// Everything the Usage panel shows, gathered on the click.
        ///
        /// Fetched fresh rather than served from the periodic refresh: the user opened the panel
        /// precisely to see where they stand now.
        /// </summary>
        public async Task SendAsync(string tabId)
        {
            try
            {
                await RefreshAsync(true);

                var account = await AuthStatus.FetchAsync(_claudePath());
                var cache = StatuslineCache.Read();

                // Session flags — fast mode, the model label, effort — only ever come from the
                // statusline payload. When the cache is old they are marked stale instead of
                // being presented as current.
                if (account != null && cache?.Session != null)
                {
                    account.Session = new UsageSession
                    {
                        FastMode = cache.Session.FastMode,
                        // The CLI's own label, never derived from the id.
                        ModelDisplay = cache.Session.ModelDisplay,
                        Effort = cache.Session.Effort,
                        OutputStyle = cache.Session.OutputStyle,
                        Kind = cache.Session.Kind,
                        Stale = cache.AgeMs != null && cache.AgeMs >= CacheMaxAgeMs,
                    };
                }

                var local = await _local.ComputeAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var tokens = await _tokens.ComputeAsync();

                _post(HostMessages.UsageData(new UsageData
                {
                    Account = account,
                    Buckets = new UsageBuckets
                    {
                        FiveHour = ToBucket(_limits?.FiveHour),
                        SevenDay = ToBucket(_limits?.SevenDay),
                        WeeklyScoped = _scoped ?? cache?.WeeklyScoped,
                    },
                    Source = _usageSource,
                    // Falling back to the estimate is never silent: the panel says why the real
                    // source did not answer.
                    SourceError = _usageSource == "estimate" ? _api.Diagnostics().LastError : null,
                    TrackingEnabled = StatuslineInstaller.IsEnabled(),
                    Breakdown = local.Breakdown,
                    Attribution = local.Attribution,
                    Tokens = tokens,
                    Otel = _otel.IsRunning ? _otel.Stats() : null,
                    GeneratedAt = DateTime.UtcNow.ToString("o"),
                }), tabId);
            }
            catch (Exception ex)
            {
                Log.Debug("usage: could not build the panel: " + ex.Message);
            }
        }

        private static UsageBucket ToBucket(LimitWindow window)
        {
            if (window == null) return null;

            return new UsageBucket
            {
                UsedPct = window.UsedPct,
                ResetsAt = window.ResetsAt,
                Tokens = window.Tokens,
                Usd = window.Usd,
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _timer?.Dispose();
            _timer = null;
            _otel.Dispose();
        }
    }
}
