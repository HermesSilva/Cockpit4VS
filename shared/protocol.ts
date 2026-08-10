// Message protocol between the extension host and the React webview.
import type { Usage } from './events';

export interface LimitWindow {
  usedPct?: number; // 0..1 — statusline (always) or stream (only close to the limit)
  resetsAt?: string; // ISO 8601
  status?: 'allowed' | 'allowed_warning' | 'rejected'; // the band reported by the stream
  usd?: number; // the window's local cost
  tokens?: number; // the window's local tokens
}

export interface ContextSlice {
  label: string;
  tokens: number;
}

export interface ToolDecision {
  tool: string;
  allow: number;
  allowAlways: number;
  deny: number;
}

/** A recorded permission denial (denial log — E5/auto-mode). */
export interface DenialEvent {
  tool: string;
  ts: number; // epoch ms
  // 'user' = denied in the modal; 'engine' = denied by the CLI itself (auto-mode
  // rule, tool not allowed, path outside the workspace…). Absent = 'user' (old
  // data, written before the distinction existed).
  source?: 'user' | 'engine';
  // The reason. On 'user' it is the typed feedback; on 'engine' the CLI's own message
  // (since 2.1.193 auto mode explains why it denied).
  reason?: string;
}

/** Accumulated usage segmented per model (a session can switch models). */
export interface ModelUsage {
  model: string;
  inputTokens: number;
  outputTokens: number;
  cacheCreateTokens: number;
  cacheReadTokens: number;
  costUsd: number;
  turns: number;
}

/** A timeline sample (one point per turn) — the basis of the consumption charts (S10). */
export interface TimelineSample {
  ts: number; // epoch ms
  contextUsed: number; // the turn's prompt size (input + cache_*)
  cacheReadPct: number; // 0..1 — fraction read from the cache this turn (efficiency)
  costUsd: number; // session cost accumulated up to here
  reset?: boolean; // this turn was a cache reset (cold TTL)
  compaction?: boolean; // this turn reduced the context (compaction)
}

/** Detected compaction event (the context shrank between turns) (S11). */
export interface CompactionEvent {
  ts: number;
  before: number;
  after: number;
  saved: number; // before - after
}

export interface StatsSnapshot {
  model?: string;
  mode?: string;
  // Session
  sessionStartTs?: number; // epoch ms — session start (system init)
  // Contexto
  contextUsed: number;
  contextLimit: number;
  contextBreakdown?: ContextSlice[];
  // Tokens accumulated in the session
  inputTokens: number;
  outputTokens: number;
  cacheCreateTokens: number;
  cacheReadTokens: number;
  cacheHitRate: number; // 0..1 — cumulative for the session (read / (read+write+input))
  lastTurnHitRate?: number; // 0..1 — hit rate of the last consolidated turn (cr/total of the turn)
  cacheSavingsUsd?: number; // estimated savings (read tokens × input→read price delta)
  // Custo
  sessionCostUsd: number;
  lastTurnCostUsd: number;
  costIsEstimate: boolean;
  // Tool acceptance (per tool_name, accumulated in the session)
  toolAcceptance?: ToolDecision[];
  // Log of the most recent permission denials (E5) — latest first.
  recentDenials?: DenialEvent[];
  // --- Persistence/coherence across context reopens ---
  turnCount?: number; // turns consolidated in the session
  reopenCount?: number; // how many times the context was reopened/resumed
  // Cache reset (cold TTL): an idle turn that lost the prefix and rewrote the cache
  cacheResetCount?: number;
  cacheRecacheCostUsd?: number; // $ re-paid in cacheWrite because of the resets
  // Compaction (context condensed between turns) — S11
  compactionCount?: number;
  peakContextUsed?: number; // largest context reached in the session
  peakCacheTokens?: number; // cache size (create+read) at the peak turn
  // REAL session execution time (sum of each prompt's time; excludes idleness)
  activeMs?: number;
  // --- Cache life (a 1h TTL) and keep-alive ---
  cacheLifeMs?: number; // the cache's whole window (1h)
  cacheAgeMs?: number; // age since the last activity (request)
  cacheExpiresInMs?: number; // quanto falta p/ o cache expirar
  cacheExpiresAt?: number; // epoch ms of the expiry — for a live countdown
  cacheAlive?: boolean; // the cache is still alive (age < 1h)
  keepCacheAlive?: boolean; // checkbox: re-send so the cache doesn't die
  // Detalhamento por modelo (S5 estendido)
  perModel?: ModelUsage[];
  // Limites de conta
  limits?: { fiveHour?: LimitWindow; sevenDay?: LimitWindow };
  // Source of the limits: statusline (real complete %) > stream (rate_limit_event:
  // status/reset always, % only close to the limit) > estimate (tokens÷local budget).
  limitsSource?: 'statusline' | 'stream' | 'estimate';
  // --- Skills (transparency) ---
  skills?: SkillState[];
  skillsListingTokens?: number; // the "Skills" category of get_context_usage: metadata only
  skillsTotal?: number; // totalSkills, before the overrides are applied
  skillsListed?: number; // includedSkills: what actually made it into the listing
  // Text hooks injected into this session's context, grouped by hook.
  hookInjections?: HookInjection[];
}

// --- Skills ---

/**
 * The states the CLI's `skillOverrides` accepts. Absent means 'on'.
 *  - 'name-only': lists the skill without its description, which drops the cost to ~4 tokens.
 *  - 'user-invocable-only': gone from the model's listing, but /name still works.
 *  - 'off': gone from both.
 */
export type SkillOverride = 'on' | 'name-only' | 'user-invocable-only' | 'off';

/** One skill: what its metadata costs, and whether its body is already in the context. */
export interface SkillState {
  name: string;
  source?: string; // 'built-in' | 'userSettings' | 'plugin'… (skillFrontmatter.source)
  metaTokens?: number; // this skill's listing cost (get_context_usage)
  listed: boolean; // it appeared in the last reading
  override?: SkillOverride; // absent means 'on'
  // The SKILL.md body injected in this session. The CLI emits no event of its own for it:
  // it comes from a `Skill` tool_use (the model invoking it), from a /name the Cockpit
  // sent, or from a hook whose injected text matches the SKILL.md on disk ('hook', inferred).
  active?: boolean;
  activeTokens?: number; // an ESTIMATE (chars/4). Absent when invoked by /name.
  activatedAt?: number;
  invokedBy?: 'model' | 'user' | 'hook';
}

/**
 * Context injected by a HOOK (`system/hook_response`), grouped by hook. It holds for any
 * hook — the text enters the prompt and weighs, skill or not. When the text matches a
 * SKILL.md body on disk, `skill` says which one (an inference, labelled as such in the UI).
 */
export interface HookInjection {
  hook: string; // hook_name, e.g. 'SessionStart:startup'
  event?: string; // hook_event, e.g. 'SessionStart'
  count: number; // how many times it injected in this session
  tokens: number; // an ESTIMATE (chars/4) of everything injected
  skill?: string; // the skill recognized from the body, when there is one
}

// --- Plugins (modal "Plugins") ---
export interface InstalledPlugin {
  id: string; // name@marketplace
  version?: string;
  scope?: string; // user | project | local
  enabled: boolean;
  description?: string; // from the plugin.json manifest
  url?: string; // the manifest's homepage, repo or author
  kind?: string; // type: skills|agents|commands|mcp|hooks|mixed (from the components)
}
export interface AvailablePlugin {
  pluginId: string; // name@marketplace
  name: string;
  description?: string;
  marketplaceName?: string;
  installCount?: number;
  url?: string; // source repository (source.url)
  kind?: string; // type (classified by Haiku)
}
export interface Marketplace {
  name: string;
  source?: string; // github | git | path
  repo?: string;
}
export interface PluginsData {
  installed: InstalledPlugin[];
  available: AvailablePlugin[];
  marketplaces: Marketplace[];
}

// --- Account & Usage ("Usage" button) ---
export interface UsageAccount {
  loggedIn: boolean;
  authMethod?: string; // 'claude.ai' | 'console' | …
  apiProvider?: string;
  email?: string;
  orgName?: string;
  plan?: string; // subscriptionType ('max' | 'pro' | …)
  loginExpiresAt?: number; // epoch ms — the login's validity (refresh token)
  // Session flags read from the statusline payload (fast_mode/model/effort/output_style).
  // Provenance is the user's statusline, not the Cockpit's headless session — same nature
  // as the real limits. Absent when the statusline wrapper isn't installed or the cache is stale.
  session?: {
    fastMode?: boolean;
    modelDisplay?: string;
    effort?: string;
    outputStyle?: string;
    kind?: string; // interactive | attached | unattended (CLI 2.1.221)
    stale?: boolean; // cache older than the trust window (shown dimmed)
  };
}
export interface UsageBucket {
  usedPct?: number; // 0..1
  resetsAt?: string; // ISO 8601
  tokens?: number; // local estimate (when there is no real %)
  usd?: number;
}
/** Weekly window restricted to a scope (e.g. one model). The label comes from the server. */
export interface ScopedBucket extends UsageBucket {
  label: string; // the scope's display_name ("Fable", "Sonnet")
}
/** A slice of the usage breakdown (per model or per source). */
export interface UsageSlice {
  key: string; // the model id, or 'main' | 'subagent'
  usd: number;
  tokens: number; // tokens NOVOS: input + output + cache-create
  cacheRead: number; // context re-read from the cache (dominates the total; displayed separately)
}

/** The local breakdown of the 7-day window — always a table estimate. */
export interface UsageBreakdown {
  byModel: UsageSlice[];
  bySource: UsageSlice[]; // main vs. subagent (sidechain)
}

/** Context injected by a tool (estimated sum of the tool_results). */
export interface ToolContextSlice {
  key: string; // the tool name; "mcp:<server>" or "skill:<name>" when grouped
  calls: number;
  tokens: number;
}

/** 7-day usage attribution: where the tokens went. */
export interface UsageAttribution {
  longContextPct: number; // 0..1 — share generated with context > 150k
  subagentPct: number; // 0..1 — parcela vinda de subagentes
  cacheHitPct?: number; // 0..1 — cache_read / (cache_read + cache_creation)
  byTool: ToolContextSlice[]; // maior primeiro
}

/** Tokens of a single day (local YYYY-MM-DD key). */
export interface DailyTokens {
  date: string; // YYYY-MM-DD, in the local time zone
  sent: number; // input + cache_read + cache_creation
  received: number; // output
}

/** GLOBAL token counter (every instance/context on the machine). */
export interface TokenTotals {
  sent: number; // all-time
  received: number; // all-time
  total: number; // sent + received
  days: DailyTokens[]; // recorte por dia (mais recente primeiro)
}

export interface UsageData {
  account: UsageAccount;
  // fiveHour = current session window; sevenDay = weekly "all models";
  // weeklyScoped = per-model weekly windows (e.g. Fable), labelled by the server.
  buckets: { fiveHour?: UsageBucket; sevenDay?: UsageBucket; weeklyScoped?: ScopedBucket[] };
  source: 'api' | 'statusline' | 'stream' | 'estimate'; // origin of the %
  // Why the real source is unavailable (HTTP 401, timeout, …). Only set when `source` fell
  // back to 'estimate' — technical code, shown as a suffix of the estimate note.
  sourceError?: string;
  trackingEnabled: boolean; // the statusline wrapper is installed, capturing the real rate_limits
  // The local 7-day breakdown, by model and by source — an estimate, always present.
  breakdown?: UsageBreakdown;
  // Local 7d attribution: long context, subagents, cache hit rate, tools/MCP.
  attribution?: UsageAttribution;
  // Global token counter (sent/received/total) per day — the whole machine.
  tokens?: TokenTotals;
  // OTEL telemetry (opt-in) aggregated by the local receiver — absent when off.
  otel?: OtelStats;
  generatedAt: string; // ISO 8601
}

/** Aggregated statistics from Claude Code's OTEL telemetry (opt-in, local). */
export interface OtelStats {
  enabled: boolean; // receiver ligado e escutando
  endpoint?: string; // ex.: http://127.0.0.1:4318 (for the user to point OTEL at)
  sinceTs?: number; // epoch ms of the collection start
  linesAdded?: number; // claude_code.lines_of_code.count (type=added)
  linesRemoved?: number; // claude_code.lines_of_code.count (type=removed)
  locByModel?: UsageSlice[]; // LOC por modelo (tokens = linhas)
  costByModel?: UsageSlice[]; // custo REAL por modelo (claude_code.cost.usage, USD)
  sessionCount?: number; // claude_code.session.count
  commitCount?: number; // claude_code.commit.count
  prCount?: number; // claude_code.pull_request.count
  toolDecisions?: { tool: string; accept: number; reject: number }[]; // claude_code.code_edit_tool.decision
  workflows?: WorkflowRun[]; // custo/tokens por run de workflow (maior custo primeiro)
}

/** A workflow run reconstructed from the telemetry (`workflow.*` attributes, CLI 2.1.202). */
export interface WorkflowRun {
  runId: string;
  name: string;
  usd: number; // REAL cost summed from the run's agents
  tokens: number;
  effort?: string; // effort(s) of the run's agents (low…max), CLI 2.1.214; absent when the model doesn't support it
}

// --- MCP (painel 🔌 Servers) ---
/** An MCP server: its state now + the tools it exposes in this session. */
export interface McpServerInfo {
  name: string;
  // 'pending' = `.mcp.json` not approved (the CLI won't even start the server — 2.1.196).
  status: 'connected' | 'failed' | 'pending' | 'unknown';
  connected: boolean;
  target?: string; // the command (stdio) or URL (http/sse), without the `(HTTP)`/`(SSE)` suffix
  transport?: string; // 'HTTP' | 'SSE' — remote servers only; absent means stdio
  notConfigured?: boolean; // a remote declared with no URL (CLI 2.1.208 shows "not configured")
  // Why the CLI refused the server while validating the config (init
  // `mcp_server_errors`, 2.1.219). Present means the server was skipped, so status is 'failed'.
  error?: string;
  tools: string[]; // short names, without the `mcp__<server>__` prefix
}

export interface McpData {
  servers: McpServerInfo[];
  generatedAt: string; // ISO 8601
}

export interface SessionConfig {
  engine: string; // 'claude' | 'tootega' — which binary backs the session
  engines: string[];
  model: string; // the selected value ('default' means the CLI's own)
  effort: string; // 'default' | 'low' | 'medium' | 'high' | 'xhigh' | 'max'
  models: string[]; // flat options (compat)
  modelGroups?: ModelGroup[]; // grouped options (aliases / versions / active)
  modelMeta?: Record<string, ModelMeta>; // context (real, /v1/models) + price (docs) per id
  efforts: string[];
  defaultModel?: string; // o que 'Default' resolve (settings.model ou init observado)
  defaultEffort?: string; // settings.effortLevel
  permissionMode: string;
  permissionModes: string[];
  allowAgents: boolean; // liberar agentes (Task) e workflows (Workflow); off economiza tokens
  showThinking: boolean; // expand thinking by default
  spellCheck: boolean; // spell-check while typing (composer overlay)
  expandToolCards: boolean; // expand tool cards by default in the timeline
  pendingRestart: boolean; // model/effort/permission changed and restarts on the next send
  userName: string; // the subscriber name for the "You" label; empty falls back to the default
  voiceCorrect: boolean; // correct the dictated text via Haiku when dictation stops
  verbosity: string; // verbose|necessary|dialogo|quiet — what to show in the timeline
}

export interface ModelGroup {
  label: string; // 'aliases' | 'versions' | 'active' | 'discovered'
  items: string[];
}

// Per-model metadata for the selector (context/price columns).
// The context comes REAL from the Models API (/v1/models: max_input_tokens); the price from the
// pricing docs (there is no price endpoint). Absent fields = unknown.
export interface ModelMeta {
  label?: string; // the official display_name ("Claude Opus 4.8"); absent means derive it from the id
  contextTokens?: number; // janela de contexto (max_input_tokens)
  inMTok?: number; // input price in USD per 1M tokens
  outMTok?: number; // output price in USD per 1M tokens
  priceMult?: number; // a normalized input multiplier, where the list's most expensive is 1x
}

// Existing session/conversation ("context") to resume.
export interface SessionInfo {
  id: string;
  title: string;
  updatedAt: string; // ISO 8601
  messageCount: number;
  // Extra statistics for the card's rich hint (all optional/tolerant).
  createdAt?: string; // ISO 8601 — transcript creation
  sizeBytes?: number; // the .jsonl's size
  userCount?: number; // user messages
  assistantCount?: number; // the assistant's messages
  toolCount?: number; // chamadas de tool (tool_use)
  model?: string; // last observed model
}

// One option of an AskUserQuestion question.
export interface AskOption {
  label: string;
  description?: string;
}

// One AskUserQuestion question (the UI renders one tab per question).
export interface AskQuestion {
  question: string;
  header: string;
  multiSelect?: boolean;
  options: AskOption[];
}

// Permission suggestion that accompanies can_use_tool (e.g. setMode acceptEdits).
export interface PermissionSuggestion {
  type?: string;
  mode?: string;
  destination?: string;
  [k: string]: unknown;
}

// Item rebuilt from the transcript to render the history when resuming.
export type HistoryItem =
  | { kind: 'user'; id: string; text: string; images?: string[]; ts?: number }
  | { kind: 'assistant'; id: string; text: string; thinking: string }
  | {
      kind: 'tool';
      id: string;
      name: string;
      input: unknown;
      result?: unknown;
      isError?: boolean;
      ts?: number;
    };

// Parallel tab/session: metadata the host keeps (id, title, status).
export interface TabInfo {
  id: string;
  title: string;
  status: 'idle' | 'busy' | 'error';
  sessionId?: string; // the session's transcript id (matches SessionInfo.id)
  /**
   * The folder the tab's conversation runs in. Per tab rather than per window, because the
   * CLI scopes conversations, permissions and CLAUDE.md directives by folder.
   */
  cwd?: string;
}

// Metadata of a vault credential (it never carries the secret value).
export interface CredentialMeta {
  id: string;
  name: string;
  username?: string;
  note?: string;
  createdAt: number;
}

// A background task in progress (Workflow / tool with run_in_background).
export interface BackgroundTask {
  id: string; // tool_use id that launched it (matches the <tool-use-id> of the notification)
  tool: string; // 'Workflow' | 'Task' | 'Bash' | …
  label: string; // what it is doing (workflow name / description / command)
}

// host -> webview. Every message can carry `tab` (the origin tab id):
// conversation/stats messages are routed to that tab's state; global
// messages (ready/config/cliStatus/locale/sessions/tabs) come without `tab`.
export type HostToWebview = HostMsg & { tab?: string };

type HostMsg =
  | { kind: 'ready' }
  | { kind: 'tabs'; tabs: TabInfo[]; activeTab: string }
  | { kind: 'config'; config: SessionConfig }
  | {
      kind: 'cliStatus';
      available: boolean;
      version?: string;
      error?: string;
      latest?: string; // latest published Claude CLI version (npm)
      cockpitVersion?: string; // this extension's version
    }
  | {
      kind: 'sessionInit';
      sessionId: string;
      model?: string;
      cwd?: string;
      mode?: string;
      tools?: string[];
      mcpServers?: { name: string; status: string }[];
      slashCommands?: string[];
    }
  | { kind: 'assistantStart'; id: string }
  | { kind: 'assistantText'; id: string; delta: string }
  | { kind: 'assistantDone'; id: string }
  | { kind: 'thinking'; id: string; delta: string }
  | { kind: 'toolUse'; id: string; name: string; input: unknown }
  // Subagent text forwarded by the CLI (--forward-subagent-text). `parentId` is the
  // Task tool_use that launched it — the webview appends it under that Task's card.
  | { kind: 'subagentText'; parentId: string; delta: string }
  | { kind: 'toolResult'; toolUseId: string; content: unknown; isError?: boolean }
  | {
      kind: 'permissionRequest';
      requestId: string;
      tool: string;
      displayName?: string;
      description?: string;
      input: unknown;
      suggestions?: PermissionSuggestion[];
      oldText?: string; // current content on disk (Write) for the diff
    }
  | { kind: 'askRequest'; requestId: string; questions: AskQuestion[] }
  | { kind: 'authRequired' }
  | { kind: 'stats'; stats: StatsSnapshot }
  // Session timeline/compactions (heavy): sent per turn, not per token.
  | { kind: 'statsTimeline'; timeline: TimelineSample[]; compactions: CompactionEvent[] }
  | { kind: 'turnComplete'; costUsd?: number; usage?: Usage }
  | { kind: 'busy'; busy: boolean }
  // Background task(s) (Workflow / run_in_background) still running after the
  // turn ended: the `result` clears busy, but the work goes on. It keeps the
  // "running" indicator in the timeline and in the Hub card until the notification arrives,
  // and lists what each process is doing (label) so the user is aware.
  | { kind: 'background'; tasks: BackgroundTask[] }
  | { kind: 'error'; message: string }
  | { kind: 'sessions'; sessions: SessionInfo[]; cwd: string }
  | { kind: 'slashCommands'; commands: string[] }
  | { kind: 'slashMeta'; meta: Record<string, SlashCmdMeta> }
  | { kind: 'slashResearching'; busy: boolean }
  | { kind: 'history'; items: HistoryItem[] }
  | { kind: 'resolvedPath'; requestId: string; text: string }
  | { kind: 'openSessions' }
  | { kind: 'taskTimings'; timings: Record<string, number> } // averages per type, already in the current (model,effort) scope (gauge)
  | { kind: 'usageData'; data: UsageData } // the answer to the Usage button
  | { kind: 'effortGate'; selected: string; min: string } // effort < the folder CLAUDE.md minimum: confirm first
  | { kind: 'voiceCorrected'; text: string } // dictation: the corrected text, which releases the input
  | { kind: 'voiceCorrectError' } // dictation: correction failed (keeps the original, unblocks)
  | { kind: 'draftRestore'; text: string } // restores the draft/dictation after a renderer reload/crash
  | { kind: 'voiceDict'; data: VoiceDictData } // the account's dictation dictionary (answer to the modal)
  | { kind: 'voiceReady' } // dictation: WS open + mic actually capturing (you may speak)
  | { kind: 'voiceTranscript'; text: string; isFinal: boolean } // dictation: partial/final transcription
  | { kind: 'voiceError'; message: string } // dictation: a failure (no token, a socket error, and so on)
  | { kind: 'voiceClosed' } // dictation: session ended
  | { kind: 'auth'; loggedIn: boolean } // estado de login (mostra Sign in OU Sign out)
  | { kind: 'pluginsData'; data: PluginsData } // lista de plugins/marketplaces (modal)
  | { kind: 'pluginsBusy'; busy: boolean; label?: string } // operation in progress
  | { kind: 'pluginsError'; message: string } // a plugin action failed
  | { kind: 'skillsBusy'; busy: boolean } // a get_context_usage reading is in flight
  // A SKILL.md body entered the context: it seals the Skill card in the timeline.
  // `tokens` is an ESTIMATE of the injected message; absent means the engine said nothing.
  | { kind: 'skillLoaded'; toolUseId: string; name: string; tokens?: number }
  // A HOOK injected text into the context, with no tool_use to seal it, so it becomes an
  // item of its own. `skill` appears when the text matches a SKILL.md body on disk.
  | { kind: 'hookInjected'; hook: string; event?: string; skill?: string; tokens?: number }
  // A warning the engine emitted mid-session as a `system` event: fast mode running out of
  // usage credits (CLI 2.1.221), a subagent whose model is restricted so the parent's model
  // answers instead (2.1.223), and whatever the next release adds. It has no tool_use to seal,
  // so it becomes its own timeline item — otherwise the effect (another model answering, fast
  // mode silently off) would reach the user with no cause.
  | { kind: 'engineNotice'; id: string; text: string; topic?: string }
  // Compaction (S11). `active` = it is happening right now (the turn is not stuck, the CLI is
  // condensing); the boundary arrives with `active: false` and the sizes, so the webview can say
  // in the user's language how much context was condensed. Numbers travel raw — no prose here,
  // the host has no i18n layer of its own for the timeline.
  | {
      kind: 'compaction';
      active: boolean;
      pre?: number;
      post?: number;
      trigger?: string;
      durationMs?: number;
    }
  // The tab was handed over to an interactive Remote Control session in the terminal: the
  // composer stops being the way in (the terminal / phone is), and the timeline keeps
  // following the transcript that session writes.
  // `phase` says what is KNOWN, not what was hoped for: 'connecting' while the interactive
  // process hasn't registered itself yet, 'active' once it has, 'failed' when it never came up
  // or died on its own. `active` stays for the composer's on/off state.
  | {
      kind: 'remoteState';
      active: boolean;
      phase?: 'connecting' | 'active' | 'failed';
      detail?: string;
    }
  | { kind: 'mcpData'; data: McpData } // MCP servers and their tools, for the modal
  | { kind: 'mcpBusy'; busy: boolean } // a `claude mcp list` health check is in flight
  // Spell-checker (host via hunspell-asm): result of a check (wrong words)
  // and of suggestions (per language).
  | { kind: 'spellResult'; bad: string[] }
  | { kind: 'spellSuggestResult'; requestId: string; word: string; pt: string[]; en: string[] }
  // --- Cofre de credenciais (TOTP 2FA) ---
  | { kind: 'credsData'; enrolled: boolean; items: CredentialMeta[] } // the vault's state
  | { kind: 'credsSetup'; qrSvg: string; secret: string; uri: string } // enrollment: QR + segredo
  | { kind: 'credsValue'; id: string; name: string; value: string } // the released value, to be injected into the composer
  | { kind: 'credsResult'; ok: boolean; action: string; message?: string } // result of an action
  | { kind: 'credsError'; message: string } // failure (storage unavailable, etc.)
  // Editor selection/active file to share as @file#a-b (composer toggle).
  | { kind: 'selection'; ref?: string }
  // Autocomplete de @-mention: resultados de arquivos p/ a query digitada.
  | { kind: 'mentionResults'; requestId: string; items: string[] };

// A slash command's metadata, researched by AI (cached globally in ~/.claude).
// `category` is an enum key (session|context|config|tools|account|info|plugin|other);
// `hint`/`detail` already come in the Cockpit's language.
export interface SlashCmdMeta {
  category: string;
  hint: string;
  detail?: string;
  group?: string; // name of the third-party plugin/tool (its own group)
}

// Dictation dictionary (per login): terms to recognize/preserve + replacements.
export interface VoiceReplacement {
  from: string; // how it is usually heard/transcribed
  to: string; // how it should be written
}
export interface VoiceDictData {
  terms: string[];
  replacements: VoiceReplacement[];
  account?: string; // account it belongs to (informative label)
  spellWords?: string[]; // spell-checker dictionary (added/ignored words)
}

// Attached image (base64 without the data: prefix).
export interface ImageAttachment {
  mediaType: string;
  data: string;
}

// webview -> host
export type WebviewToHost =
  | { kind: 'init' }
  | { kind: 'heartbeat' } // render liveness beat: prolonged silence = dead renderer (blank screen)
  | { kind: 'sendMessage'; text: string; images?: ImageAttachment[]; force?: boolean; selection?: string }
  | { kind: 'resolvePaths'; requestId: string; absPaths: string[] }
  | { kind: 'readClipboardFiles'; requestId: string }
  | { kind: 'openLink'; href: string; preview?: boolean }
  | { kind: 'interrupt' }
  | { kind: 'newSession' }
  | {
      kind: 'permissionDecision';
      requestId: string;
      decision: 'allow' | 'deny' | 'allow_always';
      message?: string; // feedback (in editable plan mode, the notes attached to "keep planning")
    }
  | { kind: 'askResponse'; requestId: string; answers: Record<string, string> }
  | { kind: 'setModel'; model: string }
  | { kind: 'removeModel'; model: string }
  | { kind: 'setEffort'; effort: string }
  | { kind: 'setPermissionMode'; mode: string }
  | { kind: 'setEngine'; engine: string }
  | { kind: 'setAllowAgents'; value: boolean }
  | { kind: 'renameSession'; sessionId: string; name: string }
  | { kind: 'openSettings' }
  | { kind: 'listSessions' }
  | { kind: 'resumeSession'; sessionId: string }
  | { kind: 'reloadSession'; sessionId: string }
  | { kind: 'remoteControl'; sessionId: string } // publishes the session for remote control (from a phone)
  | { kind: 'deleteSession'; sessionId: string }
  | { kind: 'deleteAllSessions' }
  | { kind: 'newTab'; cwd?: string } // no folder = inherit the current tab's
  | { kind: 'closeTab'; tabId: string }
  | { kind: 'switchTab'; tabId: string }
  // Moves a tab to another folder. Without a path the host opens its folder browser, which
  // the webview has no way to show.
  | { kind: 'setTabCwd'; tabId: string; path?: string }
  | { kind: 'installCli' }
  | { kind: 'updateCli' }
  | { kind: 'recheckCli' }
  | { kind: 'loginCli' }
  | { kind: 'logoutCli' }
  | { kind: 'clearContext' }
  | { kind: 'compactContext' }
  | { kind: 'mentionSearch'; requestId: string; query: string } // @-mention: busca arquivos
  | { kind: 'openDiff'; tool: string; input: unknown } // opens the proposed diff in the editor's own comparison window
  | { kind: 'draftChanged'; text: string } // mirrors the draft or dictation in the host, so a dead renderer loses nothing
  // Exports the conversation to a .md at the project root. mode 'direct' = mechanical (the
  // markdown is already built); 'ai' = rewritten via the CLI (same model/effort, spends tokens).
  | { kind: 'exportMd'; markdown: string; fileName?: string; mode: 'direct' | 'ai' }
  | { kind: 'voiceDictGet' } // modal: loads the account's dictation dictionary
  | { kind: 'voiceDictSave'; data: VoiceDictData } // modal: saves the dictation dictionary
  | { kind: 'setKeepCacheAlive'; value: boolean } // turns this tab's cache keep-alive on and off
  | { kind: 'openEditor' }
  | { kind: 'openFolder'; path: string }
  | { kind: 'taskDuration'; type: string; ms: number } // task duration sample (gauge)
  | { kind: 'rewind'; index: number } // rewinds the conversation to the (index)-th user prompt, dropping it
  | { kind: 'voiceStart'; language?: string } // dictation: the host opens the socket and captures the mic through ffmpeg
  | { kind: 'voiceStop' } // dictation: ends the capture
  | { kind: 'voiceCorrect'; text: string } // dictation: cleans the text up through a one-shot model call
  | { kind: 'pluginsRefresh'; force?: boolean } // the Plugins modal: (re)loads; force re-validates the URLs through the model
  | { kind: 'mcpRefresh' } // the MCP modal: (re)loads the init inventory plus `claude mcp list`
  // The Skills panel: re-reads get_context_usage through a control request, spending no turn.
  | { kind: 'skillsRefresh' }
  // The Skills panel: changes ONE skill's override, applied on the CLI's next spawn.
  | { kind: 'skillOverrideSet'; name: string; value: SkillOverride }
  | {
      kind: 'pluginAction';
      action: 'install' | 'uninstall' | 'enable' | 'disable' | 'update' | 'marketAdd' | 'marketRemove';
      arg: string;
      scope?: string;
    }
  | { kind: 'fetchUsage' } // the Usage button: fetches account, limits and breakdown — hot data
  | { kind: 'enableUsageTracking' } // instala o wrapper de statusline p/ capturar rate_limits real
  | { kind: 'saveImage'; mediaType: string; data: string }
  // Spell-checker: checks a batch of words; asks for suggestions for one; adds
  // to the user dictionary (persistent in the host).
  | { kind: 'spellCheck'; words: string[] }
  | { kind: 'spellSuggest'; requestId: string; word: string }
  | { kind: 'spellAdd'; word: string }
  // --- Cofre de credenciais (TOTP 2FA) ---
  | { kind: 'credsLoad' } // asks for the vault's state: enrolled, and the list
  | { kind: 'credsEnrollBegin' } // generates a fresh TOTP secret and returns the QR
  | { kind: 'credsEnrollConfirm'; code: string } // confirms the enrollment with the first code
  | { kind: 'credsAdd'; code: string; name: string; username?: string; value: string; note?: string }
  // Edit: absent/undefined value = keeps the current value; present = replaces it.
  | { kind: 'credsEdit'; code: string; id: string; name: string; username?: string; value?: string; note?: string }
  | { kind: 'credsUse'; code: string; id: string } // use: validates the TOTP and returns the value
  | { kind: 'credsDelete'; code: string; id: string };
