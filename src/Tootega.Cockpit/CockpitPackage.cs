using System;
using System.Runtime.InteropServices;
using System.Threading;
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
    /// Loading is deferred until a Cockpit command or tool window is actually used
    /// (AllowsBackgroundLoading + no autoload UI context): an extension that spawns a CLI
    /// has no business slowing down VS start for users who never open it.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#110", "#112", "1.0.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(CockpitIds.PackageGuidString)]
    [ProvideToolWindow(typeof(ChatToolWindow), Style = VsDockStyle.Tabbed, Window = EnvDTE.Constants.vsWindowKindMainWindow, Orientation = ToolWindowOrientation.Right)]
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

            // The orchestration comes up before the commands, so the ones that act on a
            // conversation are enabled from the first click rather than greyed out until
            // something else happens to construct it.
            _host = new Host.CockpitHostService(this, options);
            CockpitHost.Instance = _host;

            // Resolved once here, on the UI thread, because every session afterwards reads the
            // cached value from a CLI reader thread.
            _host.Editor.RefreshWorkspace();

            await CockpitCommands.InitializeAsync(this, cancellationToken);

            Log.Info("Tootega Cockpit activated.");
        }

        internal CockpitOptions Options => (CockpitOptions)GetDialogPage(typeof(CockpitOptions));

        /// <summary>Shows a tool window, creating it on first use.</summary>
        internal async Tasks.Task<T> ShowToolWindowAsync<T>(CancellationToken cancellationToken)
            where T : CockpitToolWindowBase
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var window = await FindToolWindowAsync(typeof(T), 0, true, DisposalToken) as T;
            if (window?.Frame == null)
            {
                Log.Error("Could not create the " + typeof(T).Name + " tool window.");
                return null;
            }

            var frame = (Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame)window.Frame;
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
            window.View?.FocusWebView();
            return window;
        }

        /// <summary>The already-open chat window, or null when it was never opened.</summary>
        internal ChatToolWindow FindOpenChatWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return FindToolWindow(typeof(ChatToolWindow), 0, false) as ChatToolWindow;
        }
    }
}
