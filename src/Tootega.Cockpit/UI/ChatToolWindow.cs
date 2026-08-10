using System.Runtime.InteropServices;
using System.ComponentModel.Design;

namespace Tootega.Cockpit.UI
{
    /// <summary>
    /// One conversation: composer, timeline and the statistics panel.
    ///
    /// Multi-instance, because a conversation is bound to a folder and a context: two of them
    /// are two windows, side by side, each with its own process — the same shape the VS Code
    /// extension had with one webview panel per conversation.
    /// </summary>
    [Guid(CockpitIds.ChatWindowGuidString)]
    internal sealed class ChatToolWindow : CockpitToolWindowBase
    {
        /// <summary>Mirrors the ChatToolbar IDSymbol in CockpitPackage.vsct.</summary>
        private const int ChatToolbarId = 0x1030;

        public ChatToolWindow() : base("Cockpit", "chat")
        {
            ToolBar = new CommandID(CockpitIds.CommandSet, ChatToolbarId);
        }

        protected override bool BindsToTab => true;
    }
}
