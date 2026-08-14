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

        private VsWebView2 _web;
        private readonly string _mode;
        private bool _ready;
        private bool _disposed;
        private bool _reviving;

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
            _web = new VsWebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
            Content = _web;
            VSColorTheme.ThemeChanged += OnThemeChanged;
        }

        /// <summary>
        /// Replaces a browser that Visual Studio disposed under us with a fresh one, in place.
        ///
        /// Called only when <see cref="IsAlive"/> has already reported the old browser dead — the
        /// close-with-the-X case. This is deliberately not a new <see cref="CockpitWebView"/>: the
        /// pane's Content must not change, because re-assigning a sited tool window's Content does
        /// not re-host it. So the outer control and its place in the frame stay, and only the inner
        /// <see cref="VsWebView2"/> is swapped, then re-initialized exactly as on first open.
        /// </summary>
        public void ReviveIfStillDead()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_disposed || _reviving) return;
            if (IsAlive) return;

            // Re-check on a later turn of the UI queue rather than now. Detaching the panel makes
            // the browser read as dead for an instant while the shell re-parents it, then the shell
            // brings it back; closing with the X leaves it dead for good. Waiting one turn tells the
            // two apart, so only the real loss is rebuilt and the detach is left to recover itself.
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await Task.Yield();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_disposed || _reviving || IsAlive) return;
                Log.Info("The " + _mode + " browser stayed disposed after the window reappeared; reviving it.");
                Revive();
            }).FileAndForget("tootega/cockpit/reviveCheck");
#pragma warning restore VSSDK007
        }

        private void Revive()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_disposed || _reviving) return;

            // Held until the new browser reports ready, so that IsAlive answers "alive" throughout
            // the rebuild. Without it, every Show and every focus hand-off during the rebuild would
            // see a not-yet-ready browser, call Revive again, and the repeated re-initialize is what
            // seized the foreground and would not let another app take focus.
            _reviving = true;

            try
            {
                _web?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug("Disposing the dead webview during revive failed: " + ex.Message);
            }

            _ready = false;
            _web = new VsWebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
            Content = _web;

#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    await InitializeAsync();
                }
                catch (Exception ex)
                {
                    Log.Error("Reviving the " + _mode + " webview failed", ex);
                }
                finally
                {
                    _reviving = false;
                }
            }).FileAndForget("tootega/cockpit/reviveView");
#pragma warning restore VSSDK007
        }

        /// <summary>The tab this surface belongs to, or null for the hub.</summary>
        public string TabId { get; }

        /// <summary>
        /// Whether the underlying browser is still usable.
        ///
        /// Closing a tool window with its X does not dispose this control — no
        /// <see cref="Dispose"/> runs — but Visual Studio disposes the WebView2 inside it all the
        /// same, and that disposal is one-way. The control then survives as a live C# object
        /// wrapping a dead browser, and reusing it paints nothing: the blank hub after a close and
        /// reopen. The owner asks this before reusing the view, and rebuilds when the answer is no.
        ///
        /// Read by probing the control rather than tracked with a flag, because the disposal
        /// happens inside WebView2 without raising an event this class sees. Touching a disposed
        /// WebView2 throws <see cref="ObjectDisposedException"/>; that throw is the signal.
        /// </summary>
        public bool IsAlive
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_disposed) return false;
                // A rebuild in flight counts as alive: the browser is coming up, and asking for
                // another rebuild on top of it is the loop that stole the foreground.
                if (_reviving) return true;
                return _web.IsAlive();
            }
        }

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
                await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildScrollTrace()).ConfigureAwait(true);

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

        /// <summary>Wakes the scroll tracer up. See <see cref="BuildScrollTrace"/>.</summary>
        private async Task StartTraceAsync()
        {
            if (_disposed || _web.CoreWebView2 == null) return;
            try
            {
                await _web.CoreWebView2
                    .ExecuteScriptAsync("window.__TOOTEGA_TRACE__ && window.__TOOTEGA_TRACE__();")
                    .ConfigureAwait(true);
                Log.Debug("scroll tracing is on for this view.");
            }
            catch (Exception ex)
            {
                Log.Debug("scroll tracing could not be started: " + ex.Message);
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
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                _web.Focus();

                // Focusing the WPF control is not the same as focusing the page. In
                // composition mode the browser is a visual rather than a window, so it does
                // not take focus by being clicked into: it has to be handed over. Without
                // this, keys reach WPF and the page behaves as if nothing is focused.
                _web.FocusBrowser();
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

                // Read here rather than when the script was injected, so turning debug logging
                // on and reloading the view is enough to start tracing.
                if (Log.DebugEnabled) await StartTraceAsync();

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

        /// <summary>
        /// Reports what happens to the transcript's scroller, so the cause is read rather than
        /// guessed.
        ///
        /// Two hypotheses were measured against the bare control, outside Visual Studio, and both
        /// were wrong: <c>CoreWebView2Controller.Bounds</c> never collapses when the panel is
        /// hidden, and MoveFocus(Programmatic) neither scrolls nor moves the focused element. So
        /// this makes no assumption about the mechanism. It records every way an offset can be
        /// lost and leaves the reading to the log:
        ///
        /// <list type="bullet">
        /// <item>an offset assigned by script, attributed to the caller that assigned it;</item>
        /// <item>a scroller replaced by a new element — a rebuilt subtree starts at zero, and no
        /// scroll event is raised for it at all;</item>
        /// <item>a scroller that measured nothing, which has no offset left to keep;</item>
        /// <item>the focus landing inside the transcript, which the browser reveals;</item>
        /// <item>the messages arriving from the host around the moment it happens.</item>
        /// </list>
        ///
        /// Timestamps are relative to the start of tracing, because the page and the host write to
        /// one pane and their clocks are not the same one.
        ///
        /// Injected by the host rather than added to the bundle — the bundle is shared with the
        /// VS Code extension and this is a question about this host — and dormant until the host
        /// calls <c>window.__TOOTEGA_TRACE__()</c>, which it does when debug logging is on. A
        /// normal session installs no hooks and no observers.
        /// </summary>
        private static string BuildScrollTrace()
        {
            return
                "(function(){" +
                "  var vs = window.chrome && window.chrome.webview;" +
                "  if (!vs) return;" +
                "  var on = false;" +
                "  var t0 = Date.now();" +
                "  window.__TOOTEGA_TRACE__ = function () { if (on) return; on = true; t0 = Date.now(); install(); };" +
                "  var say = function (text) {" +
                "    try { vs.postMessage({ kind: 'trace', text: '+' + (Date.now() - t0) + 'ms  ' + text }); } catch (e) {}" +
                "  };" +
                "  var name = function (el) {" +
                "    if (!el) return 'nothing';" +
                "    if (el === document.body) return 'BODY';" +
                "    var c = typeof el.className === 'string' && el.className ? '.' + el.className.split(/\\s+/)[0] : '';" +
                "    return (el.tagName || '?') + c + (el.id ? '#' + el.id : '');" +
                "  };" +
                // Two frames of the caller, with the bundle's origin stripped: enough to tell our
                // own code from the browser's, which is the whole question.
                "  var where = function () {" +
                "    var s = (new Error()).stack || '';" +
                "    return s.split('\\n').slice(2, 4).join(' <- ').replace(/https:\\/\\/[^ )]*\\//g, '').trim();" +
                "  };" +
                "  var scroller = null, serial = 0;" +
                "  var dims = function () {" +
                "    if (!scroller) return 'no scroller in the document';" +
                "    return 'top=' + scroller.scrollTop + ' clientH=' + scroller.clientHeight +" +
                "      ' scrollH=' + scroller.scrollHeight + ' innerH=' + window.innerHeight;" +
                "  };" +
                "  var wrote = null;" +
                "  var mark = function (what, el) {" +
                "    if (!el || !el.classList || !el.classList.contains('scroll')) return;" +
                "    wrote = { at: Date.now(), what: what, from: where() };" +
                "  };" +
                "  var blame = function () {" +
                "    return wrote && Date.now() - wrote.at < 500" +
                "      ? 'WRITTEN: ' + wrote.what + '  <- ' + wrote.from" +
                "      : 'no script wrote it';" +
                "  };" +
                "  var install = function () {" +
                // ---- every offset script assigns, and who assigned it ----
                "  var d = Object.getOwnPropertyDescriptor(Element.prototype, 'scrollTop');" +
                "  if (d && d.set) {" +
                "    Object.defineProperty(Element.prototype, 'scrollTop', {" +
                "      configurable: true, enumerable: d.enumerable, get: d.get," +
                "      set: function (v) { mark('scrollTop = ' + v, this); d.set.call(this, v); }" +
                "    });" +
                "  }" +
                "  ['scrollTo', 'scrollBy', 'scroll'].forEach(function (m) {" +
                "    var f = Element.prototype[m];" +
                "    if (typeof f !== 'function') return;" +
                "    Element.prototype[m] = function () { mark(m + '()', this); return f.apply(this, arguments); };" +
                "  });" +
                "  var into = Element.prototype.scrollIntoView;" +
                "  if (typeof into === 'function') {" +
                "    Element.prototype.scrollIntoView = function () {" +
                "      say('scrollIntoView on ' + name(this) + '  <- ' + where());" +
                "      return into.apply(this, arguments);" +
                "    };" +
                "  }" +
                // ---- the scroll events themselves, batched so a wheel is one line ----
                "  var watch = function (el) {" +
                "    el.__ttWatched = true;" +
                "    var last = el.scrollTop, batch = last, count = 0, timer = 0;" +
                "    var flush = function () {" +
                "      if (timer) { clearTimeout(timer); timer = 0; }" +
                "      if (count === 0) return;" +
                "      say('scroll ' + batch + ' -> ' + el.scrollTop + ' (' + count + ' event' +" +
                "        (count > 1 ? 's' : '') + ') | ' + dims() + ' | focused=' + name(document.activeElement) +" +
                "        ' pageFocus=' + document.hasFocus() + ' | ' + blame());" +
                "      count = 0; batch = el.scrollTop;" +
                "    };" +
                "    el.addEventListener('scroll', function () {" +
                "      var top = el.scrollTop;" +
                "      if (count === 0) batch = last;" +
                "      count++;" +
                "      var abrupt = top === 0 || Math.abs(top - last) > 150;" +
                "      last = top;" +
                "      if (abrupt) { flush(); return; }" +
                "      if (!timer) timer = setTimeout(flush, 150);" +
                "    }, true);" +
                // A scroller that measures nothing is a scroller with no offset to keep.
                "    if (typeof ResizeObserver !== 'undefined') {" +
                "      var size = '';" +
                "      var ro = new ResizeObserver(function () {" +
                "        var now = el.clientHeight + '/' + el.scrollHeight;" +
                "        if (now === size) return;" +
                "        var before = size;" +
                "        size = now;" +
                "        if (before === '') return;" +
                "        say('measured ' + now.replace('/', ' x ') + ' (was ' + before.replace('/', ' x ') +" +
                "          ') | ' + dims());" +
                "      });" +
                "      ro.observe(el);" +
                "      if (el.firstElementChild) ro.observe(el.firstElementChild);" +
                "    }" +
                "  };" +
                // ---- the scroller's identity: a replaced element is a lost offset with no event ----
                "  var look = function () {" +
                "    var el = document.querySelector('.scroll');" +
                "    if (!el) {" +
                "      if (scroller) { say('THE SCROLLER LEFT THE DOCUMENT (#' + serial + ')'); scroller = null; }" +
                "      return;" +
                "    }" +
                "    if (el === scroller) return;" +
                "    var had = scroller !== null;" +
                "    scroller = el;" +
                "    serial++;" +
                "    if (had) {" +
                "      say('THE SCROLLER WAS REPLACED by a new element (#' + serial + '): the page rebuilt " +
                "that subtree, so it starts at ' + el.scrollTop + ' and no scroll event is raised | ' + dims());" +
                "    } else {" +
                "      say('watching the scroller (#' + serial + ') | ' + dims());" +
                "    }" +
                "    if (!el.__ttWatched) watch(el);" +
                "  };" +
                // ---- focus, viewport and visibility ----
                "  document.addEventListener('focusin', function (e) {" +
                "    var t = e.target;" +
                "    var inside = t && t.closest && t.closest('.scroll');" +
                "    say('focusin ' + name(t) + (inside ? '  (INSIDE THE TRANSCRIPT)' : '') + ' | ' + dims());" +
                "  }, true);" +
                "  document.addEventListener('focusout', function (e) { say('focusout ' + name(e.target)); }, true);" +
                "  window.addEventListener('focus', function () {" +
                "    say('the page GOT focus, on ' + name(document.activeElement) + ' | ' + dims());" +
                "  });" +
                "  window.addEventListener('blur', function () {" +
                "    say('the page LOST focus, on ' + name(document.activeElement) + ' | ' + dims());" +
                "  });" +
                "  window.addEventListener('resize', function () {" +
                "    say('viewport resize -> ' + window.innerWidth + 'x' + window.innerHeight + ' | ' + dims());" +
                "  });" +
                "  document.addEventListener('visibilitychange', function () {" +
                "    say('visibility -> ' + document.visibilityState + ' | ' + dims());" +
                "  });" +
                // ---- what the host is saying while all of this happens ----
                "  var lastKind = null, kinds = 0, kindTimer = 0;" +
                "  var flushKind = function () {" +
                "    kindTimer = 0;" +
                "    if (!lastKind) return;" +
                "    say('host -> page: ' + lastKind + (kinds > 1 ? ' x' + kinds : ''));" +
                "    lastKind = null; kinds = 0;" +
                "  };" +
                "  window.addEventListener('message', function (e) {" +
                "    var k = e.data && e.data.kind;" +
                "    if (!k || k === 'trace') return;" +
                "    if (k === lastKind) { kinds++; } else { flushKind(); lastKind = k; kinds = 1; }" +
                "    if (!kindTimer) kindTimer = setTimeout(flushKind, 250);" +
                "  });" +
                "  var start = function () {" +
                "    if (typeof MutationObserver !== 'undefined') {" +
                "      new MutationObserver(look).observe(document.body, { childList: true, subtree: true });" +
                "    }" +
                "    look();" +
                "    say('tracing started | ' + dims());" +
                "  };" +
                "  if (document.readyState === 'loading') {" +
                "    document.addEventListener('DOMContentLoaded', start);" +
                "  } else {" +
                "    start();" +
                "  }" +
                "  };" +
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
