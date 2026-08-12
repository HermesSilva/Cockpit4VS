using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Session;
using Tootega.Cockpit.Stats;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    public class UsageAndTelemetryTests : IDisposable
    {
        private readonly string _root;

        public UsageAndTelemetryTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "cockpit-usage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            CostModel.ResetDiscoveredContexts();
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

        // --- UsageApi payload ---

        [Fact]
        public void ReadsTheCurrentUsagePayload()
        {
            var usage = UsageApi.Parse(
                "{\"limits\":[" +
                "{\"kind\":\"session\",\"utilization\":42,\"resets_at\":\"2026-08-10T15:00:00Z\"}," +
                "{\"kind\":\"weekly_all\",\"utilization\":70}," +
                "{\"kind\":\"weekly_scoped\",\"utilization\":15,\"scope\":{\"model\":{\"display_name\":\"Opus\"}}}]}");

            Assert.Equal(0.42, usage.FiveHour.UsedPct.Value, 6);
            Assert.Equal("2026-08-10T15:00:00Z", usage.FiveHour.ResetsAt);
            Assert.Equal(0.70, usage.SevenDay.UsedPct.Value, 6);
            Assert.Equal("Opus", usage.WeeklyScoped.Single().Label);
        }

        [Fact]
        public void FallsBackToTheLegacyTopLevelFields()
        {
            var usage = UsageApi.Parse(
                "{\"five_hour\":{\"utilization\":10},\"seven_day\":{\"utilization\":20}," +
                "\"seven_day_opus\":{\"utilization\":30},\"seven_day_sonnet\":{\"utilization\":40}}");

            Assert.Equal(0.10, usage.FiveHour.UsedPct.Value, 6);
            Assert.Equal(0.20, usage.SevenDay.UsedPct.Value, 6);
            Assert.Equal(new[] { "Opus", "Sonnet" }, usage.WeeklyScoped.Select(s => s.Label));
        }

        [Fact]
        public void AcceptsPercentAsAnAlternativeToUtilization()
        {
            var usage = UsageApi.Parse("{\"five_hour\":{\"percent\":55}}");

            Assert.Equal(0.55, usage.FiveHour.UsedPct.Value, 6);
        }

        [Fact]
        public void ApiPercentagesAreAlwaysScaledFromZeroToHundred()
        {
            // Unlike the statusline payload, this endpoint is consistent — so 1 means 1%, not
            // 100%, and must not be sniffed.
            var usage = UsageApi.Parse("{\"five_hour\":{\"utilization\":1}}");

            Assert.Equal(0.01, usage.FiveHour.UsedPct.Value, 6);
        }

        [Fact]
        public void ClampsOutOfRangePercentages()
        {
            Assert.Equal(1.0, UsageApi.Parse("{\"five_hour\":{\"utilization\":150}}").FiveHour.UsedPct.Value, 6);
            Assert.Equal(0.0, UsageApi.Parse("{\"five_hour\":{\"utilization\":-5}}").FiveHour.UsedPct.Value, 6);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[]")]
        public void UnusableUsageBodiesParseAsNothing(string body)
        {
            Assert.Null(UsageApi.Parse(body));
        }

        [Fact]
        public void AnEmptyObjectYieldsNoWindows()
        {
            var usage = UsageApi.Parse("{}");

            Assert.NotNull(usage);
            Assert.Null(usage.FiveHour);
            Assert.Null(usage.WeeklyScoped);
        }

        // --- Local usage estimate ---

        private string WriteTranscript(string project, string name, string content)
        {
            var dir = Path.Combine(_root, "projects", project);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, name + ".jsonl");
            File.WriteAllText(file, content, new UTF8Encoding(false));
            return file;
        }

        private static string AssistantLine(DateTimeOffset when, string id, string model,
                                            long input, long output, long cacheCreate, long cacheRead,
                                            bool sidechain = false, string extraBlocks = "")
        {
            var side = sidechain ? ",\"isSidechain\":true" : string.Empty;
            return "{\"type\":\"assistant\",\"timestamp\":\"" + when.ToString("o") + "\"" + side +
                   ",\"message\":{\"id\":\"" + id + "\",\"model\":\"" + model + "\"," +
                   "\"content\":[" + extraBlocks + "]," +
                   "\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output +
                   ",\"cache_creation_input_tokens\":" + cacheCreate +
                   ",\"cache_read_input_tokens\":" + cacheRead + "}}}";
        }

        [Fact]
        public void SeparatesNewTokensFromCacheReads()
        {
            // A cache read is the context re-read every turn and can be ~97% of the raw total;
            // mixing them would drown the figure that reflects actual work.
            var now = DateTimeOffset.UtcNow;
            WriteTranscript("p1", "a", AssistantLine(now.AddMinutes(-10), "m1", "claude-opus-5", 100, 50, 200, 9000) + "\n");

            var usage = new UsageAggregator(Path.Combine(_root, "projects"))
                .Compute(now.ToUnixTimeMilliseconds());

            Assert.Equal(350, usage.SevenDayTokens);       // input + output + cache-create
            Assert.Equal(9000, usage.SevenDayCacheRead);
            Assert.Equal(350, usage.FiveHourTokens);
            Assert.True(usage.SevenDayUsd > 0);
        }

        [Fact]
        public void ExcludesTurnsOutsideTheWindows()
        {
            var now = DateTimeOffset.UtcNow;
            WriteTranscript("p1", "a",
                AssistantLine(now.AddHours(-1), "recent", "claude-opus-5", 10, 0, 0, 0) + "\n" +
                AssistantLine(now.AddHours(-10), "old5h", "claude-opus-5", 100, 0, 0, 0) + "\n" +
                AssistantLine(now.AddDays(-9), "ancient", "claude-opus-5", 1000, 0, 0, 0) + "\n");

            var usage = new UsageAggregator(Path.Combine(_root, "projects"))
                .Compute(now.ToUnixTimeMilliseconds());

            Assert.Equal(10, usage.FiveHourTokens);
            // The 9-day-old turn is outside the 7-day window.
            Assert.Equal(110, usage.SevenDayTokens);
        }

        [Fact]
        public void CountsOneResponseOnceAcrossItsLines()
        {
            var now = DateTimeOffset.UtcNow;
            var line = AssistantLine(now.AddMinutes(-5), "m1", "claude-opus-5", 100, 10, 0, 0);
            WriteTranscript("p1", "a", line + "\n" + line + "\n");

            var usage = new UsageAggregator(Path.Combine(_root, "projects"))
                .Compute(now.ToUnixTimeMilliseconds());

            Assert.Equal(110, usage.SevenDayTokens);
        }

        [Fact]
        public void BreaksDownByModelAndBySource()
        {
            var now = DateTimeOffset.UtcNow;
            WriteTranscript("p1", "a",
                AssistantLine(now.AddMinutes(-5), "m1", "claude-opus-5", 1000, 100, 0, 0) + "\n" +
                AssistantLine(now.AddMinutes(-4), "m2", "claude-haiku-4-5", 500, 50, 0, 0) + "\n" +
                AssistantLine(now.AddMinutes(-3), "m3", "claude-opus-5", 200, 20, 0, 0, sidechain: true) + "\n");

            var usage = new UsageAggregator(Path.Combine(_root, "projects"))
                .Compute(now.ToUnixTimeMilliseconds());

            Assert.Equal(2, usage.Breakdown.ByModel.Count);
            // Costliest model first.
            Assert.Equal("claude-opus-5", usage.Breakdown.ByModel[0].Key);

            // Fixed main-then-subagent order, so the block reads the same every time.
            Assert.Equal(new[] { "main", "subagent" }, usage.Breakdown.BySource.Select(s => s.Key));
            Assert.Equal(220, usage.Breakdown.BySource[1].Tokens);
            Assert.Equal(220.0 / 1870, usage.Attribution.SubagentPct, 6);
        }

        [Fact]
        public void ExcludesTheSyntheticMarkerFromTheModelBreakdown()
        {
            // `<synthetic>` marks turns with no real call; it is not a model.
            var now = DateTimeOffset.UtcNow;
            WriteTranscript("p1", "a",
                AssistantLine(now.AddMinutes(-5), "m1", "<synthetic>", 100, 10, 0, 0) + "\n" +
                AssistantLine(now.AddMinutes(-4), "m2", "claude-opus-5", 100, 10, 0, 0) + "\n");

            var usage = new UsageAggregator(Path.Combine(_root, "projects"))
                .Compute(now.ToUnixTimeMilliseconds());

            Assert.Equal("claude-opus-5", usage.Breakdown.ByModel.Single().Key);
        }

        [Fact]
        public void AttributesLongContextTurns()
        {
            var now = DateTimeOffset.UtcNow;
            WriteTranscript("p1", "a",
                AssistantLine(now.AddMinutes(-5), "small", "claude-opus-5", 100, 10, 0, 1000) + "\n" +
                AssistantLine(now.AddMinutes(-4), "long", "claude-opus-5", 100, 10, 0, 200_000) + "\n");

            var usage = new UsageAggregator(Path.Combine(_root, "projects"))
                .Compute(now.ToUnixTimeMilliseconds());

            // 110 of the 220 new tokens were generated over a long context.
            Assert.Equal(0.5, usage.Attribution.LongContextPct, 6);
        }

        [Fact]
        public void ComputesTheCacheHitRateOverCacheActivityOnly()
        {
            var now = DateTimeOffset.UtcNow;
            WriteTranscript("p1", "a", AssistantLine(now.AddMinutes(-5), "m1", "claude-opus-5", 0, 0, 250, 750) + "\n");

            var usage = new UsageAggregator(Path.Combine(_root, "projects"))
                .Compute(now.ToUnixTimeMilliseconds());

            Assert.Equal(0.75, usage.Attribution.CacheHitPct.Value, 6);
        }

        [Fact]
        public void NoCacheActivityMeansNoHitRateAtAll()
        {
            var now = DateTimeOffset.UtcNow;
            WriteTranscript("p1", "a", AssistantLine(now.AddMinutes(-5), "m1", "claude-opus-5", 100, 10, 0, 0) + "\n");

            var usage = new UsageAggregator(Path.Combine(_root, "projects"))
                .Compute(now.ToUnixTimeMilliseconds());

            // Zero would read as "the cache never helps"; absent reads as "nothing measured".
            Assert.Null(usage.Attribution.CacheHitPct);
        }

        [Fact]
        public void AttributesInjectedContextToTheToolThatCausedIt()
        {
            var now = DateTimeOffset.UtcNow;
            var toolUse = "{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Read\",\"input\":{}}";
            var body = new string('x', 400);

            WriteTranscript("p1", "a",
                AssistantLine(now.AddMinutes(-5), "m1", "claude-opus-5", 10, 1, 0, 0, extraBlocks: toolUse) + "\n" +
                "{\"type\":\"user\",\"timestamp\":\"" + now.AddMinutes(-4).ToString("o") + "\"," +
                "\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\"," +
                "\"content\":\"" + body + "\"}]}}\n");

            var usage = new UsageAggregator(Path.Combine(_root, "projects"))
                .Compute(now.ToUnixTimeMilliseconds());

            var slice = usage.Attribution.ByTool.Single();
            Assert.Equal("Read", slice.Key);
            Assert.Equal(1, slice.Calls);
            Assert.Equal(100, slice.Tokens);   // 400 chars / 4
        }

        [Theory]
        [InlineData("mcp__github__create_issue", "mcp:github")]
        [InlineData("mcp__dase__dase_status", "mcp:dase")]
        [InlineData("Read", "Read")]
        [InlineData("mcp__", "mcp:?")]
        public void GroupsMcpToolsByServer(string name, string expected)
        {
            // What matters is which server inflates the context, not which of its tools did.
            Assert.Equal(expected, UsageAggregator.ToolBucket(name, null));
        }

        [Fact]
        public void GroupsSkillCallsByName()
        {
            using (var document = System.Text.Json.JsonDocument.Parse("{\"skill\":\"caveman\"}"))
            {
                Assert.Equal("skill:caveman", UsageAggregator.ToolBucket("Skill", document.RootElement));
            }
        }

        [Fact]
        public void EmptyHistoryProducesAnEmptyEstimate()
        {
            var usage = new UsageAggregator(Path.Combine(_root, "nothing-here"))
                .Compute(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            Assert.Equal(0, usage.SevenDayTokens);
            Assert.Empty(usage.Breakdown.ByModel);
            Assert.Empty(usage.Attribution.ByTool);
            // Percentages must be 0 rather than NaN when there is nothing to divide by.
            Assert.Equal(0, usage.Attribution.LongContextPct);
            Assert.Equal(0, usage.Attribution.SubagentPct);
        }

        // --- OTEL metrics ---

        private static OtelState IngestMetric(string name, string dataPoints)
        {
            var state = new OtelState(0);
            OtelMetrics.Ingest(
                "{\"resourceMetrics\":[{\"scopeMetrics\":[{\"metrics\":[" +
                "{\"name\":\"" + name + "\",\"sum\":{\"dataPoints\":[" + dataPoints + "]}}]}]}]}",
                state);
            return state;
        }

        [Fact]
        public void ReadsAsIntWhenItArrivesAsAString()
        {
            // OTLP/JSON encodes 64-bit integers as strings. Missing this silently zeroes every
            // counter, which looks like "telemetry is not working" rather than a parse bug.
            var state = IngestMetric("claude_code.session.count", "{\"asInt\":\"7\"}");

            Assert.Equal(7, state.SessionCount);
        }

        [Fact]
        public void ReadsAsDoubleToo()
        {
            var state = IngestMetric("claude_code.cost.usage",
                "{\"asDouble\":1.25,\"attributes\":[{\"key\":\"model\",\"value\":{\"stringValue\":\"claude-opus-5\"}}]}");

            Assert.Equal(1.25, state.CostByModel["claude-opus-5"], 6);
        }

        [Fact]
        public void SplitsLinesAddedFromLinesRemoved()
        {
            var state = IngestMetric("claude_code.lines_of_code.count",
                "{\"asInt\":\"100\",\"attributes\":[{\"key\":\"model\",\"value\":{\"stringValue\":\"claude-opus-5\"}}]}," +
                "{\"asInt\":\"30\",\"attributes\":[{\"key\":\"type\",\"value\":{\"stringValue\":\"removed\"}}]}");

            Assert.Equal(100, state.LinesAdded);
            Assert.Equal(30, state.LinesRemoved);
            Assert.Equal(100, state.LocByModel["claude-opus-5"]);
            // A removal is not attributed to a model, so it must not appear there.
            Assert.Single(state.LocByModel);
        }

        [Fact]
        public void CountsEditDecisionsPerTool()
        {
            var state = IngestMetric("claude_code.code_edit_tool.decision",
                "{\"asInt\":\"3\",\"attributes\":[{\"key\":\"tool_name\",\"value\":{\"stringValue\":\"Edit\"}}," +
                "{\"key\":\"decision\",\"value\":{\"stringValue\":\"accept\"}}]}," +
                "{\"asInt\":\"1\",\"attributes\":[{\"key\":\"tool_name\",\"value\":{\"stringValue\":\"Edit\"}}," +
                "{\"key\":\"decision\",\"value\":{\"stringValue\":\"reject\"}}]}");

            var decision = state.Decisions["Edit"];
            Assert.Equal(3, decision.Accept);
            Assert.Equal(1, decision.Reject);
        }

        [Fact]
        public void TotalsAWorkflowRunAcrossItsAgents()
        {
            // stream-json does not expose this; the workflow attributes are the only way to
            // total what a whole run spent.
            var state = new OtelState(0);
            OtelMetrics.Ingest(
                "{\"resourceMetrics\":[{\"scopeMetrics\":[{\"metrics\":[" +
                "{\"name\":\"claude_code.cost.usage\",\"sum\":{\"dataPoints\":[" +
                "{\"asDouble\":1.0,\"attributes\":[{\"key\":\"workflow.run_id\",\"value\":{\"stringValue\":\"r1\"}}," +
                "{\"key\":\"workflow.name\",\"value\":{\"stringValue\":\"nightly\"}}," +
                "{\"key\":\"effort\",\"value\":{\"stringValue\":\"high\"}}]}," +
                "{\"asDouble\":2.0,\"attributes\":[{\"key\":\"workflow.run_id\",\"value\":{\"stringValue\":\"r1\"}}," +
                "{\"key\":\"effort\",\"value\":{\"stringValue\":\"low\"}}]}]}}]}]}]}",
                state);

            var run = state.Workflows["r1"];
            Assert.Equal("nightly", run.Name);
            Assert.Equal(3.0, run.Usd, 6);

            var stats = OtelMetrics.ToStats(state, true, "http://127.0.0.1:4318");
            // Efforts are listed lowest to highest, not in arrival order.
            Assert.Equal("low · high", stats.Workflows.Single().Effort);
        }

        [Fact]
        public void IgnoresUnknownMetricsAndZeroPoints()
        {
            var state = IngestMetric("some.other.metric", "{\"asInt\":\"5\"}");
            Assert.Equal(0, state.SessionCount);

            var zero = IngestMetric("claude_code.session.count", "{\"asInt\":\"0\"}");
            Assert.Equal(0, zero.SessionCount);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("{}")]
        [InlineData("{\"resourceMetrics\":\"wrong\"}")]
        public void MalformedExportsAreDropped(string json)
        {
            var state = new OtelState(0);

            OtelMetrics.Ingest(json, state);

            Assert.Equal(0, state.LinesAdded);
        }

        [Fact]
        public void EmptyAggregatesAreReportedAsAbsent()
        {
            // A row of zeroes looks like measured data; nothing looks like nothing.
            var stats = OtelMetrics.ToStats(new OtelState(0), false, "http://127.0.0.1:4318");

            Assert.False(stats.Enabled);
            Assert.Null(stats.LocByModel);
            Assert.Null(stats.CostByModel);
            Assert.Null(stats.SessionCount);
            Assert.Null(stats.ToolDecisions);
            Assert.Null(stats.Workflows);
        }

        // --- OTEL receiver, end to end over a real socket ---

        [Fact]
        public async Task ReceivesAnExportOverHttp()
        {
            // The receiver runs on a raw socket rather than HttpListener, which needs a URL
            // reservation on Windows. Only a real request proves the framing is right.
            using (var receiver = StartedReceiver())
            {
                Assert.True(receiver.IsRunning, "the receiver did not start on " + receiver.Endpoint);

                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    var payload = "{\"resourceMetrics\":[{\"scopeMetrics\":[{\"metrics\":[" +
                                  "{\"name\":\"claude_code.commit.count\",\"sum\":{\"dataPoints\":[{\"asInt\":\"4\"}]}}]}]}]}";

                    var response = await client.PostAsync(receiver.Endpoint + "/v1/metrics",
                        new StringContent(payload, new UTF8Encoding(false), "application/json"));

                    Assert.True(response.IsSuccessStatusCode);
                }

                Assert.Equal(4, receiver.Stats().CommitCount);
            }
        }

        [Fact]
        public async Task AcceptsAndDiscardsLogs()
        {
            // Assistant responses can carry conversation text. Receipt is acknowledged so the
            // exporter stops retrying, but nothing is retained.
            using (var receiver = StartedReceiver())
            {
                Assert.True(receiver.IsRunning);

                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    var response = await client.PostAsync(receiver.Endpoint + "/v1/logs",
                        new StringContent("{\"resourceLogs\":[]}", new UTF8Encoding(false), "application/json"));

                    Assert.True(response.IsSuccessStatusCode);
                }
            }
        }

        [Fact]
        public async Task RejectsAnythingElse()
        {
            using (var receiver = StartedReceiver())
            {
                Assert.True(receiver.IsRunning);

                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    var response = await client.GetAsync(receiver.Endpoint + "/v1/metrics");
                    Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
                }
            }
        }

        [Fact]
        public void StoppingRemovesTheExportedEnvironment()
        {
            using (var receiver = StartedReceiver())
            {
                Assert.Equal("1", Environment.GetEnvironmentVariable("CLAUDE_CODE_ENABLE_TELEMETRY"));
                // Conversation text must never enter telemetry, so both are pinned off.
                Assert.Equal("0", Environment.GetEnvironmentVariable("OTEL_LOG_USER_PROMPTS"));
                Assert.Equal("0", Environment.GetEnvironmentVariable("OTEL_LOG_ASSISTANT_RESPONSES"));

                receiver.Stop();
                Assert.Null(Environment.GetEnvironmentVariable("CLAUDE_CODE_ENABLE_TELEMETRY"));
                Assert.Null(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));
            }
        }

        private static int FreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>
        /// A started receiver on a port that was free when it got there.
        ///
        /// Asking the OS for a free port and then binding it are two steps, and the machine
        /// is free to hand that port to somebody else in between — which it does, often
        /// enough to fail a build for reasons that have nothing to do with the change being
        /// built. Losing that race is not a defect worth reporting, so it is retried; failing
        /// every time is, so it is not retried forever.
        /// </summary>
        private static OtelReceiver StartedReceiver()
        {
            OtelReceiver receiver = null;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                receiver = new OtelReceiver(FreePort());
                receiver.Start();
                if (receiver.IsRunning) return receiver;

                receiver.Dispose();
            }

            Assert.True(receiver != null && receiver.IsRunning, "the receiver would not start on any free port");
            return receiver;
        }
    }
}
