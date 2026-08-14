using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
// Microsoft.VisualStudio.Shell also defines a Task type, so the BCL one is aliased.
using Tasks = System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;
using Tootega.Cockpit.Options;
using Tootega.Cockpit.UI;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit
{
    /// <summary>
    /// Extension entry point. Port of src/extension.ts.
    ///
    /// It loads in the background once the shell is up, rather than waiting for a command:
    /// with `AllowsBackgroundLoading` the load costs the user nothing at start-up, and a
    /// package that is already there is a package whose commands are live the first time
    /// someone reaches for them. Nothing is spawned until a conversation is opened — the CLI
    /// still starts on demand.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
    [InstalledProductRegistration("#110", "#112", CockpitIds.ProductVersion)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(CockpitIds.PackageGuidString)]
    // MultiInstances: one window per conversation, each with its own folder and process.
    //
    // Docked into the document well rather than a side panel: a conversation is a place you
    // work, not a palette you glance at, and the transcript, the diffs and the composer all
    // need the width. It opens as a tab beside the source files, over the whole editor area.
    // Visual Studio remembers where the user moves it afterwards, which is as it should be —
    // this decides where it starts, not where it has to stay.
    [ProvideToolWindow(typeof(ChatToolWindow), MultiInstances = true, Style = VsDockStyle.Tabbed, Window = "DocumentWell", Orientation = ToolWindowOrientation.Right)]
    [ProvideToolWindow(typeof(HubToolWindow), Style = VsDockStyle.Tabbed, Window = EnvDTE.Constants.vsWindowKindSolutionExplorer, Orientation = ToolWindowOrientation.Right)]
    [ProvideOptionPage(typeof(CockpitOptions), "Tootega Cockpit", "General", 0, 0, true)]
    [ProvideProfile(typeof(CockpitOptions), "Tootega Cockpit", "General", 0, 0, true)]
    public sealed class CockpitPackage : AsyncPackage
    {
        internal static CockpitPackage Instance { get; private set; }

        private Host.CockpitHostService _host;

        /// <summary>The orchestration layer. Null until the package has initialized.</summary>
        internal Host.CockpitHostService Host => _host;

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            Instance = this;

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            Log.Initialize();
            var options = (CockpitOptions)GetDialogPage(typeof(CockpitOptions));
            Log.DebugEnabled = options.DebugLog;
            Log.Info("Tootega Cockpit activating…");

            // One-off migration: rewrite the weak first Quiet default (which reached the model but
            // let the agent keep narrating) to the stronger form — only when never customized.
            if (options.MigrateStaleQuietDefault())
                Log.Info("Quiet directive: stale default rewritten to the stronger form.");

            // The orchestration comes up before the commands, so the ones that act on a
            // conversation are enabled from the first click rather than greyed out until
            // something else happens to construct it.
            _host = new Host.CockpitHostService(this, options);
            CockpitHost.Instance = _host;

            // Resolved once here, on the UI thread, because every session afterwards reads the
            // cached value from a CLI reader thread.
            _host.Editor.RefreshWorkspace();

            await CockpitCommands.InitializeAsync(this, cancellationToken);

            if (options.TitleBarButton) TitleBarButton.Install(this);

            // The option is a plain toggle: applying it adds or removes the button there and
            // then, rather than asking for a restart the shell does not need.
            CockpitOptions.Applied += OnOptionsApplied;

            Log.Info("Tootega Cockpit activated.");
        }

        internal CockpitOptions Options => (CockpitOptions)GetDialogPage(typeof(CockpitOptions));

        private void OnOptionsApplied(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (Options.TitleBarButton) TitleBarButton.Install(this);
            else TitleBarButton.Uninstall();
        }

        /// <summary>
        /// The tab the next conversation window should adopt.
        ///
        /// A handshake rather than a constructor argument: the shell builds the window and calls
        /// OnCreate itself, so there is no way to pass anything in. Both sides run on the UI
        /// thread between the request and the creation, which is what makes a single slot safe.
        /// </summary>
        private static string _pendingTabId;

        internal static void SetPendingTab(string tabId) => _pendingTabId = tabId;

        /// <summary>Consumes the pending tab, if the package asked for a specific one.</summary>
        internal static string TakePendingTab()
        {
            var tabId = _pendingTabId;
            _pendingTabId = null;
            return tabId;
        }

        /// <summary>Shows a single-instance tool window, creating it on first use.</summary>
        internal async Tasks.Task<T> ShowToolWindowAsync<T>(CancellationToken cancellationToken)
            where T : CockpitToolWindowBase
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var window = await FindToolWindowAsync(typeof(T), 0, true, DisposalToken) as T;
            return Show(window, typeof(T).Name);
        }

        /// <summary>
        /// Shows the window of one conversation, opening it when it has none.
        ///
        /// Instance ids are the shell's handle on a multi-instance window, and they must be
        /// stable for the window's life; the tab id is ours. The two are matched by walking the
        /// open instances rather than by keeping a map, which cannot drift out of date.
        /// </summary>
        internal async Tasks.Task<ChatToolWindow> ShowConversationAsync(string tabId, CancellationToken cancellationToken)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var existing = FindConversationWindow(tabId);
            if (existing != null) return Show(existing, "conversation");

            SetPendingTab(tabId);

            try
            {
                var window = await FindToolWindowAsync(typeof(ChatToolWindow), NextInstanceId(), true, DisposalToken)
                    as ChatToolWindow;

                return Show(window, "conversation");
            }
            finally
            {
                // Cleared even on failure: a stale pending tab would be adopted by the next
                // window the shell happens to restore.
                TakePendingTab();
            }
        }

        /// <summary>The open window of a conversation, or null.</summary>
        internal ChatToolWindow FindConversationWindow(string tabId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (tabId == null) return null;

            for (var id = 0; id < MaxConversationWindows; id++)
            {
                if (!(FindToolWindow(typeof(ChatToolWindow), id, false) is ChatToolWindow window)) continue;
                if (string.Equals(window.TabId, tabId, StringComparison.Ordinal)) return window;
            }

            return null;
        }

        /// <summary>
        /// How many conversation windows can exist at once.
        ///
        /// A bound, because finding a free instance id means probing them: without one, a leak
        /// would turn into an endless loop rather than a message.
        /// </summary>
        private const int MaxConversationWindows = 32;

        private int NextInstanceId()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Zero is skipped: the shell reserves it for the single-instance case, and reusing
            // it makes a restored window collide with a freshly opened one.
            for (var id = 1; id < MaxConversationWindows; id++)
            {
                if (FindToolWindow(typeof(ChatToolWindow), id, false) == null) return id;
            }

            Log.Info("Too many conversation windows are open; close one before opening another.");
            return MaxConversationWindows;
        }

        private T Show<T>(T window, string what) where T : CockpitToolWindowBase
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (window?.Frame == null)
            {
                Log.Error("Could not create the " + what + " window.");
                return null;
            }

            // Rebuild the browser first when a previous close disposed it: the shell reuses a
            // hidden pane without a second OnCreate, so without this the re-opened window would
            // show an empty frame — the close-then-reopen-the-hub failure.
            window.EnsureAlive();

            var frame = (Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame)window.Frame;
            Log.Debug("window: showing the " + what + " window and handing it the focus.");
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
            window.View?.FocusWebView();
            return window;
        }

        /// <summary>Closes a conversation's window, when it has one open.</summary>
        internal void CloseConversationWindow(string tabId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var window = FindConversationWindow(tabId);
            if (window?.Frame == null) return;

            var frame = (Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame)window.Frame;
            frame.CloseFrame((uint)Microsoft.VisualStudio.Shell.Interop.__FRAMECLOSE.FRAMECLOSE_NoSave);
        }

        /// <summary>Re-reads every conversation window's caption after a title or folder change.</summary>
        internal void RefreshConversationCaptions()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            for (var id = 0; id < MaxConversationWindows; id++)
            {
                (FindToolWindow(typeof(ChatToolWindow), id, false) as ChatToolWindow)?.UpdateCaption();
            }
        }
    }
}
