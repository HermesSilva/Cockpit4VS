using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Options;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Secrets;
using Tootega.Cockpit.Session;
using Tootega.Cockpit.Settings;
using Tootega.Cockpit.Stats;
using Tootega.Cockpit.UI;
using Tootega.Cockpit.Util;
using Tootega.Cockpit.Voice;
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

        // The auxiliary surfaces. Each owns one panel's worth of behaviour, and the router only
        // decides which of them a message belongs to.
        private readonly SpellingService _spelling;
        private readonly DictationService _dictation;
        private readonly ExtensionsBroker _extensions;
        private readonly VaultBroker _vault;
        private readonly ConversationExporter _exporter;
        private readonly DiffLauncher _diff;
        private readonly UsageMonitor _usage;
        private readonly RemoteControlBroker _remote;

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
            _library = new SessionLibrary(new SessionStore(), _state);

            _tabs = new TabRegistry(CreateSession, () => _editor.WorkspaceCwd());
            _tabs.Changed += (s, e) =>
            {
                PostTabs();

                // VSTHRD010: RefreshCaptions reads as UI-affinitized because the UI call is in
                // its body, but that call is inside a switch to the main thread — which is
                // exactly why this handler can be reached from a CLI reader thread.
#pragma warning disable VSTHRD010
                RefreshCaptions();
#pragma warning restore VSTHRD010
            };

            var dictionary = new VoiceDictionary();
            var terms = new WorkspaceTerms();
            var ai = new AiClient();
            ai.SetInternalModel(_settings.InternalModel);

            _spelling = new SpellingService(dictionary, terms);
            _dictation = new DictationService(_settings, dictionary, terms, _spelling,
                                              new TextCorrector(ai), Post);
            _extensions = new ExtensionsBroker(new PluginManager(ai), _state,
                                               () => _engines.PathFor(EngineIds.Claude), Post);
            _vault = new VaultBroker(CreateVault(), Post);
            _exporter = new ConversationExporter(_editor, () => _engines.PathFor(EngineIds.Claude));
            _diff = new DiffLauncher();

            _usage = new UsageMonitor(_state, () => _engines.PathFor(EngineIds.Claude),
                                      () => _tabs.Entries, Post);
            _remote = new RemoteControlBroker(_editor, () => _engines.PathFor(EngineIds.Claude),
                                              tabId => ReplayTab(tabId, force: true), Post);

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

        internal SpellingService Spelling => _spelling;
        internal DictationService Dictation => _dictation;
        internal ExtensionsBroker Extensions => _extensions;
        internal VaultBroker Vault => _vault;
        internal ConversationExporter Exporter => _exporter;
        internal DiffLauncher Diff => _diff;
        internal UsageMonitor Usage => _usage;
        internal RemoteControlBroker Remote => _remote;

        /// <summary>
        /// The vault, or null when this machine has no usable credential store.
        ///
        /// Null rather than a throwing stub: the modal has a message for "unavailable", and a
        /// vault that pretends to work would be the one failure that loses a secret.
        /// </summary>
        private static CredentialsStore CreateVault()
        {
            try
            {
                return new CredentialsStore(new WindowsSecretStorage());
            }
            catch (Exception ex)
            {
                Log.Debug("vault: unavailable: " + ex.Message);
                return null;
            }
        }

        /// <summary>A model/effort/permission change waiting for the next send to take effect.</summary>
        internal bool PendingRestart { get; set; }

        private bool _autoResumeDone;

        /// <summary>
        /// Takes the one auto-resume this IDE session gets. False every time after that.
        /// </summary>
        internal bool ClaimAutoResume()
        {
            if (_autoResumeDone) return false;
            _autoResumeDone = true;
            return true;
        }

        internal void Post(HostMessage message, string tabId = null) => _surfaces.Post(message, tabId);

        // ---- Surfaces ----

        public void RegisterSurface(CockpitWebView view) => _surfaces.Register(view);

        public void UnregisterSurface(CockpitWebView view) => _surfaces.Unregister(view);

        private void OnSurfaceMessage(object sender, SurfaceMessage surfaceMessage)
        {
            var message = WebviewMessage.Parse(surfaceMessage.Json);
            if (message == null)
            {
                Log.Debug("host: unusable message from the webview");
                return;
            }

            // Which conversation this message belongs to comes from the WINDOW it came from, not
            // from whichever tab happens to be active: with several conversations open, the user
            // types into one while another streams.
            var origin = surfaceMessage.TabId;

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
                    await _router.RouteAsync(message, origin);
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

                OnResult = () =>
                {
                    SendSessions(tabId);
                    // A turn just spent tokens, so the limits the panels show are now behind.
                    RefreshUsageAfterTurn();
                },

                // A permission prompt or a question is waiting: THIS conversation has to be
                // visible, or the user is blocked by something they cannot see.
                OnInteraction = () => ShowConversation(tabId),

                OnInit = (model, commands) =>
                {
                    if (commands != null && commands.Count > 0)
                        Post(HostMessages.SlashCommands(commands), tabId);

                    SendConfig();
                    SendSessions(tabId);
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

                // The tab's folder, read per turn rather than captured: the user can move a tab
                // to another folder, and the next turn has to start there.
                Cwd = () => _tabs.Cwd(tabId),

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
                ExtraSystemPrompt = () => ExtraSystemPrompt(tabId),
            };

            var session = new CockpitSession(hooks, _statsStore, _skillIndex);

            // The folder's skill overrides apply from the first spawn: they are start-up
            // arguments, so a session built without them would list skills the user turned off.
            foreach (var pair in _extensions.OverridesFor(_tabs.Cwd(tabId)))
            {
                session.SkillOverrides[pair.Key] = pair.Value;
            }

            return session;
        }

        /// <summary>
        /// The user's system-prompt text, expanded against this machine.
        ///
        /// Expansion drops lines mentioning a shell or folder that does not exist here: a table
        /// describing WSL on a machine without WSL would actively mislead the agent.
        /// </summary>
        private string ExtraSystemPrompt(string tabId)
        {
            if (!_settings.SystemPromptEnabled) return null;
            return SystemPromptTemplate.Build(_settings.SystemPromptText, _tabs.Cwd(tabId));
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

        /// <summary>
        /// Sends a tab the conversations of ITS folder.
        ///
        /// Scoped to the tab, not broadcast: two tabs on different folders have different
        /// histories, and one list for both would offer conversations that cannot be resumed
        /// where they are shown.
        /// </summary>
        internal void SendSessions(string tabId = null)
        {
            var tab = tabId != null && _tabs.Has(tabId) ? tabId : _tabs.EnsureActiveTab();
            var cwd = _tabs.Cwd(tab);
            Post(HostMessages.Sessions(_library.List(cwd), cwd), tab);
        }

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

            Post(HostMessages.History(_library.Transcript(_tabs.Cwd(tabId), sessionId)), tabId);
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
        /// <param name="cwd">
        /// When given, only tabs on that folder are detached — a mass delete is scoped to one
        /// folder, and clearing conversations that live somewhere else would be destructive
        /// beyond what the user asked for.
        /// </param>
        internal void DetachLiveSessions(string sessionId = null, bool all = false, string cwd = null)
        {
            foreach (var entry in _tabs.Entries)
            {
                if (cwd != null && !string.Equals(_tabs.Cwd(entry.Key), cwd, StringComparison.OrdinalIgnoreCase)) continue;

                var id = entry.Value.SessionId ?? entry.Value.ResumeId;
                if (!all && id != sessionId) continue;

                entry.Value.ClearConversation();
                _tabs.SetTitle(entry.Key, null);
                Post(HostMessages.History(new List<HistoryItem>()), entry.Key);
            }
        }

        /// <summary>
        /// The live sessions of one folder.
        ///
        /// Anything scoped to a folder — a skill override, a deleted transcript — has to reach
        /// exactly these and no others.
        /// </summary>
        internal IEnumerable<CockpitSession> SessionsOn(string cwd)
        {
            foreach (var entry in _tabs.Entries)
            {
                if (string.Equals(_tabs.Cwd(entry.Key), cwd, StringComparison.OrdinalIgnoreCase))
                    yield return entry.Value;
            }
        }

        internal void StartCacheKeeper()
        {
            if (_cacheKeeper != null) return;

            _cacheKeeper = new CacheKeeper(_statsStore, () => _engines.PathFor(EngineIds.Claude), PingOpenSession);
            _cacheKeeper.Start();
        }

        /// <summary>
        /// Starts the background work the first panel needs: the usage refresh, and the
        /// telemetry receiver when the user has opted in.
        /// </summary>
        internal void StartUsage()
        {
            _usage.Start();
            if (_settings.OtelEnabled) _usage.StartTelemetry();
        }

        /// <summary>Refreshes the account's limits at the end of a turn.</summary>
        internal void RefreshUsageAfterTurn()
        {
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(() => _usage.RefreshAsync(false))
                .FileAndForget("tootega/cockpit/refreshUsage");
#pragma warning restore VSSDK007
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

        /// <summary>
        /// Opens a new conversation in a window of its own.
        /// </summary>
        /// <param name="cwd">The folder it runs in; the IDE's when null.</param>
        internal void OpenConversation(string cwd = null)
        {
            var tabId = _tabs.CreateTab(cwd);
            ShowConversation(tabId);
        }

        /// <summary>
        /// Brings a conversation's window forward, opening it if it has none.
        ///
        /// Fire-and-forget: the callers are void handlers reacting to CLI events — a permission
        /// prompt arriving, a question being asked — and none of them can wait for a window.
        /// </summary>
        internal void ShowConversation(string tabId)
        {
            if (tabId == null) return;

#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await _package.ShowConversationAsync(tabId, _package.DisposalToken);
            }).FileAndForget("tootega/cockpit/showConversation");
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Renames every conversation window after its title and folder.
        ///
        /// Marshalled, because tab changes come from CLI reader threads as often as from the UI:
        /// a title is set by the first prompt of a turn.
        /// </summary>
        private void RefreshCaptions()
        {
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _package.RefreshConversationCaptions();
            }).FileAndForget("tootega/cockpit/refreshCaptions");
#pragma warning restore VSSDK007
        }

        /// <summary>The tab already holding a conversation, or null when none is.</summary>
        internal string TabOf(string sessionId)
        {
            if (sessionId == null) return null;

            foreach (var entry in _tabs.Entries)
            {
                var id = entry.Value.SessionId ?? entry.Value.ResumeId;
                if (id == sessionId) return entry.Key;
            }

            return null;
        }

        /// <summary>Brings the hub forward, opening it on first use.</summary>
        internal void ShowHub()
        {
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await _package.ShowToolWindowAsync<HubToolWindow>(_package.DisposalToken);
            }).FileAndForget("tootega/cockpit/showHub");
#pragma warning restore VSSDK007
        }

        /// <summary>Closes a conversation by closing its window.</summary>
        internal void CloseConversation(string tabId)
        {
            if (tabId == null) return;

#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // No window (it was closed already, or never opened): drop the tab directly, so
                // the hub does not keep listing a conversation nobody can reach.
                if (_package.FindConversationWindow(tabId) == null) CloseTabFromWindow(tabId);
                else _package.CloseConversationWindow(tabId);
            }).FileAndForget("tootega/cockpit/closeConversation");
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Drops a tab because its window is gone.
        ///
        /// Called from the window's teardown, which is the only place that knows it happened —
        /// the user can close a window from its own close button, the tab strip, or by closing
        /// the whole IDE layout.
        /// </summary>
        public void CloseTabFromWindow(string tabId)
        {
            if (_disposed || tabId == null) return;

            _tabs.Drop(tabId);
            SendConfig();
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

        /// <summary>
        /// Starts a conversation in a window of its own.
        ///
        /// It does not clear whatever is open: a new conversation is a new window, and wiping
        /// the one in front of the user was never what this command meant.
        /// </summary>
        public void NewSession() => OpenConversation();

        public void OpenOrFocusConversation()
        {
            // The active conversation, when there is one; a new one otherwise. Repeating the
            // command must not leave a trail of empty windows behind.
            if (_tabs.ActiveTab != null && _tabs.Has(_tabs.ActiveTab)) ShowConversation(_tabs.ActiveTab);
            else OpenConversation();
        }

        public void ReloadActiveView()
        {
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _package.FindConversationWindow(_tabs.ActiveTab)?.Reload();
            }).FileAndForget("tootega/cockpit/reloadView");
#pragma warning restore VSSDK007
        }

        public void Interrupt() => _tabs.Active().Interrupt();

        public void OpenSessions()
        {
            // The saved-context list lives in the hub, which is where this command belongs: it
            // is a question about every conversation, not about one.
            ShowHub();
            Post(HostMessages.OpenSessions());
        }

        public void ReopenClosed()
        {
            // The IDE's folder, because there is no tab yet to take one from.
            var cwd = _editor.WorkspaceCwd();

            var latest = _library.LatestSessionId(cwd);
            if (latest == null) return;

            var tabId = _tabs.CreateTab(cwd);
            _tabs.SessionFor(tabId).Resume(latest);
            _tabs.SetTitle(tabId, _library.TitleOf(cwd, latest));
            ShowConversation(tabId);
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
            // The microphone first: an ffmpeg left running would outlive the IDE.
            _dictation.Dispose();
            _usage.Dispose();
            _remote.Dispose();
            _diff.Cleanup();
            _tabs.Dispose();
            _taskTimings.Dispose();
            // Pending statistics are cheap to write and gone otherwise.
            _statsStore.Dispose();
        }
    }
}
