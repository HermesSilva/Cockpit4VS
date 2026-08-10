using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.UI
{
    /// <summary>
    /// Hosts the React webview inside a tool window and carries the host&lt;-&gt;webview
    /// protocol across the WebView2 boundary.
    ///
    /// The React side is unmodified VS Code code: it calls `acquireVsCodeApi()` and listens
    /// for `window.message`. Rather than patching thousands of lines, this control installs
    /// a shim that implements that exact API on top of `chrome.webview`. The webview cannot
    /// tell the difference, which is what keeps the two editors on one codebase.
    /// </summary>
    internal sealed class CockpitWebView : UserControl, IDisposable
    {
        /// <summary>
        /// Virtual origin the bundle is served from. A real https origin (rather than
        /// file://) is what gives the page a normal security context, so localStorage,
        /// modules and fetch behave as they do in VS Code.
        /// </summary>
        private const string VirtualHost = "cockpit.invalid";

        private readonly WebView2 _web;
        private readonly string _mode;
        private bool _ready;
        private bool _disposed;

        /// <summary>Raised with the raw JSON a webview -&gt; host message carried.</summary>
        public event EventHandler<string> MessageReceived;

        /// <summary>Raised once the bundle is loaded and the shim is live.</summary>
        public event EventHandler Ready;

        /// <param name="mode">"chat" or "hub" — the bundle renders one or the other.</param>
        /// <param name="tabId">
        /// The conversation this surface shows. One window per conversation, so it is fixed for
        /// the window's life; the hub has none, because it always reflects the active tab.
        /// </param>
        public CockpitWebView(string mode, string tabId = null)
        {
            _mode = mode ?? "chat";
            TabId = tabId;
            _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
            Content = _web;
            VSColorTheme.ThemeChanged += OnThemeChanged;
        }

        /// <summary>The tab this surface belongs to, or null for the hub.</summary>
        public string TabId { get; }

        /// <summary>Whether a message tagged with <paramref name="tabId"/> is for this surface.</summary>
        public bool Accepts(string tabId)
        {
            // An untagged message is global — config, CLI status, the tab list — and everyone
            // needs it. The hub takes everything, since it renders whichever tab is active.
            if (tabId == null || TabId == null) return true;

            return string.Equals(TabId, tabId, StringComparison.Ordinal);
        }

        public async Task InitializeAsync()
        {
            try
            {
                // Keep the profile beside the extension's own data, not in the VS profile:
                // a corrupt WebView2 cache should never be something the user has to hunt for.
                var userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Tootega", "Cockpit", "WebView2");
                Directory.CreateDirectory(userData);

                var env = await CoreWebView2Environment.CreateAsync(null, userData).ConfigureAwait(true);
                await _web.EnsureCoreWebView2Async(env).ConfigureAwait(true);

                var core = _web.CoreWebView2;
                var settings = core.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsSwipeNavigationEnabled = false;
                settings.AreDevToolsEnabled = Log.DebugEnabled;

                core.WebMessageReceived += OnWebMessageReceived;
                core.NewWindowRequested += OnNewWindowRequested;
                core.NavigationCompleted += OnNavigationCompleted;

                var assets = WebViewAssetPath();
                if (!Directory.Exists(assets))
                {
                    Log.Error("WebView assets missing at " + assets + " — build the webview bundle first.");
                    return;
                }

                core.SetVirtualHostNameToFolderMapping(
                    VirtualHost, assets, CoreWebView2HostResourceAccessKind.Allow);

                await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildShim()).ConfigureAwait(true);

                core.Navigate("https://" + VirtualHost + "/index.html");
            }
            catch (Exception ex)
            {
                Log.Error("WebView2 initialization failed", ex);
            }
        }

        /// <summary>
        /// host -&gt; webview. <paramref name="json"/> is a serialized HostToWebview message.
        ///
        /// Callable from any thread, which is the point: the CLI reader threads produce most
        /// of these, and WebView2 is a WPF control that only its own thread may touch. The
        /// switch is per-call rather than a queue because the dispatcher already delivers in
        /// the order it was posted, so a session's stream stays in order.
        /// </summary>
        // The switch is unconditional rather than guarded by a thread test: when the caller
        // is already on the main thread the await completes inline, so a caller that was in
        // order stays in order, and the method has no thread contract of its own to break.
        // Neither awaited nor joined, hence the VSSDK007 suppression.
#pragma warning disable VSSDK007
        public void PostMessage(string json)
        {
            if (_disposed || string.IsNullOrEmpty(json)) return;

            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Send(json);
            }).FileAndForget("tootega/cockpit/postMessage");
        }
#pragma warning restore VSSDK007

        /// <summary>
        /// The actual post. Only ever reached after the switch above, so it does not assert
        /// the thread: an assertion here would make every caller of the thread-agnostic
        /// PostMessage inherit a main-thread contract it deliberately does not have.
        /// </summary>
        private void Send(string json)
        {
            if (_disposed) return;

            try
            {
                if (_web.CoreWebView2 == null) return;
                _web.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                Log.Error("PostMessage failed", ex);
            }
        }

        /// <summary>Re-publishes the VS theme as CSS variables. Cheap enough to run on every change.</summary>
        public async Task ApplyThemeAsync()
        {
            if (_disposed || _web.CoreWebView2 == null) return;
            try
            {
                var css = VsThemeBridge.BuildCss();
                var dark = VsThemeBridge.IsDarkTheme() ? "true" : "false";
                var script =
                    "(function(){var s=document.getElementById('vs-theme');" +
                    "if(!s){s=document.createElement('style');s.id='vs-theme';document.head.appendChild(s);}" +
                    "s.textContent=" + JsString(css) + ";" +
                    "document.body&&document.body.classList.toggle('vscode-dark'," + dark + ");" +
                    "document.body&&document.body.classList.toggle('vscode-light',!" + dark + ");" +
                    "})();";
                await _web.CoreWebView2.ExecuteScriptAsync(script).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("ApplyTheme failed", ex);
            }
        }

        /// <summary>Reloads the bundle — the manual recovery for a gray/dead renderer.</summary>
        public void Reload()
        {
            try
            {
                _web.CoreWebView2?.Reload();
            }
            catch (Exception ex)
            {
                Log.Error("Reload failed", ex);
            }
        }

        public void FocusWebView()
        {
            try
            {
                _web.Focus();
            }
            catch
            {
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                MessageReceived?.Invoke(this, e.WebMessageAsJson);
            }
            catch (Exception ex)
            {
                Log.Error("Dispatching webview message failed", ex);
            }
        }

        /// <summary>A link in the conversation opens in the user's browser, never in the panel.</summary>
        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("Opening external link failed", ex);
            }
        }

        // Both of these are event handlers, so they cannot be async: an unobserved
        // exception in an `async void` would take the IDE process down. They hand the
        // async part to the JTF and let FileAndForget report failures instead — which is
        // deliberately neither awaiting nor joining, hence the VSSDK007 suppression.
#pragma warning disable VSSDK007
        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                Log.Error("Webview navigation failed: " + e.WebErrorStatus);
                return;
            }

            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await ApplyThemeAsync();
                _ready = true;
                Ready?.Invoke(this, EventArgs.Empty);
            }).FileAndForget("tootega/cockpit/navigationCompleted");
        }

        private void OnThemeChanged(ThemeChangedEventArgs e)
        {
            if (!_ready) return;

            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await ApplyThemeAsync();
            }).FileAndForget("tootega/cockpit/themeChanged");
        }
#pragma warning restore VSSDK007

        /// <summary>
        /// The compatibility shim. It provides `acquireVsCodeApi()` with the same three
        /// members the webview uses (postMessage / getState / setState) and forwards
        /// host messages onto `window.message`, which is where the React code listens.
        /// State is persisted in localStorage so a draft survives a renderer reload — the
        /// same guarantee VS Code's own setState gives.
        /// </summary>
        private string BuildShim()
        {
            // Per tab, so two conversations side by side do not share one draft.
            var stateKey = "tootega.cockpit.state." + _mode + "." + (TabId ?? "default");
            return
                "(function(){" +
                "  var vs = window.chrome && window.chrome.webview;" +
                "  if (!vs) return;" +
                "  var KEY = " + JsString(stateKey) + ";" +
                "  var state;" +
                "  try { state = JSON.parse(localStorage.getItem(KEY) || '{}'); } catch (e) { state = {}; }" +
                "  var api = {" +
                "    postMessage: function (msg) { vs.postMessage(msg); }," +
                "    getState: function () { return state; }," +
                "    setState: function (next) {" +
                "      state = next;" +
                "      try { localStorage.setItem(KEY, JSON.stringify(next)); } catch (e) {}" +
                "      return next;" +
                "    }" +
                "  };" +
                "  window.acquireVsCodeApi = function () { return api; };" +
                // Host messages arrive on chrome.webview; the webview listens on window.
                "  vs.addEventListener('message', function (e) {" +
                "    window.postMessage(e.data, '*');" +
                "  });" +
                // What the bundle actually reads at start-up: which view to render, and which
                // conversation this window is. They are set on the document-created script, so
                // they exist before main.js runs.
                "  window.__TOOTEGA_VIEW__ = " + JsString(_mode) + ";" +
                "  window.__TOOTEGA_SESSION__ = " + JsString(TabId ?? string.Empty) + ";" +
                "})();";
        }

        /// <summary>Where the esbuild bundle lands inside the deployed VSIX.</summary>
        private static string WebViewAssetPath()
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(dir ?? string.Empty, "WebView");
        }

        private static string JsString(string value)
        {
            return System.Text.Json.JsonSerializer.Serialize(value ?? string.Empty);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                VSColorTheme.ThemeChanged -= OnThemeChanged;
                if (_web.CoreWebView2 != null)
                {
                    _web.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    _web.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
                    _web.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                }
                _web.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("Disposing the webview failed", ex);
            }
        }
    }
}
