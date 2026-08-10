using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Host
{
    /// <summary>
    /// Opens a proposed edit in Visual Studio's own diff window.
    ///
    /// The panel already renders a diff, but it is a compact one inside a chat bubble. When a
    /// change is large enough to actually review, the editor's diff — with syntax colouring,
    /// navigation and the user's own settings — is the right tool, and reviewing a change
    /// properly is exactly what the permission prompt is asking the user to do.
    ///
    /// The proposed side is written to a temp file rather than provided virtually: VS wants two
    /// readable paths, and a temp file it can open is far less machinery than a custom document
    /// provider registered into the shell.
    /// </summary>
    internal sealed class DiffLauncher
    {
        private readonly List<string> _temporary = new List<string>();

        /// <summary>
        /// Shows the diff of what a tool is about to write.
        ///
        /// Silent when the input does not describe a file edit: this is reached from a button on
        /// a tool card, and the cards for Bash or a search have nothing to diff.
        /// </summary>
        public void Open(string cwd, string tool, JsonElement? input)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var relative = EditorBridge.FilePathOf(input);
            if (relative == null) return;

            var path = Path.IsPathRooted(relative) ? relative : Path.Combine(cwd ?? ".", relative);

            var before = ReadOrEmpty(path);
            var after = Apply(tool, before, input);

            // Nothing to look at — and an empty diff window would read as a bug.
            if (after == null || after == before) return;

            try
            {
                var proposed = WriteTemporary(Path.GetFileName(path), after);

                // A file that does not exist yet still needs a left side, or the diff cannot open.
                var original = File.Exists(path) ? path : WriteTemporary("original-" + Path.GetFileName(path), before);

                var difference = Package.GetGlobalService(typeof(SVsDifferenceService)) as IVsDifferenceService;
                if (difference == null)
                {
                    Log.Debug("diff: the difference service is unavailable");
                    return;
                }

                difference.OpenComparisonWindow2(
                    original, proposed,
                    Path.GetFileName(path) + " — proposed (" + tool + ")",
                    path,
                    "Current",
                    "Proposed (" + tool + ")",
                    null,
                    null,
                    // Marked temporary so the proposed side is not offered back as a document the
                    // user opened: it is a preview of something that has not happened yet.
                    (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary);
            }
            catch (Exception ex)
            {
                Log.Error("diff: could not open the comparison", ex);
            }
        }

        /// <summary>
        /// The file as it would look after the tool ran.
        ///
        /// Only the edit tools are reproduced, and each exactly as the CLI applies it — a
        /// preview that diverges from what actually happens is worse than no preview.
        /// </summary>
        internal static string Apply(string tool, string before, JsonElement? input)
        {
            if (input?.ValueKind != JsonValueKind.Object) return null;
            var element = input.Value;

            switch (tool)
            {
                case "Write":
                    return Text(element, "content");

                case "Edit":
                {
                    var oldString = Text(element, "old_string");
                    if (string.IsNullOrEmpty(oldString)) return null;
                    return before.Replace(oldString, Text(element, "new_string") ?? string.Empty);
                }

                case "MultiEdit":
                {
                    if (!element.TryGetProperty("edits", out var edits) ||
                        edits.ValueKind != JsonValueKind.Array) return null;

                    var text = before;

                    foreach (var edit in edits.EnumerateArray())
                    {
                        if (edit.ValueKind != JsonValueKind.Object) continue;

                        var oldString = Text(edit, "old_string");
                        if (string.IsNullOrEmpty(oldString)) continue;

                        text = text.Replace(oldString, Text(edit, "new_string") ?? string.Empty);
                    }

                    return text;
                }

                default:
                    return null;
            }
        }

        private static string Text(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static string ReadOrEmpty(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch (Exception ex)
            {
                Log.Debug("diff: could not read " + path + ": " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>
        /// Writes one side of the diff to a temp file, keeping the original extension so the
        /// editor colours it the way the user expects.
        /// </summary>
        private string WriteTemporary(string name, string content)
        {
            var folder = Path.Combine(Path.GetTempPath(), "tootega-cockpit-diff");
            Directory.CreateDirectory(folder);

            var path = Path.Combine(folder, Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + name);
            File.WriteAllText(path, content ?? string.Empty, new UTF8Encoding(false));

            _temporary.Add(path);
            return path;
        }

        /// <summary>Removes the temp files this launcher created. Best effort.</summary>
        public void Cleanup()
        {
            foreach (var path in _temporary)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Still open in a diff window, most likely; the temp folder gets it later.
                }
            }

            _temporary.Clear();
        }
    }
}
