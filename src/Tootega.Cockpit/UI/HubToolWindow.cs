using System.Runtime.InteropServices;

namespace Tootega.Cockpit.UI
{
    /// <summary>
    /// The hub: saved contexts, plugins, skills, MCP servers and the onboarding checklist.
    /// It is the counterpart of the VS Code activity-bar view, so it is narrow by default.
    /// </summary>
    [Guid(CockpitIds.HubWindowGuidString)]
    internal sealed class HubToolWindow : CockpitToolWindowBase
    {
        public HubToolWindow() : base("Cockpit Hub", "hub")
        {
            BitmapImageMoniker = CockpitMonikers.Hub;
        }
    }
}
