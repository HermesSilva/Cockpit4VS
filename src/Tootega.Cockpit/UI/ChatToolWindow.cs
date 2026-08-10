using System.Runtime.InteropServices;
using System.ComponentModel.Design;

namespace Tootega.Cockpit.UI
{
    /// <summary>
    /// The conversation itself: composer, timeline and the statistics panel.
    /// Dockable anywhere, but it defaults to a wide dock because the timeline and the
    /// stats panel sit side by side.
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
    }
}
