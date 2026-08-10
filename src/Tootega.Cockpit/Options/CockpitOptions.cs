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

        [Category(CatSession)]
        [DisplayName("Model")]
        [Description("Default model for new sessions. 'default' passes no --model flag and lets the CLI decide.")]
        public string Model { get; set; } = "default";

        [Category(CatSession)]
        [DisplayName("Effort")]
        [Description("Default reasoning effort: default, low, medium, high, xhigh or max. 'default' uses effortLevel from ~/.claude/settings.json.")]
        public string Effort { get; set; } = "default";

        [Category(CatSession)]
        [DisplayName("Permission mode")]
        [Description("Permission mode forwarded to the CLI: default, plan, acceptEdits, auto, dontAsk or bypassPermissions.")]
        public string PermissionMode { get; set; } = "default";

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
        [DisplayName("Dictation language")]
        [Description("Language code for dictation (pt, en, es, ...). Empty follows the UI language.")]
        public string VoiceLanguage { get; set; } = string.Empty;

        [Category(CatAdvanced)]
        [DisplayName("Internal model")]
        [Description("Model used for the Cockpit's own clean utility calls (dictation correction, slash-command research).")]
        public string InternalModel { get; set; } = "claude-haiku-4-5";

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

        [Category(CatAdvanced)]
        [DisplayName("Debug logging")]
        [Description("Verbose logging in the Tootega Cockpit output pane.")]
        public bool DebugLog { get; set; } = false;

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
