using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
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
        /// <summary>The buffer the shell's folder browser writes into.</summary>
        private const uint MaxPath = 260;

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

        public void OpenDocument(string path, int? line = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            try
            {
                var window = Dte()?.ItemOperations.OpenFile(path);
                if (line.HasValue && window?.Document?.Selection is TextSelection selection)
                {
                    // GotoLine is 1-based, like the #L anchor; clamp to at least 1.
                    selection.GotoLine(Math.Max(1, line.Value), false);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("editor: could not open " + path + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Opens a link the chat produced: a web URL goes to the browser; anything else is a
        /// file reference from a tool card (Read/Edit/Write) and opens in the editor.
        ///
        /// This is the counterpart of the base extension's <c>openLink</c>: the href is a
        /// workspace-relative path, an absolute path, or a bare file name, optionally suffixed
        /// with a <c>#L12</c> line anchor. A relative path is resolved against the tab's cwd;
        /// when it does not exist there, the file name is searched across the workspace. Without
        /// this, clicking a file on the timeline did nothing — OpenExternal refuses non-web hrefs.
        /// </summary>
        public void OpenLink(string href, string cwd = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(href)) return;

            if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                OpenExternal(href);
                return;
            }

            var raw = href;

            // Optional line anchor (#L12), matching the base extension.
            int? line = null;
            var anchor = System.Text.RegularExpressions.Regex.Match(raw, @"#L(\d+)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (anchor.Success)
            {
                if (int.TryParse(anchor.Groups[1].Value, out var parsed)) line = parsed;
                raw = raw.Substring(0, raw.Length - anchor.Length);
            }

            // Strip a file:// scheme and surrounding quotes, then decode percent-escapes.
            raw = System.Text.RegularExpressions.Regex.Replace(raw, @"^file://", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            raw = raw.Trim('"', '\'');
            try { raw = Uri.UnescapeDataString(raw); } catch { /* already decoded */ }
            raw = raw.Normalize(System.Text.NormalizationForm.FormC);

            var root = string.IsNullOrEmpty(cwd) ? WorkspaceCwd() : cwd;

            string abs;
            try
            {
                abs = Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(root, raw));
            }
            catch (Exception ex)
            {
                Log.Debug("editor: bad file link '" + href + "': " + ex.Message);
                return;
            }

            if (!File.Exists(abs))
            {
                // Fallback: find the bare file name anywhere in the workspace (first match).
                var found = FindByName(Path.GetFileName(raw), root);
                if (found == null)
                {
                    Log.Debug("editor: file not found for link '" + href + "'");
                    return;
                }
                abs = found;
            }

            OpenDocument(abs, line);
        }

        /// <summary>First file with this name under the workspace, or null. UI thread not required.</summary>
        private static string FindByName(string name, string root)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(root)) return null;

            try
            {
                foreach (var file in EnumerateFiles(root))
                {
                    if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase)) return file;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("editor: workspace search for '" + name + "' failed: " + ex.Message);
            }

            return null;
        }

        /// <summary>
        /// Writes a plan-mode plan to <c>Planing/&lt;timestamp&gt;-&lt;slug&gt;.md</c> at the repo
        /// root and opens it in the editor. Returns the workspace-relative path, or null when
        /// nothing could be written. Port of ChatViewProvider.savePlanFile.
        ///
        /// The file is the plan's primary surface now — the permission card keeps only the
        /// approval gate. The name carries a sortable timestamp plus a short slug from the first
        /// heading, so the folder reads as a history rather than one overwritten file.
        /// </summary>
        public string SavePlanFile(string plan, string cwd = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(plan)) return null;

            var root = string.IsNullOrEmpty(cwd) ? WorkspaceCwd() : cwd;
            if (string.IsNullOrEmpty(root)) return null;

            try
            {
                var dir = Path.Combine(root, "Planing");
                Directory.CreateDirectory(dir);

                var now = DateTime.Now;
                var stamp = now.ToString("yyyy-MM-dd-HHmm");
                var slug = PlanSlug(plan);
                var name = string.IsNullOrEmpty(slug) ? stamp + ".md" : stamp + "-" + slug + ".md";
                var abs = Path.Combine(dir, name);

                File.WriteAllText(abs, plan.EndsWith("\n") ? plan : plan + "\n");

                OpenDocument(abs);

                return MakeRelative(abs, root);
            }
            catch (Exception ex)
            {
                Log.Debug("editor: could not save the plan file: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// A short filename slug from a plan's first meaningful line — the first heading, else the
        /// first non-empty line. Lowercased ASCII words joined by '-', capped. Empty when the plan
        /// has no usable text (the caller then names by timestamp only).
        /// </summary>
        private static string PlanSlug(string plan)
        {
            var lines = plan.Split('\n');
            string first = null;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var heading = System.Text.RegularExpressions.Regex.Match(line, @"^#{1,6}\s+(.*)$");
                if (heading.Success) { first = heading.Groups[1].Value; break; }
                if (first == null) first = line; // remember the first non-empty; keep scanning for a heading
            }
            if (string.IsNullOrEmpty(first)) return string.Empty;

            var ascii = first.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();
            foreach (var ch in ascii)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark)
                    continue; // strip diacritics
                var lower = char.ToLowerInvariant(ch);
                sb.Append(lower >= 'a' && lower <= 'z' || lower >= '0' && lower <= '9' ? lower : '-');
            }

            var slug = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
            return slug.Length > 50 ? slug.Substring(0, 50).Trim('-') : slug;
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

        /// <summary>
        /// Asks the user for a folder, using the shell's own browser.
        ///
        /// Returns null when they cancel — which is a decision, not a failure, and must leave
        /// the tab exactly where it was.
        /// </summary>
        public string PickFolder(string initial, string title)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var shell = _services.GetService(typeof(SVsUIShell)) as IVsUIShell;
            if (shell == null) return null;

            var buffer = Marshal.AllocCoTaskMem((int)MaxPath * sizeof(char));

            try
            {
                var browse = new VSBROWSEINFOW[1];
                browse[0].lStructSize = (uint)Marshal.SizeOf(typeof(VSBROWSEINFOW));
                browse[0].pwzDlgTitle = title;
                browse[0].pwzInitialDir = Directory.Exists(initial) ? initial : null;
                browse[0].pwzDirName = buffer;
                browse[0].nMaxDirName = MaxPath;

                var hr = shell.GetDirectoryViaBrowseDlg(browse);

                // Cancelling reports this rather than a folder.
                if (hr == VSConstants.OLE_E_PROMPTSAVECANCELLED) return null;
                if (Microsoft.VisualStudio.ErrorHandler.Failed(hr)) return null;

                var picked = Marshal.PtrToStringUni(browse[0].pwzDirName);
                return string.IsNullOrWhiteSpace(picked) ? null : picked;
            }
            catch (Exception ex)
            {
                Log.Error("editor: the folder browser failed", ex);
                return null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }

        /// <summary>
        /// Asks the user where to save a file, using the shell's own dialog.
        ///
        /// Returns null when they cancel.
        /// </summary>
        /// <param name="filter">
        /// In the Win32 form the shell expects — label, NUL, pattern, NUL, and a final NUL.
        /// </param>
        public string PickSaveFile(string initialDirectory, string defaultName, string filter, string title)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var shell = _services.GetService(typeof(SVsUIShell)) as IVsUIShell;
            if (shell == null) return null;

            // Pre-filled with the suggested name, which is how the dialog reports it back too.
            var buffer = Marshal.StringToCoTaskMemUni((defaultName ?? string.Empty).PadRight((int)MaxPath, '\0'));

            try
            {
                var save = new VSSAVEFILENAMEW[1];
                save[0].lStructSize = (uint)Marshal.SizeOf(typeof(VSSAVEFILENAMEW));
                save[0].pwzDlgTitle = title;
                save[0].pwzFileName = buffer;
                save[0].nMaxFileName = MaxPath;
                save[0].pwzInitialDir = Directory.Exists(initialDirectory) ? initialDirectory : null;
                save[0].pwzFilter = filter;

                var hr = shell.GetSaveFileNameViaDlg(save);

                if (hr == VSConstants.OLE_E_PROMPTSAVECANCELLED) return null;
                if (Microsoft.VisualStudio.ErrorHandler.Failed(hr)) return null;

                var picked = Marshal.PtrToStringUni(save[0].pwzFileName);
                return string.IsNullOrWhiteSpace(picked) ? null : picked;
            }
            catch (Exception ex)
            {
                Log.Error("editor: the save dialog failed", ex);
                return null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }

        /// <summary>
        /// The folders worth offering as one click: the solution's, and each project's.
        ///
        /// Most of the time the folder a user wants for a second tab is a project inside the
        /// solution they already have open, and browsing for it is needless friction.
        /// </summary>
        public IReadOnlyList<string> SuggestedFolders()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var folders = new List<string>();

            void Add(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;

                try
                {
                    if (!Directory.Exists(path)) return;
                    var full = Path.GetFullPath(path);
                    if (!folders.Contains(full, StringComparer.OrdinalIgnoreCase)) folders.Add(full);
                }
                catch
                {
                    // A project can report a path this machine cannot resolve; skip it.
                }
            }

            Add(WorkspaceCwd());

            try
            {
                var solution = Dte()?.Solution;
                if (solution == null) return folders;

                foreach (Project project in solution.Projects)
                {
                    try
                    {
                        // Solution folders have no file of their own.
                        if (string.IsNullOrEmpty(project.FullName)) continue;
                        Add(Path.GetDirectoryName(project.FullName));
                    }
                    catch
                    {
                        // Unloaded and virtual projects throw here; the rest still count.
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("editor: could not enumerate the projects: " + ex.Message);
            }

            return folders;
        }

        /// <summary>Files matching a fuzzy query, for the composer's @-mention menu.</summary>
        /// <param name="root">The folder to search — the tab's, not the window's.</param>
        public Tasks.Task<IReadOnlyList<string>> SearchFilesAsync(string query, string root, int limit = 20)
        {
            if (string.IsNullOrEmpty(root)) root = WorkspaceCwd();

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
        public string QuoteResolved(string absolutePath, string root = null)
        {
            if (string.IsNullOrEmpty(absolutePath)) return string.Empty;

            var path = MakeRelative(absolutePath, root);
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
