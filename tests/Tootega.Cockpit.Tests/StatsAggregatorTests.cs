using System.Linq;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Stats;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// The aggregator is the product's differentiator, so these tests are mostly about
    /// honesty rather than arithmetic: a real cost must beat our estimate, an unmeasurable
    /// figure must be omitted rather than guessed, and the panel must not read zero while a
    /// turn is already in flight.
    /// </summary>
    public class StatsAggregatorTests
    {
        public StatsAggregatorTests()
        {
            CostModel.ResetDiscoveredContexts();
        }

        private static StatsAggregator Fresh() => new StatsAggregator(0);

        private static ClaudeEvent Event(string json) => Json.TryDeserialize<ClaudeEvent>(json);

        /// <summary>A message_start, which fixes the prompt size for the turn in flight.</summary>
        private static ClaudeEvent MessageStart(long input, long create, long read, string model = "claude-opus-5")
        {
            return Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\",\"message\":{" +
                         "\"id\":\"m1\",\"model\":\"" + model + "\",\"usage\":{" +
                         "\"input_tokens\":" + input + ",\"cache_creation_input_tokens\":" + create +
                         ",\"cache_read_input_tokens\":" + read + "}}}}");
        }

        /// <summary>The assistant event, which consolidates the turn into the totals.</summary>
        private static ClaudeEvent AssistantUsage(long input, long create, long read, long output,
                                                  string model = "claude-opus-5")
        {
            return Event("{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"model\":\"" + model +
                         "\",\"content\":[],\"usage\":{\"input_tokens\":" + input +
                         ",\"cache_creation_input_tokens\":" + create +
                         ",\"cache_read_input_tokens\":" + read +
                         ",\"output_tokens\":" + output + "}}}");
        }

        // --- Model and limit ---

        [Fact]
        public void InitIsAuthoritativeAndSetsTheLimit()
        {
            var stats = Fresh();

            var snapshot = stats.Ingest(Event(
                "{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-sonnet-4-5[1m]\",\"permissionMode\":\"plan\"}"));

            Assert.Equal("claude-sonnet-4-5[1m]", snapshot.Model);
            Assert.Equal(1_000_000, snapshot.ContextLimit);
            Assert.Equal("plan", snapshot.Mode);
        }

        [Fact]
        public void PerMessageModelDoesNotDowngradeTheLimit()
        {
            // The API id arrives WITHOUT the [1m] suffix. Letting it win would silently drop a
            // 1M session's meter to 200K mid-conversation.
            var stats = Fresh();
            stats.Ingest(Event("{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-sonnet-4-5[1m]\"}"));

            var snapshot = stats.Ingest(AssistantUsage(10, 0, 0, 5, "claude-sonnet-4-5"));

            Assert.Equal("claude-sonnet-4-5[1m]", snapshot.Model);
            Assert.Equal(1_000_000, snapshot.ContextLimit);
        }

        [Fact]
        public void AConfiguredLimitIsNotOverriddenByTheModel()
        {
            var stats = new StatsAggregator(123_456);

            var snapshot = stats.Ingest(Event("{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-fable-5\"}"));

            Assert.Equal(123_456, snapshot.ContextLimit);
        }

        [Fact]
        public void RefreshPicksUpADiscoveredContextMidSession()
        {
            // Discovery can answer after the session started; without this the meter would
            // stay wrong for the rest of the session.
            var stats = Fresh();
            stats.Ingest(Event("{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"mystery-model\"}"));
            Assert.Equal(200_000, stats.Snapshot().ContextLimit);

            CostModel.RegisterModelContext("mystery-model", 400_000);

            Assert.True(stats.RefreshContextLimit());
            Assert.Equal(400_000, stats.Snapshot().ContextLimit);
            // Idempotent: nothing changed the second time.
            Assert.False(stats.RefreshContextLimit());
        }

        // --- Turn in flight ---

        [Fact]
        public void ShowsTheTurnInFlightBeforeItConsolidates()
        {
            // The bug this prevents: a slow cold first turn showing 0 tokens while the context
            // is already full.
            var stats = Fresh();

            var snapshot = stats.Ingest(MessageStart(1000, 500, 200));

            Assert.Equal(1700, snapshot.ContextUsed);
            Assert.Equal(1000, snapshot.InputTokens);
            Assert.Equal(500, snapshot.CacheCreateTokens);
            Assert.Equal(200, snapshot.CacheReadTokens);
            // Not consolidated yet.
            Assert.Equal(0, snapshot.TurnCount);
        }

        [Fact]
        public void OutputDeltasDoNotDisturbTheContext()
        {
            // message_delta carries an INCREMENTAL input_tokens (zero mid-stream). Applying it
            // would zero the displayed context and make the number blink.
            var stats = Fresh();
            stats.Ingest(MessageStart(1000, 0, 0));

            var snapshot = stats.Ingest(Event(
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"message_delta\"," +
                "\"usage\":{\"input_tokens\":0,\"output_tokens\":42}}}"));

            Assert.Equal(1000, snapshot.ContextUsed);
            Assert.Equal(1000, snapshot.InputTokens);
            Assert.Equal(42, snapshot.OutputTokens);
        }

        [Fact]
        public void OutputOnlyEverGrowsWithinATurn()
        {
            var stats = Fresh();
            stats.Ingest(MessageStart(100, 0, 0));
            stats.Ingest(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_delta\",\"usage\":{\"output_tokens\":50}}}"));

            // A malformed or out-of-order delta must not walk the number backwards.
            var snapshot = stats.Ingest(Event(
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"message_delta\",\"usage\":{\"output_tokens\":10}}}"));

            Assert.Equal(50, snapshot.OutputTokens);
        }

        [Fact]
        public void ConsolidatesTheTurnAndClearsTheInFlightCounters()
        {
            var stats = Fresh();
            stats.Ingest(MessageStart(1000, 500, 200));

            var snapshot = stats.Ingest(AssistantUsage(1000, 500, 200, 300));

            Assert.Equal(1, snapshot.TurnCount);
            // Counted once, not once for the partial and again for the final.
            Assert.Equal(1000, snapshot.InputTokens);
            Assert.Equal(300, snapshot.OutputTokens);
        }

        // --- Cost ---

        [Fact]
        public void EstimatesCostUntilTheCliReportsTheRealOne()
        {
            var stats = Fresh();
            stats.Ingest(Event("{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-opus-5\"}"));

            var estimated = stats.Ingest(AssistantUsage(1_000_000, 0, 0, 0)).SessionCostUsd;
            Assert.Equal(5, estimated, 6);
            Assert.True(stats.Snapshot().CostIsEstimate);

            var snapshot = stats.Ingest(Event("{\"type\":\"result\",\"subtype\":\"success\",\"total_cost_usd\":9.5}"));

            // A real number always beats our table.
            Assert.False(snapshot.CostIsEstimate);
            Assert.Equal(9.5, snapshot.SessionCostUsd, 6);
        }

        [Fact]
        public void OnceRealTheEstimateStopsAccumulating()
        {
            var stats = Fresh();
            stats.Ingest(Event("{\"type\":\"result\",\"subtype\":\"success\",\"total_cost_usd\":2}"));

            var snapshot = stats.Ingest(AssistantUsage(1_000_000, 0, 0, 0));

            // Mixing a real total with added estimates would double-count.
            Assert.Equal(2, snapshot.SessionCostUsd, 6);
            Assert.False(snapshot.CostIsEstimate);
        }

        [Fact]
        public void LastTurnCostIsTheDeltaOfTheRealTotal()
        {
            var stats = Fresh();
            stats.Ingest(Event("{\"type\":\"result\",\"total_cost_usd\":2}"));

            var snapshot = stats.Ingest(Event("{\"type\":\"result\",\"total_cost_usd\":3.5}"));

            Assert.Equal(1.5, snapshot.LastTurnCostUsd, 6);
        }

        [Fact]
        public void ReportsCacheSavingsAgainstTheInputPrice()
        {
            var stats = Fresh();
            stats.Ingest(Event("{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-opus-5\"}"));

            var snapshot = stats.Ingest(AssistantUsage(0, 0, 1_000_000, 0));

            // opus: input 5, cacheRead 0.5 — so a million read tokens saved 4.5.
            Assert.Equal(4.5, snapshot.CacheSavingsUsd.Value, 6);
        }

        [Fact]
        public void NoCacheReadMeansNoSavingsFigureAtAll()
        {
            // Zero would read as a fact; absent reads as "nothing to show".
            var snapshot = Fresh().Ingest(AssistantUsage(100, 0, 0, 10));

            Assert.Null(snapshot.CacheSavingsUsd);
        }

        // --- Hit rate ---

        [Fact]
        public void CumulativeHitRateIsReadOverThePromptTotal()
        {
            var stats = Fresh();

            var snapshot = stats.Ingest(AssistantUsage(100, 100, 800, 0));

            Assert.Equal(0.8, snapshot.CacheHitRate, 6);
            Assert.Equal(0.8, snapshot.LastTurnHitRate.Value, 6);
        }

        [Fact]
        public void LastTurnHitRateIsAbsentBeforeAnyTurn()
        {
            Assert.Null(Fresh().Snapshot().LastTurnHitRate);
        }

        // --- Cache reset and compaction ---

        [Fact]
        public void DetectsACompactionWhenTheContextShrinks()
        {
            var stats = Fresh();
            stats.Ingest(AssistantUsage(0, 0, 100_000, 10));

            var snapshot = stats.Ingest(AssistantUsage(0, 0, 20_000, 10));

            Assert.Equal(1, snapshot.CompactionCount);
            var compaction = stats.TimelineSnapshot().Compactions.Single();
            Assert.Equal(100_000, compaction.Before);
            Assert.Equal(20_000, compaction.After);
            Assert.Equal(80_000, compaction.Saved);
        }

        [Fact]
        public void ASmallDropIsNotACompaction()
        {
            // Ordinary turn-to-turn variation must not be reported as the CLI condensing the
            // context.
            var stats = Fresh();
            stats.Ingest(AssistantUsage(0, 0, 100_000, 10));

            var snapshot = stats.Ingest(AssistantUsage(0, 0, 90_000, 10));

            Assert.Equal(0, snapshot.CompactionCount);
        }

        [Fact]
        public void TheFirstTurnIsNeverACompaction()
        {
            var snapshot = Fresh().Ingest(AssistantUsage(0, 0, 10, 10));

            Assert.Equal(0, snapshot.CompactionCount);
            Assert.Equal(0, snapshot.CacheResetCount);
        }

        [Fact]
        public void TracksThePeakContextAndItsCacheSize()
        {
            var stats = Fresh();
            stats.Ingest(AssistantUsage(0, 5_000, 95_000, 10));
            stats.Ingest(AssistantUsage(0, 0, 50_000, 10));

            var snapshot = stats.Snapshot();

            Assert.Equal(100_000, snapshot.PeakContextUsed.Value);
            Assert.Equal(100_000, snapshot.PeakCacheTokens.Value);
        }

        // --- Per-model breakdown ---

        [Fact]
        public void AccumulatesPerModelAcrossAModelSwitch()
        {
            var stats = Fresh();
            stats.Ingest(Event("{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-opus-5\"}"));
            stats.Ingest(AssistantUsage(100, 0, 0, 10));

            stats.SetModel("claude-haiku-4-5", true);
            var snapshot = stats.Ingest(AssistantUsage(200, 0, 0, 20));

            Assert.Equal(2, snapshot.PerModel.Count);
            var opus = snapshot.PerModel.Single(m => m.Model == "claude-opus-5");
            var haiku = snapshot.PerModel.Single(m => m.Model == "claude-haiku-4-5");
            Assert.Equal(100, opus.InputTokens);
            Assert.Equal(200, haiku.InputTokens);
            Assert.Equal(1, opus.Turns);
            Assert.Equal(1, haiku.Turns);
        }

        // --- Decisions and denials ---

        [Fact]
        public void CountsUserDecisionsPerTool()
        {
            var stats = Fresh();
            stats.RecordDecision("Bash", "allow");
            stats.RecordDecision("Bash", "allow_always");
            stats.RecordDecision("Bash", "deny", "too risky");

            var decision = stats.Snapshot().ToolAcceptance.Single();

            Assert.Equal("Bash", decision.Tool);
            Assert.Equal(1, decision.Allow);
            Assert.Equal(1, decision.AllowAlways);
            Assert.Equal(1, decision.Deny);

            var denial = stats.Snapshot().RecentDenials.Single();
            Assert.Equal("user", denial.Source);
            Assert.Equal("too risky", denial.Reason);
        }

        [Fact]
        public void EngineDenialsPickUpTheReasonFromTheErrorResult()
        {
            // The result event names the tool but not the reason; the reason arrives in the
            // error tool_result of the same tool_use_id.
            var stats = Fresh();
            stats.Ingest(Event("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[" +
                               "{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"is_error\":true," +
                               "\"content\":\"write outside the workspace is not allowed\"}]}}"));

            var snapshot = stats.Ingest(Event("{\"type\":\"result\",\"permission_denials\":[" +
                                              "{\"tool_name\":\"Write\",\"tool_use_id\":\"t1\"}]}"));

            var denial = snapshot.RecentDenials.Single();
            Assert.Equal("engine", denial.Source);
            Assert.Equal("Write", denial.Tool);
            Assert.Contains("outside the workspace", denial.Reason);
        }

        [Fact]
        public void EngineDenialsAreDeduplicatedByToolUseId()
        {
            // A turn's result can repeat denials already counted.
            var stats = Fresh();
            const string result = "{\"type\":\"result\",\"permission_denials\":[{\"tool_name\":\"Bash\",\"tool_use_id\":\"t1\"}]}";

            stats.Ingest(Event(result));
            var snapshot = stats.Ingest(Event(result));

            Assert.Single(snapshot.RecentDenials);
            Assert.Equal(1, snapshot.ToolAcceptance.Single().Deny);
        }

        [Fact]
        public void MostRecentDenialComesFirst()
        {
            var stats = Fresh();
            stats.RecordDecision("A", "deny");
            stats.RecordDecision("B", "deny");

            Assert.Equal("B", stats.Snapshot().RecentDenials[0].Tool);
        }

        [Fact]
        public void NoDecisionsMeansNoBlockAtAll()
        {
            var snapshot = Fresh().Snapshot();

            Assert.Null(snapshot.ToolAcceptance);
            Assert.Null(snapshot.RecentDenials);
        }

        // --- Skills ---

        [Fact]
        public void ASkillIsOnlyMarkedLoadedWhenItsBodyEntersTheContext()
        {
            // Triggering is not loading. "Execute skill:" runs a skill that injects nothing.
            var stats = Fresh();
            stats.Ingest(Event("{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[" +
                               "{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Skill\",\"input\":{\"skill\":\"/caveman\"}}]}}"));

            stats.Ingest(Event("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[" +
                               "{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"Execute skill: caveman\"}]}}"));

            Assert.Null(stats.Snapshot().Skills);
            Assert.Empty(stats.TakeSkillLoads());
        }

        [Fact]
        public void MeasuresTheSkillBodyFromTheMessageThatFollowsTheLaunch()
        {
            var stats = Fresh();
            stats.Ingest(Event("{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[" +
                               "{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Skill\",\"input\":{\"skill\":\"caveman\"}}]}}"));

            stats.Ingest(Event("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[" +
                               "{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"Launching skill: caveman\"}]}}"));

            // The body has no fixed header, so the window is positional: the first text block
            // after the launch. Matching a header would leave built-ins with no number.
            var body = new string('x', 400);
            stats.Ingest(Event("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[" +
                               "{\"type\":\"text\",\"text\":\"" + body + "\"}]}}"));

            var skill = stats.Snapshot().Skills.Single();
            Assert.Equal("caveman", skill.Name);
            Assert.True(skill.Active);
            Assert.Equal("model", skill.InvokedBy);
            Assert.Equal(100, skill.ActiveTokens.Value);   // 400 chars / 4
        }

        [Fact]
        public void TheBodyWindowClosesWhenTheModelSpeaksAgain()
        {
            // Otherwise a later queued message would be measured as if it were the SKILL.md.
            var stats = Fresh();
            stats.Ingest(Event("{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[" +
                               "{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Skill\",\"input\":{\"skill\":\"caveman\"}}]}}"));
            stats.Ingest(Event("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[" +
                               "{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"Launching skill: caveman\"}]}}"));

            stats.Ingest(Event("{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}"));
            stats.Ingest(Event("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[" +
                               "{\"type\":\"text\",\"text\":\"" + new string('y', 4000) + "\"}]}}"));

            // Loaded is known; the size is not, and is left absent rather than invented.
            var skill = stats.Snapshot().Skills.Single();
            Assert.True(skill.Active);
            Assert.Null(skill.ActiveTokens);
        }

        [Fact]
        public void ListingMetadataComesFromContextUsage()
        {
            var stats = Fresh();

            stats.ApplyContextUsage(new ContextUsageInfo
            {
                ListingTokens = 1928,
                TotalSkills = 14,
                IncludedSkills = 11,
                Skills = new System.Collections.Generic.List<ContextUsageSkill>
                {
                    new ContextUsageSkill { Name = "caveman", Source = "userSettings", Tokens = 120 },
                    new ContextUsageSkill { Name = "dataviz", Source = "built-in", Tokens = 300 },
                },
            });

            var snapshot = stats.Snapshot();
            Assert.Equal(1928, snapshot.SkillsListingTokens);
            Assert.Equal(14, snapshot.SkillsTotal);
            Assert.Equal(11, snapshot.SkillsListed);
            // Heaviest metadata first, since nothing is active yet.
            Assert.Equal("dataviz", snapshot.Skills[0].Name);
            Assert.True(snapshot.Skills[0].Listed);
        }

        [Fact]
        public void ActiveSkillsSortAheadOfMerelyListedOnes()
        {
            // Active is what actually weighs on the context.
            var stats = Fresh();
            stats.ApplyContextUsage(new ContextUsageInfo
            {
                Skills = new System.Collections.Generic.List<ContextUsageSkill>
                {
                    new ContextUsageSkill { Name = "heavy-listing", Tokens = 5000 },
                    new ContextUsageSkill { Name = "light", Tokens = 10 },
                },
            });
            stats.MarkSkillActive("light", "user");

            Assert.Equal("light", stats.Snapshot().Skills[0].Name);
        }

        [Fact]
        public void AnActiveSkillTurnedOffStaysVisibleAsResident()
        {
            // There is no way to unload a body from a live context, so the panel must keep
            // showing it rather than pretend it is gone.
            var stats = Fresh();
            stats.MarkSkillActive("caveman", "model", 500);
            stats.SetSkillOverrides(new System.Collections.Generic.Dictionary<string, string>
            {
                ["caveman"] = SkillOverrides.Off,
            });

            var skill = stats.Snapshot().Skills.Single();
            Assert.True(skill.Active);
            Assert.False(skill.Listed);
            Assert.Equal(SkillOverrides.Off, skill.Override);
        }

        [Fact]
        public void KeepsTheLargestSightingOfASkillBody()
        {
            // A hook may re-inject a short summary after the full body; what weighs is the body.
            var stats = Fresh();
            stats.MarkSkillActive("caveman", "hook", 900);
            stats.MarkSkillActive("caveman", "hook", 50);

            Assert.Equal(900, stats.Snapshot().Skills.Single().ActiveTokens.Value);
        }

        // --- Hooks ---

        [Fact]
        public void AccountsHookInjectedContextPerHook()
        {
            var stats = Fresh();
            var text = new string('z', 800);

            stats.Ingest(Event("{\"type\":\"system\",\"subtype\":\"hook_response\",\"hook_name\":\"SessionStart:startup\"," +
                               "\"hook_event\":\"SessionStart\",\"output\":\"" + text + "\"}"));

            var injection = stats.Snapshot().HookInjections.Single();
            Assert.Equal("SessionStart:startup", injection.Hook);
            Assert.Equal("SessionStart", injection.Event);
            Assert.Equal(1, injection.Count);
            Assert.Equal(200, injection.Tokens);
        }

        [Fact]
        public void OnlyTheFirstInjectionOfAHookReachesTheTimeline()
        {
            // A UserPromptSubmit hook fires on every prompt; repeating the marker would drown
            // the conversation while saying nothing new. The repeats are counted instead.
            var stats = Fresh();
            const string line = "{\"type\":\"system\",\"subtype\":\"hook_response\",\"hook_name\":\"OnPrompt\",\"output\":\"abcd\"}";

            stats.Ingest(Event(line));
            Assert.Single(stats.TakeHookLoads());

            stats.Ingest(Event(line));
            stats.Ingest(Event(line));
            Assert.Empty(stats.TakeHookLoads());

            var injection = stats.Snapshot().HookInjections.Single();
            Assert.Equal(3, injection.Count);
        }

        [Fact]
        public void RecognisesASkillLoadedByAHookThroughItsBody()
        {
            // A hook emits no Skill tool_use and goes through no /name, so matching the
            // injected content against SKILL.md on disk is the only signal that exists.
            var stats = Fresh();
            stats.SetSkillBodyResolver(text => text.Contains("caveman-marker") ? "caveman" : null);

            stats.Ingest(Event("{\"type\":\"system\",\"subtype\":\"hook_response\",\"hook_name\":\"SessionStart\"," +
                               "\"output\":\"caveman-marker body text\"}"));

            var skill = stats.Snapshot().Skills.Single();
            Assert.Equal("caveman", skill.Name);
            Assert.Equal("hook", skill.InvokedBy);
            Assert.Equal("caveman", stats.Snapshot().HookInjections.Single().Skill);
        }

        [Fact]
        public void AnEmptyHookOutputIsNotAnInjection()
        {
            var stats = Fresh();

            stats.Ingest(Event("{\"type\":\"system\",\"subtype\":\"hook_response\",\"hook_name\":\"X\",\"output\":\"\"}"));

            Assert.Null(stats.Snapshot().HookInjections);
        }

        // --- Limits ---

        [Fact]
        public void StatuslineLimitsAreLabelledAsSuch()
        {
            var stats = Fresh();

            stats.SetLimits(new LimitsBlock { FiveHour = new LimitWindow { UsedPct = 0.5 } }, "real");

            Assert.Equal("statusline", stats.Snapshot().LimitsSource);
        }

        [Fact]
        public void StreamLimitsWinOnStatusButKeepTheBasePercentage()
        {
            // At low usage the stream carries status and reset but no utilization; dropping to
            // no percentage at all would lose information the statusline already had.
            var stats = Fresh();
            stats.SetLimits(new LimitsBlock { FiveHour = new LimitWindow { UsedPct = 0.42, Usd = 3 } }, "real");

            stats.SetStreamLimit("fiveHour", new LimitWindow { Status = "allowed_warning", ResetsAt = "2026-08-10T00:00:00Z" });

            var window = stats.Snapshot().Limits.FiveHour;
            Assert.Equal(0.42, window.UsedPct.Value, 6);
            Assert.Equal("allowed_warning", window.Status);
            Assert.Equal("2026-08-10T00:00:00Z", window.ResetsAt);
            // Local cost always comes from the base.
            Assert.Equal(3, window.Usd.Value, 6);
        }

        [Fact]
        public void AStreamPercentageOverridesTheBase()
        {
            var stats = Fresh();
            stats.SetLimits(new LimitsBlock { SevenDay = new LimitWindow { UsedPct = 0.1 } });

            stats.SetStreamLimit("sevenDay", new LimitWindow { UsedPct = 0.9 });

            Assert.Equal(0.9, stats.Snapshot().Limits.SevenDay.UsedPct.Value, 6);
        }

        [Fact]
        public void EachStreamBucketArrivesSeparatelyWithoutClearingTheOther()
        {
            var stats = Fresh();

            stats.SetStreamLimit("fiveHour", new LimitWindow { UsedPct = 0.3 });
            stats.SetStreamLimit("sevenDay", new LimitWindow { UsedPct = 0.7 });

            var limits = stats.Snapshot().Limits;
            Assert.Equal(0.3, limits.FiveHour.UsedPct.Value, 6);
            Assert.Equal(0.7, limits.SevenDay.UsedPct.Value, 6);
            Assert.Equal("stream", stats.Snapshot().LimitsSource);
        }

        [Fact]
        public void WithNoSourceTheLimitsAreAnEstimate()
        {
            Assert.Equal("estimate", Fresh().Snapshot().LimitsSource);
        }

        // --- Cache life ---

        [Fact]
        public void CacheLifeIsAbsentUntilATurnHappens()
        {
            // A countdown from nothing would be a lie.
            var snapshot = Fresh().Snapshot();

            Assert.Equal(CostModel.CacheLifeMs, snapshot.CacheLifeMs.Value);
            Assert.Null(snapshot.CacheAgeMs);
            Assert.Null(snapshot.CacheExpiresAt);
            Assert.Null(snapshot.CacheAlive);
        }

        [Fact]
        public void ReportsCacheLifeAfterATurn()
        {
            var stats = Fresh();

            var snapshot = stats.Ingest(AssistantUsage(10, 0, 0, 5));

            Assert.True(snapshot.CacheAlive);
            Assert.True(snapshot.CacheExpiresInMs > 0);
            Assert.True(snapshot.CacheExpiresAt > 0);
        }

        // --- Active time ---

        [Fact]
        public void ActiveTimeExcludesIdleness()
        {
            var stats = Fresh();
            Assert.Equal(0, stats.Snapshot().ActiveMs.Value);

            stats.BeginTurn();
            System.Threading.Thread.Sleep(30);
            stats.EndTurn();

            var afterTurn = stats.Snapshot().ActiveMs.Value;
            Assert.True(afterTurn >= 20, "expected the worked time to be counted, got " + afterTurn);

            // Idle time between turns must not accumulate.
            System.Threading.Thread.Sleep(40);
            Assert.Equal(afterTurn, stats.Snapshot().ActiveMs.Value);
        }

        [Fact]
        public void BeginTurnIsIdempotent()
        {
            var stats = Fresh();
            stats.BeginTurn();
            System.Threading.Thread.Sleep(20);
            stats.BeginTurn();  // must not restart the stopwatch
            stats.EndTurn();

            Assert.True(stats.Snapshot().ActiveMs.Value >= 15);
        }

        // --- Persistence round trip ---

        [Fact]
        public void SurvivesAReopenWithItsNumbersIntact()
        {
            // The whole reason the store exists: the CLI does not re-emit old turns on resume.
            var original = Fresh();
            original.Ingest(Event("{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-opus-5\"}"));
            original.Ingest(AssistantUsage(1000, 200, 800, 300));
            original.RecordDecision("Bash", "deny", "nope");

            var persisted = original.Serialize("s1", @"D:\work");

            var restored = Fresh();
            restored.Hydrate(persisted);
            restored.MarkReopen();

            var snapshot = restored.Snapshot();
            Assert.Equal(1000, snapshot.InputTokens);
            Assert.Equal(800, snapshot.CacheReadTokens);
            Assert.Equal(300, snapshot.OutputTokens);
            Assert.Equal(1, snapshot.TurnCount);
            Assert.Equal(1, snapshot.ReopenCount);
            Assert.Equal("claude-opus-5", snapshot.Model);
            Assert.Single(snapshot.RecentDenials);
            // The context bar is restored immediately, before any new turn.
            Assert.Equal(2000, snapshot.ContextUsed);
        }

        [Fact]
        public void HydrateRebuildsTheLastTurnHitRate()
        {
            var persisted = new PersistedStats
            {
                Version = StatsStore.StatsVersion,
                SessionId = "s1",
                TurnCount = 4,
                LastContextUsed = 1000,
                LastCacheRead = 750,
            };

            var stats = Fresh();
            stats.Hydrate(persisted);

            Assert.Equal(0.75, stats.Snapshot().LastTurnHitRate.Value, 6);
        }

        [Fact]
        public void HydrateToleratesAMinimalState()
        {
            var stats = Fresh();

            stats.Hydrate(new PersistedStats { Version = StatsStore.StatsVersion, SessionId = "s1" });
            stats.Hydrate(null);

            Assert.Equal(0, stats.Snapshot().TurnCount);
        }

        [Fact]
        public void SerializeCarriesTheCwdForTheCacheKeeper()
        {
            // Without it the keeper cannot resume the context in the right folder.
            var persisted = Fresh().Serialize("s1", @"D:\work\project");

            Assert.Equal(@"D:\work\project", persisted.Cwd);
            Assert.Equal(StatsStore.StatsVersion, persisted.Version);
        }

        [Fact]
        public void KeepCacheAliveRoundTrips()
        {
            var stats = Fresh();
            stats.SetKeepCacheAlive(true);

            Assert.True(stats.Snapshot().KeepCacheAlive);
            Assert.True(stats.Serialize("s1").KeepCacheAlive);
        }
    }
}
