using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.UI
{
    /// <summary>
    /// Turns files dropped on the composer into paths for the prompt. Port of
    /// src/cli/DroppedFiles.ts, with the step the base cannot take.
    ///
    /// The page never sees a dropped file's path. WebView2 does: the shim hands the dropped
    /// <c>File</c> objects over with <c>postMessageWithAdditionalObjects</c>, and they arrive
    /// here as <c>CoreWebView2File</c> with the real path — so a drop from the file manager
    /// inserts exactly what pasting the same files would. Only when that is missing (a drag
    /// from a browser) is the content written to a temp folder and the copy pointed at.
    /// </summary>
    internal static class DroppedFiles
    {
        public static IReadOnlyList<string> Resolve(DropFilesPayload payload) =>
            Resolve(payload, Path.Combine(Path.GetTempPath(), "cockpit-drops"));

        public static IReadOnlyList<string> Resolve(DropFilesPayload payload, string root)
        {
            var real = (payload?.Paths ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim().Normalize(NormalizationForm.FormC))
                .ToList();
            if (real.Count > 0) return real;

            try
            {
                return Save(payload?.Files ?? new List<DroppedFilePayload>(), root);
            }
            catch (Exception ex)
            {
                Log.Debug("drop: could not save the dropped files: " + ex.Message);
                return Array.Empty<string>();
            }
        }

        private static List<string> Save(List<DroppedFilePayload> files, string root)
        {
            var dir = Path.Combine(root, DateTime.UtcNow.Ticks + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var saved = new List<string>();
            foreach (var file in files)
            {
                if (string.IsNullOrEmpty(file?.Data)) continue; // too large to send: nothing to copy

                var name = SafeName(file.Name);
                var target = Path.Combine(dir, name);
                // Two files of the same name in one drop: the second gets a suffix.
                for (var i = 2; saved.Contains(target, StringComparer.OrdinalIgnoreCase); i++)
                {
                    target = Path.Combine(dir,
                        Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
                }

                Directory.CreateDirectory(dir);
                File.WriteAllBytes(target, Convert.FromBase64String(file.Data));
                saved.Add(target);
            }
            return saved;
        }

        private static string SafeName(string name)
        {
            // The name came from the page: only its last segment is ours to use.
            var last = (name ?? string.Empty).Replace('\\', '/');
            last = last.Substring(last.LastIndexOf('/') + 1);
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(last.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return clean.Length == 0 || clean == "." || clean == ".." ? "dropped" : clean;
        }
    }
}
