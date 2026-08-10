using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Session;
// The class and its namespace share a name, so inside another namespace the namespace wins.
using CockpitSession = Tootega.Cockpit.Session.Session;
using Tootega.Cockpit.Stats;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// The session state machine, driven by real event payloads with no CLI process.
    ///
    /// What is worth pinning here is not the happy path — it is the set of judgement calls that
    /// are invisible when wrong: a stray result inflating the turn count, a partial being
    /// rendered twice, an aborted process leaving the spinner lit forever.
    /// </summary>
    public class SessionTests : IDisposable
    {
        private readonly string _root;
        private readonly List<HostMessage> _emitted = new List<HostMessage>();
        private readonly List<bool> _busyChanges = new List<bool>();
        private readonly List<TurnError> _turnErrors = new List<TurnError>();
        private int _authRequired;
        private int _interactions;

        public SessionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "cockpit-session-" + Guid.NewGuid().ToString("N"));
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

        private CockpitSession NewSession(SessionHooks overrides = null)
        {
            var hooks = overrides ?? new SessionHooks();

            hooks.Emit = hooks.Emit ?? (m => _emitted.Add(m));
            hooks.OnBusy = hooks.OnBusy ?? (b => _busyChanges.Add(b));
            hooks.OnTurnError = hooks.OnTurnError ?? (e => _turnErrors.Add(e));
            hooks.OnAuthRequired = hooks.OnAuthRequired ?? (() => _authRequired++);
            hooks.OnInteraction = hooks.OnInteraction ?? (() => _interactions++);
            hooks.Cwd = hooks.Cwd ?? (() => _root);
            hooks.ClaudePath = hooks.ClaudePath ?? (_ => "claude");
            hooks.Settings = hooks.Settings ?? (() => new SessionDefaults
            {
                Model = "default",
                Effort = "default",
                Permission = "default",
                AllowAgents = false,
            });

            return new CockpitSession(hooks, new StatsStore(Path.Combine(_root, "stats")),
                               new Tootega.Cockpit.Cli.SkillBodyIndex(Path.Combine(_root, "claude")));
        }

        private static ClaudeEvent Event(string json) => Json.TryDeserialize<ClaudeEvent>(json);

        private IEnumerable<JsonElement> MessagesOfKind(string kind)
        {
            foreach (var message in _emitted)
            {
                using (var document = JsonDocument.Parse(message.ToJson()))
                {
                    var root = document.RootElement.Clone();
                    if (root.GetProperty("kind").GetString() == kind) yield return root;
                }
            }
        }

        private JsonElement? LastOfKind(string kind)
        {
            var all = MessagesOfKind(kind).ToList();
            return all.Count > 0 ? all[all.Count - 1] : (JsonElement?)null;
        }

        // --- init ---

        [Fact]
        public void InitPinsTheSessionAndItsInventory()
        {
            var session = NewSession();

            session.HandleEvent(Event(
                "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s-1\",\"model\":\"claude-opus-5\"," +
                "\"cwd\":\"D:/work\",\"permissionMode\":\"plan\"," +
                "\"tools\":[\"Read\",\"mcp__db__query\"]," +
                "\"mcp_servers\":[{\"name\":\"db\",\"status\":\"connected\"}]," +
                "\"slash_commands\":[\"deploy\"],\"skills\":[\"caveman\"]}"));

            Assert.Equal("s-1", session.SessionId);
            // Pinned at session level too, or a respawn after a model change would start
            // without --resume and duplicate the context on disk.
            Assert.Equal("s-1", session.ResumeId);
            Assert.Equal(new[] { "deploy" }, session.SlashCommands);
            Assert.Equal(new[] { "caveman" }, session.LastSkills);
            Assert.Equal("db", session.LastMcpServers.Single().Name);

            var init = LastOfKind("sessionInit").Value;
            Assert.Equal("s-1", init.GetProperty("sessionId").GetString());
            Assert.Equal("plan", init.GetProperty("mode").GetString());
        }

        [Fact]
        public void AnEngineReportedContextWindowWinsOverTheDefault()
        {
            // Without this the meter would show 200K over a context that is really much
            // smaller, and the bar would never leave 1%.
            var session = NewSession();

            session.HandleEvent(Event(
                "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s-1\",\"context_window\":16000}"));

            Assert.Equal(16000, session.Snapshot().ContextLimit);
        }

        [Fact]
        public void SlashCommandsArriveFromTheHandshakeBeforeInit()
        {
            // The handshake answers before the first message; init only after one. Without
            // this a fresh tab would have no autocomplete until the user had already sent
            // something.
            var session = NewSession();

            session.HandleEvent(Event(
                "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\"," +
                "\"response\":{\"commands\":[\"/deploy\",\"review\"]}}}"));

            Assert.Equal(new[] { "deploy", "review" }, session.SlashCommands);
            Assert.NotNull(LastOfKind("slashCommands"));
        }

        // --- Streaming ---

        [Fact]
        public void StreamsTextAndClosesTheMessage()
        {
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\",\"message\":{\"id\":\"m1\"}}}"));
            session.HandleEvent(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":0," +
                                      "\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello \"}}}"));
            session.HandleEvent(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":0," +
                                      "\"delta\":{\"type\":\"text_delta\",\"text\":\"world\"}}}"));
            session.HandleEvent(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_stop\"}}"));

            var deltas = MessagesOfKind("assistantText").Select(m => m.GetProperty("delta").GetString()).ToList();
            Assert.Equal(new[] { "Hello ", "world" }, deltas);
            Assert.Single(MessagesOfKind("assistantStart"));
            Assert.Single(MessagesOfKind("assistantDone"));
        }

        [Fact]
        public void DoesNotRepeatTextThatAlreadyStreamed()
        {
            // The full assistant event follows the deltas; emitting both would show the answer
            // twice.
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\",\"message\":{\"id\":\"m1\"}}}"));
            session.HandleEvent(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":0," +
                                      "\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}}"));
            session.HandleEvent(Event("{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"role\":\"assistant\"," +
                                      "\"content\":[{\"type\":\"text\",\"text\":\"Hello\"}]}}"));

            Assert.Single(MessagesOfKind("assistantText"));
        }

        [Fact]
        public void EmitsTextWhenNoPartialsWereSeen()
        {
            // Some turns arrive whole; the bubble still has to appear.
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"role\":\"assistant\"," +
                                      "\"content\":[{\"type\":\"text\",\"text\":\"Complete answer\"}]}}"));

            Assert.Equal("Complete answer", LastOfKind("assistantText").Value.GetProperty("delta").GetString());
        }

        [Fact]
        public void AssemblesAToolCallFromStreamedJson()
        {
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\",\"message\":{\"id\":\"m1\"}}}"));
            session.HandleEvent(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":1," +
                                      "\"content_block\":{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Read\"}}}"));
            session.HandleEvent(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":1," +
                                      "\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"file\\\":\"}}}"));
            session.HandleEvent(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":1," +
                                      "\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"a.cs\\\"}\"}}}"));
            session.HandleEvent(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_stop\",\"index\":1}}"));

            var tool = LastOfKind("toolUse").Value;
            Assert.Equal("Read", tool.GetProperty("name").GetString());
            Assert.Equal("a.cs", tool.GetProperty("input").GetProperty("file").GetString());
        }

        [Fact]
        public void ATruncatedToolInputKeepsItsRawText()
        {
            // Showing what the tool was about to receive beats showing an empty card.
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":0," +
                                      "\"content_block\":{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Bash\"}}}"));
            session.HandleEvent(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":0," +
                                      "\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"cmd\\\":\\\"ls\"}}}"));
            session.HandleEvent(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_stop\",\"index\":0}}"));

            var input = LastOfKind("toolUse").Value.GetProperty("input");
            Assert.True(input.TryGetProperty("_raw", out _));
        }

        [Fact]
        public void ToolCallsAreEmittedOnlyOnce()
        {
            // The streamed block and the full assistant event both carry it.
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":0," +
                                      "\"content_block\":{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Read\"}}}"));
            session.HandleEvent(Event("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_stop\",\"index\":0}}"));
            session.HandleEvent(Event("{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"role\":\"assistant\"," +
                                      "\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Read\",\"input\":{}}]}}"));

            Assert.Single(MessagesOfKind("toolUse"));
        }

        [Fact]
        public void ForwardsToolResults()
        {
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[" +
                                      "{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"file body\"}]}}"));

            var result = LastOfKind("toolResult").Value;
            Assert.Equal("t1", result.GetProperty("toolUseId").GetString());
            Assert.Equal("file body", result.GetProperty("content").GetString());
        }

        // --- Subagents ---

        [Fact]
        public void RoutesSubagentTextToItsTaskCard()
        {
            // It must not reach the main bubble, and it must not touch the statistics —
            // subagent cost stays sourced from the authoritative totals.
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"stream_event\",\"parent_tool_use_id\":\"task-1\"," +
                                      "\"event\":{\"type\":\"content_block_delta\"," +
                                      "\"delta\":{\"type\":\"text_delta\",\"text\":\"working\"}}}"));

            var forwarded = LastOfKind("subagentText").Value;
            Assert.Equal("task-1", forwarded.GetProperty("parentId").GetString());
            Assert.Equal("working", forwarded.GetProperty("delta").GetString());
            Assert.Empty(MessagesOfKind("assistantText"));
        }

        [Fact]
        public void DoesNotDuplicateSubagentTextThatAlreadyStreamed()
        {
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"stream_event\",\"parent_tool_use_id\":\"task-1\"," +
                                      "\"event\":{\"type\":\"content_block_delta\"," +
                                      "\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}}"));
            session.HandleEvent(Event("{\"type\":\"assistant\",\"parent_tool_use_id\":\"task-1\"," +
                                      "\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"partial\"}]}}"));

            Assert.Single(MessagesOfKind("subagentText"));
        }

        [Fact]
        public void ASubagentPermissionPromptStillReachesTheUser()
        {
            // Only narration is diverted; a prompt nobody can answer would hang the turn.
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"control_request\",\"parent_tool_use_id\":\"task-1\"," +
                                      "\"request_id\":\"r1\",\"request\":{\"subtype\":\"can_use_tool\"," +
                                      "\"tool_name\":\"Bash\",\"input\":{\"command\":\"ls\"}}}"));

            Assert.NotNull(LastOfKind("permissionRequest"));
        }

        // --- Permissions ---

        [Fact]
        public void SurfacesAPermissionRequestWithTheFileForTheDiff()
        {
            var asked = new List<string>();
            var session = NewSession(new SessionHooks
            {
                FileText = (tool, input) => { asked.Add(tool); return "current contents"; },
            });

            session.HandleEvent(Event("{\"type\":\"control_request\",\"request_id\":\"r1\"," +
                                      "\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Write\"," +
                                      "\"input\":{\"file_path\":\"a.cs\"}}}"));

            var request = LastOfKind("permissionRequest").Value;
            Assert.Equal("Write", request.GetProperty("tool").GetString());
            Assert.Equal("current contents", request.GetProperty("oldText").GetString());
            Assert.Equal(new[] { "Write" }, asked);
            Assert.Equal(1, _interactions);
        }

        [Fact]
        public void AskUserQuestionGetsItsOwnMessage()
        {
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"control_request\",\"request_id\":\"r1\"," +
                                      "\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"AskUserQuestion\"," +
                                      "\"input\":{\"questions\":[{\"question\":\"Which?\",\"header\":\"Pick\"," +
                                      "\"options\":[{\"label\":\"A\"}]}]}}}"));

            var ask = LastOfKind("askRequest").Value;
            Assert.Equal("Which?", ask.GetProperty("questions")[0].GetProperty("question").GetString());
            Assert.Empty(MessagesOfKind("permissionRequest"));
        }

        [Fact]
        public void ADenialIsRecordedWithItsReason()
        {
            var session = NewSession();
            session.HandleEvent(Event("{\"type\":\"control_request\",\"request_id\":\"r1\"," +
                                      "\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Bash\",\"input\":{}}}"));

            session.Decide("r1", "deny", "too risky");

            var denial = session.Snapshot().RecentDenials.Single();
            Assert.Equal("Bash", denial.Tool);
            Assert.Equal("user", denial.Source);
            Assert.Equal("too risky", denial.Reason);
        }

        [Fact]
        public void DecidingTwiceOnOneRequestOnlyCountsOnce()
        {
            // The pending entry is consumed, so a double click cannot double-count.
            var session = NewSession();
            session.HandleEvent(Event("{\"type\":\"control_request\",\"request_id\":\"r1\"," +
                                      "\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Bash\",\"input\":{}}}"));

            session.Decide("r1", "allow");
            session.Decide("r1", "allow");

            Assert.Equal(1, session.Snapshot().ToolAcceptance.Single().Allow);
        }

        // --- Results ---

        [Fact]
        public void IgnoresTheTurnBookkeepingOfAResultWeDidNotStart()
        {
            // The CLI re-emits turns on --resume. Treating one as ours would close a turn that
            // never opened: a turnComplete the UI acts on, a timeline sample, a persisted
            // write. The aggregator still ingests it, because total_cost_usd is the session's
            // authoritative total rather than an increment — refusing that number would make
            // the panel disagree with the CLI.
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"result\",\"subtype\":\"success\",\"total_cost_usd\":5}"));

            Assert.Empty(MessagesOfKind("turnComplete"));
            Assert.Empty(MessagesOfKind("statsTimeline"));
            Assert.Equal(0, session.Snapshot().TurnCount);
            Assert.Equal(5, session.Snapshot().SessionCostUsd);
        }

        [Fact]
        public void ReportsAnErrorResultWithTheClisOwnText()
        {
            var session = NewSession();
            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"task_notification\"," +
                                      "\"task_id\":\"x\",\"status\":\"completed\"}"));   // marks busy

            session.HandleEvent(Event("{\"type\":\"result\",\"is_error\":true,\"subtype\":\"error\"," +
                                      "\"result\":\"model refused the request\"}"));

            var error = _turnErrors.Single();
            Assert.Equal(TurnErrorKind.Error, error.Kind);
            Assert.Equal("model refused the request", error.Text);
        }

        [Fact]
        public void ATransientFailureIsASoftWarning()
        {
            // The modern CLI preserves the partial and retries; showing this as a failure
            // would be alarming noise.
            var session = NewSession();
            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"task_notification\"," +
                                      "\"task_id\":\"x\",\"status\":\"completed\"}"));

            session.HandleEvent(Event("{\"type\":\"result\",\"is_error\":true," +
                                      "\"result\":\"stream disconnect, will retry\"}"));

            Assert.Equal(TurnErrorKind.Transient, _turnErrors.Single().Kind);
            Assert.Equal(0, _authRequired);
        }

        [Fact]
        public void AnAuthErrorAsksForSignIn()
        {
            var session = NewSession();
            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"task_notification\"," +
                                      "\"task_id\":\"x\",\"status\":\"completed\"}"));

            session.HandleEvent(Event("{\"type\":\"result\",\"is_error\":true," +
                                      "\"result\":\"Not authenticated. Please run /login\"}"));

            Assert.Equal(1, _authRequired);
            // It is an auth problem, not a turn error the tab should warn about separately.
            Assert.Empty(_turnErrors);
        }

        [Fact]
        public void AFinishedBackgroundTaskOpensATurnSoItsResultCounts()
        {
            // The CLI opens a turn of its own to react. Without marking busy that result would
            // be discarded as a stray.
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"task_notification\"," +
                                      "\"task_id\":\"t1\",\"status\":\"completed\"}"));

            Assert.True(session.Busy);
        }

        [Fact]
        public void AKilledTaskDoesNotOpenATurn()
        {
            // It generates no turn, so marking busy would leave the spinner stuck forever.
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"task_notification\"," +
                                      "\"task_id\":\"t1\",\"status\":\"killed\"}"));

            Assert.False(session.Busy);
        }

        // --- Background tasks ---

        [Fact]
        public void TracksBackgroundWorkThatOutlivesItsTurn()
        {
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"role\":\"assistant\"," +
                                      "\"content\":[{\"type\":\"tool_use\",\"id\":\"tu1\",\"name\":\"Workflow\"," +
                                      "\"input\":{\"name\":\"nightly\"}}]}}"));
            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"t1\"," +
                                      "\"tool_use_id\":\"tu1\",\"description\":\"nightly build\"}"));

            var tasks = LastOfKind("background").Value.GetProperty("tasks");
            Assert.Equal(1, tasks.GetArrayLength());
            // The name came from the tool_use we remembered; task_started carries none.
            Assert.Equal("Workflow", tasks[0].GetProperty("tool").GetString());
            Assert.Equal("nightly build", tasks[0].GetProperty("label").GetString());
        }

        [Fact]
        public void ReconcilesAgainstTheCompleteTaskList()
        {
            // It is the source of truth: work the agent killed emits no notification, and a
            // resumed session can have tasks the UI never saw start.
            var session = NewSession();
            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"t1\"," +
                                      "\"description\":\"one\"}"));

            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"background_tasks_changed\",\"tasks\":[" +
                                      "{\"task_id\":\"t2\",\"task_type\":\"local_bash\",\"description\":\"two\"}]}"));

            var tasks = LastOfKind("background").Value.GetProperty("tasks");
            Assert.Equal(1, tasks.GetArrayLength());
            Assert.Equal("Bash", tasks[0].GetProperty("tool").GetString());
        }

        [Fact]
        public void AnEmptyTaskListClearsEverything()
        {
            var session = NewSession();
            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"t1\",\"description\":\"one\"}"));

            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"background_tasks_changed\",\"tasks\":[]}"));

            Assert.Equal(0, LastOfKind("background").Value.GetProperty("tasks").GetArrayLength());
        }

        // --- Compaction ---

        [Fact]
        public void AnnouncesCompactionOnceAndSealsItWithNumbers()
        {
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"status\",\"status\":\"compacting\"}"));
            // The status repeats every thirty seconds; the banner must not flicker.
            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"status\",\"status\":\"compacting\"}"));

            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"compact_metadata\":" +
                                      "{\"pre_tokens\":100000,\"post_tokens\":20000,\"trigger\":\"auto\",\"duration_ms\":4200}}"));

            var compactions = MessagesOfKind("compaction").ToList();
            Assert.Equal(2, compactions.Count);
            Assert.True(compactions[0].GetProperty("active").GetBoolean());

            var boundary = compactions[1];
            Assert.False(boundary.GetProperty("active").GetBoolean());
            Assert.Equal(100000, boundary.GetProperty("pre").GetInt64());
            Assert.Equal(20000, boundary.GetProperty("post").GetInt64());
            Assert.Equal("auto", boundary.GetProperty("trigger").GetString());
        }

        [Fact]
        public void ABoundaryWithoutNumbersStillClosesTheBanner()
        {
            // A version that stops sending the metadata makes the numbers disappear, not the
            // banner stick.
            var session = NewSession();
            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"status\",\"status\":\"compacting\"}"));

            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"compact_boundary\"}"));

            var last = MessagesOfKind("compaction").Last();
            Assert.False(last.GetProperty("active").GetBoolean());
            Assert.False(last.TryGetProperty("pre", out _));
        }

        // --- Engine notices ---

        [Fact]
        public void SurfacesAnEngineWarningOncePerSession()
        {
            // The CLI may repeat it every turn; repeating the banner would drown the timeline.
            var session = NewSession();
            const string warning = "{\"type\":\"system\",\"subtype\":\"fast_mode_credits_warning\"," +
                                   "\"message\":\"fast mode credits are running out\"}";

            session.HandleEvent(Event(warning));
            session.HandleEvent(Event(warning));

            var notice = MessagesOfKind("engineNotice").Single();
            Assert.Equal("fast mode credits are running out", notice.GetProperty("text").GetString());
        }

        [Fact]
        public void IgnoresUnknownSystemEventsThatAreNotWarnings()
        {
            // The tolerant stream contract: an event we do not recognise is not news.
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"something_new\",\"data\":123}"));

            Assert.Empty(MessagesOfKind("engineNotice"));
        }

        // --- Rate limits ---

        [Fact]
        public void AppliesAStreamRateLimit()
        {
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"rate_limit_event\",\"rate_limit_info\":" +
                                      "{\"status\":\"allowed_warning\",\"rateLimitType\":\"five_hour\"," +
                                      "\"utilization\":0.85,\"resetsAt\":1786000000}}"));

            var limits = session.Snapshot().Limits.FiveHour;
            Assert.Equal(0.85, limits.UsedPct.Value, 6);
            Assert.Equal("allowed_warning", limits.Status);
            Assert.Contains("2026", limits.ResetsAt);
            Assert.Equal("stream", session.Snapshot().LimitsSource);
        }

        [Fact]
        public void IgnoresBucketsThatHaveNoMeter()
        {
            // The per-model weekly and overage buckets are outside the two displayed windows.
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"rate_limit_event\",\"rate_limit_info\":" +
                                      "{\"status\":\"allowed\",\"rateLimitType\":\"seven_day_opus\",\"utilization\":0.5}}"));

            Assert.Equal("estimate", session.Snapshot().LimitsSource);
        }

        // --- Session lifecycle ---

        [Fact]
        public void ResumeHydratesThePersistedStatistics()
        {
            // The whole reason the store exists: the CLI does not re-emit old turns.
            var store = new StatsStore(Path.Combine(_root, "stats"));
            store.Save(new PersistedStats
            {
                Version = StatsStore.StatsVersion,
                SessionId = "s-1",
                InputTokens = 1000,
                TurnCount = 4,
                LastContextUsed = 5000,
            });
            store.Flush();

            var session = new CockpitSession(new SessionHooks
            {
                Emit = m => _emitted.Add(m),
                Cwd = () => _root,
                Settings = () => new SessionDefaults(),
            }, store);

            session.Resume("s-1");

            var snapshot = session.Snapshot();
            Assert.Equal(1000, snapshot.InputTokens);
            Assert.Equal(4, snapshot.TurnCount);
            Assert.Equal(1, snapshot.ReopenCount);
            // The context bar is restored immediately, before any new turn.
            Assert.Equal(5000, snapshot.ContextUsed);
        }

        [Fact]
        public void ClearingAConversationDropsTheResumeIdToo()
        {
            // Otherwise the next send would respawn with --resume of the old session and
            // nothing would look cleared.
            var session = NewSession();
            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s-1\"}"));

            session.ClearConversation();

            Assert.Null(session.SessionId);
            Assert.Null(session.ResumeId);
            Assert.Equal(0, session.Snapshot().TurnCount);
        }

        [Fact]
        public void SkillOverridesOnlyTravelWhenTheyDifferFromTheDefault()
        {
            // 'on' is the CLI default: sending it changes nothing and only clutters the
            // settings file we hand over.
            var session = NewSession();

            session.SetSkillOverride("a", Tootega.Cockpit.Protocol.SkillOverrides.Off);
            session.SetSkillOverride("b", Tootega.Cockpit.Protocol.SkillOverrides.On);

            Assert.Equal(new[] { "a" }, session.SkillOverrides.Keys);
        }

        [Fact]
        public void ASlashSkillSentBeforeInitIsResolvedAfterwards()
        {
            // init carries the skill list and only arrives after the first message, so on a
            // fresh tab a /skill as the first thing typed was never being marked.
            var session = NewSession();

            session.HandleEvent(Event("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s-1\"," +
                                      "\"skills\":[\"caveman\"]}"));

            Assert.Equal(new[] { "caveman" }, session.LastSkills);
        }
    }
}
