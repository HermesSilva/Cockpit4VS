using System;
using System.ComponentModel.Design;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;
using Tootega.Cockpit.Options;
using Tootega.Cockpit.UI;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit
{
    /// <summary>
    /// Binds every entry declared in CockpitPackage.vsct to its handler.
    ///
    /// Commands that only move VS chrome around (open a window, reload it, show the
    /// options page) are handled here directly. Commands that act on a conversation are
    /// delegated to <see cref="ICockpitHost"/> and grey themselves out until that layer
    /// is available — a menu item that looks enabled and does nothing is worse than one
    /// that plainly says it is not ready.
    /// </summary>
    internal static class CockpitCommands
    {
        public static async Task InitializeAsync(CockpitPackage package, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var menu = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (menu == null)
            {
                Log.Error("IMenuCommandService unavailable — Cockpit commands are not registered.");
                return;
            }

            // --- Shell-only commands ---
            AddShellCommand(menu, CockpitIds.CmdOpen, delegate
            {
                _ = package.JoinableTaskFactory.RunAsync(() => package.ShowToolWindowAsync<ChatToolWindow>(cancellationToken));
            });

            AddShellCommand(menu, CockpitIds.CmdOpenHub, delegate
            {
                _ = package.JoinableTaskFactory.RunAsync(() => package.ShowToolWindowAsync<HubToolWindow>(cancellationToken));
            });

            AddShellCommand(menu, CockpitIds.CmdReloadView, delegate
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                package.FindOpenChatWindow()?.Reload();
            });

            AddShellCommand(menu, CockpitIds.CmdSettings, delegate
            {
                package.ShowOptionPage(typeof(CockpitOptions));
            });

            // --- Conversation commands, owned by the orchestration layer ---
            AddHostCommand(menu, CockpitIds.CmdNewSession, h => h.NewSession());
            AddHostCommand(menu, CockpitIds.CmdInterrupt, h => h.Interrupt());
            AddHostCommand(menu, CockpitIds.CmdOpenSessions, h => h.OpenSessions());
            AddHostCommand(menu, CockpitIds.CmdReopenClosed, h => h.ReopenClosed());
            AddHostCommand(menu, CockpitIds.CmdLogin, h => h.LoginCli());
            AddHostCommand(menu, CockpitIds.CmdLogout, h => h.LogoutCli());
            AddHostCommand(menu, CockpitIds.CmdSetApiKey, h => h.SetApiKeyInteractive());
            AddHostCommand(menu, CockpitIds.CmdClearApiKey, h => h.ClearApiKey());
            AddHostCommand(menu, CockpitIds.CmdEnableUsageTracking, h => h.EnableUsageTracking());
            AddHostCommand(menu, CockpitIds.CmdDisableUsageTracking, h => h.DisableUsageTracking());
            AddHostCommand(menu, CockpitIds.CmdEnableUtf8Fix, h => h.EnableUtf8Fix());
            AddHostCommand(menu, CockpitIds.CmdDisableUtf8Fix, h => h.DisableUtf8Fix());
        }

        private static void AddShellCommand(OleMenuCommandService menu, int commandId, Action handler)
        {
            var id = new CommandID(CockpitIds.CommandSet, commandId);
            menu.AddCommand(new OleMenuCommand(delegate { Invoke(commandId, handler); }, id));
        }

        private static void AddHostCommand(OleMenuCommandService menu, int commandId, Action<ICockpitHost> handler)
        {
            var id = new CommandID(CockpitIds.CommandSet, commandId);
            var command = new OleMenuCommand(delegate
            {
                var host = CockpitHost.Instance;
                if (host == null) return;
                Invoke(commandId, () => handler(host));
            }, id);

            command.BeforeQueryStatus += delegate (object sender, EventArgs e)
            {
                ((OleMenuCommand)sender).Enabled = CockpitHost.Instance != null;
            };

            menu.AddCommand(command);
        }

        /// <summary>A handler that throws must not take the IDE's command routing with it.</summary>
        private static void Invoke(int commandId, Action handler)
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                Log.Error("Command 0x" + commandId.ToString("X4") + " failed", ex);
            }
        }
    }
}
