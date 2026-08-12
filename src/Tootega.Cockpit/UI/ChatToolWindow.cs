using System.Runtime.InteropServices;

namespace Tootega.Cockpit.UI
{
    /// <summary>
    /// One conversation: composer, timeline and the statistics panel.
    ///
    /// Multi-instance, because a conversation is bound to a folder and a context: two of them
    /// are two windows, side by side, each with its own process — the same shape the VS Code
    /// extension had with one webview panel per conversation.
    ///
    /// No toolbar: the window is a conversation and nothing else. Every action it used to carry
    /// is on Extensions &gt; Tootega Cockpit, and the folder it runs in is in its own caption.
    /// </summary>
    [Guid(CockpitIds.ChatWindowGuidString)]
    internal sealed class ChatToolWindow : CockpitToolWindowBase
    {
        public ChatToolWindow() : base("Cockpit", "chat")
        {
        }

        protected override bool BindsToTab => true;
    }
}
