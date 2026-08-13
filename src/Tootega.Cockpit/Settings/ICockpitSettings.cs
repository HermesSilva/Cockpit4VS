namespace Tootega.Cockpit.Settings
{
    /// <summary>
    /// The settings the host reads, behind an interface.
    ///
    /// <see cref="Options.CockpitOptions"/> is a DialogPage, which only exists inside a
    /// loaded VS package. Everything that merely *reads* configuration — engine selection,
    /// argument building, effort gating — goes through this instead, so that logic can be
    /// tested without an IDE and so a single fake covers it.
    /// </summary>
    internal interface ICockpitSettings
    {
        // Engine
        string ClaudePath { get; }
        bool TootegaEnabled { get; }
        string TootegaPath { get; }
        string TootegaServer { get; }
        string Engine { get; }

        // Session
        string Model { get; }
        string Effort { get; }
        string PermissionMode { get; }
        bool AllowAgents { get; }
        bool AutoResumeLastSession { get; }
        bool Autosave { get; }

        // Interface
        bool NotifyOnComplete { get; }
        bool ShowThinking { get; }
        bool ExpandToolCards { get; }
        string Verbosity { get; }
        bool SpellCheck { get; }
        string UserName { get; }

        // Voice
        string FfmpegPath { get; }
        bool VoiceCorrect { get; }
        string VoiceLanguage { get; }

        // Advanced
        string InternalModel { get; }
        string QuietPrompt { get; }
        string SystemPromptText { get; }
        bool SystemPromptEnabled { get; }
        bool OtelEnabled { get; }
        bool DebugLog { get; }
    }
}
