using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tootega.Cockpit.Protocol
{
    // Port of the data types in shared/protocol.ts. Property names serialize to camelCase
    // via Json.Options, matching the names the React webview reads. Optional members are
    // nullable so "absent" stays distinguishable from "zero" — a distinction the stats
    // panel depends on: a cost of 0 is a fact, an unknown cost is not.

    // --- Limits and context ---

    internal sealed class LimitWindow
    {
        /// <summary>0..1. From the statusline (always) or the stream (only near the limit).</summary>
        public double? UsedPct { get; set; }
        /// <summary>ISO 8601.</summary>
        public string ResetsAt { get; set; }
        /// <summary>allowed | allowed_warning | rejected — the band reported by the stream.</summary>
        public string Status { get; set; }
        public double? Usd { get; set; }
        public long? Tokens { get; set; }
    }

    internal sealed class ContextSlice
    {
        public string Label { get; set; }
        public long Tokens { get; set; }
    }

    /// <summary>
    /// One @-mention autocomplete item. Port of MentionItem in shared/protocol.ts. `Label` is what
    /// gets inserted after the '@'; `Kind` is "file" (a workspace path) or "session" (a live
    /// session the CLI resolves as a SendMessage target, CLI 2.1.232) — it only drives the icon.
    /// </summary>
    internal sealed class MentionItem
    {
        public string Label { get; set; }
        public string Kind { get; set; }
    }

    internal sealed class LimitsBlock
    {
        public LimitWindow FiveHour { get; set; }
        public LimitWindow SevenDay { get; set; }
    }

    // --- Tool decisions and denials ---

    internal sealed class ToolDecision
    {
        public string Tool { get; set; }
        public int Allow { get; set; }
        public int AllowAlways { get; set; }
        public int Deny { get; set; }
    }

    /// <summary>A recorded permission denial (denial log — E5 / auto mode).</summary>
    internal sealed class DenialEvent
    {
        public string Tool { get; set; }
        /// <summary>Epoch ms.</summary>
        public long Ts { get; set; }
        /// <summary>
        /// 'user' = denied in the modal; 'engine' = denied by the CLI itself. Absent means
        /// 'user', for data written before the distinction existed.
        /// </summary>
        public string Source { get; set; }
        /// <summary>Typed feedback for 'user'; the CLI's explanation for 'engine'.</summary>
        public string Reason { get; set; }
    }

    // --- Usage segmentation ---

    /// <summary>Accumulated usage per model — a session can switch models mid-conversation.</summary>
    internal sealed class ModelUsage
    {
        public string Model { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheCreateTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public double CostUsd { get; set; }
        public int Turns { get; set; }
    }

    /// <summary>One point per turn — the basis of the consumption charts (S10).</summary>
    internal sealed class TimelineSample
    {
        public long Ts { get; set; }
        /// <summary>Prompt size (input + cache_*) on that turn.</summary>
        public long ContextUsed { get; set; }
        /// <summary>0..1 — fraction read from cache this turn.</summary>
        public double CacheReadPct { get; set; }
        /// <summary>Session cost accumulated up to this point.</summary>
        public double CostUsd { get; set; }
        /// <summary>This turn was a cache reset (cold TTL).</summary>
        public bool? Reset { get; set; }
        /// <summary>This turn reduced the context (compaction).</summary>
        public bool? Compaction { get; set; }
    }

    /// <summary>Detected compaction: the context shrank between turns (S11).</summary>
    internal sealed class CompactionEvent
    {
        public long Ts { get; set; }
        public long Before { get; set; }
        public long After { get; set; }
        public long Saved { get; set; }
    }

    // --- Skills ---

    /// <summary>
    /// Values accepted by the CLI's skillOverrides. Absent means "on".
    /// Kept as strings because the wire values contain hyphens, which no enum naming
    /// policy produces cleanly.
    /// </summary>
    internal static class SkillOverrides
    {
        public const string On = "on";
        /// <summary>Lists the skill without its description (cost drops to ~4 tokens).</summary>
        public const string NameOnly = "name-only";
        /// <summary>Hidden from the model's listing, but /name still works.</summary>
        public const string UserInvocableOnly = "user-invocable-only";
        public const string Off = "off";
    }

    /// <summary>One skill: metadata cost plus whether its body already entered the context.</summary>
    internal sealed class SkillState
    {
        public string Name { get; set; }
        /// <summary>built-in | userSettings | projectSettings | plugin…</summary>
        public string Source { get; set; }
        /// <summary>Listing cost of this skill, measured by the engine (get_context_usage).</summary>
        public long? MetaTokens { get; set; }
        public bool Listed { get; set; }
        /// <summary>Absent means <see cref="SkillOverrides.On"/>.</summary>
        public string Override { get; set; }

        /// <summary>
        /// The SKILL.md body entered this session's context. The CLI emits no dedicated
        /// event: this is inferred from the Skill tool_use, from a /name we sent, or from
        /// a hook whose injected text matches a SKILL.md on disk.
        /// </summary>
        public bool? Active { get; set; }
        /// <summary>ESTIMATE (chars/4). Absent when invoked by /name, which reports no size.</summary>
        public long? ActiveTokens { get; set; }
        public long? ActivatedAt { get; set; }
        /// <summary>model | user | hook</summary>
        public string InvokedBy { get; set; }
    }

    /// <summary>
    /// Context injected by a hook (system/hook_response), grouped by hook. It applies to
    /// any hook — the text weighs in the prompt whether or not it is a skill.
    /// </summary>
    internal sealed class HookInjection
    {
        /// <summary>hook_name, e.g. "SessionStart:startup".</summary>
        public string Hook { get; set; }
        public string Event { get; set; }
        public int Count { get; set; }
        /// <summary>ESTIMATE (chars/4) of the total injected.</summary>
        public long Tokens { get; set; }
        /// <summary>Skill recognised by body match, when there is one.</summary>
        public string Skill { get; set; }
    }

    // --- Stats snapshot ---

    internal sealed class StatsSnapshot
    {
        public string Model { get; set; }
        public string Mode { get; set; }
        /// <summary>Epoch ms of the session start (system init).</summary>
        public long? SessionStartTs { get; set; }

        public long ContextUsed { get; set; }
        public long ContextLimit { get; set; }
        public List<ContextSlice> ContextBreakdown { get; set; }

        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheCreateTokens { get; set; }
        public long CacheReadTokens { get; set; }
        /// <summary>0..1 — cumulative for the session: read / (read + write + input).</summary>
        public double CacheHitRate { get; set; }
        /// <summary>0..1 — hit rate of the last consolidated turn.</summary>
        public double? LastTurnHitRate { get; set; }
        /// <summary>Estimated savings (read tokens × input→read price delta).</summary>
        public double? CacheSavingsUsd { get; set; }

        public double SessionCostUsd { get; set; }
        public double LastTurnCostUsd { get; set; }
        public bool CostIsEstimate { get; set; }

        public List<ToolDecision> ToolAcceptance { get; set; }
        /// <summary>Most recent permission denials, latest first.</summary>
        public List<DenialEvent> RecentDenials { get; set; }

        public int? TurnCount { get; set; }
        public int? ReopenCount { get; set; }
        /// <summary>Idle turns that lost the prefix and had to rewrite the cache.</summary>
        public int? CacheResetCount { get; set; }
        /// <summary>Dollars re-paid in cache writes because of those resets.</summary>
        public double? CacheRecacheCostUsd { get; set; }
        public int? CompactionCount { get; set; }
        public long? PeakContextUsed { get; set; }
        public long? PeakCacheTokens { get; set; }
        /// <summary>REAL execution time: the sum of each prompt's time, excluding idleness.</summary>
        public long? ActiveMs { get; set; }

        // Cache life (1h TTL) and keep-alive
        public long? CacheLifeMs { get; set; }
        public long? CacheAgeMs { get; set; }
        public long? CacheExpiresInMs { get; set; }
        /// <summary>Epoch ms of expiry — lets the webview run a live countdown.</summary>
        public long? CacheExpiresAt { get; set; }
        public bool? CacheAlive { get; set; }
        public bool? KeepCacheAlive { get; set; }

        public List<ModelUsage> PerModel { get; set; }
        public LimitsBlock Limits { get; set; }
        /// <summary>statusline &gt; stream &gt; estimate, in decreasing order of truth.</summary>
        public string LimitsSource { get; set; }

        public List<SkillState> Skills { get; set; }
        /// <summary>The "Skills" category of get_context_usage — metadata only.</summary>
        public long? SkillsListingTokens { get; set; }
        /// <summary>totalSkills, before overrides.</summary>
        public int? SkillsTotal { get; set; }
        /// <summary>includedSkills — what actually entered the listing.</summary>
        public int? SkillsListed { get; set; }
        public List<HookInjection> HookInjections { get; set; }
    }

    // --- Plugins ---

    internal sealed class InstalledPlugin
    {
        /// <summary>name@marketplace</summary>
        public string Id { get; set; }
        public string Version { get; set; }
        /// <summary>user | project | local</summary>
        public string Scope { get; set; }
        public bool Enabled { get; set; }
        public string Description { get; set; }
        public string Url { get; set; }
        /// <summary>skills | agents | commands | mcp | hooks | mixed.</summary>
        public string Kind { get; set; }
    }

    internal sealed class AvailablePlugin
    {
        public string PluginId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string MarketplaceName { get; set; }
        public int? InstallCount { get; set; }
        public string Url { get; set; }
        public string Kind { get; set; }
    }

    internal sealed class Marketplace
    {
        public string Name { get; set; }
        /// <summary>github | git | path</summary>
        public string Source { get; set; }
        public string Repo { get; set; }
    }

    internal sealed class PluginsData
    {
        public List<InstalledPlugin> Installed { get; set; }
        public List<AvailablePlugin> Available { get; set; }
        public List<Marketplace> Marketplaces { get; set; }
    }

    // --- Account and usage ---

    /// <summary>
    /// Session flags read from the statusline payload. Their provenance is the user's own
    /// statusline, not the Cockpit's headless session — same nature as the real limits.
    /// </summary>
    internal sealed class UsageSession
    {
        public bool? FastMode { get; set; }
        public string ModelDisplay { get; set; }
        public string Effort { get; set; }
        public string OutputStyle { get; set; }
        /// <summary>interactive | attached | unattended (CLI 2.1.221).</summary>
        public string Kind { get; set; }
        /// <summary>Cache older than the trust window — shown dimmed rather than hidden.</summary>
        public bool? Stale { get; set; }
    }

    internal sealed class UsageAccount
    {
        public bool LoggedIn { get; set; }
        /// <summary>claude.ai | console | …</summary>
        public string AuthMethod { get; set; }
        public string ApiProvider { get; set; }
        public string Email { get; set; }
        public string OrgName { get; set; }
        /// <summary>subscriptionType: max | pro | …</summary>
        public string Plan { get; set; }
        /// <summary>Epoch ms — refresh-token validity.</summary>
        public long? LoginExpiresAt { get; set; }
        public UsageSession Session { get; set; }
    }

    internal class UsageBucket
    {
        /// <summary>0..1</summary>
        public double? UsedPct { get; set; }
        /// <summary>ISO 8601</summary>
        public string ResetsAt { get; set; }
        /// <summary>Local estimate, used when there is no real percentage.</summary>
        public long? Tokens { get; set; }
        public double? Usd { get; set; }
    }

    /// <summary>A weekly window restricted to a scope. The label comes from the server.</summary>
    internal sealed class ScopedBucket : UsageBucket
    {
        public string Label { get; set; }
    }

    internal sealed class UsageBuckets
    {
        /// <summary>The current session window.</summary>
        public UsageBucket FiveHour { get; set; }
        /// <summary>The weekly "all models" window.</summary>
        public UsageBucket SevenDay { get; set; }
        /// <summary>Per-model weekly windows, labelled by the server.</summary>
        public List<ScopedBucket> WeeklyScoped { get; set; }
    }

    /// <summary>A slice of the usage breakdown, per model or per source.</summary>
    internal sealed class UsageSlice
    {
        /// <summary>Model id, or 'main' | 'subagent'.</summary>
        public string Key { get; set; }
        public double Usd { get; set; }
        /// <summary>NEW tokens: input + output + cache-create.</summary>
        public long Tokens { get; set; }
        /// <summary>Context re-read from cache. It dominates the total, so it is shown apart.</summary>
        public long CacheRead { get; set; }
    }

    internal sealed class UsageBreakdown
    {
        public List<UsageSlice> ByModel { get; set; }
        /// <summary>main vs. subagent (sidechain).</summary>
        public List<UsageSlice> BySource { get; set; }
    }

    /// <summary>Context injected by a tool — the estimated sum of its tool_results.</summary>
    internal sealed class ToolContextSlice
    {
        /// <summary>Tool name; "mcp:&lt;server&gt;" or "skill:&lt;name&gt;" when grouped.</summary>
        public string Key { get; set; }
        public int Calls { get; set; }
        public long Tokens { get; set; }
    }

    /// <summary>7-day attribution: where the tokens went.</summary>
    internal sealed class UsageAttribution
    {
        /// <summary>0..1 — share generated with context above 150k.</summary>
        public double LongContextPct { get; set; }
        public double SubagentPct { get; set; }
        /// <summary>0..1 — cache_read / (cache_read + cache_creation).</summary>
        public double? CacheHitPct { get; set; }
        /// <summary>Largest first.</summary>
        public List<ToolContextSlice> ByTool { get; set; }
    }

    internal sealed class DailyTokens
    {
        /// <summary>YYYY-MM-DD in local time.</summary>
        public string Date { get; set; }
        /// <summary>input + cache_read + cache_creation.</summary>
        public long Sent { get; set; }
        /// <summary>output</summary>
        public long Received { get; set; }
    }

    /// <summary>GLOBAL token counter, across every instance and context on the machine.</summary>
    internal sealed class TokenTotals
    {
        public long Sent { get; set; }
        public long Received { get; set; }
        public long Total { get; set; }
        /// <summary>Per-day slices, most recent first.</summary>
        public List<DailyTokens> Days { get; set; }
    }

    /// <summary>A workflow run reconstructed from telemetry (workflow.* attributes, CLI 2.1.202).</summary>
    internal sealed class WorkflowRun
    {
        public string RunId { get; set; }
        public string Name { get; set; }
        /// <summary>REAL cost, summed from the run's agents.</summary>
        public double Usd { get; set; }
        public long Tokens { get; set; }
        /// <summary>Absent when the model does not support effort.</summary>
        public string Effort { get; set; }
    }

    internal sealed class OtelToolDecision
    {
        public string Tool { get; set; }
        public int Accept { get; set; }
        public int Reject { get; set; }
    }

    /// <summary>Aggregated statistics from Claude Code's OTEL telemetry (opt-in, local).</summary>
    internal sealed class OtelStats
    {
        public bool Enabled { get; set; }
        /// <summary>e.g. http://127.0.0.1:4318 — shown so the user can point OTEL at it.</summary>
        public string Endpoint { get; set; }
        public long? SinceTs { get; set; }
        public long? LinesAdded { get; set; }
        public long? LinesRemoved { get; set; }
        /// <summary>LOC per model (tokens carries the line count).</summary>
        public List<UsageSlice> LocByModel { get; set; }
        /// <summary>REAL cost per model (claude_code.cost.usage, USD).</summary>
        public List<UsageSlice> CostByModel { get; set; }
        public int? SessionCount { get; set; }
        public int? CommitCount { get; set; }
        public int? PrCount { get; set; }
        public List<OtelToolDecision> ToolDecisions { get; set; }
        /// <summary>Highest cost first.</summary>
        public List<WorkflowRun> Workflows { get; set; }
    }

    internal sealed class UsageData
    {
        public UsageAccount Account { get; set; }
        public UsageBuckets Buckets { get; set; }
        /// <summary>api | statusline | stream | estimate — where the percentages came from.</summary>
        public string Source { get; set; }
        /// <summary>
        /// Why the real source did not answer, when the figures fell back to the local
        /// estimate. Shown in the panel: dropping to an estimate is never silent.
        /// </summary>
        public string SourceError { get; set; }
        /// <summary>Whether the statusline wrapper is installed (captures real rate_limits).</summary>
        public bool TrackingEnabled { get; set; }
        public UsageBreakdown Breakdown { get; set; }
        public UsageAttribution Attribution { get; set; }
        public TokenTotals Tokens { get; set; }
        /// <summary>Absent when the OTEL receiver is off.</summary>
        public OtelStats Otel { get; set; }
        /// <summary>ISO 8601</summary>
        public string GeneratedAt { get; set; }
    }

    // --- MCP ---

    internal sealed class McpServerInfo
    {
        public string Name { get; set; }
        /// <summary>
        /// connected | failed | pending | unknown. 'pending' means an unapproved
        /// .mcp.json — the CLI will not even start the server (2.1.196).
        /// </summary>
        public string Status { get; set; }
        public bool Connected { get; set; }
        /// <summary>Command (stdio) or URL (http/sse), without the (HTTP)/(SSE) suffix.</summary>
        public string Target { get; set; }
        /// <summary>HTTP | SSE for remote servers; absent means stdio.</summary>
        public string Transport { get; set; }
        /// <summary>Remote server declared without a URL (CLI 2.1.208 shows "not configured").</summary>
        public bool? NotConfigured { get; set; }
        /// <summary>Why the CLI refused the server at config validation. Implies status 'failed'.</summary>
        public string Error { get; set; }
        /// <summary>Short names, without the mcp__&lt;server&gt;__ prefix.</summary>
        public List<string> Tools { get; set; }
    }

    internal sealed class McpData
    {
        public List<McpServerInfo> Servers { get; set; }
        public string GeneratedAt { get; set; }
    }

    // --- Session configuration ---

    internal sealed class ModelGroup
    {
        /// <summary>aliases | versions | active | discovered</summary>
        public string Label { get; set; }
        public List<string> Items { get; set; }
    }

    /// <summary>
    /// Per-model metadata for the picker. Context is REAL, from /v1/models
    /// (max_input_tokens); price comes from the docs, since there is no price endpoint.
    /// Absent fields mean unknown, and the UI shows nothing rather than a guess.
    /// </summary>
    internal sealed class ModelMeta
    {
        /// <summary>Official display_name. Absent means derive from the id.</summary>
        public string Label { get; set; }
        public long? ContextTokens { get; set; }
        /// <summary>USD per 1M input tokens.</summary>
        public double? InMTok { get; set; }
        /// <summary>USD per 1M output tokens.</summary>
        public double? OutMTok { get; set; }
        /// <summary>Normalized input multiplier — the most expensive in the list is 1x.</summary>
        public double? PriceMult { get; set; }
    }

    internal sealed class SessionConfig
    {
        /// <summary>claude | tootega — which binary backs the session.</summary>
        public string Engine { get; set; }
        public List<string> Engines { get; set; }
        /// <summary>Selected value; 'default' means the CLI's own default.</summary>
        public string Model { get; set; }
        /// <summary>default | low | medium | high | xhigh | max</summary>
        public string Effort { get; set; }
        /// <summary>Flat options, kept for compatibility.</summary>
        public List<string> Models { get; set; }
        public List<ModelGroup> ModelGroups { get; set; }
        public Dictionary<string, ModelMeta> ModelMeta { get; set; }
        public List<string> Efforts { get; set; }
        /// <summary>What 'Default' resolves to (settings.model, or the observed init).</summary>
        public string DefaultModel { get; set; }
        /// <summary>settings.effortLevel</summary>
        public string DefaultEffort { get; set; }
        public string PermissionMode { get; set; }
        public List<string> PermissionModes { get; set; }
        /// <summary>Allow agents (Task) and workflows. Off saves tokens.</summary>
        public bool AllowAgents { get; set; }
        public bool ShowThinking { get; set; }
        public bool SpellCheck { get; set; }
        public bool ExpandToolCards { get; set; }
        /// <summary>Model/effort/permission changed and the CLI restarts on the next send.</summary>
        public bool PendingRestart { get; set; }
        /// <summary>Name shown on your messages. Empty uses the default.</summary>
        public string UserName { get; set; }
        public bool VoiceCorrect { get; set; }
        /// <summary>verbose | necessary | dialogo | quiet.</summary>
        public string Verbosity { get; set; }
    }

    // --- Sessions ---

    /// <summary>An existing conversation ("context") that can be resumed.</summary>
    internal sealed class SessionInfo
    {
        public string Id { get; set; }
        public string Title { get; set; }
        /// <summary>ISO 8601</summary>
        public string UpdatedAt { get; set; }
        public int MessageCount { get; set; }

        // Extra statistics for the card's rich hint — all optional and tolerant.
        public string CreatedAt { get; set; }
        public long? SizeBytes { get; set; }
        public int? UserCount { get; set; }
        public int? AssistantCount { get; set; }
        public int? ToolCount { get; set; }
        public string Model { get; set; }
    }

    /// <summary>A parallel tab: what the host keeps about each conversation.</summary>
    internal sealed class TabInfo
    {
        public string Id { get; set; }
        public string Title { get; set; }
        /// <summary>idle | busy | error</summary>
        public string Status { get; set; }
        /// <summary>The transcript id, matching <see cref="SessionInfo.Id"/>.</summary>
        public string SessionId { get; set; }

        /// <summary>
        /// The folder this tab's conversation runs in. Shown on the tab so two tabs on
        /// different folders are told apart at a glance, rather than by their titles.
        /// </summary>
        public string Cwd { get; set; }
    }

    /// <summary>
    /// An item rebuilt from the transcript to replay history on resume. The three shapes
    /// of the TypeScript union are folded into one type, selected by <see cref="Kind"/>.
    /// </summary>
    internal sealed class HistoryItem
    {
        /// <summary>user | assistant | tool</summary>
        public string Kind { get; set; }
        public string Id { get; set; }

        // user
        public string Text { get; set; }
        public List<string> Images { get; set; }
        public long? Ts { get; set; }

        // assistant
        public string Thinking { get; set; }

        // tool
        public string Name { get; set; }
        public JsonElement? Input { get; set; }
        public JsonElement? Result { get; set; }
        public bool? IsError { get; set; }
    }

    // --- Questions and permissions ---

    internal sealed class AskOption
    {
        public string Label { get; set; }
        public string Description { get; set; }
    }

    /// <summary>One AskUserQuestion question — the UI renders one tab per question.</summary>
    internal sealed class AskQuestion
    {
        public string Question { get; set; }
        public string Header { get; set; }
        public bool? MultiSelect { get; set; }
        public List<AskOption> Options { get; set; }
    }

    /// <summary>A suggestion accompanying can_use_tool, e.g. setMode acceptEdits.</summary>
    internal sealed class PermissionSuggestion
    {
        public string Type { get; set; }
        public string Mode { get; set; }
        public string Destination { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }
    }

    // --- Misc ---

    /// <summary>Metadata of a vault credential. It never carries the secret value.</summary>
    internal sealed class CredentialMeta
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Username { get; set; }
        public string Note { get; set; }
        public long CreatedAt { get; set; }
    }

    /// <summary>A background task still running (Workflow / tool with run_in_background).</summary>
    internal sealed class BackgroundTask
    {
        /// <summary>The tool_use id that launched it.</summary>
        public string Id { get; set; }
        /// <summary>Workflow | Task | Bash | …</summary>
        public string Tool { get; set; }
        /// <summary>What it is doing: workflow name, description or command.</summary>
        public string Label { get; set; }
    }

    /// <summary>Slash-command metadata researched by the internal model, cached globally.</summary>
    internal sealed class SlashCmdMeta
    {
        /// <summary>session | context | config | tools | account | info | plugin | other.</summary>
        public string Category { get; set; }
        public string Hint { get; set; }
        public string Detail { get; set; }
        /// <summary>Third-party plugin/tool name, which gets its own group.</summary>
        public string Group { get; set; }
    }

    internal sealed class VoiceReplacement
    {
        /// <summary>How it is usually heard or transcribed.</summary>
        public string From { get; set; }
        /// <summary>How it should be written.</summary>
        public string To { get; set; }
    }

    /// <summary>Dictation dictionary (per login): terms to preserve plus replacements.</summary>
    internal sealed class VoiceDictData
    {
        public List<string> Terms { get; set; }
        public List<VoiceReplacement> Replacements { get; set; }
        /// <summary>The account it belongs to — an informative label.</summary>
        public string Account { get; set; }
        /// <summary>Spell-checker dictionary (added / ignored words).</summary>
        public List<string> SpellWords { get; set; }
    }

    /// <summary>An attached image, base64 without the data: prefix.</summary>
    internal sealed class ImageAttachment
    {
        public string MediaType { get; set; }
        public string Data { get; set; }
    }
}
