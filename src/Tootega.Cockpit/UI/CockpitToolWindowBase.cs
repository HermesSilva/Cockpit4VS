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
        /// </summary>
        protected void EnsureView()
        {
            if (_view != null) return;

            var host = CockpitPackage.Instance?.Host;

            if (BindsToTab)
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

        protected override void OnCreate()
        {
            base.OnCreate();
            EnsureView();
        }

        /// <summary>
        /// Closing the window closes the conversation, as closing the panel did in VS Code.
        ///
        /// Nothing is lost: the transcript is on disk and the hub still lists it. What goes away
        /// is the live process, which is the point — an invisible conversation holding a CLI
        /// open is exactly what the user meant to stop.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var host = CockpitPackage.Instance?.Host;

                if (_view != null) host?.UnregisterSurface(_view);
                _view?.Dispose();
                _view = null;

                if (TabId != null)
                {
                    host?.CloseTabFromWindow(TabId);
                    TabId = null;
                }
            }

            base.Dispose(disposing);
        }
    }
}
