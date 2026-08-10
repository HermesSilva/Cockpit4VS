using System.Collections.Generic;

namespace Tootega.Cockpit.Protocol
{
    /// <summary>
    /// Every host -&gt; webview message, one factory each. Port of the HostMsg union in
    /// shared/protocol.ts; the field names here are the contract with the React code.
    /// </summary>
    internal static class HostMessages
    {
        // --- Lifecycle and global state ---

        public static HostMessage Ready(string locale) =>
            new HostMessage("ready", ("locale", locale));

        public static HostMessage Tabs(IReadOnlyList<TabInfo> tabs, string activeTab) =>
            new HostMessage("tabs", ("tabs", tabs), ("activeTab", activeTab));

        public static HostMessage Config(SessionConfig config) =>
            new HostMessage("config", ("config", config));

        public static HostMessage Locale(string locale) =>
            new HostMessage("locale", ("locale", locale));

        public static HostMessage CliStatus(bool available, string version = null, string error = null,
                                            string latest = null, string cockpitVersion = null) =>
            new HostMessage("cliStatus",
                ("available", available), ("version", version), ("error", error),
                ("latest", latest), ("cockpitVersion", cockpitVersion));

        public static HostMessage Auth(bool loggedIn) =>
            new HostMessage("auth", ("loggedIn", loggedIn));

        public static HostMessage AuthRequired() =>
            new HostMessage("authRequired");

        public static HostMessage Error(string message) =>
            new HostMessage("error", ("message", message));

        // --- Session lifecycle ---

        public static HostMessage SessionInit(string sessionId, string model = null, string cwd = null,
                                              string mode = null, IReadOnlyList<string> tools = null,
                                              IReadOnlyList<McpServerRef> mcpServers = null,
                                              IReadOnlyList<string> slashCommands = null) =>
            new HostMessage("sessionInit",
                ("sessionId", sessionId), ("model", model), ("cwd", cwd), ("mode", mode),
                ("tools", tools), ("mcpServers", mcpServers), ("slashCommands", slashCommands));

        public static HostMessage Sessions(IReadOnlyList<SessionInfo> sessions, string cwd) =>
            new HostMessage("sessions", ("sessions", sessions), ("cwd", cwd));

        public static HostMessage OpenSessions() =>
            new HostMessage("openSessions");

        public static HostMessage History(IReadOnlyList<HistoryItem> items) =>
            new HostMessage("history", ("items", items));

        // --- Streaming conversation ---

        public static HostMessage AssistantStart(string id) =>
            new HostMessage("assistantStart", ("id", id));

        public static HostMessage AssistantText(string id, string delta) =>
            new HostMessage("assistantText", ("id", id), ("delta", delta));

        public static HostMessage AssistantDone(string id) =>
            new HostMessage("assistantDone", ("id", id));

        public static HostMessage Thinking(string id, string delta) =>
            new HostMessage("thinking", ("id", id), ("delta", delta));

        public static HostMessage ToolUse(string id, string name, object input) =>
            new HostMessage("toolUse", ("id", id), ("name", name), ("input", input));

        public static HostMessage ToolResult(string toolUseId, object content, bool? isError = null) =>
            new HostMessage("toolResult", ("toolUseId", toolUseId), ("content", content), ("isError", isError));

        /// <summary>
        /// Subagent text forwarded by the CLI (--forward-subagent-text). parentId is the
        /// Task tool_use that launched it, so the webview can nest it under that card.
        /// </summary>
        public static HostMessage SubagentText(string parentId, string delta) =>
            new HostMessage("subagentText", ("parentId", parentId), ("delta", delta));

        public static HostMessage Busy(bool busy) =>
            new HostMessage("busy", ("busy", busy));

        public static HostMessage TurnComplete(double? costUsd = null, Usage usage = null) =>
            new HostMessage("turnComplete", ("costUsd", costUsd), ("usage", usage));

        /// <summary>
        /// Work that outlived the turn (Workflow / run_in_background). The `result` event
        /// clears busy, but the process keeps going — without this the UI would look idle
        /// while the machine is still working.
        /// </summary>
        public static HostMessage Background(IReadOnlyList<BackgroundTask> tasks) =>
            new HostMessage("background", ("tasks", tasks));

        // --- Interactive protocols ---

        public static HostMessage PermissionRequest(string requestId, string tool, object input,
                                                    string displayName = null, string description = null,
                                                    IReadOnlyList<PermissionSuggestion> suggestions = null,
                                                    string oldText = null) =>
            new HostMessage("permissionRequest",
                ("requestId", requestId), ("tool", tool), ("displayName", displayName),
                ("description", description), ("input", input),
                ("suggestions", suggestions), ("oldText", oldText));

        public static HostMessage AskRequest(string requestId, IReadOnlyList<AskQuestion> questions) =>
            new HostMessage("askRequest", ("requestId", requestId), ("questions", questions));

        /// <summary>Effort below the folder's CLAUDE.md floor: confirm before sending.</summary>
        public static HostMessage EffortGate(string selected, string min) =>
            new HostMessage("effortGate", ("selected", selected), ("min", min));

        // --- Statistics ---

        public static HostMessage Stats(StatsSnapshot stats) =>
            new HostMessage("stats", ("stats", stats));

        /// <summary>Heavy series: sent once per turn, never per token.</summary>
        public static HostMessage StatsTimeline(IReadOnlyList<TimelineSample> timeline,
                                                IReadOnlyList<CompactionEvent> compactions) =>
            new HostMessage("statsTimeline", ("timeline", timeline), ("compactions", compactions));

        public static HostMessage TaskTimings(IReadOnlyDictionary<string, double> timings) =>
            new HostMessage("taskTimings", ("timings", timings));

        public static HostMessage UsageData(UsageData data) =>
            new HostMessage("usageData", ("data", data));

        // --- Timeline annotations ---

        /// <summary>
        /// A SKILL.md body entered the context: seals the Skill card with its cost.
        /// `tokens` is an ESTIMATE of the injected message; absent when the engine said nothing.
        /// </summary>
        public static HostMessage SkillLoaded(string toolUseId, string name, long? tokens = null) =>
            new HostMessage("skillLoaded", ("toolUseId", toolUseId), ("name", name), ("tokens", tokens));

        /// <summary>
        /// A hook injected text into the context. There is no tool_use to seal, so it
        /// becomes its own timeline band. `skill` appears when the text matches a
        /// SKILL.md on disk — an inference, and labelled as one in the UI.
        /// </summary>
        public static HostMessage HookInjected(string hook, string @event = null,
                                               string skill = null, long? tokens = null) =>
            new HostMessage("hookInjected", ("hook", hook), ("event", @event),
                ("skill", skill), ("tokens", tokens));

        /// <summary>
        /// A warning the engine emitted mid-session (fast-mode credits, restricted
        /// subagent model, …). Without it the effect would reach the user with no cause.
        /// </summary>
        public static HostMessage EngineNotice(string id, string text, string topic = null) =>
            new HostMessage("engineNotice", ("id", id), ("text", text), ("topic", topic));

        /// <summary>
        /// Compaction (S11). `active` means it is happening right now — the turn is not
        /// stuck. Numbers travel raw; the webview does the prose.
        /// </summary>
        public static HostMessage Compaction(bool active, long? pre = null, long? post = null,
                                             string trigger = null, double? durationMs = null) =>
            new HostMessage("compaction", ("active", active), ("pre", pre), ("post", post),
                ("trigger", trigger), ("durationMs", durationMs));

        /// <summary>
        /// The tab was handed to an interactive Remote Control session. `phase` reports
        /// what is KNOWN, not what was hoped for: spawning a terminal is not proof the
        /// session came up.
        /// </summary>
        public static HostMessage RemoteState(bool active, string phase = null, string detail = null) =>
            new HostMessage("remoteState", ("active", active), ("phase", phase), ("detail", detail));

        // --- Slash commands ---

        public static HostMessage SlashCommands(IReadOnlyList<string> commands) =>
            new HostMessage("slashCommands", ("commands", commands));

        public static HostMessage SlashMeta(IReadOnlyDictionary<string, SlashCmdMeta> meta) =>
            new HostMessage("slashMeta", ("meta", meta));

        public static HostMessage SlashResearching(bool busy) =>
            new HostMessage("slashResearching", ("busy", busy));

        // --- Plugins, skills, MCP ---

        public static HostMessage PluginsData(PluginsData data) =>
            new HostMessage("pluginsData", ("data", data));

        public static HostMessage PluginsBusy(bool busy, string label = null) =>
            new HostMessage("pluginsBusy", ("busy", busy), ("label", label));

        public static HostMessage PluginsError(string message) =>
            new HostMessage("pluginsError", ("message", message));

        public static HostMessage SkillsBusy(bool busy) =>
            new HostMessage("skillsBusy", ("busy", busy));

        public static HostMessage McpData(McpData data) =>
            new HostMessage("mcpData", ("data", data));

        public static HostMessage McpBusy(bool busy) =>
            new HostMessage("mcpBusy", ("busy", busy));

        // --- Composer helpers ---

        public static HostMessage ResolvedPath(string requestId, string text) =>
            new HostMessage("resolvedPath", ("requestId", requestId), ("text", text));

        public static HostMessage MentionResults(string requestId, IReadOnlyList<string> items) =>
            new HostMessage("mentionResults", ("requestId", requestId), ("items", items));

        /// <summary>The editor selection to share as @file#a-b, or absent to clear it.</summary>
        public static HostMessage Selection(string reference) =>
            new HostMessage("selection", ("ref", reference));

        /// <summary>Restores the draft after a renderer reload or crash.</summary>
        public static HostMessage DraftRestore(string text) =>
            new HostMessage("draftRestore", ("text", text));

        // --- Voice dictation ---

        public static HostMessage VoiceReady() =>
            new HostMessage("voiceReady");

        public static HostMessage VoiceTranscript(string text, bool isFinal) =>
            new HostMessage("voiceTranscript", ("text", text), ("isFinal", isFinal));

        public static HostMessage VoiceCorrected(string text) =>
            new HostMessage("voiceCorrected", ("text", text));

        public static HostMessage VoiceCorrectError() =>
            new HostMessage("voiceCorrectError");

        public static HostMessage VoiceError(string message) =>
            new HostMessage("voiceError", ("message", message));

        public static HostMessage VoiceClosed() =>
            new HostMessage("voiceClosed");

        public static HostMessage VoiceDict(VoiceDictData data) =>
            new HostMessage("voiceDict", ("data", data));

        // --- Spell checker ---

        public static HostMessage SpellResult(IReadOnlyList<string> bad) =>
            new HostMessage("spellResult", ("bad", bad));

        public static HostMessage SpellSuggestResult(string requestId, string word,
                                                     IReadOnlyList<string> pt, IReadOnlyList<string> en) =>
            new HostMessage("spellSuggestResult",
                ("requestId", requestId), ("word", word), ("pt", pt), ("en", en));

        // --- Credentials vault (TOTP 2FA) ---

        public static HostMessage CredsData(bool enrolled, IReadOnlyList<CredentialMeta> items) =>
            new HostMessage("credsData", ("enrolled", enrolled), ("items", items));

        public static HostMessage CredsSetup(string qrSvg, string secret, string uri) =>
            new HostMessage("credsSetup", ("qrSvg", qrSvg), ("secret", secret), ("uri", uri));

        public static HostMessage CredsValue(string id, string name, string value) =>
            new HostMessage("credsValue", ("id", id), ("name", name), ("value", value));

        public static HostMessage CredsResult(bool ok, string action, string message = null) =>
            new HostMessage("credsResult", ("ok", ok), ("action", action), ("message", message));

        public static HostMessage CredsError(string message) =>
            new HostMessage("credsError", ("message", message));
    }
}
