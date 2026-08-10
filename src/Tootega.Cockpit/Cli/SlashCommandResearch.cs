using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    internal sealed class CommandInfo
    {
        [JsonPropertyName("category")] public string Category { get; set; }
        /// <summary>Short label for the palette.</summary>
        [JsonPropertyName("hint")] public string Hint { get; set; }
        /// <summary>One full sentence.</summary>
        [JsonPropertyName("detail")] public string Detail { get; set; }
        /// <summary>Third-party plugin or tool name, which groups its commands together.</summary>
        [JsonPropertyName("group")] public string Group { get; set; }
        [JsonPropertyName("researchedAt")] public string ResearchedAt { get; set; }
    }

    /// <summary>
    /// Labels unknown slash commands using the internal model. Port of
    /// src/cli/SlashCommandResearch.ts.
    ///
    /// The CLI announces slash-command NAMES but no descriptions, so a palette listing them
    /// would be a wall of bare identifiers. Built-ins are described by the webview's own
    /// catalogue; everything else — plugin and project commands — gets classified once and
    /// cached globally, so each command is researched a single time on a machine.
    ///
    /// Best-effort throughout: a failure keeps the existing cache rather than clearing it.
    /// </summary>
    internal sealed class SlashCommandResearch
    {
        private static readonly string[] Categories =
        {
            "session", "context", "config", "tools", "account", "info", "plugin", "other",
        };

        /// <summary>
        /// Built-ins the webview already documents statically. Excluding them is the difference
        /// between researching a handful of commands and researching thirty.
        /// </summary>
        private static readonly HashSet<string> BuiltIn = new HashSet<string>(StringComparer.Ordinal)
        {
            "clear", "compact", "context", "memory", "resume", "model", "config", "permissions",
            "review", "code-review", "init", "mcp", "agents", "hooks", "login", "logout", "cost",
            "usage", "status", "help", "doctor",
        };

        /// <summary>
        /// The cache keeps a locale dimension even though this port is English-only, because
        /// the file is shared with the VS Code extension. Dropping the level would make the two
        /// read each other's cache as empty and re-research everything.
        /// </summary>
        private const string Locale = "en";

        private sealed class Cache
        {
            [JsonPropertyName("version")] public int Version { get; set; } = 1;
            [JsonPropertyName("locales")]
            public Dictionary<string, Dictionary<string, CommandInfo>> Locales { get; set; }
                = new Dictionary<string, Dictionary<string, CommandInfo>>(StringComparer.Ordinal);
        }

        private readonly AiClient _ai;
        private readonly string _cacheFile;
        private int _inFlight;

        public SlashCommandResearch(AiClient ai, string cacheDirectory = null)
        {
            _ai = ai ?? throw new ArgumentNullException(nameof(ai));
            _cacheFile = Path.Combine(cacheDirectory ?? ClaudeHome.CockpitDir, "slash-commands.json");
        }

        /// <summary>
        /// The command-to-metadata map: whatever is cached, plus anything newly researched.
        ///
        /// <paramref name="onResearchStart"/> fires only when the AI will actually be queried,
        /// so the UI does not flash a spinner for a cache hit.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, CommandInfo>> ResearchAsync(
            IReadOnlyList<string> commands, Action onResearchStart = null)
        {
            var cache = LoadCache();
            if (!cache.Locales.TryGetValue(Locale, out var known) || known == null)
            {
                known = new Dictionary<string, CommandInfo>(StringComparer.Ordinal);
                cache.Locales[Locale] = known;
            }

            var names = (commands ?? Array.Empty<string>())
                .Select(c => (c ?? string.Empty).TrimStart('/').Trim())
                .Where(c => c.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var missing = names.Where(n => !known.ContainsKey(n) && !BuiltIn.Contains(n)).ToList();
            if (missing.Count == 0) return known;

            // One research pass at a time: several tabs initialising at once would otherwise
            // ask the same question in parallel and pay for it twice.
            if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return known;

            try
            {
                onResearchStart?.Invoke();

                var text = await _ai.AskAsync(new AskOptions
                {
                    Prompt = BuildPrompt(missing),
                    MaxTokens = 2048,
                }).ConfigureAwait(false);

                var researched = ParseResponse(text, missing);
                if (researched.Count == 0) return known;

                var now = DateTime.UtcNow.ToString("o");
                foreach (var entry in researched)
                {
                    entry.Value.ResearchedAt = now;
                    known[entry.Key] = entry.Value;
                }

                SaveCache(cache);
                Log.Debug("slash research: +" + researched.Count + "/" + missing.Count);
            }
            catch (Exception ex)
            {
                // The existing cache stays; the commands are simply listed unlabelled.
                Log.Debug("slash research failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _inFlight, 0);
            }

            return known;
        }

        /// <summary>Converts the cache entries into the wire shape the webview reads.</summary>
        public static Dictionary<string, SlashCmdMeta> ToMeta(IReadOnlyDictionary<string, CommandInfo> info)
        {
            var meta = new Dictionary<string, SlashCmdMeta>(StringComparer.Ordinal);
            if (info == null) return meta;

            foreach (var entry in info)
            {
                if (entry.Value == null) continue;
                meta[entry.Key] = new SlashCmdMeta
                {
                    Category = entry.Value.Category,
                    Hint = entry.Value.Hint,
                    Detail = entry.Value.Detail,
                    Group = entry.Value.Group,
                };
            }

            return meta;
        }

        internal static string BuildPrompt(IReadOnlyList<string> missing)
        {
            return string.Join("\n", new[]
            {
                "You document Claude Code slash commands for a GUI command palette.",
                "For each command below, classify it and write help text.",
                "Reply with MINIFIED JSON ONLY (no markdown, no prose, no code fence). Shape:",
                "{\"<cmd>\":{\"category\":\"<one of: " + string.Join("|", Categories) +
                    ">\",\"group\":\"<plugin name or omit>\",\"hint\":\"<<=90 chars>\",\"detail\":\"<one full sentence>\"}}",
                "If a command belongs to a third-party plugin/extension/tool, set \"group\" to that tool's " +
                    "short lowercase name (commands of the same tool MUST share the same group), and set " +
                    "category to \"plugin\". Omit \"group\" for first-party Claude Code commands.",
                "Write \"hint\" and \"detail\" in international English.",
                "Commands (no leading slash): " + string.Join(", ", missing),
            });
        }

        /// <summary>
        /// Parses the model's reply.
        ///
        /// Only commands we actually asked about are accepted, and an entry with no hint is
        /// dropped: a labelled command with an empty label is worse in the palette than an
        /// unlabelled one, which at least renders as a plain name.
        /// </summary>
        internal static Dictionary<string, CommandInfo> ParseResponse(string text, IReadOnlyList<string> missing)
        {
            var result = new Dictionary<string, CommandInfo>(StringComparer.Ordinal);

            var json = ExtractJson(text);
            if (json == null) return result;

            JsonElement root;
            try
            {
                using (var document = JsonDocument.Parse(json))
                {
                    root = document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                return result;
            }

            if (root.ValueKind != JsonValueKind.Object) return result;

            foreach (var name in missing)
            {
                // The model sometimes echoes the leading slash back.
                if (!root.TryGetProperty(name, out var entry) && !root.TryGetProperty("/" + name, out entry)) continue;
                if (entry.ValueKind != JsonValueKind.Object) continue;

                var hint = ReadString(entry, "hint");
                if (string.IsNullOrWhiteSpace(hint)) continue;

                var group = ReadString(entry, "group");
                if (group != null) group = Truncate(group.Trim().ToLowerInvariant(), 40);

                var category = ReadString(entry, "category");
                if (category == null || !Categories.Contains(category)) category = "other";

                result[name] = new CommandInfo
                {
                    // Belonging to a tool is the stronger signal: it decides the grouping.
                    Category = !string.IsNullOrEmpty(group) ? "plugin" : category,
                    Hint = Truncate(hint, 140),
                    Detail = Truncate(ReadString(entry, "detail"), 300),
                    Group = string.IsNullOrEmpty(group) ? null : group,
                };
            }

            return result;
        }

        /// <summary>
        /// The JSON object inside the model's text. Asking for minified JSON is not a guarantee
        /// — a code fence or a preamble happens — so the outermost braces are located instead
        /// of trusting the whole reply to parse.
        /// </summary>
        internal static string ExtractJson(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            return start < 0 || end < start ? null : text.Substring(start, end - start + 1);
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static string ReadString(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        private Cache LoadCache()
        {
            var raw = FileStore.ReadAllTextOrNull(_cacheFile);
            if (raw == null) return new Cache();

            var parsed = Json.TryDeserialize<Cache>(raw);
            return parsed?.Locales != null ? parsed : new Cache();
        }

        private void SaveCache(Cache cache)
        {
            FileStore.WriteAtomic(_cacheFile, Json.Serialize(cache));
        }
    }
}
