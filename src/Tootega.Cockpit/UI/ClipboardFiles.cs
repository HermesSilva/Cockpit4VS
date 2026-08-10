using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.UI
{
    /// <summary>
    /// Reads the paths of files copied in the OS file manager. Port of
    /// src/cli/ClipboardFiles.ts.
    ///
    /// The webview sandbox does not expose them — a pasted file arrives with no path — so the
    /// host has to read the clipboard itself. On Windows that is a direct WPF call rather than
    /// the PowerShell round-trip the Node port needed, which also removes the code-page problem
    /// the original had to work around.
    /// </summary>
    internal static class ClipboardFiles
    {
        /// <summary>
        /// Absolute paths of the files on the clipboard, or empty when there are none.
        ///
        /// Must be called on the UI thread: the WPF clipboard is STA-only.
        /// </summary>
        public static IReadOnlyList<string> Read()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (!Clipboard.ContainsFileDropList()) return Array.Empty<string>();

                return Clipboard.GetFileDropList()
                    .Cast<string>()
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    // Normalized so an accented name compares equal to the same name typed
                    // elsewhere — the file manager and the editor do not always agree on form.
                    .Select(p => p.Trim().Normalize(NormalizationForm.FormC))
                    .ToList();
            }
            catch (Exception ex)
            {
                // The clipboard can be locked by another process; there is nothing to do but
                // report no files.
                Log.Debug("could not read clipboard files: " + ex.Message);
                return Array.Empty<string>();
            }
        }
    }
}
