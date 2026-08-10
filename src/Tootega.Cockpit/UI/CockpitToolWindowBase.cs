using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.UI
{
    /// <summary>
    /// Shared plumbing for the two Cockpit tool windows. Both host the same bundle and
    /// differ only in the mode they render, so everything except that lives here.
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

        /// <summary>
        /// Creates the WebView2 lazily: a tool window can be restored on VS start while
        /// hidden, and paying for a browser instance nobody is looking at is not free.
        /// </summary>
        protected void EnsureView()
        {
            if (_view != null) return;
            _view = new CockpitWebView(Mode);
            Content = _view;

            // Registered before the browser is ready: the first message the webview sends is
            // `init`, and a surface that attached late would miss it and never paint.
            CockpitPackage.Instance?.Host?.RegisterSurface(_view);

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

        public void Reload()
        {
            _view?.Reload();
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            EnsureView();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_view != null) CockpitPackage.Instance?.Host?.UnregisterSurface(_view);
                _view?.Dispose();
                _view = null;
            }
            base.Dispose(disposing);
        }
    }
}
