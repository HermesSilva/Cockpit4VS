using System;
using Microsoft.VisualStudio.Imaging.Interop;

namespace Tootega.Cockpit
{
    /// <summary>
    /// Typed access to the icons declared in Resources/Cockpit.imagemanifest. Using the
    /// ImageService instead of raw bitmaps is what makes the icons follow the VS theme:
    /// the same vector source is recolored for light, dark and high contrast.
    /// </summary>
    internal static class CockpitMonikers
    {
        public const string ImageCatalogGuidString = "092f81e8-c52d-446a-a584-57de6f62f2a1";
        private static readonly Guid Catalog = new Guid(ImageCatalogGuidString);

        public const int CockpitId = 1000;
        public const int NewSessionId = 1001;
        public const int InterruptId = 1002;
        public const int SessionsId = 1003;
        public const int SettingsId = 1004;
        public const int ReloadId = 1005;
        public const int HubId = 1006;
        public const int AccountId = 1007;
        public const int ApiKeyId = 1008;
        public const int UsageId = 1009;
        public const int Utf8FixId = 1010;
        public const int FolderId = 1011;

        public static ImageMoniker Cockpit => Make(CockpitId);
        public static ImageMoniker NewSession => Make(NewSessionId);
        public static ImageMoniker Interrupt => Make(InterruptId);
        public static ImageMoniker Sessions => Make(SessionsId);
        public static ImageMoniker Settings => Make(SettingsId);
        public static ImageMoniker Reload => Make(ReloadId);
        public static ImageMoniker Hub => Make(HubId);
        public static ImageMoniker Account => Make(AccountId);
        public static ImageMoniker ApiKey => Make(ApiKeyId);
        public static ImageMoniker Usage => Make(UsageId);
        public static ImageMoniker Utf8Fix => Make(Utf8FixId);
        public static ImageMoniker Folder => Make(FolderId);

        private static ImageMoniker Make(int id) => new ImageMoniker { Guid = Catalog, Id = id };
    }
}
