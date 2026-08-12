using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Web.WebView2.Wpf;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.UI
{
    /// <summary>
    /// A WebView2 that lets Visual Studio's keyboard shortcuts through.
    ///
    /// A hosted browser is a keyboard black hole: the web content lives in a child window of
    /// its own and takes every keystroke that reaches it, so with the caret in the composer
    /// F5 did not start the debugger, Ctrl+Shift+B did not build, and none of the user's own
    /// bindings fired. Turning off the browser's accelerators stops Chromium acting on them,
    /// but hands them to nobody — the keys simply died.
    ///
    /// So every keystroke is offered to the IDE first, and only what it does not claim
    /// continues into the page. The offer goes through the shell's own key-binding
    /// resolution, so it honours customised bindings and the active context rather than a
    /// list of shortcuts frozen into this file.
    ///
    /// It is hooked in two places, because the WebView2 renders out of process and which path a
    /// key takes is not ours to decide: the browser's own accelerator notification is where keys
    /// like F5 surface when the page has the focus, and PreviewKeyDown is where keys arrive when
    /// WPF has it. A keystroke that reaches neither was never ours to route.
    ///
    /// The offer is made here and nowhere else. The control's own habit of replaying the browser's
    /// keys back into the WPF tree is turned off — see <see cref="StopWpfReplay"/> — because it
    /// offered every key the page was typing to every ancestor in the IDE, and took the answer as
    /// binding.
    /// </summary>
    /// <remarks>
    /// It derives from the composition control rather than the ordinary WebView2 on purpose.
    /// The ordinary one is an HwndHost — a child window, and a child window always paints
    /// above WPF, whatever the z-order says. In an IDE whose panels slide over the editor
    /// that is not a detail: an unpinned Output or Error List came out *underneath* the
    /// conversation, half of it hidden by a browser that has no business being on top.
    /// Composition mode renders the page into a WPF visual instead, so the shell's own
    /// surfaces overlap it the way they overlap everything else.
    /// </remarks>
    internal sealed class VsWebView2 : WebView2CompositionControl, IKeyboardInputSink
    {
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        /// <summary>Ask the shell, and let it run the command it finds.</summary>
        private const uint UseGlobalScope = (uint)__VSTRANSACCELEXFLAGS.VSTAEXF_UseGlobalKBScope;

        private IVsFilterKeys2 _filterKeys;
        private bool _filterKeysResolved;

        public VsWebView2()
        {
            PreviewKeyDown += OnPreviewKeyDown;
            // The event is raised on the UI thread by the control itself; the assertion is
            // for the analyzer, which cannot see that from here.
            CoreWebView2InitializationCompleted += delegate
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                HookAcceleratorKeys();
            };
        }

        /// <summary>
        /// Subscribes to the browser's own accelerator notification, which is where keys like
        /// F5 actually surface.
        ///
        /// The controller that raises it is deliberately not public on the WPF control, so it
        /// is reached by reflection — with the fallbacks above still in place, because a
        /// private member is a member that can vanish in an update. Failing to find it costs
        /// a line in the log and nothing else.
        /// </summary>
        private void HookAcceleratorKeys()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var baseField = GetType().BaseType?.GetField(
                    "m_webview2Base",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                var host = baseField?.GetValue(this);
                var controller = host?.GetType()
                    .GetProperty(
                        "CoreWebView2Controller",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance)
                    ?.GetValue(host) as Microsoft.Web.WebView2.Core.CoreWebView2Controller;

                if (controller == null)
                {
                    Log.Debug("keyboard: the WebView2 controller is out of reach; shortcuts rely on the WPF paths.");
                    return;
                }

                _controller = controller;
                controller.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
                StopWpfReplay(host, controller);
                Log.Debug("keyboard: hooked the browser's accelerator keys.");
            }
            catch (Exception ex)
            {
                Log.Debug("keyboard: could not hook the browser's accelerator keys: " + ex.Message);
            }
        }

        private Microsoft.Web.WebView2.Core.CoreWebView2Controller _controller;

        /// <summary>
        /// Stops the control from replaying the page's keystrokes into Visual Studio's WPF tree.
        ///
        /// The control answers the browser's accelerator notification by manufacturing a WPF
        /// <see cref="KeyEventArgs"/> and raising it on itself as PreviewKeyDown — which, being a
        /// tunnelling event, travels from the root of the WPF tree down to this control, so every
        /// ancestor inside the IDE sees it. The composition control then re-raises those same args
        /// as KeyDown, which bubbles all the way back up. Whatever anything on either route makes
        /// of the keystroke is returned to the browser as <c>Handled</c> — and a handled keystroke
        /// is one the page never receives.
        ///
        /// That is what broke editing in the composer. Home, End, Page Up and Page Down, the
        /// arrows, Tab, Backspace and Delete all reach the browser as accelerator notifications
        /// precisely because they have no typed representation, so every one of them was being
        /// offered to a tree of WPF controls that has no business with the caret in a text box.
        /// A caret that stops moving, or a panel that scrolls instead, is what the user sees when
        /// one of them claims the key.
        ///
        /// The IDE's own claim is not lost with it. That offer is made deliberately in
        /// <see cref="OnAcceleratorKeyPressed"/>, through the shell's key-binding resolution, for
        /// the keys that are the IDE's business and no others. What goes away is a second,
        /// indiscriminate offer to whatever happens to be above us in the visual tree.
        /// </summary>
        /// <remarks>
        /// Reached by reflection, like the controller itself: the handler is private, and the
        /// event is subscribed before we exist. Failing costs a line in the log — and the old
        /// behaviour, which is the one to be wrong in.
        /// </remarks>
        private static void StopWpfReplay(object host, Microsoft.Web.WebView2.Core.CoreWebView2Controller controller)
        {
            try
            {
                var method = host.GetType().GetMethod(
                    "CoreWebView2Controller_AcceleratorKeyPressed",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var accelerator = controller.GetType().GetEvent("AcceleratorKeyPressed");

                if (method == null || accelerator == null)
                {
                    Log.Debug("keyboard: the control's key replay is out of reach; the page still shares " +
                              "its keys with the IDE's WPF tree.");
                    return;
                }

                // Delegates compare by target and method, so one built here is the one that was
                // subscribed there, and removing it removes that subscription.
                var handler = Delegate.CreateDelegate(accelerator.EventHandlerType, host, method, false);
                if (handler == null)
                {
                    Log.Debug("keyboard: the control's key replay handler has a shape we do not recognise; " +
                              "leaving it in place.");
                    return;
                }

                accelerator.RemoveEventHandler(controller, handler);
                Log.Debug("keyboard: the control's key replay into WPF is off; the page keeps its own keys.");
            }
            catch (Exception ex)
            {
                Log.Debug("keyboard: could not turn off the control's key replay: " + ex.Message);
            }
        }

        /// <summary>
        /// Hands keyboard focus to the page.
        ///
        /// Focusing the WPF control only says which element WPF will send input to; the page
        /// has a focus of its own, and in composition mode nothing moves it for us.
        /// </summary>
        public void FocusBrowser()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (_controller == null)
                {
                    Log.Debug("focus: no controller; the page was not handed the focus.");
                    return;
                }

                _controller.MoveFocus(Microsoft.Web.WebView2.Core.CoreWebView2MoveFocusReason.Programmatic);
                Log.Debug("focus: handed to the page (MoveFocus Programmatic).");
            }
            catch (Exception ex)
            {
                Log.Debug("keyboard: could not move focus into the page: " + ex.Message);
            }
        }

        /// <summary>
        /// Takes the focus without deciding where in the page it lands.
        ///
        /// This is the one that was sending the conversation back to its beginning. WPF hands
        /// focus to a hosted sink by calling <c>IKeyboardInputSink.TabInto</c>, and the shell does
        /// that every time it activates the tool window. The control's own implementation answers
        /// it with MoveFocus(<c>Next</c>), which means "focus the next element in the page" — and
        /// with nothing focused, or coming from outside, the next element is the <em>first</em>
        /// tabbable one in the document. In a conversation that is a button in the topmost
        /// message, so the browser scrolls the transcript up to reveal it.
        ///
        /// Measured rather than reasoned about, against the bare control in a host of its own:
        /// with the scroller at 1200, MoveFocus(Next) and TabInto(First) both ended at offset 0
        /// with the first button focused, taking the focus off the text box on the way.
        /// MoveFocus(Programmatic) moved neither the offset nor the focused element — so that is
        /// what the focus arrives through here.
        ///
        /// True is the honest answer: the page does take the focus. What it does not do is pick
        /// an element, which leaves whatever the page last had — the composer — holding it.
        /// Tabbing *within* the page is the browser's own and is untouched, and the way out
        /// (MoveFocusRequested, when the page runs out of elements) still belongs to the control.
        /// </summary>
        bool IKeyboardInputSink.TabInto(TraversalRequest request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Log.Debug("focus: the shell tabbed into the page (" +
                      (request == null ? "no direction" : request.FocusNavigationDirection.ToString()) +
                      "); taking it without choosing an element.");

            Focus();
            FocusBrowser();
            return true;
        }

        private void OnAcceleratorKeyPressed(
            object sender, Microsoft.Web.WebView2.Core.CoreWebView2AcceleratorKeyPressedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var down = e.KeyEventKind == Microsoft.Web.WebView2.Core.CoreWebView2KeyEventKind.KeyDown ||
                       e.KeyEventKind == Microsoft.Web.WebView2.Core.CoreWebView2KeyEventKind.SystemKeyDown;
            if (!down) return;

            var key = KeyInterop.KeyFromVirtualKey((int)e.VirtualKey);

            Log.Debug("keyboard: browser saw " + Describe(key, CurrentModifiers()) +
                      (e.Handled ? " (already handled)" : string.Empty));

            if (!BelongsToTheIde(key, CurrentModifiers())) return;

            var message = e.KeyEventKind == Microsoft.Web.WebView2.Core.CoreWebView2KeyEventKind.SystemKeyDown
                ? WM_SYSKEYDOWN
                : WM_KEYDOWN;

            if (!TranslateInIde(message, (int)e.VirtualKey)) return;

            e.Handled = true;
            Log.Debug("keyboard: the IDE took " + Describe(key, CurrentModifiers()) + " (browser path).");
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // A key that came in as an IME composition or a repeat of one already routed is
            // not a shortcut. System keys arrive here as Key.System with the real key in
            // SystemKey — that is Alt combinations, which the IDE very much wants.
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.None || e.IsRepeat) return;

            // Every key, with what was already decided about it before we looked. When a key
            // does not reach the page, this says whether it reached us at all and who claimed
            // it — the two questions that separate a routing bug from a focus one.
            Log.Debug("keyboard: wpf saw " + Describe(key, CurrentModifiers()) +
                      (e.Handled ? " (already handled)" : string.Empty));

            if (!BelongsToTheIde(key, CurrentModifiers())) return;

            var message = e.Key == Key.System ? WM_SYSKEYDOWN : WM_KEYDOWN;
            if (!TranslateInIde(message, KeyInterop.VirtualKeyFromKey(key))) return;

            // Claimed by the IDE: stop it here, or the page would act on it as well.
            e.Handled = true;
            Log.Debug("keyboard: the IDE took " + Describe(key, CurrentModifiers()) + ".");
        }

        /// <summary>
        /// Whether a keystroke is the IDE's business rather than the page's.
        ///
        /// Typing must never take this path, so a bare key is only offered when it cannot be
        /// text: the function keys. Everything held with Ctrl or Alt is offered too, minus the
        /// handful the composer implements itself — take Ctrl+C from a text box and the user
        /// loses copy to gain nothing.
        /// </summary>
        private static bool BelongsToTheIde(Key key, ModifierKeys modifiers)
        {
            if (key >= Key.F1 && key <= Key.F24) return true;

            var control = (modifiers & ModifierKeys.Control) != 0;
            var alt = (modifiers & ModifierKeys.Alt) != 0;
            if (!control && !alt) return false;

            // Moving the caret stays with the text box whatever is held down. The IDE has
            // nothing worth binding to Alt+Home inside a composer, and a caret that stops
            // moving is a defect the user feels on every line.
            if (IsCaretKey(key)) return false;

            // Ctrl+Alt is the Cockpit's own prefix, and Alt alone opens menus: nothing in the
            // page listens for either.
            if (alt) return true;

            return !IsComposerShortcut(key);
        }

        /// <summary>Keys that only ever move the caret or the view of the text.</summary>
        private static bool IsCaretKey(Key key)
        {
            switch (key)
            {
                case Key.Home:
                case Key.End:
                case Key.PageUp:
                case Key.PageDown:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The Ctrl combinations the webview handles, and must keep handling.</summary>
        private static bool IsComposerShortcut(Key key)
        {
            switch (key)
            {
                // Editing and selection inside the text box.
                case Key.A:
                case Key.C:
                case Key.V:
                case Key.X:
                case Key.Z:
                case Key.Y:
                case Key.Back:
                case Key.Delete:
                case Key.Left:
                case Key.Right:
                case Key.Up:
                case Key.Down:
                case Key.Home:
                case Key.End:
                // Find in conversation, and send.
                case Key.F:
                case Key.Enter:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Offers the keystroke to the shell. True when the shell ran a command for it.</summary>
        private bool TranslateInIde(int message, int virtualKey)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var filterKeys = FilterKeys();
            if (filterKeys == null) return false;

            // Synthesised rather than forwarded: the shell resolves a binding from the
            // message and the key, and the fields it does not read are the ones a WPF event
            // could not have told us anyway.
            var msg = new Microsoft.VisualStudio.OLE.Interop.MSG
            {
                hwnd = IntPtr.Zero,
                message = (uint)message,
                wParam = (IntPtr)virtualKey,
                lParam = IntPtr.Zero,
                time = 0,
                pt = new Microsoft.VisualStudio.OLE.Interop.POINT { x = 0, y = 0 }
            };

            try
            {
                var hr = filterKeys.TranslateAcceleratorEx(
                    new[] { msg },
                    UseGlobalScope,
                    0,
                    null,
                    out _,
                    out _,
                    out var translated,
                    out _);

                // S_OK with the "translated" flag set means the shell recognised the
                // keystroke and ran its command. Anything else — including the first key of
                // a chord — leaves it to the page, which is the safe direction to be wrong in.
                return hr == VSConstants.S_OK && translated != 0;
            }
            catch (Exception ex)
            {
                Log.Debug("keyboard: the shell could not translate the keystroke: " + ex.Message);
                return false;
            }
        }

        private IVsFilterKeys2 FilterKeys()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Resolved once and remembered, including the failure: this runs on every
            // keystroke, and a service lookup per key is a cost the typing would feel.
            if (_filterKeysResolved) return _filterKeys;

            _filterKeysResolved = true;
            _filterKeys = Package.GetGlobalService(typeof(SVsFilterKeys)) as IVsFilterKeys2;
            if (_filterKeys == null) Log.Info("keyboard: SVsFilterKeys is unavailable; shortcuts stay with the page.");

            return _filterKeys;
        }

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int virtualKey);

        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;

        /// <summary>
        /// The modifiers actually held down, read from the keyboard itself.
        ///
        /// Not <see cref="Keyboard.Modifiers"/>: that reports WPF's own view of the keyboard,
        /// and the keystrokes handled here were typed into a window WPF does not own — the
        /// browser runs out of process. Its view can be stale, and a stale Alt turns every
        /// keystroke into a candidate command: Home and End stopped moving the caret because
        /// they were being offered to the IDE and taken by it.
        /// </summary>
        private static ModifierKeys CurrentModifiers()
        {
            var modifiers = ModifierKeys.None;

            // The high bit is "down now"; the low bit is the toggle state, which is not this.
            if ((GetKeyState(VK_CONTROL) & 0x8000) != 0) modifiers |= ModifierKeys.Control;
            if ((GetKeyState(VK_MENU) & 0x8000) != 0) modifiers |= ModifierKeys.Alt;
            if ((GetKeyState(VK_SHIFT) & 0x8000) != 0) modifiers |= ModifierKeys.Shift;

            return modifiers;
        }

        private static string Describe(Key key, ModifierKeys modifiers)
        {
            var text = string.Empty;
            if ((modifiers & ModifierKeys.Control) != 0) text += "Ctrl+";
            if ((modifiers & ModifierKeys.Alt) != 0) text += "Alt+";
            if ((modifiers & ModifierKeys.Shift) != 0) text += "Shift+";
            return text + key;
        }
    }
}
