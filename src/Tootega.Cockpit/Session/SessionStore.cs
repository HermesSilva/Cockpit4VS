using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Session
{
    /// <summary>
    /// Reads the sessions Claude Code persists as
    /// ~/.claude/projects/&lt;encoded-cwd&gt;/&lt;id&gt;.jsonl. Port of src/session/SessionStore.ts.
    ///
    /// This is the CLI's own storage, not ours. We only read it — and, for rewind and
    /// delete, cut it. Nothing here invents a format: the folder naming, the line shapes and
    /// the ai-title record all belong to the CLI, so every field is treated as optional.
    ///
    /// The projects root is injectable so the parsing can be tested against fixtures instead
    /// of against the developer's real conversations.
    /// </summary>
    internal sealed class SessionStore
    {
        /// <summary>Zero-width characters and the BOM, which turn up in pasted prompts.</summary>
        private static readonly Regex Invisible = new Regex(@"[​-‍﻿]", RegexOptions.Compiled);
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        /// <summary>First sentence end or line break — where a fallback title is cut.</summary>
        private static readonly Regex SentenceEnd = new Regex(@"(?<=[.!?])\s|\n", RegexOptions.Compiled);
        private static readonly Regex TrailingWord = new Regex(@"\s+\S*$", RegexOptions.Compiled);

        private readonly string _projectsRoot;
        private int _generatedIdSequence;

        public SessionStore(string projectsRoot = null)
        {
            _projectsRoot = projectsRoot ?? ClaudeHome.ProjectsDir;
        }

        /// <summary>Encodes the cwd the way Claude Code names its folders: ':' '\' '/' become '-'.</summary>
        public static string EncodeCwd(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return string.Empty;
            var sb = new StringBuilder(cwd.Length);
            foreach (var c in cwd) sb.Append(c == ':' || c == '\\' || c == '/' ? '-' : c);
            return sb.ToString();
        }

        public string ProjectDirectory(string cwd) => Path.Combine(_projectsRoot, EncodeCwd(cwd));

        private string TranscriptPath(string cwd, string sessionId) =>
            Path.Combine(ProjectDirectory(cwd), sessionId + ".jsonl");

        private IReadOnlyList<string> TranscriptFiles(string cwd)
        {
            try
            {
                var dir = ProjectDirectory(cwd);
                if (!Directory.Exists(dir)) return Array.Empty<string>();
                return Directory.GetFiles(dir, "*.jsonl");
            }
            catch
            {
                // A folder that does not exist yet simply has no sessions.
                return Array.Empty<string>();
            }
        }

        /// <summary>Lists the cwd's sessions, most recent first.</summary>
        public IReadOnlyList<SessionInfo> ListSessions(string cwd, int limit = 50)
        {
            var sessions = new List<SessionInfo>();

            foreach (var file in TranscriptFiles(cwd))
            {
                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (!info.Exists) continue;
                }
                catch
                {
                    continue;
                }

                var summary = Summarize(file);
                sessions.Add(new SessionInfo
                {
                    Id = Path.GetFileNameWithoutExtension(file),
                    Title = summary.Title,
                    UpdatedAt = info.LastWriteTimeUtc.ToString("o"),
                    MessageCount = summary.Count,
                    CreatedAt = SafeCreationTime(info),
                    SizeBytes = info.Length,
                    UserCount = summary.UserCount,
                    AssistantCount = summary.AssistantCount,
                    ToolCount = summary.ToolCount,
                    Model = summary.Model,
                });
            }

            return sessions
                .OrderByDescending(s => s.UpdatedAt, StringComparer.Ordinal)
                .Take(limit)
                .ToList();
        }

        /// <summary>Id of the most recent session by mtime, without reading any content.</summary>
        public string LatestSessionId(string cwd)
        {
            string best = null;
            var bestTime = DateTime.MinValue;

            foreach (var file in TranscriptFiles(cwd))
            {
                try
                {
                    var written = File.GetLastWriteTimeUtc(file);
                    if (best != null && written <= bestTime) continue;
                    best = Path.GetFileNameWithoutExtension(file);
                    bestTime = written;
                }
                catch
                {
                    // Skipped; another file may still answer.
                }
            }

            return best;
        }

        /// <summary>Deletes a session's transcript. Irreversible.</summary>
        public bool DeleteSession(string cwd, string sessionId)
        {
            try
            {
                var path = TranscriptPath(cwd, sessionId);
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("could not delete session " + sessionId + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Deletes every transcript in the cwd and reports how many went. Irreversible.</summary>
        public int DeleteAllSessions(string cwd)
        {
            var removed = 0;
            foreach (var file in TranscriptFiles(cwd))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // A locked or already-removed file is not a failure of the whole operation.
                }
            }
            return removed;
        }

        /// <summary>
        /// Rewinds the transcript: keeps only the lines BEFORE the one whose uuid matches,
        /// dropping the target prompt and everything after it.
        ///
        /// Written atomically (temp file plus replace) because a half-written transcript is
        /// worse than no rewind at all — the CLI would resume from a truncated line.
        /// Irreversible.
        /// </summary>
        public bool TruncateTranscriptAt(string cwd, string sessionId, string uuid)
        {
            var path = TranscriptPath(cwd, sessionId);
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch
            {
                return false;
            }

            var cut = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                if (ReadUuid(lines[i]) == uuid)
                {
                    cut = i;
                    break;
                }
            }

            if (cut < 0) return false;

            var kept = string.Join("\n", lines.Take(cut)).TrimEnd('\n') + "\n";
            try
            {
                var temp = path + ".tmp";
                File.WriteAllText(temp, kept, new UTF8Encoding(false));
                // Delete-then-move rather than File.Replace: the transcript may have no
                // backup target and Replace fails across volumes.
                File.Delete(path);
                File.Move(temp, path);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("rewind failed for session " + sessionId, ex);
                return false;
            }
        }

        private static string ReadUuid(string line)
        {
            try
            {
                using (var document = JsonDocument.Parse(line))
                {
                    return document.RootElement.TryGetProperty("uuid", out var uuid) &&
                           uuid.ValueKind == JsonValueKind.String
                        ? uuid.GetString()
                        : null;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // --- Summary ---

        private sealed class Summary
        {
            public string Title = string.Empty;
            public int Count;
            public int UserCount;
            public int AssistantCount;
            public int ToolCount;
            public string Model;
        }

        /// <summary>
        /// Title and counts.
        ///
        /// Prefers the CLI's own generated title (`ai-title`, latest wins) — the same one the
        /// /resume picker shows. Falls back to the first user message only when the session
        /// is too short to have earned a title.
        /// </summary>
        private Summary Summarize(string file)
        {
            var summary = new Summary();
            var aiTitle = string.Empty;
            var firstUser = string.Empty;

            foreach (var line in ReadLines(file))
            {
                JsonElement root;
                try
                {
                    using (var document = JsonDocument.Parse(line))
                    {
                        root = document.RootElement.Clone();
                    }
                }
                catch (JsonException)
                {
                    continue;
                }

                var type = ReadString(root, "type");

                if (type == "ai-title")
                {
                    var candidate = Clean(ReadString(root, "aiTitle"));
                    if (!string.IsNullOrEmpty(candidate)) aiTitle = candidate;
                    continue;
                }

                var isMeta = root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True;

                if (type == "user")
                {
                    if (isMeta) continue;
                    summary.Count++;
                    summary.UserCount++;

                    if (string.IsNullOrEmpty(firstUser))
                    {
                        var text = TextOfContent(MessageContent(root));
                        if (!IsMetaUserText(text)) firstUser = Clean(text);
                    }
                }
                else if (type == "assistant")
                {
                    if (!isMeta) summary.Count++;
                    summary.AssistantCount++;

                    if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                    {
                        var model = ReadString(message, "model");
                        if (model != null) summary.Model = model;

                        if (message.TryGetProperty("content", out var content) &&
                            content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var block in content.EnumerateArray())
                            {
                                if (ReadString(block, "type") == "tool_use") summary.ToolCount++;
                            }
                        }
                    }
                }
                else if (type == "system" && ReadString(root, "subtype") == "init")
                {
                    var model = ReadString(root, "model");
                    if (model != null) summary.Model = model;
                }
            }

            // A raw prompt can be a whole paragraph and does not work as a label, so the
            // fallback is truncated.
            summary.Title = !string.IsNullOrEmpty(aiTitle) ? aiTitle : TruncateTitle(firstUser);
            return summary;
        }

        /// <summary>Cuts at the first sentence end or line break, then caps the length.</summary>
        public static string TruncateTitle(string text, int max = 60)
        {
            var cleaned = Clean(text);
            if (string.IsNullOrEmpty(cleaned)) return string.Empty;

            var head = SentenceEnd.Split(cleaned).FirstOrDefault() ?? cleaned;
            var basis = head.Length <= max ? head : cleaned;
            if (basis.Length <= max) return basis;

            // Cut on a word boundary: half a word plus an ellipsis reads as corruption.
            return TrailingWord.Replace(basis.Substring(0, max), string.Empty) + "…";
        }

        // --- History replay ---

        /// <summary>Rebuilds the transcript items used to replay history on resume.</summary>
        public IReadOnlyList<HistoryItem> LoadTranscript(string cwd, string sessionId)
        {
            var items = new List<HistoryItem>();
            var toolIndex = new Dictionary<string, HistoryItem>();
            var assistantIndex = new Dictionary<string, HistoryItem>();

            foreach (var line in ReadLines(TranscriptPath(cwd, sessionId)))
            {
                JsonElement root;
                try
                {
                    using (var document = JsonDocument.Parse(line))
                    {
                        root = document.RootElement.Clone();
                    }
                }
                catch (JsonException)
                {
                    continue;
                }

                if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) continue;

                var type = ReadString(root, "type");
                if (type == "user") ReplayUserLine(root, items, toolIndex);
                else if (type == "assistant") ReplayAssistantLine(root, items, toolIndex, assistantIndex);
            }

            return items;
        }

        private void ReplayUserLine(JsonElement root, List<HistoryItem> items, Dictionary<string, HistoryItem> toolIndex)
        {
            var content = MessageContent(root);

            if (content?.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.Value.EnumerateArray())
                {
                    // A tool result is stored on the user line that carried it back; it
                    // belongs to the tool card, not to a bubble of its own.
                    if (ReadString(block, "type") != "tool_result") continue;
                    var toolUseId = ReadString(block, "tool_use_id");
                    if (toolUseId == null || !toolIndex.TryGetValue(toolUseId, out var tool)) continue;

                    if (block.TryGetProperty("content", out var result)) tool.Result = result.Clone();
                    if (block.TryGetProperty("is_error", out var isError))
                        tool.IsError = isError.ValueKind == JsonValueKind.True;
                }
            }

            var images = CollectImages(content);
            // The user body keeps its line breaks; Clean is only for titles.
            var text = Invisible.Replace(TextOfContent(content), string.Empty).Trim();
            var meaningful = !string.IsNullOrEmpty(text) && !IsMetaUserText(text);

            if (!meaningful && images.Count == 0) return;

            items.Add(new HistoryItem
            {
                Kind = "user",
                Id = ReadString(root, "uuid") ?? GenerateId(),
                Text = meaningful ? text : string.Empty,
                Images = images.Count > 0 ? images : null,
                Ts = ReadTimestamp(root),
            });
        }

        private void ReplayAssistantLine(JsonElement root, List<HistoryItem> items,
                                         Dictionary<string, HistoryItem> toolIndex,
                                         Dictionary<string, HistoryItem> assistantIndex)
        {
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;

            // One assistant message can span several transcript lines; they are merged by id
            // so the bubble is one piece of text rather than a run of fragments.
            var id = ReadString(message, "id") ?? ReadString(root, "uuid") ?? GenerateId();
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;

            HistoryItem assistant = null;
            HistoryItem Ensure()
            {
                if (assistant != null) return assistant;
                if (assistantIndex.TryGetValue(id, out var existing))
                {
                    assistant = existing;
                    return assistant;
                }
                assistant = new HistoryItem { Kind = "assistant", Id = id, Text = string.Empty, Thinking = string.Empty };
                assistantIndex[id] = assistant;
                items.Add(assistant);
                return assistant;
            }

            foreach (var block in content.EnumerateArray())
            {
                switch (ReadString(block, "type"))
                {
                    case "text":
                        Ensure().Text += ReadString(block, "text") ?? string.Empty;
                        break;

                    case "thinking":
                        Ensure().Thinking += ReadString(block, "thinking") ?? string.Empty;
                        break;

                    case "tool_use":
                        var toolId = ReadString(block, "id");
                        if (toolId == null || toolIndex.ContainsKey(toolId)) break;
                        var tool = new HistoryItem
                        {
                            Kind = "tool",
                            Id = toolId,
                            Name = ReadString(block, "name"),
                            Input = block.TryGetProperty("input", out var input) ? input.Clone() : (JsonElement?)null,
                            Ts = ReadTimestamp(root),
                        };
                        toolIndex[toolId] = tool;
                        items.Add(tool);
                        break;
                }
            }
        }

        private static List<string> CollectImages(JsonElement? content)
        {
            var images = new List<string>();
            if (content?.ValueKind != JsonValueKind.Array) return images;

            foreach (var block in content.Value.EnumerateArray())
            {
                if (ReadString(block, "type") != "image") continue;
                if (!block.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object) continue;
                if (ReadString(source, "type") != "base64") continue;

                var data = ReadString(source, "data");
                if (string.IsNullOrEmpty(data)) continue;

                var mediaType = ReadString(source, "media_type") ?? "image/png";
                images.Add("data:" + mediaType + ";base64," + data);
            }
            return images;
        }

        // --- Shared helpers ---

        private static IEnumerable<string> ReadLines(string path)
        {
            string content;
            try
            {
                if (!File.Exists(path)) return Array.Empty<string>();
                content = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                Log.Debug("could not read transcript " + path + ": " + ex.Message);
                return Array.Empty<string>();
            }

            return content.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l));
        }

        private static JsonElement? MessageContent(JsonElement root)
        {
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return null;
            return message.TryGetProperty("content", out var content) ? content : (JsonElement?)null;
        }

        /// <summary>Content is either a bare string or a block list; both are flattened to text.</summary>
        public static string TextOfContent(JsonElement? content)
        {
            if (content == null) return string.Empty;
            if (content.Value.ValueKind == JsonValueKind.String) return content.Value.GetString() ?? string.Empty;
            if (content.Value.ValueKind != JsonValueKind.Array) return string.Empty;

            var sb = new StringBuilder();
            foreach (var block in content.Value.EnumerateArray())
            {
                if (ReadString(block, "type") != "text") continue;
                sb.Append(ReadString(block, "text") ?? string.Empty);
            }
            return sb.ToString();
        }

        /// <summary>Strips invisible characters and collapses whitespace. For titles only.</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return Whitespace.Replace(Invisible.Replace(text, string.Empty), " ").Trim();
        }

        /// <summary>
        /// True for transcript entries that are not really user messages: command wrappers,
        /// system reminders and background-task notifications the CLI injects itself. Without
        /// this they would render as the user saying raw XML.
        /// </summary>
        public static bool IsMetaUserText(string text)
        {
            var cleaned = Clean(text);
            if (string.IsNullOrEmpty(cleaned)) return true;

            return cleaned.StartsWith("<command-", StringComparison.Ordinal)
                   || cleaned.StartsWith("<local-command", StringComparison.Ordinal)
                   || cleaned.StartsWith("<system-reminder", StringComparison.Ordinal)
                   || cleaned.StartsWith("<task-notification", StringComparison.Ordinal);
        }

        private static string ReadString(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            if (!parent.TryGetProperty(name, out var value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        /// <summary>Epoch ms from the line's ISO `timestamp`, when present.</summary>
        private static long? ReadTimestamp(JsonElement root)
        {
            var raw = ReadString(root, "timestamp");
            if (raw == null) return null;
            return DateTimeOffset.TryParse(raw, out var parsed) ? parsed.ToUnixTimeMilliseconds() : (long?)null;
        }

        private static string SafeCreationTime(FileInfo info)
        {
            try
            {
                var created = info.CreationTimeUtc;
                // Some filesystems report no birth time; a sentinel date is worse than absent.
                return created.Year > 1601 ? created.ToString("o") : null;
            }
            catch
            {
                return null;
            }
        }

        private string GenerateId() => "h_" + _generatedIdSequence++;
    }
}
