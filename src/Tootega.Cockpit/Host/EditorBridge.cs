using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Tootega.Cockpit.Util;
// EnvDTE also defines a Task type, so the BCL namespace is aliased rather than the type.
using Tasks = System.Threading.Tasks;

namespace Tootega.Cockpit.Host
{
    /// <summary>
    /// Everything the conversation needs from the IDE: the workspace folder, the open
    /// documents, external links and terminals.
    ///
    /// It is the only class that talks to DTE and the VS shell on a conversation's behalf,
    /// which is what keeps the rest of the host free of IDE dependencies. Every method assumes
    /// the UI thread and says so.
    /// </summary>
    internal sealed class EditorBridge
    {
        private readonly IServiceProvider _services;

        /// <summary>
        /// The resolved workspace folder, cached.
        ///
        /// Resolving it needs DTE and therefore the UI thread, but it is asked for from CLI
        /// reader threads on every turn. Since the folder changes only when a solution opens
        /// or closes, it is resolved on the UI thread and read from anywhere — which is far
        /// better than marshalling a hot path or blocking a reader thread.
        /// </summary>
        private volatile string _workspaceCwd;

        public EditorBridge(IServiceProvider services)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _workspaceCwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private DTE2 Dte()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return _services.GetService(typeof(SDTE)) as DTE2;
        }

        /// <summary>The folder the agent works in. Safe from any thread.</summary>
        public string WorkspaceCwd() => _workspaceCwd;

        /// <summary>
        /// Re-resolves the workspace folder. UI thread only.
        ///
        /// A solution's directory when there is one, an open folder otherwise, and the user
        /// profile as a last resort — the CLI needs a real directory, and refusing to start
        /// because no solution is loaded would be worse than starting somewhere neutral.
        /// </summary>
        public void RefreshWorkspace()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var fullName = Dte()?.Solution?.FullName;

                if (!string.IsNullOrEmpty(fullName))
                {
                    // An open folder reports a directory here rather than a .sln path.
                    if (Directory.Exists(fullName))
                    {
                        _workspaceCwd = fullName;
                        return;
                    }

                    var directory = Path.GetDirectoryName(fullName);
                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    {
                        _workspaceCwd = directory;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("editor: could not resolve the workspace: " + ex.Message);
            }

            _workspaceCwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        /// <summary>
        /// The current content of the file a tool is about to write, so the permission modal
        /// can show a diff rather than only the proposed text.
        ///
        /// The unsaved buffer wins over the file on disk: that is what the user is looking at,
        /// and diffing against a stale file would show changes they already made.
        /// </summary>
        public string CurrentFileText(string tool, JsonElement? input)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var path = FilePathOf(input);
            if (path == null) return null;

            try
            {
                var document = FindDocument(path);
                if (document?.Object("TextDocument") is TextDocument text)
                {
                    return text.StartPoint.CreateEditPoint().GetText(text.EndPoint);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("editor: could not read the open buffer: " + ex.Message);
            }

            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Flushes a dirty buffer before the agent reads or writes that file.
        ///
        /// Without it the agent reads a stale file, or writes over edits the user can still
        /// see on screen — and the conflict surfaces much later, as a confusing diff.
        /// </summary>
        public void AutoSaveForTool(string tool, JsonElement? input)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var path = FilePathOf(input);
            if (path == null) return;

            try
            {
                var document = FindDocument(path);
                if (document == null || document.Saved) return;

                document.Save();
                Log.Debug("editor: saved " + Path.GetFileName(path) + " before " + tool);
            }
            catch (Exception ex)
            {
                // Saving is a courtesy; failing it must not block the tool.
                Log.Debug("editor: autosave failed: " + ex.Message);
            }
        }

        /// <summary>The editor selection as an @file#a-b reference, or null when there is none.</summary>
        public string SelectionReference()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var document = Dte()?.ActiveDocument;
                if (document?.Object("TextDocument") is not TextDocument text) return null;

                var selection = text.Selection;
                if (selection == null) return null;

                var from = selection.TopPoint.Line;
                var to = selection.BottomPoint.Line;
                // A caret with no selection is not a selection: sharing the line under the
                // cursor would attach context the user never asked for.
                if (from == to && selection.IsEmpty) return null;

                var relative = MakeRelative(document.FullName);
                return "@" + relative + "#" + from + "-" + to;
            }
            catch (Exception ex)
            {
                Log.Debug("editor: could not read the selection: " + ex.Message);
                return null;
            }
        }

        public void OpenDocument(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            try
            {
                Dte()?.ItemOperations.OpenFile(path);
            }
            catch (Exception ex)
            {
                Log.Debug("editor: could not open " + path + ": " + ex.Message);
            }
        }

        /// <summary>Opens a URL in the user's browser, never inside the panel.</summary>
        public void OpenExternal(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            // Only web links: a message from the webview must not be able to launch an
            // arbitrary local program.
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Log.Debug("editor: refused to open a non-web link");
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Debug("editor: could not open the link: " + ex.Message);
            }
        }

        /// <summary>Reveals a folder in the OS file manager.</summary>
        public void OpenFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

            try
            {
                System.Diagnostics.Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Debug("editor: could not open the folder: " + ex.Message);
            }
        }

        /// <summary>
        /// Runs a command in a visible console.
        ///
        /// Visible on purpose: these are the interactive flows — signing in, updating the CLI —
        /// where the user has to see a prompt and answer it. Hiding them would leave the
        /// conversation waiting on a question nobody was shown.
        /// </summary>
        public void RunVisible(string command, string title)
        {
            if (string.IsNullOrWhiteSpace(command)) return;

            try
            {
                // `/k` keeps the window open, so a failure's message survives long enough to
                // be read.
                System.Diagnostics.Process.Start(new ProcessStartInfo("cmd.exe", "/k title " + (title ?? "Cockpit") + " && " + command)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Log.Error("editor: could not start '" + command + "'", ex);
            }
        }

        /// <summary>Files matching a fuzzy query, for the composer's @-mention menu.</summary>
        public Tasks.Task<IReadOnlyList<string>> SearchFilesAsync(string query, int limit = 20)
        {
            var root = WorkspaceCwd();

            return Tasks.Task.Run<IReadOnlyList<string>>(() =>
            {
                var matches = new List<string>();
                if (string.IsNullOrWhiteSpace(query)) return matches;

                var needle = query.Trim();

                try
                {
                    foreach (var file in EnumerateFiles(root))
                    {
                        var name = Path.GetFileName(file);
                        if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;

                        matches.Add(MakeRelative(file, root));
                        if (matches.Count >= limit) break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("editor: file search failed: " + ex.Message);
                }

                return matches;
            });
        }

        /// <summary>
        /// Walks the workspace, skipping the directories that would dominate the results and
        /// cost the most to enumerate.
        /// </summary>
        private static IEnumerable<string> EnumerateFiles(string root)
        {
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", "node_modules", "bin", "obj", ".vs", "packages", "dist", ".next",
            };

            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var directory = pending.Pop();

                string[] entries;
                try
                {
                    entries = Directory.GetFiles(directory);
                }
                catch
                {
                    continue;
                }

                foreach (var file in entries) yield return file;

                string[] children;
                try
                {
                    children = Directory.GetDirectories(directory);
                }
                catch
                {
                    continue;
                }

                foreach (var child in children)
                {
                    if (skip.Contains(Path.GetFileName(child))) continue;
                    pending.Push(child);
                }
            }
        }

        /// <summary>
        /// A path as the composer should show it: relative to the workspace when it is inside,
        /// absolute otherwise. Quoted only when it contains a space, since the quotes would
        /// otherwise be noise in the prompt.
        /// </summary>
        public string QuoteResolved(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return string.Empty;

            var path = MakeRelative(absolutePath);
            return path.IndexOf(' ') >= 0 ? "\"" + path + "\"" : path;
        }

        public string MakeRelative(string absolutePath, string root = null)
        {
            if (string.IsNullOrEmpty(absolutePath)) return absolutePath;

            try
            {
                root = root ?? WorkspaceCwd();
                if (string.IsNullOrEmpty(root)) return absolutePath;

                var rooted = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!absolutePath.StartsWith(rooted, StringComparison.OrdinalIgnoreCase)) return absolutePath;

                return absolutePath.Substring(rooted.Length).Replace('\\', '/');
            }
            catch
            {
                return absolutePath;
            }
        }

        private Document FindDocument(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var documents = Dte()?.Documents;
            if (documents == null) return null;

            foreach (Document document in documents)
            {
                try
                {
                    if (string.Equals(document.FullName, path, StringComparison.OrdinalIgnoreCase)) return document;
                }
                catch
                {
                    // A document can vanish mid-enumeration; the next one may still match.
                }
            }

            return null;
        }

        /// <summary>The file a tool input refers to, under any of the names the tools use.</summary>
        internal static string FilePathOf(JsonElement? input)
        {
            if (input?.ValueKind != JsonValueKind.Object) return null;

            foreach (var key in new[] { "file_path", "filePath", "path", "notebook_path" })
            {
                if (!input.Value.TryGetProperty(key, out var value)) continue;
                if (value.ValueKind != JsonValueKind.String) continue;

                var path = value.GetString();
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }

            return null;
        }
    }
}
