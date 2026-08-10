using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Options;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Session;
using Tootega.Cockpit.Settings;
using Tootega.Cockpit.Stats;
using Tootega.Cockpit.UI;
using Tootega.Cockpit.Util;
using CockpitSession = Tootega.Cockpit.Session.Session;

namespace Tootega.Cockpit.Host
{
    /// <summary>
    /// Composes the host: it owns the collaborators, builds each session's hooks, and answers
    /// the menu commands.
    ///
    /// The routing itself lives in <see cref="CockpitMessageRouter"/>, the tabs in
    /// <see cref="TabRegistry"/>, the IDE calls in <see cref="EditorBridge"/>. What is left
    /// here is the wiring and the lifecycle — deliberately, because this is the class that
    /// would otherwise grow into the three-thousand-line object the original had become.
    /// </summary>
    internal sealed class CockpitHostService : ICockpitHost, IDisposable
    {
        private readonly CockpitPackage _package;
        private readonly ICockpitSettings _settings;
        private readonly Engines _engines;
        private readonly StatsStore _statsStore;
        private readonly SkillBodyIndex _skillIndex;
        private readonly TaskTimings _taskTimings;
        private readonly StateStore _state;

        private readonly SurfaceBroadcaster _surfaces;
        private readonly TabRegistry _tabs;
        private readonly SessionLibrary _library;
        private readonly EditorBridge _editor;
        private readonly ModelCatalog _models;
        private readonly CliStatusReporter _cliStatus;
        private readonly CockpitMessageRouter _router;

        private CacheKeeper _cacheKeeper;
        private bool _disposed;

        public CockpitHostService(CockpitPackage package, CockpitOptions options)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _settings = new OptionsSettings(options ?? throw new ArgumentNullException(nameof(options)));

            _engines = new Engines(_settings);
            _statsStore = new StatsStore();
            _skillIndex = new SkillBodyIndex();
            _taskTimings = new TaskTimings();
            _state = new StateStore();

            _surfaces = new SurfaceBroadcaster();
            _editor = new EditorBridge(package);
            _models = new ModelCatalog(_state);
            _cliStatus = new CliStatusReporter(_engines);
            _library = new SessionLibrary(new SessionStore(), _state, () => _editor.WorkspaceCwd());

            _tabs = new TabRegistry(CreateSession);
            _tabs.Changed += (s, e) => PostTabs();

            _router = new CockpitMessageRouter(this);
            _surfaces.MessageReceived += OnSurfaceMessage;
        }

        // ---- Collaborators, for the router ----

        internal ICockpitSettings Settings => _settings;
        internal Engines Engines => _engines;
        internal TabRegistry Tabs => _tabs;
        internal SessionLibrary Library => _library;
        internal EditorBridge Editor => _editor;
        internal ModelCatalog Models => _models;
        internal CliStatusReporter CliStatus => _cliStatus;
        internal TaskTimings Timings => _taskTimings;
        internal StatsStore StatsStore => _statsStore;
        internal CockpitPackage Package => _package;

        /// <summary>A model/effort/permission change waiting for the next send to take effect.</summary>
        internal bool PendingRestart { get; set; }

        internal void Post(HostMessage message, string tabId = null) => _surfaces.Post(message, tabId);

        // ---- Surfaces ----

        public void RegisterSurface(CockpitWebView view) => _surfaces.Register(view);

        public void UnregisterSurface(CockpitWebView view) => _surfaces.Unregister(view);

        private void OnSurfaceMessage(object sender, string json)
        {
            var message = WebviewMessage.Parse(json);
            if (message == null)
            {
                Log.Debug("host: unusable message from the webview");
                return;
            }

            // Handlers touch VS services and the webview, so the whole route runs on the UI
            // thread rather than each handler marshalling for itself.
            //
            // VSSDK007 wants the JoinableTask awaited or joined; FileAndForget is the
            // vs-threading-sanctioned way to do neither on purpose. Messages arrive from the
            // browser's event handler, which cannot block waiting for the handler to finish.
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    await _router.RouteAsync(message);
                }
                catch (Exception ex)
                {
                    Log.Error("host: handling '" + message.Kind + "' failed", ex);
                }
            }).FileAndForget("tootega/cockpit/webviewMessage");
#pragma warning restore VSSDK007
        }

        // ---- Session wiring ----

        /// <summary>
        /// Builds a session bound to a tab. This is where a conversation gets everything it
        /// cannot know: where it runs, what the settings say, and how to reach the UI.
        /// </summary>
        private CockpitSession CreateSession(string tabId)
        {
            var hooks = new SessionHooks
            {
                Emit = message => Post(message, tabId),

                OnBusy = busy =>
                {
                    _tabs.SetStatus(tabId, busy ? "busy" : "idle");

                    // The local engine keeps no transcript we can replay from mid-turn, so the
                    // end of a turn is the moment its conversation can be repainted from what
                    // the agent just wrote.
                    if (!busy && _tabs.SessionFor(tabId).Engine() == EngineIds.Tootega)
                        ReplayTab(tabId, force: true);
                },

                OnResult = () => SendSessions(),

                // A permission prompt or a question is waiting: the conversation has to be
                // visible, or the user is blocked by something they cannot see.
                OnInteraction = ShowChatWindow,

                OnInit = (model, commands) =>
                {
                    if (commands != null && commands.Count > 0)
                        Post(HostMessages.SlashCommands(commands), tabId);

                    SendConfig();
                    SendSessions();
                },

                OnAuthRequired = () => Post(HostMessages.AuthRequired(), tabId),

                OnTurnError = error => Post(HostMessages.Error(DescribeTurnError(error)), tabId),

                // Both of these arrive on a CLI reader thread and need DTE, so they hop to the
                // UI thread and wait. Waiting is correct here rather than fire-and-forget: the
                // permission modal needs the text to render its diff, and the buffer has to be
                // flushed BEFORE the tool reads the file, not eventually.
                FileText = (tool, input) => ThreadHelper.JoinableTaskFactory.Run(async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    return _editor.CurrentFileText(tool, input);
                }),

                OnToolUse = (tool, input) =>
                {
                    if (!_settings.Autosave) return;

                    ThreadHelper.JoinableTaskFactory.Run(async delegate
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        _editor.AutoSaveForTool(tool, input);
                    });
                },

                ClaudePath = engine => _engines.PathFor(engine ?? _engines.Current),
                Cwd = () => _editor.WorkspaceCwd(),
                Engine = () => _engines.Current,
                EngineServer = () => _engines.TootegaServer,

                Settings = () => new SessionDefaults
                {
                    Model = Blank(_settings.Model) ?? ModelCatalog.DefaultModelId,
                    Effort = Blank(_settings.Effort) ?? ModelCatalog.DefaultModelId,
                    Permission = Blank(_settings.PermissionMode) ?? ModelCatalog.DefaultModelId,
                    AllowAgents = _settings.AllowAgents,
                },

                // English-only: the questions come back in the same language as the UI.
                AskLanguage = () => "en",
                ExtraSystemPrompt = ExtraSystemPrompt,
            };

            return new CockpitSession(hooks, _statsStore, _skillIndex);
        }

        /// <summary>
        /// The user's system-prompt text, expanded against this machine.
        ///
        /// Expansion drops lines mentioning a shell or folder that does not exist here: a table
        /// describing WSL on a machine without WSL would actively mislead the agent.
        /// </summary>
        private string ExtraSystemPrompt()
        {
            if (!_settings.SystemPromptEnabled) return null;
            return SystemPromptTemplate.Build(_settings.SystemPromptText, _editor.WorkspaceCwd());
        }

        private static string DescribeTurnError(TurnError error)
        {
            switch (error?.Kind)
            {
                case TurnErrorKind.Aborted:
                    return "The Claude process exited unexpectedly (code " + (error.Code?.ToString() ?? "?") +
                           ") before finishing the turn. Send again to continue.";

                case TurnErrorKind.Transient:
                    return "The connection was unstable — the turn may be incomplete. Send again if needed." +
                           (string.IsNullOrEmpty(error.Text) ? string.Empty : " (" + error.Text + ")");

                default:
                    return "The turn ended with an error." +
                           (string.IsNullOrEmpty(error?.Text) ? string.Empty : " " + error.Text);
            }
        }

        // ---- Shared operations ----

        internal void PostTabs() => Post(HostMessages.Tabs(_tabs.Snapshot(), _tabs.ActiveTab));

        internal void SendSessions() => Post(HostMessages.Sessions(_library.List(), _editor.WorkspaceCwd()));

        internal void SendConfig()
        {
            var active = _tabs.Active();
            var options = _models.Options(active.Model());

            Post(HostMessages.Config(new SessionConfig
            {
                Engine = active.Engine(),
                Engines = new List<string>(_engines.Available),
                Model = active.Model(),
                Effort = active.Effort(),
                Models = options,
                ModelMeta = _models.BuildMeta(options),
                Efforts = new List<string> { "default", "low", "medium", "high", "xhigh", "max" },
                DefaultModel = Blank(_settings.Model),
                DefaultEffort = Blank(_settings.Effort),
                PermissionMode = active.Permission(),
                PermissionModes = new List<string>
                {
                    "default", "plan", "acceptEdits", "auto", "dontAsk", "bypassPermissions",
                },
                AllowAgents = active.AllowAgents(),
                ShowThinking = _settings.ShowThinking,
                SpellCheck = _settings.SpellCheck,
                ExpandToolCards = _settings.ExpandToolCards,
                PendingRestart = PendingRestart,
                UserName = UserName(),
                VoiceCorrect = _settings.VoiceCorrect,
                Verbosity = Blank(_settings.Verbosity) ?? "verbose",
            }));
        }

        /// <summary>
        /// Repaints a tab from its transcript.
        ///
        /// Forced when a panel mounts even mid-turn: otherwise reopening a running conversation
        /// would show only what arrives afterwards, and the deltas in flight would append to
        /// nothing.
        /// </summary>
        internal void ReplayTab(string tabId, bool force = false)
        {
            if (!_tabs.Has(tabId)) return;

            var session = _tabs.SessionFor(tabId);
            var sessionId = session.SessionId ?? session.ResumeId;

            if (sessionId == null)
            {
                if (force) Post(HostMessages.History(new List<HistoryItem>()), tabId);
                return;
            }

            Post(HostMessages.History(_library.Transcript(sessionId)), tabId);
            Post(HostMessages.Stats(session.Snapshot()), tabId);
            session.SendTimeline();
        }

        internal void PostTaskTimings(string tabId)
        {
            var scope = TimingScope(_tabs.SessionFor(tabId));
            Post(HostMessages.TaskTimings(_taskTimings.Scoped(scope.Model, scope.Effort, scope.Verbosity)), tabId);
        }

        internal (string Model, string Effort, string Verbosity) TimingScope(CockpitSession session)
        {
            return (session.Model() ?? ModelCatalog.DefaultModelId,
                    session.Effort() ?? ModelCatalog.DefaultModelId,
                    Blank(_settings.Verbosity) ?? "verbose");
        }

        /// <summary>
        /// Detaches live tabs from a session before its transcript is deleted.
        ///
        /// Without it the open session still owns the file and rewrites it, and the context the
        /// user just deleted reappears in the hub.
        /// </summary>
        internal void DetachLiveSessions(string sessionId = null, bool all = false)
        {
            foreach (var entry in _tabs.Entries)
            {
                var id = entry.Value.SessionId ?? entry.Value.ResumeId;
                if (!all && id != sessionId) continue;

                entry.Value.ClearConversation();
                _tabs.SetTitle(entry.Key, null);
                Post(HostMessages.History(new List<HistoryItem>()), entry.Key);
            }
        }

        internal void StartCacheKeeper()
        {
            if (_cacheKeeper != null) return;

            _cacheKeeper = new CacheKeeper(_statsStore, () => _engines.PathFor(EngineIds.Claude), PingOpenSession);
            _cacheKeeper.Start();
        }

        /// <summary>
        /// Renews an OPEN conversation through its own live process, rather than a parallel
        /// --resume that would put two processes on one transcript.
        /// </summary>
        private OpenPingResult PingOpenSession(string sessionId)
        {
            foreach (var session in _tabs.Sessions)
            {
                var id = session.SessionId ?? session.ResumeId;
                if (id != sessionId) continue;

                if (session.Busy) return OpenPingResult.Busy;
                return session.KeepAlivePing() ? OpenPingResult.Pinged : OpenPingResult.Busy;
            }

            return OpenPingResult.None;
        }

        internal void ShowChatWindow()
        {
            // Fire-and-forget for the same reason as the router: the callers are void handlers
            // reacting to CLI events, and none of them can wait for a window to open.
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await _package.ShowToolWindowAsync<ChatToolWindow>(_package.DisposalToken);
            }).FileAndForget("tootega/cockpit/showChat");
#pragma warning restore VSSDK007
        }

        internal string UserName()
        {
            var configured = Blank(_settings.UserName);
            if (configured != null) return configured;

            try
            {
                return Environment.UserName;
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static string Blank(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        // ---- ICockpitHost: the menu commands ----

        public void NewSession()
        {
            var tabId = _tabs.EnsureActiveTab();
            _tabs.SessionFor(tabId).ClearConversation();
            _tabs.SetTitle(tabId, null);
            Post(HostMessages.History(new List<HistoryItem>()), tabId);
            SendConfig();
        }

        public void Interrupt() => _tabs.Active().Interrupt();

        public void OpenSessions()
        {
            ShowChatWindow();
            SendSessions();
            Post(HostMessages.OpenSessions());
        }

        public void ReopenClosed()
        {
            var latest = _library.LatestSessionId();
            if (latest == null) return;

            var tabId = _tabs.CreateTab();
            _tabs.SessionFor(tabId).Resume(latest);
            _tabs.SetTitle(tabId, _library.TitleOf(latest));
            ReplayTab(tabId, force: true);
            ShowChatWindow();
        }

        // Sign-in and update run in a visible console: they are interactive, and hiding them
        // would leave the user waiting on a prompt nobody showed.
        public void LoginCli() => _editor.RunVisible(Quote(_engines.PathFor(EngineIds.Claude)) + " /login", "Claude sign-in");

        public void LogoutCli() => _editor.RunVisible(Quote(_engines.PathFor(EngineIds.Claude)) + " /logout", "Claude sign-out");

        private static string Quote(string path)
        {
            return path != null && path.IndexOf(' ') >= 0 ? "\"" + path + "\"" : path;
        }

        public void SetApiKeyInteractive()
        {
            // Not wired yet: the key is read from the environment or the CLI's own OAuth
            // token, which covers the cases the picker needs today.
            Log.Info("Setting an API key from the UI is not wired yet; use ANTHROPIC_API_KEY.");
        }

        public void ClearApiKey()
        {
            Log.Info("Clearing the API key from the UI is not wired yet.");
        }

        public void EnableUsageTracking()
        {
            Report(new StatuslineInstaller(_state).Enable(), "Real usage tracking enabled.");
        }

        public void DisableUsageTracking()
        {
            Report(new StatuslineInstaller(_state).Disable(), "Real usage tracking disabled.");
        }

        public void EnableUtf8Fix()
        {
            Report(Utf8HookInstaller.Enable(), "Accent fix installed. New PowerShell tool calls return UTF-8.");
        }

        public void DisableUtf8Fix()
        {
            Report(Utf8HookInstaller.Disable(), "Accent fix removed.");
        }

        /// <summary>
        /// Reports a settings edit. The parse failure gets its own message because it is the
        /// one the user can act on: their settings.json has comments, and we refuse to rewrite
        /// a file we cannot round-trip.
        /// </summary>
        private static void Report(SettingsEditResult result, string success)
        {
            switch (result)
            {
                case SettingsEditResult.Ok:
                    Log.Info(success);
                    return;

                case SettingsEditResult.Unsupported:
                    Log.Info("Nothing to install on this platform.");
                    return;

                case SettingsEditResult.ParseError:
                    Log.Info("Could not update ~/.claude/settings.json — it exists but could not be parsed " +
                             "(does it have comments?). Edit it by hand.");
                    return;

                default:
                    Log.Info("Could not write ~/.claude/settings.json.");
                    return;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _surfaces.MessageReceived -= OnSurfaceMessage;

            _cacheKeeper?.Dispose();
            _tabs.Dispose();
            _taskTimings.Dispose();
            // Pending statistics are cheap to write and gone otherwise.
            _statsStore.Dispose();
        }
    }
}
