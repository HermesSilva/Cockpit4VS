using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Stats
{
    internal sealed class SkillLoad
    {
        public string Name { get; set; }
        public string ToolUseId { get; set; }
        public long? Tokens { get; set; }
    }

    internal sealed class HookLoad
    {
        public string Hook { get; set; }
        public string Event { get; set; }
        public string Skill { get; set; }
        public long? Tokens { get; set; }
    }

    /// <summary>
    /// Turns the event stream into the StatsSnapshot the panel renders. Port of
    /// src/stats/StatsAggregator.ts.
    ///
    /// This is the heart of the product's differentiator, so it is opinionated about honesty:
    /// a real cost reported by the CLI always beats our table estimate; a figure we cannot
    /// measure is omitted rather than guessed; and the panel shows the turn in flight so it
    /// does not read zero while a slow first turn is already filling the context.
    ///
    /// It does no I/O and knows nothing about the workspace. Anything requiring either — the
    /// skill-body recogniser, the discovered context windows, whether the 1M window is
    /// disabled — is injected.
    /// </summary>
    internal sealed class StatsAggregator
    {
        /// <summary>
        /// A turn counts as a cold TTL reset when it idled past the cache life, read almost
        /// nothing from cache and rewrote the prefix. Conservative on purpose: a false
        /// positive would invent a cost that never happened.
        /// </summary>
        private const double ColdReadFraction = 0.1;

        /// <summary>
        /// Compaction: the TOTAL context shrank below this fraction of the previous turn. A
        /// cold reset does not shrink the total — it only shifts read into create — so the two
        /// detections cannot collide.
        /// </summary>
        private const double CompactFraction = 0.6;

        /// <summary>Denials kept for the audit log — enough to review a session.</summary>
        private const int DenialCap = 50;

        /// <summary>Cap on the tool_use_id to error-text map: almost no error is a denial.</summary>
        private const int DenialReasonCap = 200;

        /// <summary>The reason is a UI label, not a log entry, so it is cut before it becomes a paragraph.</summary>
        private const int ReasonMax = 300;

        /// <summary>
        /// The tool_result of `Skill` when the CLI actually LOADS the SKILL.md into the
        /// context. A second path ("Execute skill: …") injects no body at all, so nothing
        /// enters the context and nothing is marked.
        /// </summary>
        private const string SkillLaunchPrefix = "Launching skill:";

        private const int SkillToolUseCap = 200;

        /// <summary>Text tool_result: roughly four characters per token.</summary>
        private const int CharsPerToken = 4;

        private sealed class SkillMeta
        {
            public string Source { get; set; }
            public long? Tokens { get; set; }
        }

        private sealed class ActiveSkill
        {
            public long At { get; set; }
            public string By { get; set; }
            public long? Tokens { get; set; }
        }

        private string _model;
        private string _mode;
        private long _contextLimit;
        private bool _autoLimit;
        private long _contextUsed;

        private long _inputTokens;
        private long _outputTokens;
        private long _cacheCreateTokens;
        private long _cacheReadTokens;

        // Turn in flight, from message_start/message_delta. Shown until the assistant event
        // consolidates it, so the panel does not read zero during a slow first turn when the
        // context is already full.
        private long _currentInput;
        private long _currentOutput;
        private long _currentCreate;
        private long _currentRead;

        private double _sessionCostUsd;
        private double _lastTurnCostUsd;
        private double _lastTurnHitRate;
        private bool _costIsEstimate = true;

        private long? _sessionStartTs;

        private Dictionary<string, ToolDecisionCounts> _toolDecisions = new Dictionary<string, ToolDecisionCounts>(StringComparer.Ordinal);
        private List<DenialEvent> _denials = new List<DenialEvent>();
        /// <summary>Error text per tool_use_id, awaiting the result that says whether it was a denial.</summary>
        private readonly Dictionary<string, string> _denialReasons = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _seenDenials = new HashSet<string>(StringComparer.Ordinal);

        // Counters that survive a reopen.
        private int _turnCount;
        private int _reopenCount;
        private int _cacheResetCount;
        private double _cacheRecacheCostUsd;
        private int _compactionCount;
        private long _peakContextUsed;
        private long? _peakContextTs;
        private long _peakCacheTokens;

        /// <summary>Real execution time: each prompt's duration summed, excluding idleness.</summary>
        private long _activeMs;
        private long? _turnStartTs;

        private bool _keepCacheAlive;

        // Between-turn detection state.
        private long _previousContextUsed;
        private long _previousCacheRead;
        private long _lastTurnTs;

        private Dictionary<string, ModelUsage> _perModel = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
        private List<TimelineSample> _timeline = new List<TimelineSample>();
        private List<CompactionEvent> _compactions = new List<CompactionEvent>();

        private LimitsBlock _limits = new LimitsBlock();
        /// <summary>real | estimate — 'real' means the statusline supplied it.</summary>
        private string _limitsSource = "estimate";

        /// <summary>
        /// Limits from the stream (rate_limit_event), kept apart from the statusline/estimate
        /// channel so neither overwrites the other, and merged at snapshot time.
        /// </summary>
        private readonly LimitsBlock _streamLimits = new LimitsBlock();
        private bool _streamSeen;

        // Skills
        private readonly Dictionary<string, SkillMeta> _skillMeta = new Dictionary<string, SkillMeta>(StringComparer.Ordinal);
        private long? _skillsListingTokens;
        private int? _skillsTotal;
        private int? _skillsListed;
        private readonly Dictionary<string, ActiveSkill> _skillsActive = new Dictionary<string, ActiveSkill>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _skillByToolUse = new Dictionary<string, string>(StringComparer.Ordinal);
        private SkillLoad _skillAwaitingBody;
        private List<SkillLoad> _skillLoads = new List<SkillLoad>();
        private Dictionary<string, string> _skillOverrides = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, HookInjection> _hookInjections = new Dictionary<string, HookInjection>(StringComparer.Ordinal);
        private List<HookLoad> _hookLoads = new List<HookLoad>();

        /// <summary>
        /// Recognises a skill from an injected body by matching SKILL.md files on disk.
        /// Injected because this type does no I/O and knows no workspace.
        /// </summary>
        private Func<string, string> _skillByBody;

        /// <param name="configuredLimit">
        /// Above zero is a manual override; zero means auto, derived from the active model.
        /// </param>
        public StatsAggregator(long configuredLimit)
        {
            if (configuredLimit > 0)
            {
                _contextLimit = configuredLimit;
                _autoLimit = false;
            }
            else
            {
                _contextLimit = CostModel.DeriveContextLimit(null);
                _autoLimit = true;
            }
        }

        /// <summary>
        /// Sets the model.
        ///
        /// <paramref name="authoritative"/> distinguishes the init event and explicit
        /// overrides — which carry the [1m] suffix and therefore define the limit — from
        /// per-message events, whose API id comes WITHOUT the suffix. Letting the latter win
        /// would silently downgrade a 1M session's limit to 200K.
        /// </summary>
        public void SetModel(string model, bool authoritative = false)
        {
            var normalized = CostModel.NormalizeModel(model);
            if (string.IsNullOrEmpty(normalized)) return;

            if (authoritative)
            {
                _model = normalized;
                if (_autoLimit) _contextLimit = CostModel.DeriveContextLimit(normalized);
            }
            else if (string.IsNullOrEmpty(_model))
            {
                _model = normalized;
            }
        }

        public void SetMode(string mode)
        {
            if (!string.IsNullOrEmpty(mode)) _mode = mode;
        }

        public void SetContextLimit(long limit)
        {
            if (limit <= 0) return;
            _contextLimit = limit;
            _autoLimit = false;
        }

        /// <summary>
        /// Recomputes the limit for the active model. Called when model discovery answers
        /// AFTER the session started — otherwise a natively-1M model with no [1m] suffix would
        /// stay pinned at 200K for the whole session. Returns true when the value changed.
        /// </summary>
        public bool RefreshContextLimit()
        {
            if (!_autoLimit || string.IsNullOrEmpty(_model)) return false;
            var next = CostModel.DeriveContextLimit(_model);
            if (next == _contextLimit) return false;
            _contextLimit = next;
            return true;
        }

        /// <summary>Account limits: real from the statusline, or a local estimate.</summary>
        public void SetLimits(LimitsBlock limits, string source = "estimate")
        {
            _limits = limits ?? new LimitsBlock();
            _limitsSource = source;
        }

        /// <summary>
        /// A window from the stream. Merged per bucket, because the events arrive one bucket
        /// at a time and must not clear the other window.
        /// </summary>
        public void SetStreamLimit(string which, LimitWindow window)
        {
            if (window == null) return;

            var existing = which == "fiveHour" ? _streamLimits.FiveHour : _streamLimits.SevenDay;
            var merged = new LimitWindow
            {
                UsedPct = window.UsedPct ?? existing?.UsedPct,
                ResetsAt = window.ResetsAt ?? existing?.ResetsAt,
                Status = window.Status ?? existing?.Status,
                Usd = window.Usd ?? existing?.Usd,
                Tokens = window.Tokens ?? existing?.Tokens,
            };

            if (which == "fiveHour") _streamLimits.FiveHour = merged;
            else _streamLimits.SevenDay = merged;

            _streamSeen = true;
        }

        // --- Skills ---

        /// <summary>Listing metadata from get_context_usage. Spends no turn and no tokens.</summary>
        public void ApplyContextUsage(ContextUsageInfo info)
        {
            if (info == null) return;

            _skillMeta.Clear();
            foreach (var skill in info.Skills ?? new List<ContextUsageSkill>())
            {
                if (string.IsNullOrEmpty(skill?.Name)) continue;
                _skillMeta[skill.Name] = new SkillMeta { Source = skill.Source, Tokens = skill.Tokens };
            }

            _skillsListingTokens = info.ListingTokens;
            _skillsTotal = info.TotalSkills;
            _skillsListed = info.IncludedSkills;
        }

        /// <summary>Overrides in force this session. Displayed here; applied at CLI spawn.</summary>
        public void SetSkillOverrides(IDictionary<string, string> overrides)
        {
            _skillOverrides = overrides == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(overrides, StringComparer.Ordinal);
        }

        /// <summary>The skill-body recogniser. Session wires the real one; tests inject a fake.</summary>
        public void SetSkillBodyResolver(Func<string, string> resolver)
        {
            _skillByBody = resolver;
        }

        /// <summary>
        /// Marks a skill as active, meaning its SKILL.md body is in the context.
        ///
        /// <paramref name="tokens"/> is only present when the injected body could be measured.
        /// A /name invocation reports nothing, so the cost stays unknown — omitted rather than
        /// invented.
        /// </summary>
        public void MarkSkillActive(string name, string by, long? tokens = null)
        {
            if (string.IsNullOrEmpty(name)) return;

            _skillsActive.TryGetValue(name, out var current);
            var largest = Math.Max(tokens.GetValueOrDefault(), current?.Tokens ?? 0);

            _skillsActive[name] = new ActiveSkill
            {
                At = current?.At ?? Now(),
                By = current?.By ?? by,
                // The largest sighting wins: a hook may re-inject a short summary of the same
                // skill after the full body, and what weighs on the context is the body.
                Tokens = largest > 0 ? largest : (long?)null,
            };
        }

        /// <summary>
        /// A hook returned text and the CLI injected it into the context.
        ///
        /// Always accounted per hook — the text weighs in the prompt whether or not it is a
        /// skill. When it matches a SKILL.md body the skill is also marked loaded, because
        /// that is the ONLY signal for this path: a hook emits no Skill tool_use and goes
        /// through no /name.
        /// </summary>
        private void NoteHookOutput(string hookName, string hookEvent, string output)
        {
            if (string.IsNullOrEmpty(output)) return;

            var hook = string.IsNullOrEmpty(hookName) ? "hook" : hookName;
            var tokens = (long)Math.Round((double)output.Length / CharsPerToken);

            _hookInjections.TryGetValue(hook, out var current);
            var skill = current?.Skill ?? _skillByBody?.Invoke(output);
            var resolvedEvent = !string.IsNullOrEmpty(hookEvent) ? hookEvent : current?.Event;

            _hookInjections[hook] = new HookInjection
            {
                Hook = hook,
                Event = resolvedEvent,
                Count = (current?.Count ?? 0) + 1,
                Tokens = (current?.Tokens ?? 0) + tokens,
                Skill = skill,
            };

            if (!string.IsNullOrEmpty(skill)) MarkSkillActive(skill, "hook", tokens);

            // Timeline gets only the FIRST injection per hook. A UserPromptSubmit hook fires
            // on every prompt, and repeating the marker would drown the conversation while
            // saying nothing new — the repetitions are counted in the panel instead.
            if (current == null)
                _hookLoads.Add(new HookLoad { Hook = hook, Event = resolvedEvent, Skill = skill, Tokens = tokens });
        }

        /// <summary>Hook injections since the last call, for the timeline. Drains the queue.</summary>
        public IReadOnlyList<HookLoad> TakeHookLoads()
        {
            if (_hookLoads.Count == 0) return Array.Empty<HookLoad>();
            var drained = _hookLoads;
            _hookLoads = new List<HookLoad>();
            return drained;
        }

        /// <summary>
        /// A `Skill` tool_use means the MODEL triggered a skill. Nothing is marked yet:
        /// triggering is not loading. This only records the id so the matching tool_result can
        /// be read.
        /// </summary>
        private void NoteSkillToolUse(IEnumerable<ContentBlock> content)
        {
            if (content == null) return;

            foreach (var block in content)
            {
                if (block?.Type != "tool_use" || block.Name != "Skill") continue;
                if (string.IsNullOrEmpty(block.Id)) continue;

                var name = ReadSkillName(block.Input);
                if (string.IsNullOrEmpty(name)) continue;

                if (_skillByToolUse.Count > SkillToolUseCap) _skillByToolUse.Clear();
                _skillByToolUse[block.Id] = name;
            }
        }

        private static string ReadSkillName(JsonElement? input)
        {
            if (input?.ValueKind != JsonValueKind.Object) return null;
            if (!input.Value.TryGetProperty("skill", out var skill) || skill.ValueKind != JsonValueKind.String) return null;
            return skill.GetString()?.TrimStart('/');
        }

        /// <summary>
        /// Two signals in the user message close a skill trigger:
        ///  - a tool_result starting with "Launching skill:", meaning the body ENTERED the context;
        ///  - the synthetic message that follows, carrying only the body, from which the token
        ///    estimate comes.
        ///
        /// The body has no fixed header: a skill with its own directory opens with "Base
        /// directory for this skill: …" while a built-in ships the SKILL.md raw. So the window
        /// is positional — the first text block after the launch — rather than a prefix match,
        /// which would leave built-ins with no number at all. The window closes on the next
        /// assistant event, so a message queued by the UI is never read as a body.
        /// </summary>
        private void NoteSkillBody(IEnumerable<ContentBlock> content)
        {
            foreach (var block in content)
            {
                if (block == null) continue;

                if (block.Type == "tool_result")
                {
                    var toolUseId = block.ToolUseId;
                    if (string.IsNullOrEmpty(toolUseId)) continue;
                    if (!_skillByToolUse.TryGetValue(toolUseId, out var name)) continue;
                    _skillByToolUse.Remove(toolUseId);

                    // "Execute skill:" loads nothing, so it is not marked.
                    if (!ResultText(block).StartsWith(SkillLaunchPrefix, StringComparison.Ordinal)) continue;

                    MarkSkillActive(name, "model");
                    _skillAwaitingBody = new SkillLoad { Name = name, ToolUseId = toolUseId };
                    // Record the load now: the size may never arrive, but the FACT did.
                    _skillLoads.Add(new SkillLoad { Name = name, ToolUseId = toolUseId });
                    continue;
                }

                if (block.Type != "text" || _skillAwaitingBody == null) continue;
                if (string.IsNullOrEmpty(block.Text)) continue;

                var tokens = (long)Math.Round((double)block.Text.Length / CharsPerToken);
                MarkSkillActive(_skillAwaitingBody.Name, "model", tokens);
                _skillLoads.Add(new SkillLoad
                {
                    Name = _skillAwaitingBody.Name,
                    ToolUseId = _skillAwaitingBody.ToolUseId,
                    Tokens = tokens,
                });
                _skillAwaitingBody = null;
            }
        }

        /// <summary>Skill loads since the last call, for the timeline. Drains the queue.</summary>
        public IReadOnlyList<SkillLoad> TakeSkillLoads()
        {
            if (_skillLoads.Count == 0) return Array.Empty<SkillLoad>();
            var drained = _skillLoads;
            _skillLoads = new List<SkillLoad>();
            return drained;
        }

        /// <summary>Known skills (listed plus active), heaviest first.</summary>
        private List<SkillState> SkillStates()
        {
            var names = new HashSet<string>(_skillMeta.Keys, StringComparer.Ordinal);
            names.UnionWith(_skillsActive.Keys);
            if (names.Count == 0) return null;

            var states = new List<SkillState>();
            foreach (var name in names)
            {
                _skillMeta.TryGetValue(name, out var meta);
                _skillsActive.TryGetValue(name, out var active);
                _skillOverrides.TryGetValue(name, out var over);

                states.Add(new SkillState
                {
                    Name = name,
                    Source = meta?.Source,
                    MetaTokens = meta?.Tokens,
                    Listed = _skillMeta.ContainsKey(name),
                    Override = over,
                    Active = active != null ? true : (bool?)null,
                    ActiveTokens = active?.Tokens,
                    ActivatedAt = active?.At,
                    InvokedBy = active?.By,
                });
            }

            // Active first — that is what weighs on the context — then by metadata cost.
            return states
                .OrderByDescending(s => s.Active == true)
                .ThenByDescending(s => s.ActiveTokens ?? 0)
                .ThenByDescending(s => s.MetaTokens ?? 0)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // --- Decisions and denials ---

        /// <summary>Records the user's permission decision, logging denials with their reason.</summary>
        public void RecordDecision(string tool, string decision, string reason = null)
        {
            if (string.IsNullOrEmpty(tool)) tool = "unknown";
            var entry = EnsureDecision(tool);

            if (decision == "allow") entry.Allow++;
            else if (decision == "allow_always") entry.AllowAlways++;
            else
            {
                entry.Deny++;
                PushDenial(tool, "user", reason);
            }
        }

        private ToolDecisionCounts EnsureDecision(string tool)
        {
            if (_toolDecisions.TryGetValue(tool, out var entry)) return entry;
            entry = new ToolDecisionCounts();
            _toolDecisions[tool] = entry;
            return entry;
        }

        private void PushDenial(string tool, string source, string reason)
        {
            var trimmed = reason?.Trim();
            _denials.Add(new DenialEvent
            {
                Tool = tool,
                Ts = Now(),
                Source = source,
                Reason = string.IsNullOrEmpty(trimmed) ? null : trimmed,
            });

            if (_denials.Count > DenialCap) _denials = _denials.Skip(_denials.Count - DenialCap).ToList();
        }

        /// <summary>
        /// Denials the ENGINE decided: auto mode, a tool outside the allowlist, a write outside
        /// the workspace.
        ///
        /// They arrive on the result event with the tool but NOT the reason — that comes in the
        /// error tool_result of the same tool_use_id. Deduplicated by that id, because a turn's
        /// result can repeat denials already counted.
        /// </summary>
        private void RecordEngineDenials(IEnumerable<PermissionDenial> denials)
        {
            if (denials == null) return;

            foreach (var denial in denials)
            {
                if (denial == null) continue;

                var id = denial.ToolUseId;
                if (!string.IsNullOrEmpty(id) && !_seenDenials.Add(id)) continue;

                var tool = string.IsNullOrEmpty(denial.ToolName) ? "unknown" : denial.ToolName;
                EnsureDecision(tool).Deny++;

                string reason = null;
                if (!string.IsNullOrEmpty(id) && _denialReasons.TryGetValue(id, out var stored))
                {
                    reason = stored;
                    _denialReasons.Remove(id);
                }

                PushDenial(tool, "engine", reason);
            }
        }

        /// <summary>
        /// Stores an error tool_result's text per tool_use_id.
        ///
        /// Most of these are ordinary execution errors that will never be used; the text is
        /// only consumed if the turn's result lists that id as a denial. Capped so a long
        /// session cannot grow it without bound.
        /// </summary>
        private void NoteToolError(string id, JsonElement? content)
        {
            if (string.IsNullOrEmpty(id)) return;

            var text = ToolErrorText(content);
            if (string.IsNullOrEmpty(text)) return;

            if (_denialReasons.Count > DenialReasonCap) _denialReasons.Clear();
            _denialReasons[id] = text;
        }

        // --- Ingest ---

        /// <summary>Processes one event and returns the updated snapshot.</summary>
        public StatsSnapshot Ingest(ClaudeEvent value)
        {
            switch (value?.Type)
            {
                case EventTypes.System:
                    // init is authoritative: it carries the [1m] suffix.
                    SetModel(value.Model, true);
                    if (!string.IsNullOrEmpty(value.PermissionMode)) _mode = value.PermissionMode;
                    if (!_sessionStartTs.HasValue && value.Subtype == "init") _sessionStartTs = Now();

                    if (value.Subtype == "hook_response")
                    {
                        NoteHookOutput(ReadExtraString(value, "hook_name"),
                                       ReadExtraString(value, "hook_event"),
                                       ReadExtraString(value, "output"));
                    }
                    break;

                case EventTypes.StreamEvent:
                    IngestStreamEvent(value);
                    break;

                case EventTypes.Assistant:
                {
                    var message = value.AsAssistantMessage();
                    // The API id arrives without [1m], so it must not be authoritative.
                    SetModel(message?.Model, false);
                    if (message?.Usage != null) ApplyPromptUsage(message.Usage, true);

                    // The model is speaking again: if the body has not arrived by now it never
                    // will. Closing the window prevents measuring a later message as a SKILL.md.
                    _skillAwaitingBody = null;
                    NoteSkillToolUse(message?.Content);
                    break;
                }

                case EventTypes.User:
                {
                    var message = value.AsUserMessage();
                    var blocks = ReadContentBlocks(message?.Content);
                    if (blocks == null) break;

                    foreach (var block in blocks)
                    {
                        // An error tool_result's text becomes the REASON if the turn's result
                        // says this tool_use_id was denied.
                        if (block?.Type == "tool_result" && block.IsError == true)
                            NoteToolError(block.ToolUseId, block.Content);
                    }

                    NoteSkillBody(blocks);
                    break;
                }

                case EventTypes.Result:
                    if (value.TotalCostUsd.HasValue)
                    {
                        // A real cost from the CLI always beats our estimate.
                        _lastTurnCostUsd = Math.Max(0, value.TotalCostUsd.Value - _sessionCostUsd);
                        _sessionCostUsd = value.TotalCostUsd.Value;
                        _costIsEstimate = false;
                    }
                    RecordEngineDenials(value.PermissionDenials);
                    break;
            }

            return Snapshot();
        }

        private void IngestStreamEvent(ClaudeEvent value)
        {
            if (value.Event?.ValueKind != JsonValueKind.Object) return;
            var raw = value.Event.Value;

            if (!raw.TryGetProperty("type", out var typeProperty) || typeProperty.ValueKind != JsonValueKind.String) return;

            switch (typeProperty.GetString())
            {
                case "message_start":
                    if (raw.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                    {
                        if (message.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                            SetModel(model.GetString(), false);

                        if (message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                            ApplyPromptUsage(Json.TryDeserialize<Usage>(usage));
                    }
                    break;

                case "message_delta":
                    if (raw.TryGetProperty("usage", out var deltaUsage) && deltaUsage.ValueKind == JsonValueKind.Object)
                        ApplyDeltaUsage(Json.TryDeserialize<Usage>(deltaUsage));
                    break;
            }
        }

        /// <summary>
        /// message_delta carries the turn's cumulative output_tokens.
        ///
        /// It updates ONLY the output. input/cache are fixed at message_start and must not be
        /// touched here: the delta's input_tokens is incremental (zero mid-stream), and
        /// applying it would zero the displayed context and make the number blink.
        /// </summary>
        private void ApplyDeltaUsage(Usage usage)
        {
            if (usage == null) return;
            // Cumulative within the turn, so it only ever goes up. The guard also stops a
            // malformed delta from clearing the display.
            var output = CostModel.Num(usage.OutputTokens);
            if (output > _currentOutput) _currentOutput = output;
        }

        /// <summary>input + cache_* of the request is the prompt size, i.e. the context used.</summary>
        private void ApplyPromptUsage(Usage usage, bool isFinal = false)
        {
            if (usage == null) return;

            var input = CostModel.Num(usage.InputTokens);
            var create = CostModel.Num(usage.CacheCreationInputTokens);
            var read = CostModel.Num(usage.CacheReadInputTokens);
            var output = CostModel.Num(usage.OutputTokens);

            _contextUsed = input + create + read;

            if (!isFinal)
            {
                // message_start: reflect the current turn immediately.
                _currentInput = input;
                _currentCreate = create;
                _currentRead = read;
                _currentOutput = output;
                return;
            }

            _inputTokens += input;
            _cacheCreateTokens += create;
            _cacheReadTokens += read;
            _outputTokens += output;
            _currentInput = _currentCreate = _currentRead = _currentOutput = 0;

            var price = CostModel.PriceFor(_model);
            var turnCost = (input * price.Input
                            + create * price.CacheWrite
                            + read * price.CacheRead
                            + output * price.Output) / 1_000_000.0;

            if (_costIsEstimate)
            {
                _lastTurnCostUsd = turnCost;
                _sessionCostUsd += turnCost;
            }

            ConsolidateTurn(input, output, create, read, turnCost, price);
        }

        /// <summary>
        /// Post-consolidation: detect a cold cache reset and a compaction, then update
        /// counters, peak, per-model breakdown and the timeline sample.
        /// </summary>
        private void ConsolidateTurn(long input, long output, long create, long read, double turnCost, TokenPrice price)
        {
            var now = Now();
            var total = input + create + read;
            var readFraction = total > 0 ? (double)read / total : 0;
            var gap = _lastTurnTs > 0 ? now - _lastTurnTs : 0;

            // Cold TTL reset: not the first turn, idled past the cache life, read almost
            // nothing and rewrote the prefix. It re-pays the cache write, so the loss is
            // accounted rather than hidden.
            var isReset = _turnCount > 0 && gap > CostModel.CacheLifeMs && readFraction < ColdReadFraction && create > 0;
            if (isReset)
            {
                _cacheResetCount++;
                var cost = create * price.CacheWrite / 1_000_000.0;
                _cacheRecacheCostUsd += cost;
                Log.Debug("cache reset #" + _cacheResetCount + " (" + (_model ?? "?") + "): idle " +
                          (gap / 60000.0).ToString("F1") + "m, readFrac=" + readFraction.ToString("F3") +
                          ", re-cache " + create + " tok = $" + cost.ToString("F4"));
            }

            // Compaction: the total context shrank against the previous turn. A reset does not
            // shrink the total, so the two cannot be confused.
            var isCompaction = false;
            if (_turnCount > 0 && !isReset && _previousContextUsed > 0 && total < _previousContextUsed * CompactFraction)
            {
                isCompaction = true;
                _compactionCount++;
                _compactions.Add(new CompactionEvent
                {
                    Ts = now,
                    Before = _previousContextUsed,
                    After = total,
                    Saved = _previousContextUsed - total,
                });
                Log.Debug("compaction #" + _compactionCount + ": " + _previousContextUsed + " → " + total +
                          " tok (−" + (_previousContextUsed - total) + ")");
            }

            _turnCount++;

            if (total > _peakContextUsed)
            {
                _peakContextUsed = total;
                _peakContextTs = now;
                _peakCacheTokens = create + read;
            }

            // Per-model accumulation. Its cost is always a table estimate, even when the
            // session total is real: the CLI reports one number for the turn, not per model.
            var key = _model ?? "unknown";
            if (!_perModel.TryGetValue(key, out var usage))
            {
                usage = new ModelUsage { Model = key };
                _perModel[key] = usage;
            }
            usage.InputTokens += input;
            usage.OutputTokens += output;
            usage.CacheCreateTokens += create;
            usage.CacheReadTokens += read;
            usage.CostUsd += turnCost;
            usage.Turns++;

            _timeline.Add(new TimelineSample
            {
                Ts = now,
                ContextUsed = total,
                CacheReadPct = readFraction,
                CostUsd = _sessionCostUsd,
                Reset = isReset ? true : (bool?)null,
                Compaction = isCompaction ? true : (bool?)null,
            });
            _timeline = StatsStore.CapTimeline(_timeline);

            _lastTurnHitRate = readFraction;
            _previousContextUsed = total;
            _previousCacheRead = read;
            _lastTurnTs = now;
        }

        // --- Session lifecycle ---

        /// <summary>Records a reopen or resume of this context.</summary>
        public void MarkReopen() => _reopenCount++;

        public void SetKeepCacheAlive(bool value) => _keepCacheAlive = value;

        /// <summary>Start of a prompt: arms the active-time stopwatch.</summary>
        public void BeginTurn()
        {
            if (!_turnStartTs.HasValue) _turnStartTs = Now();
        }

        /// <summary>End of a prompt: adds the worked time and ignores the idle time.</summary>
        public void EndTurn()
        {
            if (!_turnStartTs.HasValue) return;
            _activeMs += Math.Max(0, Now() - _turnStartTs.Value);
            _turnStartTs = null;
        }

        /// <summary>Elapsed time of the turn in flight, so the display can include it without closing the turn.</summary>
        private long LiveTurnMs()
        {
            return _turnStartTs.HasValue ? Math.Max(0, Now() - _turnStartTs.Value) : 0;
        }

        /// <summary>Timeline and compactions for the statsTimeline message, sent once per turn.</summary>
        public (List<TimelineSample> Timeline, List<CompactionEvent> Compactions) TimelineSnapshot()
        {
            return (_timeline, _compactions);
        }

        /// <summary>Restores the accumulators from a persisted state, so counting continues coherently.</summary>
        public void Hydrate(PersistedStats persisted)
        {
            if (persisted == null) return;

            _model = persisted.Model ?? _model;
            _mode = persisted.Mode ?? _mode;

            if (persisted.ContextLimit > 0)
            {
                _contextLimit = persisted.ContextLimit;
                _autoLimit = persisted.AutoLimit;
            }

            _sessionStartTs = persisted.SessionStartTs ?? _sessionStartTs;
            _inputTokens = persisted.InputTokens;
            _outputTokens = persisted.OutputTokens;
            _cacheCreateTokens = persisted.CacheCreateTokens;
            _cacheReadTokens = persisted.CacheReadTokens;
            _sessionCostUsd = persisted.SessionCostUsd;
            _costIsEstimate = persisted.CostIsEstimate;
            _turnCount = persisted.TurnCount;
            _reopenCount = persisted.ReopenCount;
            _cacheResetCount = persisted.CacheResetCount;
            _cacheRecacheCostUsd = persisted.CacheRecacheCostUsd;
            _compactionCount = persisted.CompactionCount;
            _peakContextUsed = persisted.PeakContextUsed;
            _peakContextTs = persisted.PeakContextTs;
            _peakCacheTokens = persisted.PeakCacheTokens ?? 0;
            _activeMs = persisted.ActiveMs;
            _keepCacheAlive = persisted.KeepCacheAlive ?? false;

            _previousContextUsed = persisted.LastContextUsed;
            // Restores the context bar immediately, before the first new turn.
            _contextUsed = persisted.LastContextUsed;
            _previousCacheRead = persisted.LastCacheRead;
            // Last turn's hit rate rebuilt from the persisted pair.
            _lastTurnHitRate = persisted.LastContextUsed > 0
                ? (double)persisted.LastCacheRead / persisted.LastContextUsed
                : 0;
            _lastTurnTs = persisted.LastTurnTs;

            _perModel = persisted.PerModel != null
                ? new Dictionary<string, ModelUsage>(persisted.PerModel, StringComparer.Ordinal)
                : new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
            _toolDecisions = persisted.ToolDecisions != null
                ? new Dictionary<string, ToolDecisionCounts>(persisted.ToolDecisions, StringComparer.Ordinal)
                : new Dictionary<string, ToolDecisionCounts>(StringComparer.Ordinal);
            _denials = persisted.Denials != null
                ? persisted.Denials.Skip(Math.Max(0, persisted.Denials.Count - DenialCap)).ToList()
                : new List<DenialEvent>();
            _timeline = persisted.Timeline ?? new List<TimelineSample>();
            _compactions = persisted.Compactions ?? new List<CompactionEvent>();
        }

        /// <summary>
        /// Serializes the state for persistence. <paramref name="cwd"/> is what lets the
        /// CacheKeeper resume this context in the right folder with the tab closed.
        /// </summary>
        public PersistedStats Serialize(string sessionId, string cwd = null)
        {
            return new PersistedStats
            {
                Version = StatsStore.StatsVersion,
                SessionId = sessionId,
                Cwd = cwd,
                KeepCacheAlive = _keepCacheAlive,
                Model = _model,
                Mode = _mode,
                ContextLimit = _contextLimit,
                AutoLimit = _autoLimit,
                SessionStartTs = _sessionStartTs,
                InputTokens = _inputTokens,
                OutputTokens = _outputTokens,
                CacheCreateTokens = _cacheCreateTokens,
                CacheReadTokens = _cacheReadTokens,
                SessionCostUsd = _sessionCostUsd,
                CostIsEstimate = _costIsEstimate,
                TurnCount = _turnCount,
                CacheResetCount = _cacheResetCount,
                CacheRecacheCostUsd = _cacheRecacheCostUsd,
                CompactionCount = _compactionCount,
                ReopenCount = _reopenCount,
                PeakContextUsed = _peakContextUsed,
                PeakContextTs = _peakContextTs,
                PeakCacheTokens = _peakCacheTokens > 0 ? _peakCacheTokens : (long?)null,
                // Includes the turn in flight, so a crash mid-turn does not lose its time.
                ActiveMs = _activeMs + LiveTurnMs(),
                LastContextUsed = _previousContextUsed,
                LastCacheRead = _previousCacheRead,
                LastTurnTs = _lastTurnTs,
                PerModel = new Dictionary<string, ModelUsage>(_perModel, StringComparer.Ordinal),
                ToolDecisions = new Dictionary<string, ToolDecisionCounts>(_toolDecisions, StringComparer.Ordinal),
                Denials = _denials,
                Timeline = _timeline,
                Compactions = _compactions,
                UpdatedAt = DateTime.UtcNow.ToString("o"),
            };
        }

        public StatsSnapshot Snapshot()
        {
            // Display = consolidated totals plus the turn in flight, so the panel does not
            // read zero during a slow first turn.
            var input = _inputTokens + _currentInput;
            var output = _outputTokens + _currentOutput;
            var create = _cacheCreateTokens + _currentCreate;
            var read = _cacheReadTokens + _currentRead;

            // Cumulative hit rate = read / (read + write + input): stable and informative as
            // cache efficiency. The cold first turn correctly sits low.
            var promptTotal = read + create + input;
            var hitRate = promptTotal > 0 ? (double)read / promptTotal : 0;

            var price = CostModel.PriceFor(_model);
            // Savings = what those read tokens would have cost as ordinary input.
            var savings = read > 0 ? read * (price.Input - price.CacheRead) / 1_000_000.0 : (double?)null;

            var toolAcceptance = _toolDecisions.Count > 0
                ? _toolDecisions.Select(kv => new ToolDecision
                {
                    Tool = kv.Key,
                    Allow = kv.Value.Allow,
                    AllowAlways = kv.Value.AllowAlways,
                    Deny = kv.Value.Deny,
                }).ToList()
                : null;

            // Most recent denials first.
            var recentDenials = _denials.Count > 0 ? Enumerable.Reverse(_denials).ToList() : null;

            var snapshot = new StatsSnapshot
            {
                Model = _model,
                Mode = _mode,
                SessionStartTs = _sessionStartTs,
                ContextUsed = _contextUsed,
                ContextLimit = _contextLimit,
                // Filled in once a stable /context source exists; the UI is ready for it.
                ContextBreakdown = null,
                InputTokens = input,
                OutputTokens = output,
                CacheCreateTokens = create,
                CacheReadTokens = read,
                CacheHitRate = hitRate,
                LastTurnHitRate = _turnCount > 0 ? _lastTurnHitRate : (double?)null,
                CacheSavingsUsd = savings,
                SessionCostUsd = _sessionCostUsd,
                LastTurnCostUsd = _lastTurnCostUsd,
                CostIsEstimate = _costIsEstimate,
                ToolAcceptance = toolAcceptance,
                RecentDenials = recentDenials,
                TurnCount = _turnCount,
                ReopenCount = _reopenCount,
                CacheResetCount = _cacheResetCount,
                CacheRecacheCostUsd = _cacheRecacheCostUsd > 0 ? _cacheRecacheCostUsd : (double?)null,
                CompactionCount = _compactionCount,
                PeakContextUsed = _peakContextUsed > 0 ? _peakContextUsed : (long?)null,
                PeakCacheTokens = _peakCacheTokens > 0 ? _peakCacheTokens : (long?)null,
                ActiveMs = _activeMs + LiveTurnMs(),
                PerModel = _perModel.Count > 0 ? _perModel.Values.ToList() : null,
                KeepCacheAlive = _keepCacheAlive,
                Limits = new LimitsBlock
                {
                    FiveHour = MergeWindow(_limits.FiveHour, _streamLimits.FiveHour),
                    SevenDay = MergeWindow(_limits.SevenDay, _streamLimits.SevenDay),
                },
                // The statusline's real, complete percentage wins; then the stream; then the
                // local estimate. The UI labels the source, so it must be accurate.
                LimitsSource = _limitsSource == "real" ? "statusline" : (_streamSeen ? "stream" : "estimate"),
                Skills = SkillStates(),
                SkillsListingTokens = _skillsListingTokens,
                SkillsTotal = _skillsTotal,
                SkillsListed = _skillsListed,
                HookInjections = _hookInjections.Count > 0
                    ? _hookInjections.Values.OrderByDescending(h => h.Tokens).ToList()
                    : null,
            };

            ApplyCacheLife(snapshot);
            return snapshot;
        }

        /// <summary>
        /// Cache life: how long since the last request and how much of the 1h window is left.
        /// Left absent until a turn has happened — a countdown from nothing would be a lie.
        /// </summary>
        private void ApplyCacheLife(StatsSnapshot snapshot)
        {
            snapshot.CacheLifeMs = CostModel.CacheLifeMs;
            if (_lastTurnTs <= 0) return;

            var age = Math.Max(0, Now() - _lastTurnTs);
            snapshot.CacheAgeMs = age;
            snapshot.CacheExpiresInMs = Math.Max(0, CostModel.CacheLifeMs - age);
            // Epoch ms, so the webview can run a live countdown without polling the host.
            snapshot.CacheExpiresAt = _lastTurnTs + CostModel.CacheLifeMs;
            snapshot.CacheAlive = age < CostModel.CacheLifeMs;
        }

        /// <summary>
        /// Merges a limit window. The stream wins on status, reset and percentage, but the
        /// percentage falls back to the base when the stream carries no utilization — which is
        /// the normal case at low usage. Local usd/tokens always come from the base.
        /// </summary>
        internal static LimitWindow MergeWindow(LimitWindow baseWindow, LimitWindow streamWindow)
        {
            if (baseWindow == null && streamWindow == null) return null;

            return new LimitWindow
            {
                UsedPct = streamWindow?.UsedPct ?? baseWindow?.UsedPct,
                ResetsAt = streamWindow?.ResetsAt ?? baseWindow?.ResetsAt,
                Status = streamWindow?.Status ?? baseWindow?.Status,
                Usd = baseWindow?.Usd,
                Tokens = baseWindow?.Tokens,
            };
        }

        // --- Helpers ---

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static string ReadExtraString(ClaudeEvent value, string name)
        {
            if (value.Extra == null || !value.Extra.TryGetValue(name, out var element)) return null;
            return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        }

        private static List<ContentBlock> ReadContentBlocks(JsonElement? content)
        {
            if (content?.ValueKind != JsonValueKind.Array) return null;
            return Json.TryDeserialize<List<ContentBlock>>(content.Value);
        }

        /// <summary>
        /// Text of an error tool_result. The content may be a plain string or the API's rich
        /// blocks; both are accepted and anything else is ignored. Truncated because this is a
        /// UI label, not a log entry.
        /// </summary>
        private static string ToolErrorText(JsonElement? content)
        {
            var raw = FlattenText(content);
            if (string.IsNullOrEmpty(raw)) return string.Empty;

            raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ").Trim();
            if (raw.Length <= ReasonMax) return raw;
            return raw.Substring(0, ReasonMax - 1) + "…";
        }

        /// <summary>Text of a tool_result, untruncated. Used only for prefix checks.</summary>
        private static string ResultText(ContentBlock block)
        {
            return (FlattenText(block?.Content) ?? string.Empty).Trim();
        }

        private static string FlattenText(JsonElement? content)
        {
            if (content == null) return string.Empty;
            if (content.Value.ValueKind == JsonValueKind.String) return content.Value.GetString() ?? string.Empty;
            if (content.Value.ValueKind != JsonValueKind.Array) return string.Empty;

            var parts = new List<string>();
            foreach (var block in content.Value.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (!block.TryGetProperty("type", out var type) || type.GetString() != "text") continue;
                if (!block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) continue;

                var value = text.GetString();
                if (!string.IsNullOrEmpty(value)) parts.Add(value);
            }

            return string.Join(" ", parts);
        }
    }
}
