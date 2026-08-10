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
            // Opening the Cockpit means opening a conversation. Focus the active one when there
            // is one, so the command does not pile up empty windows.
            AddHostCommand(menu, CockpitIds.CmdOpen, h => h.OpenOrFocusConversation());

            AddShellCommand(menu, CockpitIds.CmdOpenHub, delegate
            {
                _ = package.JoinableTaskFactory.RunAsync(() => package.ShowToolWindowAsync<HubToolWindow>(cancellationToken));
            });

            AddHostCommand(menu, CockpitIds.CmdReloadView, h => h.ReloadActiveView());

            AddFolderCommand(menu);

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

        /// <summary>
        /// The toolbar's folder control: it shows where the conversation runs and changes it.
        ///
        /// The caption is rewritten on every status query rather than pushed on change,
        /// because the shell asks before it draws and there is no event for "the folder moved"
        /// that arrives earlier than that. Only the leaf name is shown — a full path would
        /// push the rest of the toolbar off the window — and the tooltip carries the rest.
        /// </summary>
        private static void AddFolderCommand(OleMenuCommandService menu)
        {
            var id = new CommandID(CockpitIds.CommandSet, CockpitIds.CmdFolder);
            var command = new OleMenuCommand(delegate
            {
                var host = CockpitHost.Instance;
                if (host == null) return;
                Invoke(CockpitIds.CmdFolder, host.ChangeFolder);
            }, id);

            command.BeforeQueryStatus += delegate (object sender, EventArgs e)
            {
                var item = (OleMenuCommand)sender;
                var host = CockpitHost.Instance;

                item.Enabled = host != null;

                var folder = host?.CurrentFolder;
                item.Text = string.IsNullOrEmpty(folder) ? "No folder" : LeafName(folder);
            };

            menu.AddCommand(command);
        }

        /// <summary>The last segment of a path, tolerating a trailing separator and a bare root.</summary>
        private static string LeafName(string path)
        {
            var trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 0) return path;

            var cut = trimmed.LastIndexOfAny(new[]
            {
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar
            });

            return cut < 0 || cut == trimmed.Length - 1 ? trimmed : trimmed.Substring(cut + 1);
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
