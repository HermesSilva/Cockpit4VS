using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.UI
{
    /// <summary>
    /// Shared plumbing for the Cockpit tool windows. Both host the same bundle and differ in
    /// the mode they render, so everything except that lives here.
    ///
    /// A chat window IS a conversation: it binds to one tab for its whole life, exactly as the
    /// VS Code extension gave each conversation its own webview panel. That is what lets two
    /// conversations on different folders sit side by side without either one repainting when
    /// the other streams.
    /// </summary>
    internal abstract class CockpitToolWindowBase : ToolWindowPane
    {
        private CockpitWebView _view;
        private bool _tabAcquired;
        private bool _closingConversation;

        protected CockpitToolWindowBase(string caption, string mode)
        {
            Caption = caption;
            Mode = mode;
            BitmapImageMoniker = CockpitMonikers.Cockpit;
        }

        protected string Mode { get; }

        public CockpitWebView View => _view;

        /// <summary>The conversation this window shows. Null for the hub.</summary>
        public string TabId { get; private set; }

        /// <summary>
        /// Whether this window carries a conversation of its own. The hub does not.
        /// </summary>
        protected virtual bool BindsToTab => false;

        /// <summary>
        /// Creates the WebView2 lazily: a tool window can be restored on VS start while
        /// hidden, and paying for a browser instance nobody is looking at is not free.
        ///
        /// Rebuildable on purpose. Closing a tool window frame disposes the WebView (see
        /// <see cref="Dispose"/>), but the shell keeps the pane instance and reuses it when the
        /// window is re-opened, without calling <see cref="OnCreate"/> a second time. A window
        /// that only built its view in OnCreate would then come back blank — the symptom of
        /// closing and re-opening the hub. So this both creates the view and recreates it, and
        /// <see cref="EnsureAlive"/> calls it on every show.
        ///
        /// The tab is acquired once and kept: a rebuilt conversation window keeps the same
        /// conversation rather than taking a fresh one each time it is re-shown.
        /// </summary>
        protected void EnsureView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // A view that is still alive needs nothing. A view that exists but whose browser was
            // disposed — the shell does that when the tool window is closed with its X, without
            // disposing this pane — has its browser rebuilt in place. The CockpitWebView object and
            // the pane's Content stay the same: re-assigning a ToolWindowPane's Content after it is
            // sited does not re-host it in the frame, which is why the whole panel vanished when
            // the view was swapped wholesale. Only the dead WebView2 inside it is replaced.
            if (_view != null)
            {
                // A browser can read as dead for a moment while Visual Studio re-parents it — that
                // is what detaching the panel does — and it comes back on its own. Only a browser
                // that is still dead a tick later, which is the close-with-the-X case, is rebuilt.
                // Deciding immediately is what turned a detach into a rebuild loop that seized the
                // foreground.
                _view.ReviveIfStillDead();
                return;
            }

            var host = CockpitPackage.Instance?.Host;

            if (BindsToTab && !_tabAcquired)
            {
                // The tab the package asked for, when it is opening a specific conversation.
                // Otherwise this window is being restored by the shell on start-up and there is
                // nobody to ask, so it takes a fresh conversation rather than none.
                TabId = CockpitPackage.TakePendingTab() ?? host?.Tabs.CreateTab();

                if (TabId == null)
                {
                    Log.Error("The host is not available yet; the conversation window cannot open.");
                    return;
                }

                _tabAcquired = true;
            }

            _view = new CockpitWebView(Mode, TabId);
            Content = _view;

            UpdateCaption();

            // Registered before the browser is ready: the first message the webview sends is
            // `init`, and a surface that attached late would miss it and never paint.
            host?.RegisterSurface(_view);

            // VSSDK007 wants the JoinableTask awaited or joined; FileAndForget is the
            // vs-threading-sanctioned way to do neither on purpose. Nothing can await this
            // — OnCreate is void and the window must paint before the browser is ready.
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    await _view.InitializeAsync();
                }
                catch (Exception ex)
                {
                    Log.Error("Initializing " + Mode + " view failed", ex);
                }
            }).FileAndForget("tootega/cockpit/initializeView");
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Rebuilds the view if a previous frame close disposed it.
        ///
        /// The shell reuses a hidden pane on re-open without a second <see cref="OnCreate"/>, so
        /// showing a window whose view was torn down would show nothing. Every show path runs
        /// this so the browser is present whenever the window is.
        /// </summary>
        public void EnsureAlive()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsureView();
        }

        /// <summary>
        /// Names the window after its conversation, and after the folder when there is one.
        ///
        /// With several conversations open, identical captions would make the tabs
        /// indistinguishable — which is the whole reason each has a window of its own.
        /// </summary>
        public void UpdateCaption()
        {
            if (TabId == null) return;

            var tabs = CockpitPackage.Instance?.Host?.Tabs;
            if (tabs == null || !tabs.Has(TabId)) return;

            var title = tabs.Title(TabId);
            var folder = FolderName(tabs.Cwd(TabId));

            Caption = string.IsNullOrEmpty(title)
                ? "Cockpit — " + folder
                : "Cockpit: " + title + " — " + folder;
        }

        private static string FolderName(string cwd)
        {
            try
            {
                return System.IO.Path.GetFileName(cwd?.TrimEnd(System.IO.Path.DirectorySeparatorChar)) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Reload()
        {
            _view?.Reload();
        }

        /// <summary>
        /// Marks that the frame is about to close BECAUSE the user is ending this conversation
        /// (the hub's close button / the <c>closeTab</c> message), not merely putting the window
        /// away. Only then does <see cref="Dispose"/> stop the CLI process.
        ///
        /// Closing the window by its X, dragging it out of the layout, or shutting the IDE down
        /// leaves this false: the conversation keeps running headless and the hub keeps listing
        /// it, exactly as closing a WebviewPanel does in the VS Code base — there, panel disposal
        /// only drops the UI and remembers the tab as reopenable; it never kills the session.
        /// </summary>
        public void MarkConversationClosing() => _closingConversation = true;

        protected override void OnCreate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            base.OnCreate();
            EnsureView();
        }

        /// <summary>
        /// Tearing the window down drops only the webview, NOT the conversation.
        ///
        /// Closing a WebviewPanel in the VS Code base does not stop the agent: its
        /// <c>onDidDispose</c> just clears the UI records and remembers the tab as reopenable —
        /// the CLI process keeps running and the hub keeps listing it. The port must match that.
        /// A window closed by its X, dragged out of the layout, or torn down when the IDE shuts
        /// leaves the session alive; only an explicit "close conversation" — which sets
        /// <see cref="MarkConversationClosing"/> before closing the frame — stops the process.
        ///
        /// Nothing is lost either way: the transcript is on disk and the tab stays in the hub,
        /// so a window closed by accident is reopened from there onto the same live conversation.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var host = CockpitPackage.Instance?.Host;

                if (_view != null) host?.UnregisterSurface(_view);
                _view?.Dispose();
                _view = null;

                // The tab (and its CLI process) is only stopped on a deliberate close. Otherwise
                // it outlives the window: EnsureView reacquires it via the pending-tab handshake
                // when the shell reuses this pane, and ShowConversation reopens it from the hub.
                if (_closingConversation && TabId != null)
                {
                    host?.CloseTabFromWindow(TabId);
                    TabId = null;
                }
            }

            base.Dispose(disposing);
        }
    }
}
