using System;

namespace Tootega.Cockpit
{
    /// <summary>
    /// GUIDs and command ids shared between the C# package and CockpitPackage.vsct.
    /// Both sides must agree: a mismatch here shows up as a command that silently
    /// never fires, so the vsct symbols are mirrored one-to-one.
    /// </summary>
    internal static class CockpitIds
    {
        /// <summary>
        /// The product version, as Help &gt; About reports it.
        ///
        /// A constant because the attribute that consumes it needs one at compile time. It is
        /// rewritten by scripts/bump-version.ps1 together with the VSIX manifest and the
        /// assembly attributes — three statements of the same fact, which must never disagree.
        /// </summary>
        public const string ProductVersion = "1.0.57";

        public const string PackageGuidString = "92c17b2d-a9a9-460d-a1e2-d48f8f21e29f";
        public const string CommandSetGuidString = "8b14bea4-9c47-451d-8143-63d452bc8422";
        public const string ChatWindowGuidString = "6b893699-8efa-4b1c-9ef8-3415734fd375";
        public const string HubWindowGuidString = "b745b862-63d7-4040-a255-cf1477ca11eb";
        public const string OptionsPageGuidString = "7b48d81f-c5a1-499e-82af-717dfc8c2476";

        public static readonly Guid CommandSet = new Guid(CommandSetGuidString);

        // Command ids — mirror of the <IDSymbol> entries in CockpitPackage.vsct.
        public const int CmdOpen = 0x0100;
        public const int CmdNewSession = 0x0101;
        public const int CmdInterrupt = 0x0102;
        public const int CmdOpenSessions = 0x0103;
        public const int CmdSettings = 0x0104;
        public const int CmdReloadView = 0x0105;
        public const int CmdReopenClosed = 0x0106;
        public const int CmdLogin = 0x0107;
        public const int CmdLogout = 0x0108;
        public const int CmdSetApiKey = 0x0109;
        public const int CmdClearApiKey = 0x010A;
        public const int CmdEnableUsageTracking = 0x010B;
        public const int CmdDisableUsageTracking = 0x010C;
        public const int CmdEnableUtf8Fix = 0x010D;
        public const int CmdDisableUtf8Fix = 0x010E;
        public const int CmdOpenHub = 0x010F;
    }
}
