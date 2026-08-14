// UI state model.
export type ItemKind = 'user' | 'assistant' | 'tool' | 'hook';

// Token usage of a turn (normalized for the UI from the engine's usage).
export interface TurnUsage {
  input?: number;
  output?: number;
  cacheCreate?: number;
  cacheRead?: number;
}

export interface UserItem {
  kind: 'user';
  id: string;
  text: string;
  images?: string[]; // data URLs for the preview
  ts?: number; // epoch ms of when the message entered the UI
}

export interface AssistantItem {
  kind: 'assistant';
  id: string;
  text: string;
  thinking: string;
  done: boolean;
  canceled?: boolean;
  ts?: number; // epoch ms of the streaming start
  endTs?: number; // epoch ms of the turn's end (turnComplete)
  usage?: TurnUsage; // attached at the end of the turn (turnComplete)
  costUsd?: number; // turn cost (turnComplete)
}

export interface ToolItem {
  kind: 'tool';
  id: string; // tool_use id
  name: string;
  input: unknown;
  result?: unknown;
  isError?: boolean;
  done: boolean;
  ts?: number; // epoch ms of the tool_use
  endTs?: number; // epoch ms of the tool_result (duration = endTs - ts)
  // A `Skill` tool whose body entered the context. `skillTokens` is an ESTIMATE of the
  // message the engine injected; absent means it loaded, but with no size reported.
  skillLoaded?: string;
  skillTokens?: number;
  // Subagent text forwarded by the CLI (--forward-subagent-text), accumulated.
  // Only for the Task/Agent tool: it shows what the subagent wrote while working.
  subagentText?: string;
}

/**
 * Context a HOOK injected into the prompt. It goes through no tool_use at all — without this
 * item the cost, and the skill that came in with it, would only show in the panel.
 */
export interface HookItem {
  kind: 'hook';
  id: string;
  hook: string; // hook_name, ex.: 'SessionStart:startup'
  skill?: string; // skill reconhecida pelo corpo injetado
  tokens?: number; // ESTIMATIVA (chars/4)
  ts?: number;
}

/**
 * A warning the engine emitted mid-session (fast-mode credits running out, a restricted
 * subagent model, and so on). It has no tool_use to seal, so it becomes an item of its own —
 * otherwise the effect would reach the user with no cause.
 */
export interface NoticeItem {
  kind: 'notice';
  id: string;
  text: string;
  topic?: string; // the event's subtype, when the engine reported one
  ts?: number;
  // The compaction boundary (S11): the banner says how much was condensed, worded here —
  // the host sends raw numbers, since it has no i18n layer of its own for the timeline.
  compaction?: { pre?: number; post?: number; trigger?: string; durationMs?: number };
}

export type TimelineItem = UserItem | AssistantItem | ToolItem | HookItem | NoticeItem;

export interface PermissionSuggestion {
  type?: string;
  mode?: string;
  destination?: string;
  [k: string]: unknown;
}

export interface PermissionRequest {
  requestId: string;
  tool: string;
  displayName?: string;
  description?: string;
  input: unknown;
  suggestions?: PermissionSuggestion[];
  oldText?: string;
  planFile?: string; // ExitPlanMode: relative path of the plan saved under Planing/
}

export interface AskOption {
  label: string;
  description?: string;
}

export interface AskQuestion {
  question: string;
  header: string;
  multiSelect?: boolean;
  options: AskOption[];
}

export interface AskRequest {
  requestId: string;
  questions: AskQuestion[];
}

export interface TodoItem {
  content: string;
  status: 'pending' | 'in_progress' | 'completed';
  activeForm?: string;
  description?: string;
  id?: number; // the task's number when it comes from a Task* tool (TaskList "#N", TaskUpdate id)
}
