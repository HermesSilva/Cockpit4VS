using System.Collections.Generic;

namespace Tootega.Cockpit.Cli
{
    /// <summary>How to launch the engine for one session. Port of CliOptions in
    /// src/cli/CliProcessManager.ts.</summary>
    internal sealed class CliOptions
    {
        public string ExecutablePath { get; set; }
        public string Cwd { get; set; }

        /// <summary>Which engine the binary is; decides the argument list. Default: claude.</summary>
        public string Engine { get; set; } = EngineIds.Claude;

        /// <summary>host:port of the Tootega engine server (Tootega engine only).</summary>
        public string Server { get; set; }

        public string Model { get; set; }
        public string Effort { get; set; }
        public string PermissionMode { get; set; }

        /// <summary>
        /// Tools to disable (--disallowedTools), e.g. Task and Workflow to block subagents
        /// and workflows, which spend a lot of tokens. Empty disables nothing.
        /// </summary>
        public List<string> DisallowedTools { get; set; }

        public string ResumeSessionId { get; set; }

        /// <summary>
        /// Short language code (pt, en…) forcing the language of the QUESTIONS only.
        ///
        /// Empty is the normal case and is not "no rule": it means the questions follow the
        /// language of the conversation. Either way an appended system prompt is injected.
        /// </summary>
        public string AskLanguage { get; set; }

        /// <summary>
        /// The user's own system-prompt text, already expanded. It travels in the SAME
        /// --append-system-prompt payload as <see cref="AskLanguage"/>: repeating the flag
        /// does not accumulate, the last one wins (measured on CLI 2.1.217).
        /// </summary>
        public string ExtraSystemPrompt { get; set; }

        /// <summary>
        /// The "quiet" directive: it goes at the very START of the appended payload, before
        /// anything else. Empty injects nothing — the agent narrates and reports as usual.
        /// </summary>
        public string QuietPrompt { get; set; }

        /// <summary>
        /// Skill listing overrides (--settings JSON). Scoped to THIS process only; the
        /// user's ~/.claude/settings.json is never touched.
        /// </summary>
        public Dictionary<string, string> SkillOverrides { get; set; }

        /// <summary>
        /// Forwards subagent text in the stream (--forward-subagent-text, 2.1.211). Only
        /// meaningful when agents are allowed. Each event arrives tagged with
        /// parent_tool_use_id, so it is routed to the Task card that launched it instead of
        /// polluting the main bubble.
        /// </summary>
        public bool ForwardSubagentText { get; set; }
    }
}
