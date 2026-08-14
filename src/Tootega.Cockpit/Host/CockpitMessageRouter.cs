using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Options;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Session;
using Tootega.Cockpit.UI;
using Tootega.Cockpit.Util;
using CockpitSession = Tootega.Cockpit.Session.Session;

namespace Tootega.Cockpit.Host
{
    /// <summary>
    /// Turns a webview message into host action. The counterpart of the original's
    /// onWebviewMessage switch, kept apart from the host's wiring so each stays readable.
    ///
    /// Every handler assumes the UI thread; the host marshals before calling in.
    /// </summary>
    internal sealed class CockpitMessageRouter
    {
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        private readonly CockpitHostService _host;

        // Backs the @-mention of live sessions (CLI 2.1.232): the same registry Remote Control
        // reads, queried by name here so the composer can offer another session as a target.
        private readonly SessionRegistry _registry = new SessionRegistry();

        public CockpitMessageRouter(CockpitHostService host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <param name="origin">
        /// The conversation of the window the message came from, or null when it came from the
        /// hub — which has no conversation of its own and speaks for the active one.
        /// </param>
        public async Task RouteAsync(WebviewMessage message, string origin = null)
        {
            // Asserting would be wrong in a Task-returning method: it is cheaper and safer to
            // switch than to demand the caller got it right.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Before anything is resolved, because a trace line must not create a conversation,
            // make one active, or otherwise be visible in the thing it is observing.
            if (message.Kind == WebviewMessageKinds.Trace)
            {
                Log.Debug("webview: " + message.GetString("text"));
                return;
            }

            var tabId = _host.Tabs.Has(origin) ? origin : _host.Tabs.EnsureActiveTab();
            var session = _host.Tabs.SessionFor(tabId);

            // The conversation being used is the active one, which is what the hub reflects.
            if (origin != null) _host.Tabs.SetActive(origin);

            // Resolved once per message: nearly every handler below is scoped to the folder of
            // the tab the message came from, never to the window's.
            var cwd = _host.Tabs.Cwd(tabId);

            switch (message.Kind)
            {
                case WebviewMessageKinds.Init:
                    await OnInitAsync(tabId);
                    return;

                case WebviewMessageKinds.Heartbeat:
                    // Proof the renderer is alive. Nothing to do beyond not treating it as noise.
                    return;

                // --- Conversation ---

                case WebviewMessageKinds.SendMessage:
                    OnSendMessage(tabId, session, message.As<SendMessagePayload>());
                    return;

                case WebviewMessageKinds.Interrupt:
                    session.Interrupt();
                    return;

                case WebviewMessageKinds.NewSession:
                    _host.NewSession();
                    return;

                case WebviewMessageKinds.ClearContext:
                    session.ClearConversation();
                    _host.Tabs.SetTitle(tabId, null);
                    _host.Post(HostMessages.History(new List<HistoryItem>()), tabId);
                    return;

                case WebviewMessageKinds.CompactContext:
                    // Compaction is the CLI's own command; we only send it.
                    session.Send("/compact");
                    return;

                case WebviewMessageKinds.DraftChanged:
                    _host.Tabs.SetDraft(tabId, message.GetString("text"));
                    return;

                // --- Interactive protocols ---

                case WebviewMessageKinds.PermissionDecision:
                {
                    var payload = message.As<PermissionDecisionPayload>();
                    session.Decide(payload.RequestId, payload.Decision, payload.Message);
                    return;
                }

                case WebviewMessageKinds.AskResponse:
                {
                    var payload = message.As<AskResponsePayload>();
                    session.Answer(payload.RequestId, payload.Answers ?? new Dictionary<string, string>());
                    return;
                }

                // --- Session configuration ---
                // Each of these restarts the CLI on the next send: they are start-up
                // arguments, so the change is announced as pending rather than applied
                // silently in the middle of a conversation.

                case WebviewMessageKinds.SetModel:
                    session.SetModel(message.GetString("model") ?? ModelCatalog.DefaultModelId);
                    MarkPendingRestart();
                    _host.PostTaskTimings(tabId);
                    return;

                case WebviewMessageKinds.SetEffort:
                    session.SetEffort(message.GetString("effort") ?? ModelCatalog.DefaultModelId);
                    MarkPendingRestart();
                    // A new scope means the activity gauge has to recalibrate.
                    _host.PostTaskTimings(tabId);
                    return;

                case WebviewMessageKinds.SetPermissionMode:
                    session.SetPermission(message.GetString("mode") ?? ModelCatalog.DefaultModelId);
                    MarkPendingRestart();
                    return;

                case WebviewMessageKinds.SetAllowAgents:
                    session.SetAllowAgents(message.GetBool("value"));
                    MarkPendingRestart();
                    return;

                case WebviewMessageKinds.SetEngine:
                    session.SetEngine(message.GetString("engine") == EngineIds.Tootega
                        ? EngineIds.Tootega
                        : EngineIds.Claude);
                    MarkPendingRestart();
                    return;

                case WebviewMessageKinds.SetKeepCacheAlive:
                    session.SetKeepCacheAlive(message.GetBool("value"));
                    return;

                case WebviewMessageKinds.RemoveModel:
                    RemoveModel(session, message.GetString("model"));
                    return;

                // --- Saved contexts ---

                case WebviewMessageKinds.ListSessions:
                    _host.SendSessions(tabId);
                    return;

                case WebviewMessageKinds.ResumeSession:
                    OpenSession(cwd, message.GetString("sessionId"));
                    return;

                case WebviewMessageKinds.ReloadSession:
                    _host.ReplayTab(tabId, force: true);
                    return;

                case WebviewMessageKinds.RenameSession:
                    _host.Library.Rename(message.GetString("sessionId"), message.GetString("name"));
                    _host.SendSessions(tabId);
                    return;

                case WebviewMessageKinds.DeleteSession:
                {
                    // The webview already confirmed. Detach first, or the live session rewrites
                    // the transcript and the deleted context comes back.
                    var id = message.GetString("sessionId");
                    _host.DetachLiveSessions(id);
                    _host.Library.Delete(cwd, id);
                    _host.SendSessions(tabId);
                    return;
                }

                case WebviewMessageKinds.DeleteAllSessions:
                    // Only this tab's folder: the other tabs' conversations are not the user's
                    // to lose from here.
                    _host.DetachLiveSessions(all: true, cwd: cwd);
                    _host.Library.DeleteAll(cwd);
                    _host.SendSessions(tabId);
                    return;

                // --- Tabs ---

                case WebviewMessageKinds.NewTab:
                    // A new tab inherits the folder of the one it was opened from, which is
                    // almost always the folder the user is still thinking about. It gets a window
                    // of its own, because that is what a conversation is here.
                    _host.OpenConversation(message.GetString("cwd") ?? cwd);
                    return;

                case WebviewMessageKinds.SetTabCwd:
                    SetTabCwd(message.GetString("tabId") ?? tabId, message.GetString("path"));
                    return;

                case WebviewMessageKinds.CloseTab:
                    // Closing the window is what closes the conversation; the window's own
                    // teardown then drops the tab, so there is one path for both routes.
                    _host.CloseConversation(message.GetString("tabId") ?? tabId);
                    return;

                case WebviewMessageKinds.SwitchTab:
                {
                    // Switching means bringing that conversation's window forward — from the hub,
                    // that is exactly what the user is asking for.
                    var target = message.GetString("tabId");
                    if (!_host.Tabs.Has(target)) return;

                    _host.ShowConversation(target);
                    return;
                }

                // --- CLI and account ---

                case WebviewMessageKinds.RecheckCli:
                    await ReportCliAsync();
                    return;

                case WebviewMessageKinds.InstallCli:
                    _host.Editor.OpenExternal("https://code.claude.com/docs/en/quickstart");
                    return;

                case WebviewMessageKinds.UpdateCli:
                    _host.Editor.RunVisible("claude update", "Claude CLI update");
                    return;

                case WebviewMessageKinds.LoginCli:
                    _host.LoginCli();
                    return;

                case WebviewMessageKinds.LogoutCli:
                    _host.LogoutCli();
                    return;

                case WebviewMessageKinds.FetchUsage:
                    await _host.Usage.SendAsync(tabId);
                    return;

                case WebviewMessageKinds.EnableUsageTracking:
                    _host.EnableUsageTracking();
                    // The panel is open and showing the old tracking state.
                    await _host.Usage.SendAsync(tabId);
                    return;

                case WebviewMessageKinds.RemoteControl:
                    _host.Remote.Toggle(tabId, cwd, session, _host.Tabs.Title(tabId));
                    return;

                // --- Editor integration ---

                case WebviewMessageKinds.OpenSettings:
                    _host.Package.ShowOptionPage(typeof(CockpitOptions));
                    return;

                case WebviewMessageKinds.OpenLink:
                    // A file card on the timeline sends a relative path here, not a URL; OpenLink
                    // opens it in the editor (OpenExternal alone refused it silently).
                    _host.Editor.OpenLink(message.GetString("href"), cwd);
                    return;

                case WebviewMessageKinds.OpenEditor:
                    // Sent by the hub after it resumes or creates a conversation: bring THAT
                    // conversation's window forward, not some other one.
                    _host.ShowConversation(tabId);
                    return;

                case WebviewMessageKinds.OpenFolder:
                    _host.Editor.OpenFolder(message.GetString("path"));
                    return;

                case WebviewMessageKinds.ResolvePaths:
                {
                    var payload = message.As<ResolvePathsPayload>();
                    _host.Post(HostMessages.ResolvedPath(payload.RequestId, JoinResolved(payload.AbsPaths, cwd)), tabId);
                    return;
                }

                case WebviewMessageKinds.ReadClipboardFiles:
                {
                    // The webview sandbox does not expose a pasted file's path, so the host
                    // reads the clipboard itself.
                    var requestId = message.GetString("requestId");
                    _host.Post(HostMessages.ResolvedPath(requestId, JoinResolved(ClipboardFiles.Read(), cwd)), tabId);
                    return;
                }

                case WebviewMessageKinds.MentionSearch:
                {
                    var requestId = message.GetString("requestId");
                    var query = message.GetString("query");
                    var items = await BuildMentionsAsync(query, cwd, session);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _host.Post(HostMessages.MentionResults(requestId, items), tabId);
                    return;
                }

                case WebviewMessageKinds.TaskDuration:
                {
                    var payload = message.As<TaskDurationPayload>();
                    var scope = _host.TimingScope(session);
                    _host.Timings.Record(scope.Model, scope.Effort, scope.Verbosity, payload.Type, payload.Ms);
                    _host.PostTaskTimings(tabId);
                    return;
                }

                // --- Rewind ---

                case WebviewMessageKinds.Rewind:
                    Rewind(tabId, session, cwd, message.GetInt("index"));
                    return;

                // --- Spell checker ---

                case WebviewMessageKinds.SpellCheck:
                    _host.Post(await _host.Spelling.CheckAsync(cwd, message.GetStringList("words")), tabId);
                    return;

                case WebviewMessageKinds.SpellSuggest:
                    _host.Post(await _host.Spelling.SuggestAsync(cwd, message.GetString("requestId"),
                                                                 message.GetString("word")), tabId);
                    return;

                case WebviewMessageKinds.SpellAdd:
                    _host.Spelling.Add(cwd, message.GetString("word"));
                    return;

                // --- Dictation ---

                case WebviewMessageKinds.VoiceStart:
                    await _host.Dictation.StartAsync(tabId, cwd, message.GetString("language"));
                    return;

                case WebviewMessageKinds.VoiceStop:
                    _host.Dictation.Stop();
                    return;

                case WebviewMessageKinds.VoiceCorrect:
                    await _host.Dictation.CorrectAsync(tabId, message.GetString("text"));
                    return;

                case WebviewMessageKinds.VoiceDictGet:
                    _host.Dictation.Send(tabId, cwd);
                    return;

                case WebviewMessageKinds.VoiceDictSave:
                    _host.Dictation.Save(tabId, cwd, message.As<VoiceDictSavePayload>()?.Data);
                    return;

                // --- Plugins, MCP and skills ---

                case WebviewMessageKinds.PluginsRefresh:
                    await _host.Extensions.SendPluginsAsync(tabId, message.GetBool("force"));
                    return;

                case WebviewMessageKinds.PluginAction:
                {
                    var payload = message.As<PluginActionPayload>();
                    await _host.Extensions.RunPluginActionAsync(tabId, payload.Action, payload.Arg, payload.Scope);
                    return;
                }

                case WebviewMessageKinds.McpRefresh:
                    await _host.Extensions.SendMcpAsync(tabId, session);
                    return;

                case WebviewMessageKinds.SkillsRefresh:
                    await _host.Extensions.SendSkillsAsync(tabId, session);
                    return;

                case WebviewMessageKinds.SkillOverrideSet:
                    // Only the sessions of THIS folder: `.claude/skills/` belongs to the
                    // project, so the override must not follow the user into another one.
                    _host.Extensions.SetOverride(cwd, message.GetString("name"), message.GetString("value"),
                                                 _host.SessionsOn(cwd));
                    MarkPendingRestart();
                    return;

                // --- Export and images ---

                case WebviewMessageKinds.ExportMd:
                {
                    var payload = message.As<ExportMdPayload>();
                    await _host.Exporter.ExportAsync(cwd, payload.Markdown, payload.FileName, payload.Mode,
                                                     session.Model(), session.Effort());
                    return;
                }

                case WebviewMessageKinds.SaveImage:
                    _host.Exporter.SaveImage(cwd, message.GetString("mediaType"), message.GetString("data"));
                    return;

                case WebviewMessageKinds.OpenDiff:
                    _host.Diff.Open(cwd, message.GetString("tool"), message.GetElement("input"));
                    return;

                // --- Credentials vault ---

                case WebviewMessageKinds.CredsLoad:
                case WebviewMessageKinds.CredsEnrollBegin:
                case WebviewMessageKinds.CredsEnrollConfirm:
                case WebviewMessageKinds.CredsAdd:
                case WebviewMessageKinds.CredsEdit:
                case WebviewMessageKinds.CredsUse:
                case WebviewMessageKinds.CredsDelete:
                    _host.Vault.Handle(tabId, message);
                    return;

                default:
                    // Not wired yet. Logged rather than silently dropped, so a missing surface
                    // shows up in the output pane instead of looking like a dead button.
                    Log.Debug("host: '" + message.Kind + "' is not handled yet");
                    return;
            }
        }

        // ---- Handlers ----

        private async Task OnInitAsync(string tabId)
        {
            _host.Post(HostMessages.Ready());
            _host.SendConfig();
            _host.PostTabs();
            _host.SendSessions(tabId);
            _host.PostTaskTimings(tabId);

            // A freshly mounted panel always replays, even mid-turn: otherwise reopening a
            // running conversation would show only what arrives afterwards.
            _host.ReplayTab(tabId, force: true);

            var draft = _host.Tabs.Draft(tabId);
            if (!string.IsNullOrEmpty(draft)) _host.Post(HostMessages.DraftRestore(draft), tabId);

            _host.StartCacheKeeper();
            _host.StartUsage();
            AutoResumeLast(tabId);

            await ReportCliAsync();
            await RefreshModelsAsync();
        }

        private async Task ReportCliAsync()
        {
            var status = await _host.CliStatus.BuildStatusAsync();
            var auth = await _host.CliStatus.BuildAuthAsync();

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _host.Post(status);
            _host.Post(auth);
        }

        private async Task RefreshModelsAsync()
        {
            var discovered = await _host.Models.RefreshAsync(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Discovery can answer after a session started, so the limit is recomputed —
            // otherwise a natively-1M model would stay pinned at 200K for the whole session.
            if (discovered)
            {
                foreach (var session in _host.Tabs.Sessions)
                {
                    if (session.Stats.RefreshContextLimit()) _host.Post(HostMessages.Stats(session.Snapshot()));
                }
            }

            _host.SendConfig();
        }

        private void OnSendMessage(string tabId, CockpitSession session, SendMessagePayload payload)
        {
            var text = payload?.Text ?? string.Empty;

            // The minimum-effort gate is resolved NOW from the CLAUDE.md that applies to this
            // tab's folder. It is not configuration — different folders can demand different
            // things — and below the floor without confirmation, nothing is sent.
            var cwd = _host.Tabs.Cwd(tabId);
            var minimum = RepoDirectives.ResolveMinEffort(cwd, cwd);
            var effort = _host.TimingScope(session).Effort;

            if (payload?.Force != true && RepoDirectives.IsBelow(effort, minimum))
            {
                _host.Post(HostMessages.EffortGate(effort, minimum), tabId);
                return;
            }

            // The first prompt names the tab, so a row of untitled conversations does not all
            // look alike.
            if (string.IsNullOrEmpty(_host.Tabs.Title(tabId)) && !string.IsNullOrWhiteSpace(text))
            {
                var title = Whitespace.Replace(text, " ").Trim();
                _host.Tabs.SetTitle(tabId, title.Length > 28 ? title.Substring(0, 28) : title);
            }

            if (_host.PendingRestart)
            {
                // This send's respawn applies the pending change, so the warning goes away.
                _host.PendingRestart = false;
                _host.SendConfig();
            }

            // The shared editor selection rides in front of the prompt as context.
            var body = string.IsNullOrEmpty(payload?.Selection) ? text : payload.Selection + "\n" + text;

            session.Send(body, payload?.Images);
            _host.Tabs.ClearDraft(tabId);
        }

        /// <summary>
        /// Rewinds the conversation to the given user prompt, dropping it and everything after.
        ///
        /// The index counts the user's prompts as the timeline shows them, and it is resolved
        /// against the transcript rather than trusted: the webview's list can be a repaint
        /// behind, and cutting at the wrong prompt would destroy work irreversibly.
        /// </summary>
        private void Rewind(string tabId, CockpitSession session, string cwd, int index)
        {
            // Never mid-turn: the CLI is still appending to the very file we would cut.
            if (session.Busy) return;

            var sessionId = session.SessionId ?? session.ResumeId;
            if (sessionId == null) return;

            var prompts = _host.Library.Transcript(cwd, sessionId)
                .Where(item => item.Kind == "user")
                .ToList();

            if (index < 0 || index >= prompts.Count) return;

            var target = prompts[index];

            if (!_host.Library.Rewind(cwd, sessionId, target.Id))
            {
                Log.Debug("rewind: prompt #" + index + " was not found in the transcript");
                return;
            }

            // Re-armed against the truncated file: the next message continues from that point.
            session.Resume(sessionId);
            _host.ReplayTab(tabId, force: true);
            _host.PostTabs();
            _host.SendSessions(tabId);

            Log.Info("Conversation rewound to prompt #" + index + ".");
        }

        private void MarkPendingRestart()
        {
            _host.PendingRestart = true;
            _host.SendConfig();
        }

        private void RemoveModel(CockpitSession session, string model)
        {
            if (string.IsNullOrEmpty(model)) return;

            _host.Models.Remove(model);

            // A session pinned to the removed model falls back to the CLI's own default,
            // rather than keeping a pin the picker no longer offers.
            var pinned = session.ModelOverride;
            if (!string.IsNullOrEmpty(pinned) &&
                string.Equals(Regex.Replace(pinned, @"\[1m\]", string.Empty, RegexOptions.IgnoreCase),
                              Regex.Replace(model, @"\[1m\]", string.Empty, RegexOptions.IgnoreCase),
                              StringComparison.OrdinalIgnoreCase))
            {
                session.SetModel(ModelCatalog.DefaultModelId);
            }

            _host.SendConfig();
        }

        /// <summary>
        /// Opens a saved conversation.
        ///
        /// In its own window, and never over the one the request came from: the conversation the
        /// user was looking at is not the one they asked to open, and replacing it would lose a
        /// running turn. A conversation already open is focused instead of duplicated — two
        /// processes on one transcript would double the context on disk.
        /// </summary>
        private void OpenSession(string cwd, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;

            var open = _host.TabOf(sessionId);
            if (open != null)
            {
                _host.ShowConversation(open);
                return;
            }

            var tabId = _host.Tabs.CreateTab(cwd);
            _host.Tabs.SessionFor(tabId).Resume(sessionId);
            _host.Tabs.SetTitle(tabId, _host.Library.TitleOf(cwd, sessionId));
            _host.ShowConversation(tabId);
        }

        /// <summary>
        /// Moves a tab to another folder, asking for one when the webview did not name it.
        ///
        /// The conversation does not survive the move, so the new folder's history is sent
        /// right after: the tab lands somewhere the user can pick up from, not empty.
        /// </summary>
        internal void SetTabCwd(string tabId, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var target = string.IsNullOrWhiteSpace(path)
                ? _host.Editor.PickFolder(_host.Tabs.Cwd(tabId), "Folder for this conversation")
                : path;

            // Cancelled, or already there.
            if (target == null || !_host.Tabs.SetCwd(tabId, target)) return;

            _host.Post(HostMessages.History(new List<HistoryItem>()), tabId);
            _host.SendSessions(tabId);
            _host.SendConfig();
        }

        /// <summary>
        /// Resumes the folder's most recent conversation, when the setting asks for it and the
        /// tab has none.
        /// </summary>
        private void AutoResumeLast(string tabId)
        {
            if (!_host.Settings.AutoResumeLastSession) return;

            // Once per IDE session, on the first conversation window. Every window after that
            // was opened deliberately — "New session" resuming the previous conversation would
            // be the opposite of what was asked.
            if (!_host.ClaimAutoResume()) return;

            var session = _host.Tabs.SessionFor(tabId);
            if (session.SessionId != null || session.ResumeId != null) return;

            var cwd = _host.Tabs.Cwd(tabId);

            var latest = _host.Library.LatestSessionId(cwd);
            if (latest == null) return;

            session.Resume(latest);
            _host.Tabs.SetTitle(tabId, _host.Library.TitleOf(cwd, latest));
            _host.ReplayTab(tabId, force: true);
        }

        private string JoinResolved(IEnumerable<string> paths, string cwd)
        {
            if (paths == null) return string.Empty;
            return string.Join(" ", System.Linq.Enumerable.Select(paths, p => _host.Editor.QuoteResolved(p, cwd)));
        }

        /// <summary>
        /// The @-mention autocomplete list: live sessions first (by name), then workspace files.
        /// Mirrors the base extension's searchMentions. Sessions come first because they are few
        /// and more specific than the file sweep, and are what the CLI 2.1.232 resolves as
        /// `@name` -> SendMessage. The current conversation is excluded — mentioning itself is
        /// meaningless. Runs off the UI thread (registry + file walk are both background work).
        /// </summary>
        private async Task<IReadOnlyList<MentionItem>> BuildMentionsAsync(string query, string cwd, CockpitSession self)
        {
            var items = new List<MentionItem>();

            // Live named sessions matching the query, minus this conversation's own ids.
            try
            {
                var selfIds = new HashSet<string>(StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(self?.SessionId)) selfIds.Add(self.SessionId);
                if (!string.IsNullOrEmpty(self?.ResumeId)) selfIds.Add(self.ResumeId);

                var q = query?.Trim() ?? string.Empty;
                var sessions = await _registry.LiveSessionsAsync().ConfigureAwait(false);
                items.AddRange(sessions
                    .Where(s => !string.IsNullOrEmpty(s.Name) && !selfIds.Contains(s.SessionId))
                    .Where(s => q.Length == 0 || s.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(s => s.Name.Length)
                    .Select(s => new MentionItem { Label = s.Name, Kind = "session" }));
            }
            catch (Exception ex)
            {
                Log.Debug("mention: session lookup failed: " + ex.Message);
            }

            // Workspace files (fuzzy by name), reusing the existing search.
            try
            {
                var files = await _host.Editor.SearchFilesAsync(query, cwd).ConfigureAwait(false);
                items.AddRange(files.Select(f => new MentionItem { Label = f, Kind = "file" }));
            }
            catch (Exception ex)
            {
                Log.Debug("mention: file search failed: " + ex.Message);
            }

            // The webview shows 12; cap here so the session rows can never be pushed off by files.
            return items.Count > 12 ? items.GetRange(0, 12) : items;
        }
    }
}
