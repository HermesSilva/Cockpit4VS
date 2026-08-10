using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Host
{
    /// <summary>
    /// Writes a conversation out as a document, and saves pasted images.
    ///
    /// Two modes, and the difference is worth being explicit about: 'direct' writes the
    /// mechanical transcript the webview already assembled, and costs nothing; 'ai' has the
    /// model rewrite it into a readable document, and spends subscription tokens. The second
    /// runs in a separate one-shot process so the conversation's own context is untouched.
    /// </summary>
    internal sealed class ConversationExporter
    {
        /// <summary>
        /// The instruction for the AI mode.
        ///
        /// It asks for the reasoning and the decisions and explicitly rejects the technical
        /// noise, because a document that reproduces every command and diff is just the
        /// transcript again — which the direct mode already provides for free.
        /// </summary>
        private const string DocumentPrompt =
            "You are a technical editor. From the conversation record below (between a developer and an AI " +
            "assistant), write a DOCUMENT in Markdown — organized, high level and coherent — telling the story " +
            "of the work: what was asked, what was thought and decided, what was done, WHY and HOW, and the " +
            "final outcome. Prioritize the reasoning, the decisions and the motivation. OMIT technical noise " +
            "(commands, tool output, raw diffs). Structure it with headings, sections and lists when that helps " +
            "reading. Be faithful to the content — do not invent. Write in the SAME language that predominates " +
            "in the conversation. Answer ONLY with the document Markdown — no comments, no code fence around " +
            "the whole thing.";

        /// <summary>Generous: a long conversation rewritten by a large model is not quick.</summary>
        private const int GenerateTimeoutMs = 180_000;

        private readonly EditorBridge _editor;
        private readonly Func<string> _claudePath;

        public ConversationExporter(EditorBridge editor, Func<string> claudePath)
        {
            _editor = editor ?? throw new ArgumentNullException(nameof(editor));
            _claudePath = claudePath ?? throw new ArgumentNullException(nameof(claudePath));
        }

        /// <summary>
        /// Exports the conversation into the folder it runs in and opens the result.
        /// </summary>
        /// <param name="mode">direct writes the transcript; ai rewrites it, spending tokens.</param>
        public async Task ExportAsync(string cwd, string markdown, string fileName, string mode,
                                      string model, string effort)
        {
            try
            {
                var content = markdown;

                if (string.Equals(mode, "ai", StringComparison.Ordinal))
                {
                    var generated = await GenerateAsync(cwd, markdown, model, effort);
                    if (string.IsNullOrWhiteSpace(generated))
                    {
                        Log.Info("Could not generate the document with AI. The conversation was not exported.");
                        return;
                    }

                    content = generated;
                }

                var target = UniquePath(Path.Combine(cwd, SafeName(fileName)));
                File.WriteAllText(target, content, new UTF8Encoding(false));

                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _editor.OpenDocument(target);

                Log.Info("Conversation exported to " + target);
            }
            catch (Exception ex)
            {
                Log.Error("export: could not write the document", ex);
            }
        }

        /// <summary>
        /// Rewrites the transcript through a one-shot CLI process, with the tab's own model and
        /// effort so the document matches the quality the user is paying for.
        ///
        /// The prompt goes through stdin, never argv: a long conversation would blow the
        /// Windows command-line limit, and the failure would look like the CLI misbehaving.
        /// </summary>
        private async Task<string> GenerateAsync(string cwd, string sourceMarkdown, string model, string effort)
        {
            var args = new System.Collections.Generic.List<string> { "-p", "--output-format", "json" };

            if (!string.IsNullOrEmpty(model) && model != ModelCatalog.DefaultModelId)
            {
                args.Add("--model");
                args.Add(model);
            }

            if (!string.IsNullOrEmpty(effort) && effort != ModelCatalog.DefaultModelId)
            {
                args.Add("--effort");
                args.Add(effort);
            }

            var prompt = DocumentPrompt + "\n\n--- CONVERSATION RECORD ---\n\n" + sourceMarkdown;

            try
            {
                var info = ProcessLauncher.Build(_claudePath(), args, cwd);
                info.RedirectStandardInput = true;

                using (var process = Process.Start(info))
                {
                    if (process == null) return null;

                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();

                    try
                    {
                        await process.StandardInput.WriteAsync(prompt);
                        process.StandardInput.Close();
                    }
                    catch (IOException)
                    {
                        // The pipe closed early; whatever the process says is still read below.
                    }

                    var reading = Task.WhenAll(stdout, stderr);
                    if (await Task.WhenAny(reading, Task.Delay(GenerateTimeoutMs)) != reading)
                    {
                        ProcessLauncher.KillTree(process);
                        Log.Debug("export: the AI generation timed out");
                        return null;
                    }

                    process.WaitForExit(2000);
                    return ExtractResult(await stdout);
                }
            }
            catch (Exception ex)
            {
                Log.Error("export: the AI generation failed", ex);
                return null;
            }
        }

        /// <summary>
        /// The document out of `claude -p --output-format json`.
        ///
        /// Tolerant by design: the wrapper shape has changed between CLI versions, and falling
        /// back to the raw text is better than discarding a document that is right there.
        /// </summary>
        internal static string ExtractResult(string output)
        {
            var trimmed = (output ?? string.Empty).Trim();
            if (trimmed.Length == 0) return null;

            try
            {
                using (var document = JsonDocument.Parse(trimmed))
                {
                    var root = document.RootElement;

                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in root.EnumerateArray())
                        {
                            var text = ResultOf(item);
                            if (text != null) return text;
                        }
                    }
                    else
                    {
                        var text = ResultOf(root);
                        if (text != null) return text;
                    }
                }
            }
            catch (JsonException)
            {
                // Not JSON: the CLI printed the document plainly.
                return StripFence(trimmed);
            }

            return null;
        }

        private static string ResultOf(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            if (!element.TryGetProperty("result", out var result)) return null;
            if (result.ValueKind != JsonValueKind.String) return null;

            var text = result.GetString()?.Trim();
            return string.IsNullOrEmpty(text) ? null : StripFence(text);
        }

        /// <summary>
        /// Removes a fence wrapping the WHOLE document.
        ///
        /// Models add one despite being asked not to, and a document that starts with ```markdown
        /// renders as one big code block — the fences inside it must survive, though.
        /// </summary>
        internal static string StripFence(string text)
        {
            var value = (text ?? string.Empty).Trim();
            if (!value.StartsWith("```", StringComparison.Ordinal)) return value;

            var firstBreak = value.IndexOf('\n');
            if (firstBreak < 0) return value;

            var lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence <= firstBreak) return value;

            return value.Substring(firstBreak + 1, lastFence - firstBreak - 1).Trim();
        }

        /// <summary>A name that cannot escape the folder or carry characters the file system rejects.</summary>
        private static string SafeName(string fileName)
        {
            var name = string.IsNullOrWhiteSpace(fileName) ? "conversation.md" : Path.GetFileName(fileName.Trim());

            foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '-');

            return string.IsNullOrWhiteSpace(name) ? "conversation.md" : name;
        }

        /// <summary>
        /// A path that does not exist yet, by inserting -2, -3… before the extension.
        ///
        /// Exporting twice must never overwrite the first document: the user would lose it
        /// without being asked.
        /// </summary>
        internal static string UniquePath(string full)
        {
            if (!Exists(full)) return full;

            var directory = Path.GetDirectoryName(full) ?? string.Empty;
            var extension = Path.GetExtension(full);
            var stem = Path.GetFileNameWithoutExtension(full);

            for (var index = 2; index < 1000; index++)
            {
                var candidate = Path.Combine(directory, stem + "-" + index + extension);
                if (!Exists(candidate)) return candidate;
            }

            return full;
        }

        private static bool Exists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        // ---- Images ----

        /// <summary>
        /// Saves a pasted image, through the shell's save dialog.
        ///
        /// The user picks the location: an image dropped silently into the project root would be
        /// a file they did not ask for, in a folder that is probably under version control.
        /// </summary>
        public void SaveImage(string cwd, string mediaType, string base64)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var extension = ExtensionFor(mediaType);
            var suggested = "image-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "." + extension;

            var target = _editor.PickSaveFile(
                cwd, suggested,
                "Images\0*." + extension + "\0All files\0*.*\0\0",
                "Save image");

            if (target == null) return;

            try
            {
                File.WriteAllBytes(target, Convert.FromBase64String(base64 ?? string.Empty));
                Log.Info("Image saved to " + target);
            }
            catch (Exception ex)
            {
                Log.Error("Could not save the image", ex);
            }
        }

        private static string ExtensionFor(string mediaType)
        {
            var parts = (mediaType ?? string.Empty).Split('/');
            var subtype = parts.Length > 1 ? parts[1] : "png";

            subtype = subtype.Replace("+xml", string.Empty);
            if (subtype == "jpeg") subtype = "jpg";

            return string.IsNullOrWhiteSpace(subtype) ? "png" : subtype;
        }
    }
}
