using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace Tootega.Cockpit.Options
{
    /// <summary>
    /// Tools &gt; Options &gt; Tootega Cockpit. Port of the `tootega.*` contribution block of
    /// the VS Code manifest. Defaults are deliberately identical, so a user moving between
    /// the two editors finds the same behaviour.
    /// </summary>
    [Guid(CockpitIds.OptionsPageGuidString)]
    [ComVisible(true)]
    public class CockpitOptions : DialogPage
    {
        private const string CatEngine = "Engine";
        private const string CatSession = "Session";
        private const string CatInterface = "Interface";
        private const string CatVoice = "Voice";
        private const string CatAdvanced = "Advanced";

        [Category(CatEngine)]
        [DisplayName("Claude CLI path")]
        [Description("Path to the Claude Code CLI executable. Leave as 'claude' to use the PATH.")]
        public string ClaudePath { get; set; } = "claude";

        [Category(CatEngine)]
        [DisplayName("Enable Tootega engine")]
        [Description("Master switch for the local Tootega Code engine. Off, nothing spawns agent.exe and the engine picker stays hidden.")]
        public bool TootegaEnabled { get; set; } = false;

        [Category(CatEngine)]
        [DisplayName("Tootega engine path")]
        [Description("Path to the Tootega Code CLI binary (agent.exe).")]
        public string TootegaPath { get; set; } = "agent.exe";

        [Category(CatEngine)]
        [DisplayName("Tootega server")]
        [Description("Where the TootegaEngine server listens.")]
        public string TootegaServer { get; set; } = "127.0.0.1:8080";

        [Category(CatEngine)]
        [DisplayName("Engine")]
        [Description("Which binary backs new sessions: claude or tootega. Ignored while the Tootega engine is disabled.")]
        public string Engine { get; set; } = "claude";

        // What a new conversation starts as. The three below are the working configuration
        // rather than the CLI's own defaults: the largest window, reasoning turned up, and
        // permissions out of the way. A user who wants the CLI to decide sets "default" here
        // and the flag stops being passed at all.
        [Category(CatSession)]
        [DisplayName("Model")]
        [Description("Default model for new sessions. 'default' passes no --model flag and lets the CLI decide. The [1m] suffix asks for the 1M context window.")]
        public string Model { get; set; } = "claude-opus-5[1m]";

        [Category(CatSession)]
        [DisplayName("Effort")]
        [Description("Default reasoning effort: default, low, medium, high, xhigh or max. 'default' uses effortLevel from ~/.claude/settings.json.")]
        public string Effort { get; set; } = "high";

        [Category(CatSession)]
        [DisplayName("Permission mode")]
        [Description("Permission mode forwarded to the CLI: default, plan, acceptEdits, auto, dontAsk or bypassPermissions.")]
        public string PermissionMode { get; set; } = "bypassPermissions";

        [Category(CatSession)]
        [DisplayName("Allow agents")]
        [Description("Allow the agent to launch subagents (Task) and workflows. Off saves tokens.")]
        public bool AllowAgents { get; set; } = false;

        [Category(CatSession)]
        [DisplayName("Auto-resume last session")]
        [Description("On opening a solution, resume the most recent session for that directory.")]
        public bool AutoResumeLastSession { get; set; } = true;

        [Category(CatSession)]
        [DisplayName("Auto-save before read/write")]
        [Description("Flush a dirty buffer before the agent reads or writes that file.")]
        public bool Autosave { get; set; } = true;

        [Category(CatInterface)]
        [DisplayName("Notify on complete")]
        [Description("Notify when the agent finishes and the Cockpit is not visible.")]
        public bool NotifyOnComplete { get; set; } = true;

        [Category(CatInterface)]
        [DisplayName("Expand thinking blocks")]
        [Description("Expand the model's thinking blocks by default.")]
        public bool ShowThinking { get; set; } = false;

        [Category(CatInterface)]
        [DisplayName("Expand tool cards")]
        [Description("Expand tool cards by default in the timeline.")]
        public bool ExpandToolCards { get; set; } = false;

        [Category(CatInterface)]
        [DisplayName("Timeline verbosity")]
        [Description("How much of the timeline to show: verbose, necessary, dialogo or quiet. Display only — it does not change the agent.")]
        public string Verbosity { get; set; } = "verbose";

        [Category(CatInterface)]
        [DisplayName("Spell check")]
        [Description("Inline PT-BR + EN spell-checker in the composer. Marks only, never auto-corrects.")]
        public bool SpellCheck { get; set; } = false;

        [Category(CatInterface)]
        [DisplayName("User name")]
        [Description("Name shown on your messages. Empty uses the OS user.")]
        public string UserName { get; set; } = string.Empty;

        [Category(CatVoice)]
        [DisplayName("ffmpeg path")]
        [Description("Path to the ffmpeg binary used for microphone capture. Empty uses ffmpeg from the PATH.")]
        public string FfmpegPath { get; set; } = string.Empty;

        [Category(CatVoice)]
        [DisplayName("Correct dictated text")]
        [Description("After dictation stops, run a clean one-shot spelling/grammar pass with the internal model.")]
        public bool VoiceCorrect { get; set; } = false;

        [Category(CatVoice)]
        [DisplayName("Dictation and question language")]
        [Description("Language code (pt, en, es, ...) for dictation and for the questions the agent asks. " +
                     "Empty dictates in English and lets the questions follow the language of the conversation.")]
        public string VoiceLanguage { get; set; } = string.Empty;

        [Category(CatAdvanced)]
        [DisplayName("Internal model")]
        [Description("Model used for the Cockpit's own clean utility calls (dictation correction, slash-command research).")]
        public string InternalModel { get; set; } = "claude-haiku-4-5";

        [Category(CatAdvanced)]
        [DisplayName("Quiet directive")]
        [Description("Injected at the very start of the system prompt on every CLI start: the agent stops " +
                     "narrating the execution and stops closing with a report or summary. Independent of the " +
                     "custom system prompt below — empty injects nothing.")]
        [Editor("System.ComponentModel.Design.MultilineStringEditor, System.Design", "System.Drawing.Design.UITypeEditor, System.Drawing")]
        public string QuietPrompt { get; set; } =
            "IMPORTANT — output discipline (this overrides any default instruction to summarize or " +
            "explain your work): Work silently. Do not narrate the task while doing it: no preamble, " +
            "no \"I will now…\", no play-by-play between tool calls, no \"let me…\". When the work is " +
            "finished, state ONLY that it is finished — a single short sentence such as \"Done.\" Do NOT " +
            "produce a closing report, a summary, a bulleted list of changes, or a restatement of what " +
            "the files or edits contain. The user can see the changes; repeating them is exactly what " +
            "this rule forbids. This rule governs the shape of every response. Answer direct questions " +
            "normally; this is about narration and closing reports, not about replying to the user.";

        [Category(CatAdvanced)]
        [DisplayName("Custom system prompt")]
        [Description("Text appended to the CLI system prompt on every start. Supports ${defaultShell}, ${projectPathWin}, ${wslRow} and friends.")]
        [Editor("System.ComponentModel.Design.MultilineStringEditor, System.Design", "System.Drawing.Design.UITypeEditor, System.Drawing")]
        public string SystemPromptText { get; set; } = string.Empty;

        [Category(CatAdvanced)]
        [DisplayName("Enable custom system prompt")]
        [Description("Whether the custom system prompt above is appended.")]
        public bool SystemPromptEnabled { get; set; } = false;

        [Category(CatAdvanced)]
        [DisplayName("OTEL receiver")]
        [Description("Run the local OpenTelemetry receiver that aggregates Claude Code telemetry.")]
        public bool OtelEnabled { get; set; } = false;

        [Category(CatInterface)]
        [DisplayName("Title bar button")]
        [Description("Show a Cockpit button in the Visual Studio title bar, beside Copilot's. Visual Studio has no supported extension point there, so the button is added to the shell's own window and may stop appearing after a Visual Studio update.")]
        public bool TitleBarButton { get; set; } = true;

        [Category(CatAdvanced)]
        [DisplayName("Debug logging")]
        [Description("Verbose logging in the Tootega Cockpit output pane.")]
        public bool DebugLog { get; set; } = false;

        /// <summary>
        /// The first <see cref="QuietPrompt"/> default. It reached the model but was too weak to
        /// override the CLI's own instinct to summarize its work, so the agent kept narrating even
        /// with the box filled. Kept only so the one-off migration can recognize an untouched value.
        /// </summary>
        private const string OldQuietDefault =
            "Work silently. While executing the task, do not narrate it: no commentary on what you are " +
            "about to do, what you are doing, or what you have just done, and no running explanation " +
            "between tool calls. When the work is finished, do not produce a report, a summary, or a list " +
            "of the changes — state only that it is finished. Answer direct questions normally; this rule " +
            "is about narration and closing reports, not about replying to the user.";

        /// <summary>
        /// One-off migration: a user who never touched the Quiet box has the OLD default persisted in
        /// the registry, so the new stronger default never reaches them and the agent keeps narrating.
        /// This rewrites it to the current default — but ONLY when the saved value is byte-for-byte the
        /// old default, so a real customization is never clobbered. Returns true when it rewrote.
        /// </summary>
        public bool MigrateStaleQuietDefault()
        {
            if (QuietPrompt != OldQuietDefault) return false;
            QuietPrompt = new CockpitOptions().QuietPrompt; // the current default
            base.SaveSettingsToStorage();
            return true;
        }

        /// <summary>Raised after the user closes the page with OK, so live sessions can react.</summary>
        public static event EventHandler Applied;

        public override void SaveSettingsToStorage()
        {
            base.SaveSettingsToStorage();
            Util.Log.DebugEnabled = DebugLog;
            Applied?.Invoke(this, EventArgs.Empty);
        }
    }
}
