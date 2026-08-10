using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Stats;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// These stores exist because the numbers cannot be re-derived: the CLI does not re-emit
    /// old turns on --resume. So the properties under test are about not losing or corrupting
    /// what was recorded — a torn file is silently discarded, which loses a session's whole
    /// history.
    /// </summary>
    public class StatsPersistenceTests : IDisposable
    {
        private readonly string _root;

        public StatsPersistenceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "cockpit-stats-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
            }
        }

        // --- UsageKey ---

        private static JsonElement Entry(string json)
        {
            using (var document = JsonDocument.Parse(json))
            {
                return document.RootElement.Clone();
            }
        }

        [Fact]
        public void UsageKeyCombinesMessageIdAndRequestId()
        {
            var key = UsageKey.For(Entry("{\"message\":{\"id\":\"msg_1\"},\"requestId\":\"req_1\"}"));

            Assert.Equal("msg_1:req_1", key);
        }

        [Fact]
        public void UsageKeyIsStableAcrossLinesOfOneResponse()
        {
            // This is the whole point: one response spans several transcript lines that all
            // repeat the same usage. Summing them inflated the 7-day total by ~59%.
            var first = UsageKey.For(Entry("{\"message\":{\"id\":\"msg_1\",\"content\":[]},\"requestId\":\"r\"}"));
            var second = UsageKey.For(Entry("{\"message\":{\"id\":\"msg_1\",\"content\":[{}]},\"requestId\":\"r\"}"));

            Assert.Equal(first, second);
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"message\":{}}")]
        [InlineData("{\"message\":{\"id\":\"\"}}")]
        [InlineData("{\"message\":{\"id\":42}}")]
        [InlineData("{\"message\":\"not an object\"}")]
        public void UsageKeyIsNullWhenTheResponseCannotBeIdentified(string json)
        {
            // Null means "count this line": under-counting a real response is worse than
            // double-counting one we cannot identify.
            Assert.Null(UsageKey.For(Entry(json)));
        }

        [Fact]
        public void UsageKeyToleratesAMissingRequestId()
        {
            Assert.Equal("msg_1:", UsageKey.For(Entry("{\"message\":{\"id\":\"msg_1\"}}")));
        }

        // --- StatsStore ---

        private static PersistedStats Sample(string sessionId) => new PersistedStats
        {
            Version = StatsStore.StatsVersion,
            SessionId = sessionId,
            Cwd = @"D:\work",
            InputTokens = 100,
            OutputTokens = 50,
            CacheReadTokens = 900,
            SessionCostUsd = 1.5,
            TurnCount = 3,
            ContextLimit = 200000,
            UpdatedAt = DateTime.UtcNow.ToString("o"),
        };

        [Fact]
        public void PersistsAndReloadsASession()
        {
            using (var store = new StatsStore(_root))
            {
                store.Save(Sample("s1"));
                store.Flush();

                var loaded = new StatsStore(_root).Load("s1");

                Assert.NotNull(loaded);
                Assert.Equal(100, loaded.InputTokens);
                Assert.Equal(900, loaded.CacheReadTokens);
                Assert.Equal(1.5, loaded.SessionCostUsd);
                Assert.Equal(3, loaded.TurnCount);
                Assert.Equal(@"D:\work", loaded.Cwd);
            }
        }

        [Fact]
        public void ReadsBackPendingStateBeforeItIsFlushed()
        {
            // The debounce means the freshest numbers live in the buffer for a few seconds;
            // a reader that skipped it would see stale values.
            using (var store = new StatsStore(_root))
            {
                store.Save(Sample("s1"));

                Assert.Equal(100, store.Load("s1").InputTokens);
            }
        }

        [Fact]
        public void UnknownSessionLoadsAsNull()
        {
            using (var store = new StatsStore(_root))
            {
                Assert.Null(store.Load("nope"));
                Assert.Null(store.Load(null));
                Assert.Null(store.Load(string.Empty));
            }
        }

        [Fact]
        public void DiscardsStateFromAnIncompatibleVersion()
        {
            // Half-reading an old format would produce wrong numbers presented as right ones.
            var stale = Sample("s1");
            stale.Version = 999;
            File.WriteAllText(Path.Combine(_root, "s1.json"), Json.Serialize(stale), new UTF8Encoding(false));

            Assert.Null(new StatsStore(_root).Load("s1"));
        }

        [Fact]
        public void DiscardsCorruptState()
        {
            File.WriteAllText(Path.Combine(_root, "s1.json"), "{ not json", new UTF8Encoding(false));

            Assert.Null(new StatsStore(_root).Load("s1"));
        }

        [Fact]
        public void NormalisesUnsafeSessionIdsIntoFileNames()
        {
            // The id comes off disk, so it is normalised rather than trusted.
            using (var store = new StatsStore(_root))
            {
                store.Save(Sample(@"../../escape\me"));
                store.Flush();

                Assert.All(Directory.GetFiles(_root),
                    f => Assert.Equal(_root, Path.GetDirectoryName(f)));
            }
        }

        [Fact]
        public void LoadAllReturnsEverySession()
        {
            using (var store = new StatsStore(_root))
            {
                store.Save(Sample("s1"));
                store.Save(Sample("s2"));
                store.Flush();

                Assert.Equal(2, new StatsStore(_root).LoadAll().Count);
            }
        }

        [Fact]
        public void LoadAllOnAMissingDirectoryIsEmpty()
        {
            Assert.Empty(new StatsStore(Path.Combine(_root, "absent")).LoadAll());
        }

        [Fact]
        public void BumpCacheActivityMovesOnlyTheTimestamp()
        {
            using (var store = new StatsStore(_root))
            {
                store.Save(Sample("s1"));
                store.Flush();

                var when = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                store.BumpCacheActivity("s1", when);

                var loaded = new StatsStore(_root).Load("s1");
                Assert.Equal(when, loaded.LastTurnTs);
                // Everything else must be untouched — the keeper is not a turn.
                Assert.Equal(100, loaded.InputTokens);
                Assert.Equal(3, loaded.TurnCount);
            }
        }

        [Fact]
        public void BumpOnAnUnknownSessionDoesNothing()
        {
            using (var store = new StatsStore(_root))
            {
                store.BumpCacheActivity("ghost", 1);
                Assert.Empty(store.LoadAll());
            }
        }

        [Fact]
        public void KeepAliveLockIsExclusiveThenReleasable()
        {
            // Several VS instances sweep the same folder; without this they ping one session
            // twice on the same tick.
            using (var store = new StatsStore(_root))
            {
                Assert.True(store.AcquireKeepAliveLock("s1"));
                Assert.False(store.AcquireKeepAliveLock("s1"));

                store.ReleaseKeepAliveLock("s1");
                Assert.True(store.AcquireKeepAliveLock("s1"));
                store.ReleaseKeepAliveLock("s1");
            }
        }

        [Fact]
        public void AnotherInstanceCannotTakeAHeldLock()
        {
            using (var mine = new StatsStore(_root))
            using (var theirs = new StatsStore(_root))
            {
                Assert.True(mine.AcquireKeepAliveLock("s1"));
                Assert.False(theirs.AcquireKeepAliveLock("s1"));

                mine.ReleaseKeepAliveLock("s1");
                Assert.True(theirs.AcquireKeepAliveLock("s1"));
                theirs.ReleaseKeepAliveLock("s1");
            }
        }

        [Fact]
        public void DisposeFlushesPendingState()
        {
            using (var store = new StatsStore(_root))
            {
                store.Save(Sample("s1"));
            }

            Assert.NotNull(new StatsStore(_root).Load("s1"));
        }

        // --- Timeline decimation ---

        [Fact]
        public void ShortTimelinesAreLeftAlone()
        {
            var timeline = Enumerable.Range(0, 10).Select(i => new TimelineSample { Ts = i }).ToList();

            Assert.Same(timeline, StatsStore.CapTimeline(timeline));
        }

        [Fact]
        public void LongTimelinesKeepRecentSamplesDenseAndThinOldOnes()
        {
            var timeline = Enumerable.Range(0, 600).Select(i => new TimelineSample { Ts = i }).ToList();

            var capped = StatsStore.CapTimeline(timeline);

            Assert.True(capped.Count < timeline.Count);
            // The recent half — what the user is actually looking at — survives intact.
            Assert.Equal(Enumerable.Range(300, 300), capped.Skip(capped.Count - 300).Select(s => (int)s.Ts));
            // The oldest sample stays, so the session's start is not lost.
            Assert.Equal(0, capped[0].Ts);
        }

        [Fact]
        public void CapTimelineToleratesNull()
        {
            Assert.Null(StatsStore.CapTimeline(null));
        }

        // --- TaskTimings ---

        [Fact]
        public void ExposesAnAverageOnlyAfterEnoughSamples()
        {
            // An average of one sample is not an average; exposing it would calibrate the
            // gauge worse than the default does.
            using (var timings = new TaskTimings(_root))
            {
                timings.Record("opus", "high", "verbose", "tool:Read", 1000);
                timings.Record("opus", "high", "verbose", "tool:Read", 1000);
                timings.Flush();

                Assert.Empty(timings.Scoped("opus", "high", "verbose"));

                timings.Record("opus", "high", "verbose", "tool:Read", 1000);
                timings.Flush();

                Assert.Equal(1000, timings.Scoped("opus", "high", "verbose")["tool:Read"], 0);
            }
        }

        [Fact]
        public void SegmentsByModelEffortAndVerbosity()
        {
            // The same task takes very different times per model and effort, so one global
            // average would be wrong for every combination.
            using (var timings = new TaskTimings(_root))
            {
                for (var i = 0; i < 3; i++)
                {
                    timings.Record("opus", "high", "verbose", "assistant", 5000);
                    timings.Record("haiku", "low", "verbose", "assistant", 500);
                }
                timings.Flush();

                Assert.Equal(5000, timings.Scoped("opus", "high", "verbose")["assistant"], 0);
                Assert.Equal(500, timings.Scoped("haiku", "low", "verbose")["assistant"], 0);
                Assert.Empty(timings.Scoped("opus", "low", "verbose"));
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(50)]              // noise: a near-instant restart, not a task
        [InlineData(40 * 60 * 1000)]  // outlier: a wedged process, not a slow task
        [InlineData(double.NaN)]
        public void IgnoresSamplesOutsideThePlausibleRange(double ms)
        {
            using (var timings = new TaskTimings(_root))
            {
                for (var i = 0; i < 5; i++) timings.Record("opus", "high", "verbose", "assistant", ms);
                timings.Flush();

                Assert.Empty(timings.Scoped("opus", "high", "verbose"));
            }
        }

        [Fact]
        public void IgnoresSamplesWithNoModelOrType()
        {
            using (var timings = new TaskTimings(_root))
            {
                for (var i = 0; i < 5; i++)
                {
                    timings.Record(null, "high", "verbose", "assistant", 1000);
                    timings.Record("opus", "high", "verbose", null, 1000);
                }
                timings.Flush();

                Assert.Empty(timings.Scoped("opus", "high", "verbose"));
            }
        }

        [Fact]
        public void DefaultsMissingEffortAndVerbosity()
        {
            using (var timings = new TaskTimings(_root))
            {
                for (var i = 0; i < 3; i++) timings.Record("opus", null, null, "assistant", 1200);
                timings.Flush();

                Assert.Equal(1200, timings.Scoped("opus", "default", "verbose")["assistant"], 0);
            }
        }

        [Fact]
        public void MergesSamplesWrittenByAnotherWindow()
        {
            // Two windows share this file; a flush re-reads and merges rather than
            // overwriting what the other recorded.
            using (var first = new TaskTimings(_root))
            using (var second = new TaskTimings(_root))
            {
                for (var i = 0; i < 3; i++) first.Record("opus", "high", "verbose", "tool:Read", 1000);
                first.Flush();

                for (var i = 0; i < 3; i++) second.Record("opus", "high", "verbose", "tool:Edit", 2000);
                second.Flush();

                var scoped = new TaskTimings(_root).Scoped("opus", "high", "verbose");
                Assert.Equal(1000, scoped["tool:Read"], 0);
                Assert.Equal(2000, scoped["tool:Edit"], 0);
            }
        }

        [Fact]
        public void AverageMovesTowardsNewSamples()
        {
            using (var timings = new TaskTimings(_root))
            {
                for (var i = 0; i < 3; i++) timings.Record("opus", "high", "verbose", "assistant", 1000);
                timings.Flush();
                var before = timings.Scoped("opus", "high", "verbose")["assistant"];

                for (var i = 0; i < 5; i++) timings.Record("opus", "high", "verbose", "assistant", 3000);
                timings.Flush();
                var after = timings.Scoped("opus", "high", "verbose")["assistant"];

                Assert.True(after > before, after + " should exceed " + before);
                // An EMA adapts without discarding history, so it must not jump straight to
                // the new value either.
                Assert.True(after < 3000, after + " should stay below the newest sample");
            }
        }

        [Fact]
        public void DiscardsAStoreFromAnOlderKeyFormat()
        {
            // Averages recorded under a different segmentation are meaningless now.
            File.WriteAllText(Path.Combine(_root, "task-timings.json"),
                "{\"version\":1,\"stats\":{\"opus :: high :: assistant\":{\"ms\":1000,\"n\":50}}}",
                new UTF8Encoding(false));

            Assert.Empty(new TaskTimings(_root).Scoped("opus", "high", "verbose"));
        }

        // --- DailyTokens ---

        [Fact]
        public void CountsSentAndReceivedPerResponse()
        {
            var days = DailyTokensCounter.ParseFile(
                "{\"type\":\"assistant\",\"timestamp\":\"2026-08-09T10:00:00Z\",\"message\":{\"id\":\"m1\"," +
                "\"usage\":{\"input_tokens\":10,\"output_tokens\":5," +
                "\"cache_read_input_tokens\":100,\"cache_creation_input_tokens\":20}}}\n");

            var day = days.Values.Single();
            // sent = input + cache_read + cache_creation
            Assert.Equal(130, day.Sent);
            Assert.Equal(5, day.Received);
        }

        [Fact]
        public void CountsOneResponseOnceAcrossItsLines()
        {
            // The bug this prevents: the same usage repeated on every block of a response,
            // measured at ~59% inflation over seven days.
            const string line = "{\"type\":\"assistant\",\"timestamp\":\"2026-08-09T10:00:00Z\"," +
                                "\"requestId\":\"r1\",\"message\":{\"id\":\"m1\"," +
                                "\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}";

            var days = DailyTokensCounter.ParseFile(line + "\n" + line + "\n" + line + "\n");

            Assert.Equal(10, days.Values.Single().Sent);
        }

        [Fact]
        public void CountsDistinctResponsesSeparately()
        {
            var days = DailyTokensCounter.ParseFile(
                "{\"type\":\"assistant\",\"timestamp\":\"2026-08-09T10:00:00Z\",\"message\":{\"id\":\"m1\",\"usage\":{\"input_tokens\":10}}}\n" +
                "{\"type\":\"assistant\",\"timestamp\":\"2026-08-09T11:00:00Z\",\"message\":{\"id\":\"m2\",\"usage\":{\"input_tokens\":7}}}\n");

            Assert.Equal(17, days.Values.Single().Sent);
        }

        [Fact]
        public void SkipsLinesThatAreNotUsableAssistantUsage()
        {
            var days = DailyTokensCounter.ParseFile(
                "{\"type\":\"user\",\"message\":{\"usage\":{\"input_tokens\":99}}}\n" +
                "{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"usage\":{\"input_tokens\":99}}}\n" +   // no timestamp
                "{\"type\":\"assistant\",\"timestamp\":\"nonsense\",\"message\":{\"id\":\"m2\",\"usage\":{\"input_tokens\":99}}}\n" +
                "not json but mentions \"assistant\" and usage\n");

            Assert.Empty(days);
        }

        [Fact]
        public void GroupsByLocalDayNotUtc()
        {
            // "Per day" is the user's day: a turn late at night must not land on tomorrow
            // because UTC says so.
            var when = new DateTimeOffset(2026, 8, 9, 23, 30, 0, TimeSpan.Zero);

            Assert.Equal(when.ToLocalTime().ToString("yyyy-MM-dd"), DailyTokensCounter.LocalDay(when));
        }

        [Fact]
        public async Task EmptyProjectsRootYieldsZeroes()
        {
            var counter = new DailyTokensCounter(Path.Combine(_root, "no-projects"),
                                                 Path.Combine(_root, "rollup.json"));

            var totals = await counter.ComputeAsync();

            Assert.Equal(0, totals.Total);
            Assert.Empty(totals.Days);
        }

        [Fact]
        public async Task AggregatesAcrossProjectsAndCachesTheRollup()
        {
            var projects = Path.Combine(_root, "projects");
            var rollup = Path.Combine(_root, "rollup.json");
            Directory.CreateDirectory(Path.Combine(projects, "p1"));
            Directory.CreateDirectory(Path.Combine(projects, "p2"));

            File.WriteAllText(Path.Combine(projects, "p1", "a.jsonl"),
                "{\"type\":\"assistant\",\"timestamp\":\"2026-08-09T10:00:00Z\",\"message\":{\"id\":\"m1\",\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n");
            File.WriteAllText(Path.Combine(projects, "p2", "b.jsonl"),
                "{\"type\":\"assistant\",\"timestamp\":\"2026-08-09T10:00:00Z\",\"message\":{\"id\":\"m2\",\"usage\":{\"input_tokens\":20,\"output_tokens\":2}}}\n");

            var counter = new DailyTokensCounter(projects, rollup);
            var totals = await counter.ComputeAsync();

            Assert.Equal(30, totals.Sent);
            Assert.Equal(3, totals.Received);
            Assert.Equal(33, totals.Total);
            Assert.True(File.Exists(rollup));

            // A second pass reuses the rollup and must produce the same answer.
            Assert.Equal(33, (await counter.ComputeAsync()).Total);
        }

        [Fact]
        public async Task RescansAFileThatChanged()
        {
            var projects = Path.Combine(_root, "projects");
            var rollup = Path.Combine(_root, "rollup.json");
            Directory.CreateDirectory(Path.Combine(projects, "p1"));
            var transcript = Path.Combine(projects, "p1", "a.jsonl");

            File.WriteAllText(transcript,
                "{\"type\":\"assistant\",\"timestamp\":\"2026-08-09T10:00:00Z\",\"message\":{\"id\":\"m1\",\"usage\":{\"input_tokens\":10}}}\n");

            var counter = new DailyTokensCounter(projects, rollup);
            Assert.Equal(10, (await counter.ComputeAsync()).Sent);

            File.AppendAllText(transcript,
                "{\"type\":\"assistant\",\"timestamp\":\"2026-08-09T10:05:00Z\",\"message\":{\"id\":\"m2\",\"usage\":{\"input_tokens\":5}}}\n");

            Assert.Equal(15, (await counter.ComputeAsync()).Sent);
        }

        [Fact]
        public async Task LimitsTheDaySliceButNotTheTotals()
        {
            var projects = Path.Combine(_root, "projects");
            Directory.CreateDirectory(Path.Combine(projects, "p1"));

            var lines = new StringBuilder();
            for (var day = 1; day <= 5; day++)
            {
                lines.Append("{\"type\":\"assistant\",\"timestamp\":\"2026-08-0" + day +
                             "T12:00:00Z\",\"message\":{\"id\":\"m" + day + "\",\"usage\":{\"input_tokens\":10}}}\n");
            }
            File.WriteAllText(Path.Combine(projects, "p1", "a.jsonl"), lines.ToString());

            var totals = await new DailyTokensCounter(projects, Path.Combine(_root, "rollup.json"))
                .ComputeAsync(2);

            Assert.Equal(2, totals.Days.Count);
            // The totals stay all-time even when the display slice is small.
            Assert.Equal(50, totals.Sent);
            // Most recent first.
            Assert.True(string.CompareOrdinal(totals.Days[0].Date, totals.Days[1].Date) > 0);
        }
    }
}
