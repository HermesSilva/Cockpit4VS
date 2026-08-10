using System;
using System.Collections.Generic;
using System.Text.Json;
using Tootega.Cockpit.Protocol;

namespace Tootega.Cockpit.Session
{
    /// <summary>How a turn ended abnormally.</summary>
    internal enum TurnErrorKind
    {
        /// <summary>The process died mid-turn without emitting a result.</summary>
        Aborted,
        /// <summary>The CLI reported an error result, carrying its own text.</summary>
        Error,
        /// <summary>A drop or stall the CLI retries. A soft warning, not a failure.</summary>
        Transient,
    }

    internal sealed class TurnError
    {
        public TurnErrorKind Kind { get; set; }
        public int? Code { get; set; }
        public string Text { get; set; }
    }

    /// <summary>The session defaults that come from settings, resolved when there is no override.</summary>
    internal sealed class SessionDefaults
    {
        public string Model { get; set; }
        public string Effort { get; set; }
        public string Permission { get; set; }
        public bool AllowAgents { get; set; }
    }

    /// <summary>
    /// Everything a <see cref="Session"/> needs from its host, as callbacks.
    ///
    /// The session owns a conversation and knows nothing about tabs, the IDE or the webview:
    /// it emits protocol messages and asks for what it cannot know. That is what lets several
    /// run in parallel and what makes the whole state machine testable with no IDE at all.
    /// </summary>
    internal sealed class SessionHooks
    {
        public Action<HostMessage> Emit { get; set; }
        public Action<bool> OnBusy { get; set; }
        public Action OnResult { get; set; }
        /// <summary>The agent is waiting on the user — the tab should draw attention.</summary>
        public Action OnInteraction { get; set; }
        public Action<string, IReadOnlyList<string>> OnInit { get; set; }
        public Action OnAuthRequired { get; set; }
        public Action<TurnError> OnTurnError { get; set; }

        /// <summary>Current content of the file a tool is about to write, for the diff.</summary>
        public Func<string, JsonElement?, string> FileText { get; set; }

        /// <summary>Each tool_use before execution, so a dirty buffer can be flushed first.</summary>
        public Action<string, JsonElement?> OnToolUse { get; set; }

        public Func<string, string> ClaudePath { get; set; }
        public Func<string> Cwd { get; set; }
        public Func<string> Engine { get; set; }
        public Func<string> EngineServer { get; set; }
        public Func<SessionDefaults> Settings { get; set; }

        /// <summary>Short language code for AskUserQuestion.</summary>
        public Func<string> AskLanguage { get; set; }

        /// <summary>
        /// The user's system-prompt text, already expanded. Applied on EVERY spawn including
        /// the silent respawn that continues a conversation — dropping it there would make the
        /// directive vanish mid-conversation with nobody noticing.
        /// </summary>
        public Func<string> ExtraSystemPrompt { get; set; }
    }
}
