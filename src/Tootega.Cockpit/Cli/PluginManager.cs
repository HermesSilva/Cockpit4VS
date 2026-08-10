using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    internal static class PluginActions
    {
        public const string Install = "install";
        public const string Uninstall = "uninstall";
        public const string Enable = "enable";
        public const string Disable = "disable";
        public const string Update = "update";
        public const string MarketAdd = "marketAdd";
        public const string MarketRemove = "marketRemove";
    }

    /// <summary>
    /// Plugin management through the official CLI. Port of src/cli/PluginManager.ts.
    ///
    /// Everything goes through `claude plugin …` — listing, install, remove, enable, disable,
    /// update, and marketplaces. The Cockpit only surfaces it, which is the founding principle
    /// applied to a management surface rather than to the agent loop.
    ///
    /// Two things are added on top, both best-effort. The KIND of an installed plugin is
    /// computed precisely from the components on disk; the kind and canonical URL of everything
    /// else are resolved once by the internal model and cached, because the CLI does not report
    /// them and a wall of unlabelled plugin ids is not a browsable list.
    /// </summary>
    internal sealed class PluginManager
    {
        private static readonly string[] Kinds = { "skills", "agents", "commands", "mcp", "hooks", "mixed" };

        private static readonly Regex HttpUrl = new Regex(@"^https?://", RegexOptions.Compiled);
        private static readonly Regex OwnerRepo = new Regex(@"^[\w.-]+/[\w.-]+$", RegexOptions.Compiled);
        private static readonly Regex GitSuffix = new Regex(@"\.git$", RegexOptions.Compiled);
        private static readonly Regex GitPrefix = new Regex(@"^git\+", RegexOptions.Compiled);
        private static readonly Regex LeadingSlash = new Regex(@"^\.?/", RegexOptions.Compiled);

        /// <summary>Internal rather than private so the parser can be tested directly.</summary>
        internal sealed class MetaEntry
        {
            [JsonPropertyName("url")] public string Url { get; set; }
            [JsonPropertyName("kind")] public string Kind { get; set; }
        }

        private sealed class MetaCache
        {
            [JsonPropertyName("version")] public int Version { get; set; } = 2;
            [JsonPropertyName("meta")]
            public Dictionary<string, MetaEntry> Meta { get; set; } = new Dictionary<string, MetaEntry>(StringComparer.Ordinal);
        }

        private readonly AiClient _ai;
        private readonly string _cacheFile;

        public PluginManager(AiClient ai, string cacheDirectory = null)
        {
            _ai = ai;
            _cacheFile = Path.Combine(cacheDirectory ?? ClaudeHome.CockpitDir, "plugin-urls.json");
        }

        /// <summary>
        /// Lists installed and available plugins plus marketplaces.
        /// <paramref name="forceMetadata"/> re-validates the AI-resolved metadata.
        /// </summary>
        public async Task<PluginsData> ListAsync(string claudePath, bool forceMetadata = false)
        {
            var listTask = CliRunner.RunAsync(claudePath, new[] { "plugin", "list", "--json", "--available" }, 60_000);
            var marketTask = CliRunner.RunAsync(claudePath, new[] { "plugin", "marketplace", "list", "--json" }, 30_000);

            var listResult = await listTask.ConfigureAwait(false);
            var marketResult = await marketTask.ConfigureAwait(false);

            var markets = ParseMarketplaces(marketResult.Output);
            var marketUrls = markets
                .Where(m => !string.IsNullOrEmpty(m.Name))
                .ToDictionary(m => m.Name, MarketplaceUrl, StringComparer.Ordinal);

            var data = new PluginsData
            {
                Installed = ParseInstalled(listResult.Output),
                Available = ParseAvailable(listResult.Output, marketUrls),
                Marketplaces = markets,
            };

            try
            {
                await ResolveMetadataAsync(data, forceMetadata).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A failure keeps whatever was derived locally, which is still useful.
                Log.Debug("plugin metadata resolution failed: " + ex.Message);
            }

            return data;
        }

        /// <summary>Runs one management action.</summary>
        public async Task<(bool Ok, string Message)> ActionAsync(string claudePath, string action, string arg,
                                                                 string scope = null)
        {
            var args = ActionArgs(action, arg, scope);
            if (args == null) return (false, "unknown action: " + action);

            var result = await CliRunner.RunAsync(claudePath, args).ConfigureAwait(false);
            if (result.Ok)
            {
                Log.Info("plugin " + action + " " + arg + ": ok");
                return (true, null);
            }

            var message = result.Message();
            Log.Info("plugin " + action + " " + arg + " failed (" + result.Code + "): " + message);
            return (false, message);
        }

        internal static string[] ActionArgs(string action, string arg, string scope)
        {
            switch (action)
            {
                case PluginActions.Install:
                    return string.IsNullOrEmpty(scope)
                        ? new[] { "plugin", "install", arg }
                        : new[] { "plugin", "install", arg, "--scope", scope };
                case PluginActions.Uninstall: return new[] { "plugin", "uninstall", arg };
                case PluginActions.Enable: return new[] { "plugin", "enable", arg };
                case PluginActions.Disable: return new[] { "plugin", "disable", arg };
                case PluginActions.Update: return new[] { "plugin", "update", arg };
                case PluginActions.MarketAdd: return new[] { "plugin", "marketplace", "add", arg };
                case PluginActions.MarketRemove: return new[] { "plugin", "marketplace", "remove", arg };
                default: return null;
            }
        }

        // --- Parsing the CLI output ---

        internal static List<InstalledPlugin> ParseInstalled(string stdout)
        {
            var installed = new List<InstalledPlugin>();
            var root = ExtractJson(stdout);
            if (root?.ValueKind != JsonValueKind.Object) return installed;

            if (!root.Value.TryGetProperty("installed", out var array) || array.ValueKind != JsonValueKind.Array)
                return installed;

            foreach (var entry in array.EnumerateArray())
            {
                var id = ReadString(entry, "id");
                if (id == null) continue;

                var installPath = ReadString(entry, "installPath");
                var manifest = ReadManifest(installPath);

                installed.Add(new InstalledPlugin
                {
                    Id = id,
                    Version = ReadString(entry, "version"),
                    Scope = ReadString(entry, "scope"),
                    // Absent means enabled: only an explicit false disables.
                    Enabled = !(entry.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False),
                    Description = manifest.Description,
                    Url = manifest.Url,
                    // Computed from the components actually present, which beats any guess.
                    Kind = ComponentKind(installPath),
                });
            }

            return installed;
        }

        internal static List<AvailablePlugin> ParseAvailable(string stdout, IDictionary<string, string> marketUrls)
        {
            var available = new List<AvailablePlugin>();
            var root = ExtractJson(stdout);
            if (root?.ValueKind != JsonValueKind.Object) return available;

            if (!root.Value.TryGetProperty("available", out var array) || array.ValueKind != JsonValueKind.Array)
                return available;

            foreach (var entry in array.EnumerateArray())
            {
                var pluginId = ReadString(entry, "pluginId");
                if (pluginId == null) continue;

                var marketplace = ReadString(entry, "marketplaceName");
                string marketUrl = null;
                if (marketplace != null && marketUrls != null) marketUrls.TryGetValue(marketplace, out marketUrl);

                available.Add(new AvailablePlugin
                {
                    PluginId = pluginId,
                    Name = ReadString(entry, "name") ?? pluginId.Split('@')[0],
                    Description = ReadString(entry, "description"),
                    MarketplaceName = marketplace,
                    InstallCount = entry.TryGetProperty("installCount", out var count) &&
                                   count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var n)
                        ? n
                        : (int?)null,
                    Url = AvailableUrl(entry.TryGetProperty("source", out var source) ? source : (JsonElement?)null, marketUrl),
                });
            }

            return available;
        }

        internal static List<Marketplace> ParseMarketplaces(string stdout)
        {
            var markets = new List<Marketplace>();
            var root = ExtractJson(stdout);
            if (root?.ValueKind != JsonValueKind.Array) return markets;

            foreach (var entry in root.Value.EnumerateArray())
            {
                var name = ReadString(entry, "name");
                if (name == null) continue;

                markets.Add(new Marketplace
                {
                    Name = name,
                    Source = ReadString(entry, "source"),
                    Repo = ReadString(entry, "repo"),
                });
            }

            return markets;
        }

        /// <summary>
        /// The first JSON value in the output, object or array.
        ///
        /// The CLI can print progress lines around it, so the value is located rather than the
        /// whole stream being parsed.
        /// </summary>
        internal static JsonElement? ExtractJson(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var brace = text.IndexOf('{');
            var bracket = text.IndexOf('[');
            var start = brace < 0 ? bracket : (bracket < 0 ? brace : Math.Min(brace, bracket));
            if (start < 0) return null;

            var close = text[start] == '{' ? '}' : ']';
            var end = text.LastIndexOf(close);
            if (end < start) return null;

            try
            {
                using (var document = JsonDocument.Parse(text.Substring(start, end - start + 1)))
                {
                    return document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // --- Local enrichment ---

        /// <summary>Repository URL of a marketplace, for plugins with no source of their own.</summary>
        internal static string MarketplaceUrl(Marketplace marketplace)
        {
            var repo = marketplace?.Repo;
            if (string.IsNullOrEmpty(repo)) return null;

            if (HttpUrl.IsMatch(repo)) return GitSuffix.Replace(repo, string.Empty);
            if (OwnerRepo.IsMatch(repo)) return "https://github.com/" + repo;
            return null;
        }

        /// <summary>
        /// URL of an available plugin: its own source when it has one, otherwise the
        /// marketplace's repository. A source with a path points into a monorepo, so the link
        /// goes to that subtree rather than to the repository root.
        /// </summary>
        internal static string AvailableUrl(JsonElement? source, string marketplaceUrl)
        {
            if (source?.ValueKind == JsonValueKind.Object)
            {
                var url = ReadString(source.Value, "url");
                if (url != null)
                {
                    url = GitSuffix.Replace(url, string.Empty);

                    var path = ReadString(source.Value, "path");
                    if (!string.IsNullOrEmpty(path))
                    {
                        var reference = ReadString(source.Value, "ref") ?? "HEAD";
                        return url + "/tree/" + reference + "/" + LeadingSlash.Replace(path, string.Empty);
                    }

                    return url;
                }
            }

            // A string source is a relative path inside the marketplace monorepo.
            return marketplaceUrl;
        }

        /// <summary>Description and URL from an installed plugin's manifest.</summary>
        internal static (string Description, string Url) ReadManifest(string installPath)
        {
            if (string.IsNullOrEmpty(installPath)) return (null, null);

            try
            {
                var manifestPath = Path.Combine(installPath, ".claude-plugin", "plugin.json");
                if (!File.Exists(manifestPath)) return (null, null);

                using (var document = JsonDocument.Parse(File.ReadAllText(manifestPath)))
                {
                    var root = document.RootElement;

                    // Several conventions for the same idea; the first that answers wins.
                    var url = ReadString(root, "homepage");
                    if (url == null && root.TryGetProperty("repository", out var repository))
                    {
                        url = repository.ValueKind == JsonValueKind.String
                            ? repository.GetString()
                            : ReadString(repository, "url");
                    }
                    if (url == null && root.TryGetProperty("author", out var author))
                        url = ReadString(author, "url");

                    if (url != null) url = GitSuffix.Replace(GitPrefix.Replace(url, string.Empty), string.Empty);

                    return (ReadString(root, "description"), url);
                }
            }
            catch
            {
                return (null, null);
            }
        }

        /// <summary>
        /// The plugin's kind, derived from the components actually installed.
        ///
        /// This is why installed plugins do not need the model: the folders are the truth. A
        /// substantive component (mcp, commands, agents) outranks hooks and skills, since a
        /// plugin that ships an MCP server and a hook is an MCP plugin.
        /// </summary>
        internal static string ComponentKind(string installPath)
        {
            if (string.IsNullOrEmpty(installPath)) return null;

            var present = new List<string>();
            if (CountEntries(installPath, "skills") > 0) present.Add("skills");
            if (CountEntries(installPath, "agents") > 0) present.Add("agents");
            if (CountEntries(installPath, "commands") > 0) present.Add("commands");

            var mcp = CountEntries(installPath, "mcp-servers") > 0 || CountEntries(installPath, ".mcp") > 0;
            var hooks = CountEntries(installPath, "hooks") > 0;

            // Both can also be declared in the manifest rather than as folders.
            try
            {
                var manifestPath = Path.Combine(installPath, ".claude-plugin", "plugin.json");
                if (File.Exists(manifestPath))
                {
                    using (var document = JsonDocument.Parse(File.ReadAllText(manifestPath)))
                    {
                        if (HasMembers(document.RootElement, "mcpServers")) mcp = true;
                        if (HasMembers(document.RootElement, "hooks")) hooks = true;
                    }
                }
            }
            catch
            {
                // No readable manifest; the folders already answered.
            }

            if (mcp) present.Add("mcp");
            if (hooks) present.Add("hooks");
            if (present.Count == 0) return null;

            var strong = present.Where(p => p != "hooks").ToList();
            if (strong.Count > 1) return "mixed";
            if (strong.Count == 1) return strong[0];
            return present[0];
        }

        private static bool HasMembers(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.Object &&
                   value.EnumerateObject().Any();
        }

        private static int CountEntries(string basePath, string subdirectory)
        {
            try
            {
                var path = Path.Combine(basePath, subdirectory);
                if (!Directory.Exists(path)) return 0;
                return Directory.EnumerateFileSystemEntries(path)
                    .Count(e => !Path.GetFileName(e).StartsWith(".", StringComparison.Ordinal));
            }
            catch
            {
                return 0;
            }
        }

        // --- AI-resolved metadata ---

        /// <summary>
        /// Fills in the canonical URL and kind the CLI does not report, cached per plugin id.
        ///
        /// Installed plugins keep their component-derived kind: it is measured, not inferred.
        /// The AI only fills what nothing local can answer.
        /// </summary>
        private async Task ResolveMetadataAsync(PluginsData data, bool force)
        {
            if (_ai == null) return;

            var cache = LoadCache();
            var candidates = new Dictionary<string, (string Name, string Market, string Repo, string Description)>(StringComparer.Ordinal);

            foreach (var plugin in data.Available ?? new List<AvailablePlugin>())
            {
                candidates[plugin.PluginId] = (plugin.Name, plugin.MarketplaceName, plugin.Url, plugin.Description);
            }

            foreach (var plugin in data.Installed ?? new List<InstalledPlugin>())
            {
                if (candidates.ContainsKey(plugin.Id)) continue;
                candidates[plugin.Id] = (plugin.Id.Split('@')[0], null, plugin.Url, plugin.Description);
            }

            var needed = candidates.Keys
                .Where(id => force
                             || !cache.Meta.TryGetValue(id, out var entry)
                             || string.IsNullOrEmpty(entry?.Url)
                             || string.IsNullOrEmpty(entry?.Kind))
                .ToList();

            if (needed.Count > 0)
            {
                // Chunked: a hundred plugins in one prompt would blow the reply budget and
                // return a truncated object.
                foreach (var chunk in Chunk(needed, 50))
                {
                    var text = await _ai.AskAsync(new AskOptions
                    {
                        Prompt = BuildMetadataPrompt(chunk, candidates),
                        MaxTokens = 4096,
                    }).ConfigureAwait(false);

                    var resolved = ParseMetadata(text);
                    if (resolved == null) continue;

                    foreach (var id in chunk)
                    {
                        // The model may key by id or by display name.
                        if (!resolved.TryGetValue(id, out var entry) &&
                            !resolved.TryGetValue(candidates[id].Name ?? string.Empty, out entry)) continue;

                        cache.Meta.TryGetValue(id, out var current);
                        cache.Meta[id] = new MetaEntry
                        {
                            Url = entry.Url ?? current?.Url,
                            Kind = entry.Kind ?? current?.Kind,
                        };
                    }
                }

                SaveCache(cache);
                Log.Debug("plugins: resolved metadata for " + needed.Count);
            }

            foreach (var plugin in data.Available ?? new List<AvailablePlugin>())
            {
                if (!cache.Meta.TryGetValue(plugin.PluginId, out var entry) || entry == null) continue;
                if (!string.IsNullOrEmpty(entry.Url)) plugin.Url = entry.Url;
                if (!string.IsNullOrEmpty(entry.Kind)) plugin.Kind = entry.Kind;
            }

            foreach (var plugin in data.Installed ?? new List<InstalledPlugin>())
            {
                if (!cache.Meta.TryGetValue(plugin.Id, out var entry) || entry == null) continue;
                if (!string.IsNullOrEmpty(entry.Url) && string.IsNullOrEmpty(plugin.Url)) plugin.Url = entry.Url;
                // Component-derived kind wins: it was measured.
                if (string.IsNullOrEmpty(plugin.Kind)) plugin.Kind = entry.Kind;
            }
        }

        private static IEnumerable<List<string>> Chunk(List<string> source, int size)
        {
            for (var i = 0; i < source.Count; i += size)
            {
                yield return source.GetRange(i, Math.Min(size, source.Count - i));
            }
        }

        private static string BuildMetadataPrompt(
            IReadOnlyList<string> ids,
            IDictionary<string, (string Name, string Market, string Repo, string Description)> candidates)
        {
            var lines = ids.Select(id =>
            {
                var c = candidates[id];
                var description = (c.Description ?? string.Empty);
                if (description.Length > 120) description = description.Substring(0, 120);
                return id + " | name=" + c.Name + " | marketplace=" + (c.Market ?? "?") +
                       " | repo=" + (c.Repo ?? "?") + " | desc=" + description;
            });

            return string.Join("\n", new[]
            {
                "You classify Claude Code plugins and map them to a canonical URL.",
                "Official plugins (marketplace \"claude-plugins-official\") have a page at " +
                    "https://claude.com/plugins/<plugin-name>; others use the source repository (the provided repo).",
                "For each plugin return: \"url\" (claude.com page if official else repo) and \"kind\" — the main " +
                    "thing it provides, ONE of: " + string.Join("|", Kinds) + ". Use \"mcp\" for external tool " +
                    "integrations, \"commands\" for slash commands, \"agents\" for subagents, \"skills\" for skill " +
                    "packs, \"mixed\" if clearly several.",
                "The JSON key MUST be the exact id before the first \" | \". Reply with MINIFIED JSON ONLY " +
                    "(no markdown/fence): {\"<id>\":{\"url\":\"...\",\"kind\":\"...\"}}.",
                string.Empty,
            }.Concat(lines));
        }

        /// <summary>
        /// Parses the metadata reply, keeping only values that are actually usable: a URL must
        /// look like one, and a kind must be from the list we asked for.
        /// </summary>
        internal static Dictionary<string, MetaEntry> ParseMetadata(string text)
        {
            var json = SlashCommandResearch.ExtractJson(text);
            if (json == null) return null;

            try
            {
                using (var document = JsonDocument.Parse(json))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

                    var result = new Dictionary<string, MetaEntry>(StringComparer.Ordinal);
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.Object) continue;

                        var url = ReadString(property.Value, "url");
                        if (url != null && !HttpUrl.IsMatch(url)) url = null;

                        var kind = ReadString(property.Value, "kind");
                        if (kind != null && !Kinds.Contains(kind)) kind = null;

                        if (url == null && kind == null) continue;
                        result[property.Name] = new MetaEntry { Url = url, Kind = kind };
                    }

                    return result;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private MetaCache LoadCache()
        {
            var raw = FileStore.ReadAllTextOrNull(_cacheFile);
            if (raw == null) return new MetaCache();

            var parsed = Json.TryDeserialize<MetaCache>(raw);
            if (parsed?.Meta != null) return parsed;

            // Migrates the older { urls: { id: url } } shape rather than discarding it: those
            // entries cost AI calls to produce.
            try
            {
                using (var document = JsonDocument.Parse(raw))
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("urls", out var urls) &&
                        urls.ValueKind == JsonValueKind.Object)
                    {
                        var migrated = new MetaCache();
                        foreach (var property in urls.EnumerateObject())
                        {
                            if (property.Value.ValueKind != JsonValueKind.String) continue;
                            migrated.Meta[property.Name] = new MetaEntry { Url = property.Value.GetString() };
                        }
                        return migrated;
                    }
                }
            }
            catch (JsonException)
            {
            }

            return new MetaCache();
        }

        private void SaveCache(MetaCache cache)
        {
            FileStore.WriteAtomic(_cacheFile, Json.Serialize(cache));
        }

        private static string ReadString(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }
    }
}
