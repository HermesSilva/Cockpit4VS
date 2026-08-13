using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Stats;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Session
{
    /// <summary>
    /// One conversation at runtime: a CLI process, its statistics and all the streaming state.
    /// Port of src/session/Session.ts.
    ///
    /// It emits protocol messages through a callback — the host tags them with a tab id and
    /// forwards them — and knows nothing about tabs or the IDE. Several instances run in
    /// parallel, one per tab.
    ///
    /// Threading: CLI events arrive on a background reader thread, so everything the hooks do
    /// with the UI must marshal itself. That is the host's job, not this class's.
    /// </summary>
    internal sealed class Session : IDisposable
    {
        private static readonly Regex SlashCommand = new Regex(@"^/([A-Za-z0-9_:-]+)", RegexOptions.Compiled);

        private readonly SessionHooks _hooks;
        private readonly StatsStore _statsStore;
        private readonly SkillBodyIndex _skillIndex;

        public CliProcessManager Cli { get; private set; }
        public StatsAggregator Stats { get; private set; }

        public string SessionId { get; private set; }
        public string ResumeId { get; private set; }
        public bool Busy { get; private set; }

        /// <summary>The CLI is condensing the context right now.</summary>
        private bool _compacting;

        public List<string> SlashCommands { get; private set; } = new List<string>();

        /// <summary>
        /// The latest init inventory. Kept because the MCP panel needs it at any moment, not
        /// only at the instant the event went past.
        /// </summary>
        public IReadOnlyList<string> LastTools { get; private set; }
        public IReadOnlyList<McpServerRef> LastMcpServers { get; private set; }
        public IReadOnlyList<McpConfigError> LastMcpErrors { get; private set; }
        public IReadOnlyList<string> LastSkills { get; private set; }

        /// <summary>Per-skill listing overrides for this tab, applied at the next spawn.</summary>
        public Dictionary<string, string> SkillOverrides { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Slash commands sent before init revealed which names are skills.</summary>
        private readonly List<string> _pendingSlashSkills = new List<string>();

        /// <summary>Warnings already shown; the CLI may repeat one every turn.</summary>
        private readonly HashSet<string> _noticesSeen = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Background work still running after the turn that launched it ended.
        ///
        /// The source of truth is the stream's system events. It cannot be deduced from the
        /// notification text: when a task finishes with a turn in flight, the CLI queues the
        /// notification and it never reaches stdout as a message — only the system event does.
        /// </summary>
        private readonly Dictionary<string, BackgroundTask> _bgTasks = new Dictionary<string, BackgroundTask>(StringComparer.Ordinal);

        /// <summary>tool_use id to tool name, so a task can be named when task_started arrives.</summary>
        private readonly Dictionary<string, string> _toolNames = new Dictionary<string, string>(StringComparer.Ordinal);

        // Per-tab overrides. Null means "use the settings default".
        public string EngineOverride { get; private set; }
        public string ModelOverride { get; private set; }
        public string EffortOverride { get; private set; }
        public string PermissionOverride { get; private set; }
        public bool? AllowAgentsOverride { get; private set; }

        /// <summary>Empty history already announced for this tab. Local engine only.</summary>
        private bool _historyAnnounced;

        // Streaming state
        private string _currentAssistantId;
        private readonly HashSet<string> _streamedText = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<int, ToolBuffer> _toolBuffers = new Dictionary<int, ToolBuffer>();
        private readonly HashSet<string> _emittedTools = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _subagentStreamed = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingPermission> _pendingPermissions =
            new Dictionary<string, PendingPermission>(StringComparer.Ordinal);

        private sealed class ToolBuffer
        {
            public string Id;
            public string Name;
            public string Json = string.Empty;
        }

        private sealed class PendingPermission
        {
            public string Tool;
            public JsonElement? Input;
            public List<PermissionSuggestion> Suggestions;
        }

        public Session(SessionHooks hooks, StatsStore statsStore = null, SkillBodyIndex skillIndex = null)
        {
            _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
            _statsStore = statsStore ?? new StatsStore();
            _skillIndex = skillIndex ?? new SkillBodyIndex();
            Stats = NewStats();
        }

        /// <summary>
        /// A fresh aggregator with the skill-body recogniser already wired.
        ///
        /// Hooks inject context without emitting a Skill tool_use, and matching the body is the
        /// only way to know WHICH skill came in that way. The init list is the good source, but
        /// a SessionStart hook fires before init — until then, what is on disk has to do.
        /// </summary>
        private StatsAggregator NewStats()
        {
            var stats = new StatsAggregator(0);
            stats.SetSkillBodyResolver(text =>
            {
                var cwd = _hooks.Cwd?.Invoke();
                var names = LastSkills ?? _skillIndex.NamesOnDisk(cwd);
                return _skillIndex.Match(text, names, cwd);
            });
            return stats;
        }

        // ---- Effective configuration ----

        public string Engine() => EngineOverride ?? _hooks.Engine?.Invoke() ?? EngineIds.Claude;
        public string Model() => ModelOverride ?? Defaults().Model;
        public string Effort() => EffortOverride ?? Defaults().Effort;
        public string Permission() => PermissionOverride ?? Defaults().Permission;
        public bool AllowAgents() => AllowAgentsOverride ?? Defaults().AllowAgents;

        private SessionDefaults Defaults() => _hooks.Settings?.Invoke() ?? new SessionDefaults();

        /// <summary>Changing the engine changes which program answers, so the process has to go.</summary>
        public void SetEngine(string engine)
        {
            if (Engine() == engine) return;
            EngineOverride = engine;
            Stop();
        }

        // Each of these restarts the CLI on the next send: they are start-up arguments, not
        // something the running process can be told about.
        public void SetModel(string model) { ModelOverride = model; Stop(); }
        public void SetEffort(string effort) { EffortOverride = effort; Stop(); }
        public void SetPermission(string mode) { PermissionOverride = mode; Stop(); }
        public void SetAllowAgents(bool value) { AllowAgentsOverride = value; Stop(); }

        // ---- Lifecycle ----

        public void EnsureCli()
        {
            if (Cli != null) return;

            var engine = Engine();
            var model = Model();
            var effort = Effort();

            Cli = new CliProcessManager(new CliOptions
            {
                ExecutablePath = _hooks.ClaudePath?.Invoke(engine),
                Cwd = _hooks.Cwd?.Invoke(),
                Engine = engine,
                Server = engine == EngineIds.Tootega ? _hooks.EngineServer?.Invoke() : null,
                Model = !string.IsNullOrEmpty(model) && model != "default" ? model : null,
                Effort = !string.IsNullOrEmpty(effort) && effort != "default" ? effort : null,
                PermissionMode = Permission(),
                // Blocking subagents and workflows is the token saving; allowing them turns
                // forwarding on so their text can be shown under the Task card.
                DisallowedTools = AllowAgents() ? null : new List<string> { "Task", "Workflow" },
                ForwardSubagentText = AllowAgents(),
                // ResumeId first, but falling back to SessionId: any path that knows the
                // session but never pinned the resume id would otherwise spawn without
                // --resume and duplicate the context on disk.
                ResumeSessionId = ResumeId ?? SessionId,
                AskLanguage = _hooks.AskLanguage?.Invoke(),
                ExtraSystemPrompt = _hooks.ExtraSystemPrompt?.Invoke(),
                QuietPrompt = _hooks.QuietPrompt?.Invoke(),
                // 'on' is the CLI default: sending it changes nothing and only clutters
                // the settings file.
                SkillOverrides = SkillOverrides
                    .Where(kv => kv.Value != Protocol.SkillOverrides.On)
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            });

            Cli.Event += (s, e) => HandleEvent(e.Event);
            Cli.Stderr += (s, e) => OnStderr(e.Text);
            Cli.Exit += (s, e) => OnExit(e.ExitCode);

            Cli.Start();
        }

        private void OnStderr(string text)
        {
            Log.Info("[cli stderr] " + (text ?? string.Empty).Trim());
            if (SessionHeuristics.IsAuthError(text)) _hooks.OnAuthRequired?.Invoke();
        }

        private void OnExit(int? code)
        {
            Log.Info("CLI exited (" + code + ")");

            // Busy still set means this was not our stop or interrupt — both clear it first.
            // So the process aborted mid-turn without emitting a result, and without a warning
            // the indicator would simply vanish while the user believes it is still running.
            var abortedMidTurn = Busy;

            if (abortedMidTurn)
            {
                if (_currentAssistantId != null) Emit(HostMessages.AssistantDone(_currentAssistantId));
                Stats.EndTurn();
                Persist();
                ResetStreamingState();
            }

            SetBusy(false);
            // A dead process takes its background work with it.
            ResetBackgroundTasks();

            if (!abortedMidTurn) return;

            _hooks.OnTurnError?.Invoke(new TurnError { Kind = TurnErrorKind.Aborted, Code = code });
            Emit(HostMessages.Stats(Stats.Snapshot()));
        }

        public void Send(string text, IReadOnlyList<ImageAttachment> images = null)
        {
            EnsureCli();
            SetBusy(true);
            // The stopwatch of real execution time, which excludes idleness.
            Stats.BeginTurn();

            LogSendState(text, images);
            NoteSlashSkill(text);

            Cli.SendUserMessage(text, images);
        }

        /// <summary>
        /// One line describing what this prompt is walking into — mainly the cache state.
        ///
        /// It is the difference between "the turn was slow" and "the turn was slow because the
        /// prefix had expired and it re-paid the whole cache write".
        /// </summary>
        private void LogSendState(string text, IReadOnlyList<ImageAttachment> images)
        {
            var s = Stats.Snapshot();

            string cache;
            if (!s.CacheExpiresInMs.HasValue) cache = "cold (no previous turn)";
            else if (s.CacheAlive == true)
                cache = "warm, expires in " + (s.CacheExpiresInMs.Value / 60000.0).ToString("F1") + "m";
            else
                cache = "EXPIRED, re-caching this turn";

            Log.Info("[session] send (" + (SessionId ?? ResumeId ?? "new") + "): " +
                     (text ?? string.Empty).Length + " chars, " + (images?.Count ?? 0) + " img | " +
                     "ctx=" + s.ContextUsed + "/" + s.ContextLimit + " | cache: " + cache + " | " +
                     "hit=" + (s.CacheHitRate * 100).ToString("F0") + "% read=" + s.CacheReadTokens +
                     " write=" + s.CacheCreateTokens + " resets=" + (s.CacheResetCount ?? 0) + " | " +
                     "cost=$" + s.SessionCostUsd.ToString("F4") + (s.CostIsEstimate ? "~" : string.Empty) +
                     " turns=" + (s.TurnCount ?? 0));
        }

        /// <summary>
        /// A /name for a known skill is a USER invocation.
        ///
        /// That path emits NOTHING in the stream — no tool_use, no system event; the body is
        /// injected silently. Since we are the ones who sent it, it is marked here. No token
        /// count, because the engine reports none and inventing one would be worse.
        /// </summary>
        private void NoteSlashSkill(string text)
        {
            var match = SlashCommand.Match((text ?? string.Empty).Trim());
            if (!match.Success) return;

            var name = match.Groups[1].Value;

            // init carries the skill list and only arrives after the first message, so on a
            // fresh tab we cannot yet tell whether /foo is a skill. Hold it and resolve later —
            // without this, a /skill as the tab's first message was never marked.
            if (LastSkills == null)
            {
                _pendingSlashSkills.Add(name);
                return;
            }

            if (LastSkills.Contains(name)) Stats.MarkSkillActive(name, "user");
        }

        private void ResolvePendingSlashSkills()
        {
            if (_pendingSlashSkills.Count == 0) return;

            var pending = _pendingSlashSkills.ToList();
            _pendingSlashSkills.Clear();

            foreach (var name in pending)
            {
                if (LastSkills != null && LastSkills.Contains(name)) Stats.MarkSkillActive(name, "user");
            }
        }

        public void Interrupt()
        {
            Cli?.Interrupt();
            Stats.EndTurn();
            Persist();
            SetBusy(false);
            ResetBackgroundTasks();
            Log.Debug("session: interrupt (" + (SessionId ?? "?") + ")");
        }

        /// <summary>Stops the process but keeps the statistics. The next message respawns it.</summary>
        public void Stop()
        {
            ResetStreamingState();
            _pendingPermissions.Clear();
            // Closes the turn in flight, so idle time after it is not counted as work.
            Stats.EndTurn();
            Persist();

            if (Cli != null) Log.Debug("session: stop (" + (SessionId ?? ResumeId ?? "?") + ")");

            Cli?.Stop();
            Cli = null;
            SetBusy(false);
            ResetBackgroundTasks();
        }

        /// <summary>Clears the conversation entirely, statistics included.</summary>
        public void ClearConversation()
        {
            Stop();
            SessionId = null;
            // The resume id goes too. Without that, after clearing a resumed session the next
            // send would respawn with --resume of the old one and nothing would look cleared.
            ResumeId = null;
            Stats = NewStats();
            // A new conversation gets a new id, and the webview goes back to waiting for
            // history — so it has to be announced again.
            _historyAnnounced = false;
        }

        public void Resume(string sessionId)
        {
            ClearConversation();
            ResumeId = sessionId;

            // Hydrating is what keeps the numbers coherent: the CLI does not re-emit the usage
            // of old turns on --resume, so without this a reopened context reads as zero.
            var persisted = _statsStore.Load(sessionId);
            if (persisted != null) Stats.Hydrate(persisted);
            Stats.MarkReopen();

            var snapshot = Stats.Snapshot();
            Log.Debug("session: resume " + sessionId + " (" +
                      (persisted != null ? "hydrated" : "no saved stats") + "), reopen=" + snapshot.ReopenCount +
                      ", ctx=" + snapshot.ContextUsed + ", turns=" + snapshot.TurnCount);

            // Restores the context bar immediately, before any new turn.
            Emit(HostMessages.Stats(snapshot));
            EmitTimeline();
        }

        /// <summary>Persists this session's statistics. Needs an id to file them under.</summary>
        private void Persist()
        {
            var id = SessionId ?? ResumeId;
            if (id == null) return;
            _statsStore.Save(Stats.Serialize(id, _hooks.Cwd?.Invoke()));
        }

        /// <summary>
        /// Keep-alive through this session's LIVE process, rather than a parallel --resume that
        /// would conflict with it. It reuses the normal turn flow, so the result stops the
        /// stopwatch and persists the timestamp, restarting the cache life.
        ///
        /// Returns false when busy: a turn in progress already keeps the prefix warm.
        /// </summary>
        public bool KeepAlivePing()
        {
            if (Busy) return false;

            EnsureCli();
            SetBusy(true);
            Stats.BeginTurn();

            Log.Debug("session: keep-alive ping (" + (SessionId ?? ResumeId ?? "?") + ")");
            Cli.SendUserMessage("keep-alive: answer only \"ok\". Do not use tools and do not change files.");
            return true;
        }

        public void SetKeepCacheAlive(bool value)
        {
            Stats.SetKeepCacheAlive(value);
            Persist();
            Emit(HostMessages.Stats(Stats.Snapshot()));
        }

        public void ApplyLimits(LimitsBlock limits, string source)
        {
            Stats.SetLimits(limits, source);
        }

        public StatsSnapshot Snapshot() => Stats.Snapshot();

        /// <summary>Timeline and compactions — heavy, so once per turn rather than per token.</summary>
        private void EmitTimeline()
        {
            var (timeline, compactions) = Stats.TimelineSnapshot();
            Emit(HostMessages.StatsTimeline(timeline, compactions));
        }

        public void SendTimeline() => EmitTimeline();

        // ---- Skills ----

        /// <summary>
        /// Re-reads skill metadata through get_context_usage. It is a local computation in the
        /// engine: no turn, no tokens, no transcript line.
        ///
        /// Does nothing without a live CLI — spawning one just to read would put a process on
        /// the account for nothing.
        /// </summary>
        public async Task RefreshSkillsAsync()
        {
            if (Cli == null || !Cli.IsRunning) return;

            var payload = await Cli.RequestControlAsync("get_context_usage").ConfigureAwait(false);
            var info = ContextUsage.Parse(payload);
            // A CLI without the subtype, or a payload in a new shape: keep what we had rather
            // than blanking the panel.
            if (info == null) return;

            Stats.ApplyContextUsage(info);
            Emit(HostMessages.Stats(Stats.Snapshot()));
        }

        /// <summary>
        /// Sets a skill's listing override. The effect is real — the listing shrinks — but only
        /// from the next spawn, because to the CLI this is start-up configuration.
        ///
        /// Context already loaded is preserved: a skill body that entered does NOT leave here.
        /// </summary>
        public void SetSkillOverride(string name, string value)
        {
            if (value == Protocol.SkillOverrides.On) SkillOverrides.Remove(name);
            else SkillOverrides[name] = value;

            Stats.SetSkillOverrides(SkillOverrides);
            Stop();
            Emit(HostMessages.Stats(Stats.Snapshot()));
        }

        // ---- Control protocol ----

        public void Decide(string requestId, string decision, string message = null)
        {
            _pendingPermissions.TryGetValue(requestId, out var pending);
            _pendingPermissions.Remove(requestId);

            if (pending?.Tool != null)
            {
                Stats.RecordDecision(pending.Tool, decision, decision == "deny" ? message : null);
                Emit(HostMessages.Stats(Stats.Snapshot()));
            }

            if (decision == "deny")
            {
                Cli?.SendControlResponse(requestId, new Dictionary<string, object>
                {
                    ["behavior"] = "deny",
                    // The user's feedback, which in editable plan mode is their notes.
                    ["message"] = string.IsNullOrWhiteSpace(message) ? "Denied by user" : message.Trim(),
                });
                return;
            }

            var response = new Dictionary<string, object>
            {
                ["behavior"] = "allow",
                // Required: the CLI validates the response and rejects an allow without it.
                ["updatedInput"] = pending?.Input ?? (object)new Dictionary<string, object>(),
            };

            if (decision == "allow_always" && pending?.Suggestions != null && pending.Suggestions.Count > 0)
                response["updatedPermissions"] = pending.Suggestions;

            Cli?.SendControlResponse(requestId, response);
        }

        public void Answer(string requestId, IDictionary<string, string> answers)
        {
            _pendingPermissions.TryGetValue(requestId, out var pending);
            _pendingPermissions.Remove(requestId);

            var input = new Dictionary<string, object>();
            if (pending?.Input?.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in pending.Input.Value.EnumerateObject()) input[property.Name] = property.Value;
            }
            input["answers"] = answers;

            Cli?.SendControlResponse(requestId, new Dictionary<string, object>
            {
                ["behavior"] = "allow",
                ["updatedInput"] = input,
            });
        }

        // ---- Internals ----

        private void Emit(HostMessage message) => _hooks.Emit?.Invoke(message);

        private void SetBusy(bool busy)
        {
            Busy = busy;
            // A turn that ends without a boundary — interrupted, or an error while compacting —
            // must not leave the indicator lit: nothing is being condensed after it is over.
            if (!busy) SetCompacting(false);
            _hooks.OnBusy?.Invoke(busy);
        }

        /// <summary>Announces compaction once; the status event repeats every thirty seconds.</summary>
        private void SetCompacting(bool active)
        {
            if (_compacting == active) return;
            _compacting = active;
            Emit(HostMessages.Compaction(active));
        }

        /// <summary>
        /// Routes one stream event. Internal rather than private so the whole state machine can
        /// be exercised against real event payloads without spawning a CLI.
        /// </summary>
        internal void HandleEvent(ClaudeEvent value)
        {
            if (value == null) return;

            // A subagent event carries the Task that launched it. Its narration is routed to
            // that card and stops here: it must not reach the main bubble, nor the statistics —
            // subagent cost stays sourced from the authoritative totals, so the live counters
            // read the same with forwarding on or off.
            var parentId = ReadExtraString(value, "parent_tool_use_id");
            if (!string.IsNullOrEmpty(parentId) && value.Type != EventTypes.ControlRequest)
            {
                // A subagent's permission prompt still has to reach the normal handler; only
                // its narration is diverted.
                OnSubagentEvent(parentId, value);
                return;
            }

            Emit(HostMessages.Stats(Stats.Ingest(value)));

            // A skill body that just entered the context becomes a seal on its card.
            foreach (var load in Stats.TakeSkillLoads())
                Emit(HostMessages.SkillLoaded(load.ToolUseId, load.Name, load.Tokens));

            // Hook-injected context has no card to seal, so it becomes its own timeline band.
            foreach (var load in Stats.TakeHookLoads())
                Emit(HostMessages.HookInjected(load.Hook, load.Event, load.Skill, load.Tokens));

            switch (value.Type)
            {
                case EventTypes.System: OnSystem(value); break;
                case EventTypes.StreamEvent: OnRawStream(value.Event); break;
                case EventTypes.Assistant: OnAssistant(value); break;
                case EventTypes.User: OnUser(value); break;
                case EventTypes.ControlRequest: OnControlRequest(value); break;
                case EventTypes.ControlResponse: OnControlResponse(value); break;
                case EventTypes.Result: OnResult(value); break;
                case EventTypes.RateLimitEvent: OnRateLimit(value.RateLimitInfo); break;
            }
        }

        private void OnSystem(ClaudeEvent value)
        {
            var raw = RawElement(value);

            switch (value.Subtype)
            {
                case "background_tasks_changed":
                    SyncBackgroundTasks(raw);
                    return;

                case "task_started":
                {
                    var toolUseId = ReadExtraString(value, "tool_use_id");
                    var taskId = ReadExtraString(value, "task_id");
                    if (taskId == null) return;

                    if (toolUseId == null || !_toolNames.TryGetValue(toolUseId, out var tool))
                        tool = SessionHeuristics.TaskTool(ReadExtraString(value, "task_type"));
                    if (toolUseId != null) _toolNames.Remove(toolUseId);

                    AddBackgroundTask(taskId, tool, ReadExtraString(value, "description") ?? taskId);
                    return;
                }

                case "task_notification":
                case "task_updated":
                {
                    var taskId = ReadExtraString(value, "task_id");
                    var status = ReadExtraString(value, "status") ?? ReadNestedString(raw, "patch", "status");

                    // Anything other than running means the task is gone — finished, failed, or
                    // killed by the agent. background_tasks_changed covers this too, but closing
                    // here avoids depending on the order between the two events.
                    if (taskId != null && !string.IsNullOrEmpty(status) && status != "running")
                        ClearBackgroundTask(taskId);

                    // A task finishing while the session is idle makes the CLI open a turn of
                    // its own to react. Without marking busy, that turn's result would be
                    // discarded as a stray replay and never counted. A killed task opens no
                    // turn, so marking busy there would leave the spinner stuck.
                    if (value.Subtype == "task_notification" && !Busy &&
                        (status == "completed" || status == "failed"))
                    {
                        SetBusy(true);
                        Stats.BeginTurn();
                    }
                    return;
                }

                case "status":
                {
                    var status = ReadExtraString(value, "status");
                    if (status == "compacting") SetCompacting(true);
                    else if (HasExtra(value, "compact_result") || HasExtra(value, "compact_error")) SetCompacting(false);
                    return;
                }

                case "compact_boundary":
                    // The flag is cleared without announcing it, because the boundary message
                    // below already says compaction is over AND carries the numbers. Emitting
                    // both would send two compaction messages back to back, the first with no
                    // data, which reads as a flicker in the timeline.
                    _compacting = false;
                    EmitCompactBoundary(raw);
                    return;

                case "init":
                    OnInit(value, raw);
                    return;
            }

            var notice = SessionHeuristics.ReadEngineNotice(raw);
            if (notice == null) return;

            // Once per session per warning: the CLI may repeat it every turn.
            if (!_noticesSeen.Add(notice.Id)) return;
            Emit(HostMessages.EngineNotice(notice.Id, notice.Text, notice.Topic));
        }

        private void OnInit(ClaudeEvent value, JsonElement raw)
        {
            if (value.SlashCommands != null) SlashCommands = value.SlashCommands.ToList();

            LastTools = value.Tools;
            LastMcpServers = value.McpServers;
            LastMcpErrors = McpInventory.ParseErrors(value.McpServerErrors);
            LastSkills = ReadStringArray(raw, "skills");

            // An engine that knows its own window says so. Without this the meter would show
            // the Claude default of 200K over a context that is really much smaller, and the
            // bar would never leave 1%.
            if (value.ContextWindow.GetValueOrDefault() > 0) Stats.SetContextLimit(value.ContextWindow.Value);

            // The local engine keeps no transcript on disk: its conversation IS the timeline.
            // Saying "history loaded, and it is empty" right here is what lets the view paint,
            // since the webview hides everything while a session id is set and history never
            // arrived.
            if (Engine() == EngineIds.Tootega && !_historyAnnounced)
            {
                _historyAnnounced = true;
                Emit(HostMessages.History(new List<HistoryItem>()));
            }

            ResolvePendingSlashSkills();
            SessionId = value.SessionId;

            if (!string.IsNullOrEmpty(value.SessionId))
            {
                // A silent respawn must continue THIS session.
                Cli?.SetResumeId(value.SessionId);
                // Pinned at session level too: a Stop discards the process manager, and the
                // next send respawns through EnsureCli reading this. Without it that respawn
                // would start WITHOUT --resume and the CLI would create a second transcript —
                // a duplicated context in the hub.
                ResumeId = value.SessionId;
                Persist();
                Log.Debug("session: init sessionId=" + value.SessionId + " model=" + (value.Model ?? "?"));
            }

            Emit(HostMessages.SessionInit(value.SessionId, value.Model, value.Cwd, value.PermissionMode,
                                          value.Tools, value.McpServers, SlashCommands));

            _hooks.OnInit?.Invoke(value.Model, SlashCommands);
        }

        private void OnControlRequest(ClaudeEvent value)
        {
            if (value.Request?.Subtype != "can_use_tool") return;

            var requestId = value.RequestId;
            if (requestId == null) return;

            var tool = value.Request.ToolName ?? "tool";
            var input = value.Request.Input;

            List<PermissionSuggestion> suggestions = null;
            if (value.Request.Extra != null &&
                value.Request.Extra.TryGetValue("permission_suggestions", out var raw) &&
                raw.ValueKind == JsonValueKind.Array)
            {
                suggestions = Json.TryDeserialize<List<PermissionSuggestion>>(raw);
            }

            _pendingPermissions[requestId] = new PendingPermission
            {
                Tool = tool,
                Input = input,
                Suggestions = suggestions,
            };

            _hooks.OnInteraction?.Invoke();

            if (tool == "AskUserQuestion")
            {
                var questions = new List<AskQuestion>();
                if (input?.ValueKind == JsonValueKind.Object &&
                    input.Value.TryGetProperty("questions", out var questionsRaw) &&
                    questionsRaw.ValueKind == JsonValueKind.Array)
                {
                    questions = Json.TryDeserialize<List<AskQuestion>>(questionsRaw) ?? new List<AskQuestion>();
                }

                Emit(HostMessages.AskRequest(requestId, questions));
                return;
            }

            Emit(HostMessages.PermissionRequest(
                requestId, tool, input,
                ReadRequestString(value, "display_name"),
                ReadRequestString(value, "description"),
                suggestions,
                // Current content on disk, so the modal can show a diff rather than just the
                // proposed text.
                _hooks.FileText?.Invoke(tool, input)));
        }

        private void OnControlResponse(ClaudeEvent value)
        {
            // The initialize handshake answers before the first message, while init only
            // arrives after one — so this is what gives a fresh tab its command autocomplete.
            if (value.Extra == null || !value.Extra.TryGetValue("response", out var response)) return;

            var commands = SessionHeuristics.ExtractSlashCommands(response);
            if (commands.Count == 0 && response.ValueKind == JsonValueKind.Object &&
                response.TryGetProperty("response", out var inner))
            {
                commands = SessionHeuristics.ExtractSlashCommands(inner);
            }

            if (commands.Count == 0 || SlashCommands.Count > 0) return;

            SlashCommands = commands.ToList();
            Emit(HostMessages.SlashCommands(SlashCommands));
            _hooks.OnInit?.Invoke(null, SlashCommands);
        }

        private void OnResult(ClaudeEvent value)
        {
            // Only what WE started counts. A result with busy unset is a stray or a replay —
            // the CLI re-emits turns on --resume — and processing it would inflate the local
            // turn count and cost.
            if (!Busy)
            {
                Log.Debug("session: result ignored (not busy), CLI stray or replay");
                return;
            }

            var errorText = (value.Result ?? ReadExtraString(value, "error") ?? string.Empty).Trim();

            if (value.IsError == true && SessionHeuristics.IsAuthError(errorText))
            {
                _hooks.OnAuthRequired?.Invoke();
            }
            else if (value.IsError == true)
            {
                // A transient failure gets a soft warning; a real one carries the CLI's own
                // text. Without either, the turn would just die with no explanation.
                var transient = SessionHeuristics.IsTransientError(errorText, value.Subtype);
                Log.Info("[session] result " + (transient ? "transient" : "error") + ": " +
                         (errorText.Length > 160 ? errorText.Substring(0, 160) : errorText));

                _hooks.OnTurnError?.Invoke(new TurnError
                {
                    Kind = transient ? TurnErrorKind.Transient : TurnErrorKind.Error,
                    Text = errorText.Length > 0 ? errorText : null,
                });
            }

            Stats.EndTurn();

            Emit(HostMessages.TurnComplete(value.TotalCostUsd, value.Usage));
            Emit(HostMessages.Stats(Stats.Snapshot()));
            EmitTimeline();
            Persist();

            SetBusy(false);
            _hooks.OnResult?.Invoke();
            ResetStreamingState();
        }

        /// <summary>
        /// An account limit from the stream. The automatic channel, needing no statusline:
        /// status, reset and window always; the percentage only near the limit.
        /// </summary>
        private void OnRateLimit(RateLimitInfo info)
        {
            if (info == null) return;

            var which = info.RateLimitType == RateLimitBuckets.FiveHour ? "fiveHour"
                : info.RateLimitType == RateLimitBuckets.SevenDay ? "sevenDay"
                : null;
            // The per-model weekly and overage buckets are outside the two displayed windows.
            if (which == null) return;

            double? percent = info.Utilization;
            // Defensive: the field has arrived as 0..100 in some versions.
            if (percent.HasValue && percent.Value > 1.5) percent = percent.Value / 100.0;

            string resetsAt = null;
            if (info.ResetsAt.HasValue)
            {
                var raw = info.ResetsAt.Value;
                var milliseconds = raw > 1e12 ? raw : raw * 1000;
                try
                {
                    resetsAt = DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds).UtcDateTime.ToString("o");
                }
                catch (ArgumentOutOfRangeException)
                {
                    // An impossible epoch is left absent rather than shown wrong.
                }
            }

            var status = info.Status == "allowed" || info.Status == "allowed_warning" || info.Status == "rejected"
                ? info.Status
                : null;

            Stats.SetStreamLimit(which, new LimitWindow { UsedPct = percent, ResetsAt = resetsAt, Status = status });
            Emit(HostMessages.Stats(Stats.Snapshot()));
        }

        private void OnRawStream(JsonElement? rawEvent)
        {
            if (rawEvent?.ValueKind != JsonValueKind.Object) return;
            var raw = rawEvent.Value;

            var type = ReadString(raw, "type");
            if (type == null) return;

            switch (type)
            {
                case "message_start":
                {
                    var message = raw.TryGetProperty("message", out var m) ? m : (JsonElement?)null;
                    var id = message.HasValue ? ReadString(message.Value, "id") : null;
                    _currentAssistantId = id ?? ("m_" + _emittedTools.Count + "_" + _toolBuffers.Count);

                    _toolBuffers.Clear();
                    Emit(HostMessages.AssistantStart(_currentAssistantId));
                    return;
                }

                case "content_block_start":
                {
                    var index = ReadInt(raw, "index");
                    if (!raw.TryGetProperty("content_block", out var block) || block.ValueKind != JsonValueKind.Object)
                        return;

                    if (ReadString(block, "type") != "tool_use") return;

                    _toolBuffers[index] = new ToolBuffer
                    {
                        Id = ReadString(block, "id"),
                        Name = ReadString(block, "name"),
                    };
                    return;
                }

                case "content_block_delta":
                {
                    var index = ReadInt(raw, "index");
                    if (!raw.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) return;

                    var id = _currentAssistantId ?? "m";
                    switch (ReadString(delta, "type"))
                    {
                        case "text_delta":
                            // Recorded so the full assistant event that follows is not emitted
                            // a second time.
                            _streamedText.Add(id);
                            Emit(HostMessages.AssistantText(id, ReadString(delta, "text") ?? string.Empty));
                            return;

                        case "thinking_delta":
                            Emit(HostMessages.Thinking(id, ReadString(delta, "thinking") ?? string.Empty));
                            return;

                        case "input_json_delta":
                            if (_toolBuffers.TryGetValue(index, out var buffer))
                                buffer.Json += ReadString(delta, "partial_json") ?? string.Empty;
                            return;
                    }
                    return;
                }

                case "content_block_stop":
                {
                    var index = ReadInt(raw, "index");
                    if (!_toolBuffers.TryGetValue(index, out var buffer)) return;
                    if (buffer.Id == null || !_emittedTools.Add(buffer.Id)) return;

                    var input = SessionHeuristics.SafeJson(buffer.Json);
                    Emit(HostMessages.ToolUse(buffer.Id, buffer.Name, input));
                    _hooks.OnToolUse?.Invoke(buffer.Name, input);
                    return;
                }

                case "message_stop":
                    if (_currentAssistantId != null) Emit(HostMessages.AssistantDone(_currentAssistantId));
                    return;
            }
        }

        private void OnAssistant(ClaudeEvent value)
        {
            var message = value.AsAssistantMessage();
            var blocks = message?.Content ?? new List<ContentBlock>();
            var id = message?.Id ?? _currentAssistantId ?? ("m_" + _emittedTools.Count);

            // Only when the text did NOT arrive as deltas: otherwise it would be shown twice.
            if (!_streamedText.Contains(id))
            {
                var text = string.Concat(blocks.Where(b => b?.Type == "text").Select(b => b.Text ?? string.Empty));
                if (text.Length > 0)
                {
                    Emit(HostMessages.AssistantStart(id));
                    Emit(HostMessages.AssistantText(id, text));
                    Emit(HostMessages.AssistantDone(id));
                }
            }

            foreach (var block in blocks)
            {
                if (block?.Type != "tool_use" || block.Id == null) continue;
                if (!_emittedTools.Add(block.Id)) continue;

                Emit(HostMessages.ToolUse(block.Id, block.Name, block.Input));
                _hooks.OnToolUse?.Invoke(block.Name, block.Input);

                // A background launch: remember the name so the task can be labelled when the
                // matching task_started arrives, since that event carries no tool name.
                var background = block.Input?.ValueKind == JsonValueKind.Object &&
                                 block.Input.Value.TryGetProperty("run_in_background", out var flag) &&
                                 flag.ValueKind == JsonValueKind.True;

                if (block.Name == "Workflow" || background) _toolNames[block.Id] = block.Name;
            }
        }

        /// <summary>
        /// A subagent event forwarded by the CLI. Only its visible TEXT is surfaced, attributed
        /// to the Task that launched it — thinking, tool calls and usage are deliberately kept
        /// out of the main timeline.
        /// </summary>
        private void OnSubagentEvent(string parentId, ClaudeEvent value)
        {
            var delta = string.Empty;

            if (value.Type == EventTypes.StreamEvent)
            {
                if (value.Event?.ValueKind == JsonValueKind.Object &&
                    value.Event.Value.TryGetProperty("delta", out var d) &&
                    d.ValueKind == JsonValueKind.Object &&
                    ReadString(d, "type") == "text_delta")
                {
                    delta = ReadString(d, "text") ?? string.Empty;
                }

                if (delta.Length > 0) _subagentStreamed.Add(parentId);
            }
            else if (value.Type == EventTypes.Assistant && !_subagentStreamed.Contains(parentId))
            {
                // No partials were seen for this parent, so the full message is the text.
                var blocks = value.AsAssistantMessage()?.Content ?? new List<ContentBlock>();
                delta = string.Concat(blocks.Where(b => b?.Type == "text").Select(b => b.Text ?? string.Empty));
            }

            if (delta.Length > 0) Emit(HostMessages.SubagentText(parentId, delta));
        }

        private void OnUser(ClaudeEvent value)
        {
            var message = value.AsUserMessage();
            if (message?.Content?.ValueKind != JsonValueKind.Array) return;

            var blocks = Json.TryDeserialize<List<ContentBlock>>(message.Content.Value);
            if (blocks == null) return;

            foreach (var block in blocks)
            {
                if (block?.Type != "tool_result") continue;
                Emit(HostMessages.ToolResult(block.ToolUseId, block.Content, block.IsError));
            }
        }

        // ---- Background tasks ----

        private void AddBackgroundTask(string id, string tool, string label)
        {
            if (_bgTasks.TryGetValue(id, out var current) && current.Tool == tool && current.Label == label) return;

            _bgTasks[id] = new BackgroundTask { Id = id, Tool = tool, Label = label };
            EmitBackground();
        }

        private void ClearBackgroundTask(string id)
        {
            if (!_bgTasks.Remove(id)) return;
            EmitBackground();
        }

        /// <summary>
        /// background_tasks_changed carries the COMPLETE list of what is running, so it is
        /// reconciled against rather than merged into: whatever died disappears — including
        /// tasks the agent killed, which emit no notification — and whatever the UI never saw
        /// being born appears, such as a resumed session with work already in flight.
        /// </summary>
        private void SyncBackgroundTasks(JsonElement raw)
        {
            if (!raw.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            {
                if (_bgTasks.Count == 0) return;
                _bgTasks.Clear();
                EmitBackground();
                return;
            }

            var live = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var task in tasks.EnumerateArray())
            {
                var id = ReadString(task, "task_id");
                if (id != null) live[id] = task;
            }

            var changed = false;

            foreach (var id in _bgTasks.Keys.ToList())
            {
                if (live.ContainsKey(id)) continue;
                _bgTasks.Remove(id);
                changed = true;
            }

            foreach (var entry in live)
            {
                if (_bgTasks.ContainsKey(entry.Key)) continue;
                _bgTasks[entry.Key] = new BackgroundTask
                {
                    Id = entry.Key,
                    Tool = SessionHeuristics.TaskTool(ReadString(entry.Value, "task_type")),
                    Label = ReadString(entry.Value, "description") ?? entry.Key,
                };
                changed = true;
            }

            if (changed) EmitBackground();
        }

        private void ResetBackgroundTasks()
        {
            _toolNames.Clear();
            if (_bgTasks.Count == 0) return;
            _bgTasks.Clear();
            EmitBackground();
        }

        private void EmitBackground()
        {
            Emit(HostMessages.Background(_bgTasks.Values.ToList()));
        }

        /// <summary>
        /// Seals a compaction with what the CLI measured. Shape-tolerant: a version that stops
        /// sending a field just makes that number disappear from the band.
        /// </summary>
        private void EmitCompactBoundary(JsonElement raw)
        {
            JsonElement meta = default;
            if (!raw.TryGetProperty("compact_metadata", out meta) &&
                !raw.TryGetProperty("compactMetadata", out meta))
            {
                Emit(HostMessages.Compaction(false));
                return;
            }

            Emit(HostMessages.Compaction(false,
                ReadLong(meta, "pre_tokens") ?? ReadLong(meta, "preTokens"),
                ReadLong(meta, "post_tokens") ?? ReadLong(meta, "postTokens"),
                ReadString(meta, "trigger"),
                ReadDouble(meta, "duration_ms") ?? ReadDouble(meta, "durationMs")));
        }

        private void ResetStreamingState()
        {
            _currentAssistantId = null;
            _streamedText.Clear();
            _toolBuffers.Clear();
            _emittedTools.Clear();
            _subagentStreamed.Clear();
        }

        // ---- Element helpers ----

        /// <summary>
        /// Rebuilds the raw element of an event, so subtype-specific fields the DTO does not
        /// model stay reachable. Cheap, and only for events that need it.
        /// </summary>
        private static JsonElement RawElement(ClaudeEvent value)
        {
            try
            {
                using (var document = JsonDocument.Parse(Json.Serialize(value)))
                {
                    return document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                using (var document = JsonDocument.Parse("{}"))
                {
                    return document.RootElement.Clone();
                }
            }
        }

        private static bool HasExtra(ClaudeEvent value, string name)
        {
            return value.Extra != null && value.Extra.ContainsKey(name);
        }

        private static string ReadExtraString(ClaudeEvent value, string name)
        {
            if (value.Extra == null || !value.Extra.TryGetValue(name, out var element)) return null;

            if (element.ValueKind == JsonValueKind.String)
            {
                var text = element.GetString();
                return string.IsNullOrEmpty(text) ? null : text;
            }

            // Ids arrive as numbers in some versions; both are the same identifier.
            return element.ValueKind == JsonValueKind.Number ? element.ToString() : null;
        }

        private static string ReadRequestString(ClaudeEvent value, string name)
        {
            if (value.Request?.Extra == null) return null;
            if (!value.Request.Extra.TryGetValue(name, out var element)) return null;
            return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        }

        private static string ReadNestedString(JsonElement parent, string child, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            if (!parent.TryGetProperty(child, out var nested) || nested.ValueKind != JsonValueKind.Object) return null;
            return ReadString(nested, name);
        }

        private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return null;

            var values = new List<string>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) values.Add(item.GetString());
            }
            return values;
        }

        private static string ReadString(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        private static int ReadInt(JsonElement parent, string name)
        {
            return parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                   value.TryGetInt32(out var number)
                ? number
                : 0;
        }

        private static long? ReadLong(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            return parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                   value.TryGetInt64(out var number)
                ? number
                : (long?)null;
        }

        private static double? ReadDouble(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            return parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                   value.TryGetDouble(out var number)
                ? number
                : (double?)null;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
