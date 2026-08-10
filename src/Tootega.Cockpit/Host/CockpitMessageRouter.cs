using System;
using System.Collections.Generic;
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

        public CockpitMessageRouter(CockpitHostService host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public async Task RouteAsync(WebviewMessage message)
        {
            // Asserting would be wrong in a Task-returning method: it is cheaper and safer to
            // switch than to demand the caller got it right.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var tabId = _host.Tabs.EnsureActiveTab();
            var session = _host.Tabs.SessionFor(tabId);

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
                    OpenSession(tabId, message.GetString("sessionId"));
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
                {
                    // A new tab inherits the folder of the one it was opened from, which is
                    // almost always the folder the user is still thinking about.
                    var created = _host.Tabs.CreateTab(message.GetString("cwd") ?? cwd);
                    _host.SendConfig();
                    _host.Post(HostMessages.History(new List<HistoryItem>()), created);
                    _host.SendSessions(created);
                    return;
                }

                case WebviewMessageKinds.SetTabCwd:
                    SetTabCwd(message.GetString("tabId") ?? tabId, message.GetString("path"));
                    return;

                case WebviewMessageKinds.CloseTab:
                {
                    var target = message.GetString("tabId");
                    if (!_host.Tabs.Close(target, out var emptied)) return;

                    if (emptied) _host.Post(HostMessages.History(new List<HistoryItem>()), target);
                    else if (_host.Tabs.ActiveTab != null) _host.ReplayTab(_host.Tabs.ActiveTab, force: true);
                    return;
                }

                case WebviewMessageKinds.SwitchTab:
                {
                    var target = message.GetString("tabId");
                    if (!_host.Tabs.SetActive(target)) return;

                    _host.SendConfig();
                    // The history panel follows the tab, since it lists that folder's
                    // conversations and the folder may have just changed.
                    _host.SendSessions(target);
                    _host.ReplayTab(target, force: true);
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

                case WebviewMessageKinds.EnableUsageTracking:
                    _host.EnableUsageTracking();
                    return;

                // --- Editor integration ---

                case WebviewMessageKinds.OpenSettings:
                    _host.Package.ShowOptionPage(typeof(CockpitOptions));
                    return;

                case WebviewMessageKinds.OpenLink:
                    _host.Editor.OpenExternal(message.GetString("href"));
                    return;

                case WebviewMessageKinds.OpenEditor:
                    _host.ShowChatWindow();
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
                    var matches = await _host.Editor.SearchFilesAsync(message.GetString("query"), cwd);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _host.Post(HostMessages.MentionResults(requestId, matches), tabId);
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

        private void OpenSession(string tabId, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;

            _host.Tabs.SessionFor(tabId).Resume(sessionId);
            _host.Tabs.SetTitle(tabId, _host.Library.TitleOf(_host.Tabs.Cwd(tabId), sessionId));
            _host.ReplayTab(tabId, force: true);
        }

        /// <summary>
        /// Moves a tab to another folder, asking for one when the webview did not name it.
        ///
        /// The conversation does not survive the move, so the new folder's history is sent
        /// right after: the tab lands somewhere the user can pick up from, not empty.
        /// </summary>
        private void SetTabCwd(string tabId, string path)
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
    }
}
