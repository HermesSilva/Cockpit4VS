using Tootega.Cockpit.Options;

namespace Tootega.Cockpit.Settings
{
    /// <summary>
    /// Adapts the Tools &gt; Options page to <see cref="ICockpitSettings"/>. It reads the
    /// live page every time rather than caching, so a setting changed mid-session takes
    /// effect on the next read without anything having to invalidate a copy.
    /// </summary>
    internal sealed class OptionsSettings : ICockpitSettings
    {
        private readonly CockpitOptions _options;

        public OptionsSettings(CockpitOptions options)
        {
            _options = options;
        }

        public string ClaudePath => _options.ClaudePath;
        public bool TootegaEnabled => _options.TootegaEnabled;
        public string TootegaPath => _options.TootegaPath;
        public string TootegaServer => _options.TootegaServer;
        public string Engine => _options.Engine;

        public string Model => _options.Model;
        public string Effort => _options.Effort;
        public string PermissionMode => _options.PermissionMode;
        public bool AllowAgents => _options.AllowAgents;
        public bool AutoResumeLastSession => _options.AutoResumeLastSession;
        public bool Autosave => _options.Autosave;

        public bool NotifyOnComplete => _options.NotifyOnComplete;
        public bool ShowThinking => _options.ShowThinking;
        public bool ExpandToolCards => _options.ExpandToolCards;
        public string Verbosity => _options.Verbosity;
        public bool SpellCheck => _options.SpellCheck;
        public string UserName => _options.UserName;

        public string FfmpegPath => _options.FfmpegPath;
        public bool VoiceCorrect => _options.VoiceCorrect;
        public string VoiceLanguage => _options.VoiceLanguage;

        public string InternalModel => _options.InternalModel;
        public string QuietPrompt => _options.QuietPrompt;
        public string SystemPromptText => _options.SystemPromptText;
        public bool SystemPromptEnabled => _options.SystemPromptEnabled;
        public bool OtelEnabled => _options.OtelEnabled;
        public bool DebugLog => _options.DebugLog;
    }
}
