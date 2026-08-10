using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Session;
using Tootega.Cockpit.Util;
using CockpitSession = Tootega.Cockpit.Session.Session;

namespace Tootega.Cockpit.Host
{
    /// <summary>
    /// Hands a conversation over to an interactive CLI session so it can be driven from a
    /// phone, and follows it while it runs.
    ///
    /// It does NOT send `/remote-control`. That command only exists in an interactive session;
    /// ours is headless (`-p --input-format stream-json`, which is what gives us the stream)
    /// and there the CLI answers that the command is unavailable — it is not even in that
    /// session's slash_commands. Measured on CLI 2.1.226.
    ///
    /// So the conversation is handed to `claude --remote-control --resume &lt;id&gt;` in a visible
    /// console, which continues THIS conversation and prints the pairing link. Our own process
    /// is stopped first: two processes owning one transcript would duplicate the context on
    /// disk. The tab keeps showing the conversation, repainted from the file the remote session
    /// is writing to.
    /// </summary>
    internal sealed class RemoteControlBroker : IDisposable
    {
        /// <summary>
        /// How long the interactive session gets to register itself before we call it a
        /// failure. Sign-in and a cold start both live inside this window.
        /// </summary>
        private const int ConnectWindowMs = 45_000;

        private const int PollIntervalMs = 3000;

        private sealed class Handover
        {
            public string SessionId;
            public string Phase;
            public long StartedAt;
            public Timer Poll;
            public string[] Ids;
        }

        private readonly Dictionary<string, Handover> _active =
            new Dictionary<string, Handover>(StringComparer.Ordinal);

        private readonly SessionRegistry _registry = new SessionRegistry();

        private readonly EditorBridge _editor;
        private readonly Func<string> _claudePath;
        private readonly Action<string> _replay;
        private readonly Action<HostMessage, string> _post;

        public RemoteControlBroker(EditorBridge editor, Func<string> claudePath,
                                   Action<string> replay, Action<HostMessage, string> post)
        {
            _editor = editor ?? throw new ArgumentNullException(nameof(editor));
            _claudePath = claudePath ?? throw new ArgumentNullException(nameof(claudePath));
            _replay = replay ?? throw new ArgumentNullException(nameof(replay));
            _post = post ?? throw new ArgumentNullException(nameof(post));
        }

        public bool IsActive(string tabId) => tabId != null && _active.ContainsKey(tabId);

        /// <summary>
        /// Toggles remote control for a tab.
        ///
        /// A toggle rather than two commands, matching the official extension: clicking again
        /// takes the conversation back. Nothing is lost either way — it lives on disk, and the
        /// next message resumes it wherever it is.
        /// </summary>
        public void Toggle(string tabId, string cwd, CockpitSession session, string title)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_active.ContainsKey(tabId))
            {
                End(tabId);
                return;
            }

            var sessionId = session.SessionId ?? session.ResumeId;
            if (sessionId == null)
            {
                _post(HostMessages.RemoteState(false, "failed",
                    "There is no conversation to hand over yet. Send a message first."), tabId);
                return;
            }

            // The handover is exclusive: our process releases the transcript before the
            // interactive one takes it.
            session.Stop();

            _editor.RunVisible(
                Quote(_claudePath()) + " --remote-control --resume " + sessionId,
                "Claude Remote Control · " + (string.IsNullOrEmpty(title) ? sessionId.Substring(0, 8) : title));

            var handover = new Handover
            {
                SessionId = sessionId,
                Phase = "connecting",
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Ids = new[] { sessionId, session.SessionId, session.ResumeId },
            };

            _active[tabId] = handover;

            _post(HostMessages.RemoteState(true, "connecting"), tabId);
            _post(HostMessages.EngineNotice("remote:" + sessionId,
                "Remote Control: this conversation now runs in the console. The pairing link is there, and the " +
                "timeline keeps following it. Type in the console, on your phone or at claude.ai/code — the " +
                "Cockpit takes over again when you close the console.",
                "remote_control"), tabId);

            handover.Poll = new Timer(_ => Tick(tabId), null, PollIntervalMs, PollIntervalMs);
        }

        private void Tick(string tabId)
        {
            _ = TickAsync(tabId);
        }

        /// <summary>
        /// Repaints the tab from the transcript the remote session is writing, and confirms —
        /// or denies — that the handover actually happened.
        ///
        /// Confirmation matters because everything else here is optimistic: the console may
        /// have failed to sign in, or the session may have died an hour in, and a timeline that
        /// simply stops updating looks exactly like a quiet conversation.
        /// </summary>
        private async Task TickAsync(string tabId)
        {
            if (!_active.TryGetValue(tabId, out var handover)) return;

            var live = await _registry.IsSessionLiveAsync(handover.Ids);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Toggled off while we were reading.
            if (!_active.TryGetValue(tabId, out handover)) return;

            _replay(tabId);

            if (live)
            {
                if (handover.Phase != "active")
                {
                    handover.Phase = "active";
                    _post(HostMessages.RemoteState(true, "active"), tabId);
                }

                return;
            }

            // Still starting up, and never seen alive: keep waiting.
            var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - handover.StartedAt;
            if (handover.Phase == "connecting" && elapsed < ConnectWindowMs) return;

            Fail(tabId, handover.Phase == "active"
                ? "Remote Control dropped: the interactive session is no longer running. The conversation is " +
                  "intact on disk — check the console for the reason, then click the button to reconnect."
                : "Remote Control did not connect: the interactive session never started. The console is still " +
                  "open with the reason (sign-in, network or a CLI error) — fix it and click the button again.");
        }

        /// <summary>
        /// Failure: stop following, leave the console open — it holds the reason — and leave the
        /// tab ready for another attempt.
        /// </summary>
        private void Fail(string tabId, string detail)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Forget(tabId);

            _post(HostMessages.RemoteState(false, "failed", detail), tabId);
            _post(HostMessages.EngineNotice("remote-fail:" + DateTime.UtcNow.Ticks, detail, "remote_control"), tabId);
            _replay(tabId);
        }

        /// <summary>Takes the conversation back. Idempotent.</summary>
        public void End(string tabId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!_active.ContainsKey(tabId)) return;

            Forget(tabId);

            _post(HostMessages.RemoteState(false), tabId);
            _post(HostMessages.EngineNotice("remote-off:" + DateTime.UtcNow.Ticks,
                "Remote Control off: the conversation is back in the Cockpit. The next message resumes it here.",
                "remote_control"), tabId);
            _replay(tabId);
        }

        private void Forget(string tabId)
        {
            if (!_active.TryGetValue(tabId, out var handover)) return;

            handover.Poll?.Dispose();
            _active.Remove(tabId);
        }

        private static string Quote(string path)
        {
            return path != null && path.IndexOf(' ') >= 0 ? "\"" + path + "\"" : path;
        }

        public void Dispose()
        {
            foreach (var handover in _active.Values) handover.Poll?.Dispose();
            _active.Clear();
        }
    }
}
