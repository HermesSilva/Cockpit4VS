using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    public class PluginAndSlashTests : IDisposable
    {
        private readonly string _root;

        public PluginAndSlashTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "cockpit-plugins-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
            }
        }

        // --- Plugin list parsing ---

        [Fact]
        public void ParsesInstalledPlugins()
        {
            var installed = PluginManager.ParseInstalled(
                "{\"installed\":[{\"id\":\"dase@tootega\",\"version\":\"1.2.0\",\"scope\":\"user\",\"enabled\":true}]}");

            var plugin = installed.Single();
            Assert.Equal("dase@tootega", plugin.Id);
            Assert.Equal("1.2.0", plugin.Version);
            Assert.Equal("user", plugin.Scope);
            Assert.True(plugin.Enabled);
        }

        [Fact]
        public void OnlyAnExplicitFalseDisablesAPlugin()
        {
            // Absent must mean enabled, or every plugin from an older CLI would show as off.
            Assert.True(PluginManager.ParseInstalled("{\"installed\":[{\"id\":\"a\"}]}").Single().Enabled);
            Assert.False(PluginManager.ParseInstalled("{\"installed\":[{\"id\":\"a\",\"enabled\":false}]}").Single().Enabled);
        }

        [Fact]
        public void DropsEntriesWithoutAnId()
        {
            Assert.Empty(PluginManager.ParseInstalled("{\"installed\":[{\"version\":\"1\"}]}"));
            Assert.Empty(PluginManager.ParseAvailable("{\"available\":[{\"name\":\"x\"}]}", null));
        }

        [Fact]
        public void FindsTheJsonAmongProgressOutput()
        {
            // The CLI prints progress lines around the payload.
            var installed = PluginManager.ParseInstalled(
                "Fetching marketplaces...\n{\"installed\":[{\"id\":\"a\"}]}\nDone.\n");

            Assert.Single(installed);
        }

        [Fact]
        public void DerivesTheAvailablePluginNameFromItsId()
        {
            var available = PluginManager.ParseAvailable("{\"available\":[{\"pluginId\":\"dase@tootega\"}]}", null);

            Assert.Equal("dase", available.Single().Name);
        }

        [Fact]
        public void LinksAnAvailablePluginToItsMarketplaceRepository()
        {
            var urls = new Dictionary<string, string> { ["tootega"] = "https://github.com/tootega/plugins" };

            var available = PluginManager.ParseAvailable(
                "{\"available\":[{\"pluginId\":\"a@tootega\",\"marketplaceName\":\"tootega\"}]}", urls);

            Assert.Equal("https://github.com/tootega/plugins", available.Single().Url);
        }

        [Theory]
        [InlineData("owner/repo", "https://github.com/owner/repo")]
        [InlineData("https://github.com/owner/repo.git", "https://github.com/owner/repo")]
        [InlineData("https://example.com/x", "https://example.com/x")]
        [InlineData("some local path", null)]
        [InlineData(null, null)]
        public void ResolvesMarketplaceUrls(string repo, string expected)
        {
            Assert.Equal(expected, PluginManager.MarketplaceUrl(new Marketplace { Name = "m", Repo = repo }));
        }

        private static JsonElement Element(string json)
        {
            using (var document = JsonDocument.Parse(json))
            {
                return document.RootElement.Clone();
            }
        }

        [Fact]
        public void PointsAMonorepoPluginAtItsSubtree()
        {
            // Linking to the repository root would send the user to the wrong place.
            var url = PluginManager.AvailableUrl(
                Element("{\"url\":\"https://github.com/o/r.git\",\"path\":\"./plugins/mine\",\"ref\":\"main\"}"), null);

            Assert.Equal("https://github.com/o/r/tree/main/plugins/mine", url);
        }

        [Fact]
        public void DefaultsTheMonorepoRefToHead()
        {
            var url = PluginManager.AvailableUrl(
                Element("{\"url\":\"https://github.com/o/r\",\"path\":\"plugins/mine\"}"), null);

            Assert.Equal("https://github.com/o/r/tree/HEAD/plugins/mine", url);
        }

        [Fact]
        public void FallsBackToTheMarketplaceForAStringSource()
        {
            // A string source is a relative path inside the marketplace monorepo.
            Assert.Equal("https://github.com/o/market",
                PluginManager.AvailableUrl(Element("\"./some/path\""), "https://github.com/o/market"));
        }

        // --- Component kind ---

        private string MakePlugin(string name, params string[] componentDirs)
        {
            var path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            foreach (var dir in componentDirs)
            {
                Directory.CreateDirectory(Path.Combine(path, dir));
                File.WriteAllText(Path.Combine(path, dir, "item.md"), "x", new UTF8Encoding(false));
            }
            return path;
        }

        [Fact]
        public void DerivesTheKindFromTheComponentsOnDisk()
        {
            Assert.Equal("skills", PluginManager.ComponentKind(MakePlugin("p1", "skills")));
            Assert.Equal("commands", PluginManager.ComponentKind(MakePlugin("p2", "commands")));
            Assert.Equal("agents", PluginManager.ComponentKind(MakePlugin("p3", "agents")));
        }

        [Fact]
        public void ASubstantiveComponentOutranksHooks()
        {
            // A plugin shipping an MCP server and a hook is an MCP plugin.
            Assert.Equal("mcp", PluginManager.ComponentKind(MakePlugin("p4", "mcp-servers", "hooks")));
        }

        [Fact]
        public void SeveralSubstantiveComponentsMeanMixed()
        {
            Assert.Equal("mixed", PluginManager.ComponentKind(MakePlugin("p5", "skills", "commands")));
        }

        [Fact]
        public void HooksAloneAreStillAKind()
        {
            Assert.Equal("hooks", PluginManager.ComponentKind(MakePlugin("p6", "hooks")));
        }

        [Fact]
        public void ReadsMcpAndHooksDeclaredInTheManifest()
        {
            // They can be declared rather than laid out as folders.
            var path = MakePlugin("p7");
            Directory.CreateDirectory(Path.Combine(path, ".claude-plugin"));
            File.WriteAllText(Path.Combine(path, ".claude-plugin", "plugin.json"),
                "{\"mcpServers\":{\"x\":{}}}", new UTF8Encoding(false));

            Assert.Equal("mcp", PluginManager.ComponentKind(path));
        }

        [Fact]
        public void AnEmptyPluginHasNoKind()
        {
            // Absent is honest; guessing would put a wrong badge on a row.
            Assert.Null(PluginManager.ComponentKind(MakePlugin("p8")));
            Assert.Null(PluginManager.ComponentKind(null));
            Assert.Null(PluginManager.ComponentKind(Path.Combine(_root, "does-not-exist")));
        }

        [Fact]
        public void ReadsDescriptionAndUrlFromTheManifest()
        {
            var path = MakePlugin("p9");
            Directory.CreateDirectory(Path.Combine(path, ".claude-plugin"));
            File.WriteAllText(Path.Combine(path, ".claude-plugin", "plugin.json"),
                "{\"description\":\"does things\",\"repository\":{\"url\":\"git+https://github.com/o/r.git\"}}",
                new UTF8Encoding(false));

            var manifest = PluginManager.ReadManifest(path);

            Assert.Equal("does things", manifest.Description);
            // The git+ prefix and .git suffix are not part of a browsable URL.
            Assert.Equal("https://github.com/o/r", manifest.Url);
        }

        [Fact]
        public void PrefersHomepageOverRepository()
        {
            var path = MakePlugin("p10");
            Directory.CreateDirectory(Path.Combine(path, ".claude-plugin"));
            File.WriteAllText(Path.Combine(path, ".claude-plugin", "plugin.json"),
                "{\"homepage\":\"https://example.com\",\"repository\":\"https://github.com/o/r\"}",
                new UTF8Encoding(false));

            Assert.Equal("https://example.com", PluginManager.ReadManifest(path).Url);
        }

        [Fact]
        public void AMissingManifestIsNotAnError()
        {
            var manifest = PluginManager.ReadManifest(MakePlugin("p11"));

            Assert.Null(manifest.Description);
            Assert.Null(manifest.Url);
        }

        // --- Action arguments ---

        [Fact]
        public void BuildsTheActionArguments()
        {
            Assert.Equal(new[] { "plugin", "install", "a@m" }, PluginManager.ActionArgs("install", "a@m", null));
            Assert.Equal(new[] { "plugin", "install", "a@m", "--scope", "user" },
                PluginManager.ActionArgs("install", "a@m", "user"));
            Assert.Equal(new[] { "plugin", "uninstall", "a@m" }, PluginManager.ActionArgs("uninstall", "a@m", null));
            Assert.Equal(new[] { "plugin", "marketplace", "add", "o/r" },
                PluginManager.ActionArgs("marketAdd", "o/r", null));
            Assert.Equal(new[] { "plugin", "marketplace", "remove", "m" },
                PluginManager.ActionArgs("marketRemove", "m", null));
        }

        [Fact]
        public void AnUnknownActionIsRefused()
        {
            // Better than composing a command line from an unrecognised verb.
            Assert.Null(PluginManager.ActionArgs("destroy-everything", "x", null));
        }

        // --- AI-resolved metadata ---

        [Fact]
        public void KeepsOnlyUsableMetadata()
        {
            var meta = PluginManager.ParseMetadata(
                "{\"a\":{\"url\":\"https://claude.com/plugins/a\",\"kind\":\"mcp\"}," +
                "\"b\":{\"url\":\"not-a-url\",\"kind\":\"invented-kind\"}," +
                "\"c\":{\"kind\":\"skills\"}}");

            Assert.Equal("https://claude.com/plugins/a", meta["a"].Url);
            Assert.Equal("mcp", meta["a"].Kind);
            // Neither field survived validation, so the entry is dropped entirely.
            Assert.False(meta.ContainsKey("b"));
            // A kind alone is still worth keeping.
            Assert.Equal("skills", meta["c"].Kind);
            Assert.Null(meta["c"].Url);
        }

        [Theory]
        [InlineData("no json here")]
        [InlineData("")]
        [InlineData(null)]
        public void UnusableMetadataRepliesYieldNothing(string text)
        {
            Assert.Null(PluginManager.ParseMetadata(text));
        }

        // --- Slash-command research ---

        [Fact]
        public void ParsesCommandMetadata()
        {
            var parsed = SlashCommandResearch.ParseResponse(
                "{\"deploy\":{\"category\":\"tools\",\"hint\":\"Deploys the app\",\"detail\":\"Runs the deploy pipeline.\"}}",
                new[] { "deploy" });

            var info = parsed["deploy"];
            Assert.Equal("tools", info.Category);
            Assert.Equal("Deploys the app", info.Hint);
            Assert.Equal("Runs the deploy pipeline.", info.Detail);
        }

        [Fact]
        public void BelongingToAToolForcesThePluginCategory()
        {
            // Grouping is the stronger signal: commands of one tool must sit together.
            var parsed = SlashCommandResearch.ParseResponse(
                "{\"x\":{\"category\":\"tools\",\"group\":\"MyTool\",\"hint\":\"h\"}}", new[] { "x" });

            Assert.Equal("plugin", parsed["x"].Category);
            Assert.Equal("mytool", parsed["x"].Group);
        }

        [Fact]
        public void AnUnknownCategoryBecomesOther()
        {
            var parsed = SlashCommandResearch.ParseResponse(
                "{\"x\":{\"category\":\"invented\",\"hint\":\"h\"}}", new[] { "x" });

            Assert.Equal("other", parsed["x"].Category);
        }

        [Fact]
        public void DropsEntriesWithNoHint()
        {
            // A labelled command with an empty label is worse in a palette than an unlabelled
            // one, which at least renders as a plain name.
            var parsed = SlashCommandResearch.ParseResponse(
                "{\"x\":{\"category\":\"tools\"},\"y\":{\"hint\":\"  \"}}", new[] { "x", "y" });

            Assert.Empty(parsed);
        }

        [Fact]
        public void AcceptsTheKeyWithOrWithoutASlash()
        {
            var parsed = SlashCommandResearch.ParseResponse("{\"/deploy\":{\"hint\":\"h\"}}", new[] { "deploy" });

            Assert.True(parsed.ContainsKey("deploy"));
        }

        [Fact]
        public void IgnoresCommandsThatWereNotAskedAbout()
        {
            var parsed = SlashCommandResearch.ParseResponse(
                "{\"asked\":{\"hint\":\"h\"},\"invented\":{\"hint\":\"h\"}}", new[] { "asked" });

            Assert.Equal(new[] { "asked" }, parsed.Keys);
        }

        [Fact]
        public void TruncatesOverlongText()
        {
            var parsed = SlashCommandResearch.ParseResponse(
                "{\"x\":{\"hint\":\"" + new string('h', 500) + "\",\"detail\":\"" + new string('d', 500) + "\"}}",
                new[] { "x" });

            Assert.Equal(140, parsed["x"].Hint.Length);
            Assert.Equal(300, parsed["x"].Detail.Length);
        }

        [Fact]
        public void FindsTheJsonInsideAFencedReply()
        {
            // Asking for minified JSON is not a guarantee.
            var json = SlashCommandResearch.ExtractJson("Here you go:\n```json\n{\"a\":1}\n```\n");

            Assert.Equal("{\"a\":1}", json);
        }

        [Theory]
        [InlineData("no braces at all")]
        [InlineData("")]
        [InlineData(null)]
        public void UnusableRepliesExtractToNothing(string text)
        {
            Assert.Null(SlashCommandResearch.ExtractJson(text));
        }

        [Fact]
        public void AskingAboutNothingIsNotAnError()
        {
            Assert.Empty(SlashCommandResearch.ParseResponse("{}", new string[0]));
            Assert.Empty(SlashCommandResearch.ParseResponse("garbage", new[] { "x" }));
        }

        [Fact]
        public void ConvertsCachedInfoIntoTheWireShape()
        {
            var meta = SlashCommandResearch.ToMeta(new Dictionary<string, CommandInfo>
            {
                ["deploy"] = new CommandInfo { Category = "plugin", Hint = "h", Detail = "d", Group = "tool" },
            });

            Assert.Equal("plugin", meta["deploy"].Category);
            Assert.Equal("tool", meta["deploy"].Group);
        }

        // --- AI client response shape ---

        [Fact]
        public void ExtractsTheFirstTextBlockOfAResponse()
        {
            var text = AiClient.ExtractText(
                "{\"content\":[{\"type\":\"thinking\",\"thinking\":\"hm\"},{\"type\":\"text\",\"text\":\"  answer  \"}]}");

            Assert.Equal("answer", text);
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"content\":[]}")]
        [InlineData("{\"content\":[{\"type\":\"text\",\"text\":\"   \"}]}")]
        [InlineData("not json")]
        [InlineData("")]
        public void UnusableResponsesExtractToNothing(string json)
        {
            Assert.Null(AiClient.ExtractText(json));
        }

        [Fact]
        public void TheInternalModelFallsBackToHaiku()
        {
            // These calls should be cheap and fast; an empty setting must not mean "no model".
            var client = new AiClient();
            Assert.Equal("claude-haiku-4-5", client.InternalModel);

            client.SetInternalModel("   ");
            Assert.Equal("claude-haiku-4-5", client.InternalModel);

            client.SetInternalModel(" claude-sonnet-5 ");
            Assert.Equal("claude-sonnet-5", client.InternalModel);
        }

        // --- MCP status merge ---

        [Fact]
        public void ListStatusWinsOverInitStatus()
        {
            // init's status is from session start; mcp list was measured just now.
            var merged = McpStatus.Merge(
                new[] { "mcp__db__query" },
                new List<McpServerRef> { new McpServerRef { Name = "db", Status = "connected" } },
                new List<McpListEntry>
                {
                    new McpListEntry { Name = "db", Status = McpListStatuses.Failed, Target = "node db.js" },
                });

            var server = merged.Single();
            Assert.Equal(McpListStatuses.Failed, server.Status);
            Assert.False(server.Connected);
            Assert.Equal("node db.js", server.Target);
            // The tools still come from init, which is the only source that knows them.
            Assert.Equal(new[] { "query" }, server.Tools);
        }

        [Fact]
        public void SurfacesAServerOnlyMcpListKnowsAbout()
        {
            // An unapproved .mcp.json server never reaches init, because the CLI will not
            // start it — so this is the only way the user learns it is waiting.
            var merged = McpStatus.Merge(null, null,
                new List<McpListEntry>
                {
                    new McpListEntry { Name = "waiting", Status = McpListStatuses.Pending },
                });

            var server = merged.Single();
            Assert.Equal(McpListStatuses.Pending, server.Status);
            Assert.Empty(server.Tools);
        }

        [Fact]
        public void AttachesAConfigErrorToItsServer()
        {
            var merged = McpStatus.Merge(
                new[] { "mcp__db__query" },
                new List<McpServerRef> { new McpServerRef { Name = "db", Status = "connected" } },
                new List<McpListEntry>(),
                new List<McpConfigError> { new McpConfigError { Name = "db", Error = "invalid url" } });

            var server = merged.Single();
            Assert.Equal("invalid url", server.Error);
            Assert.Equal(McpListStatuses.Failed, server.Status);
            Assert.False(server.Connected);
        }

        [Fact]
        public void ANamelessConfigErrorStillGetsARow()
        {
            // Something was refused; hiding it would leave the user with a silent absence.
            var merged = McpStatus.Merge(null, null, new List<McpListEntry>(),
                new List<McpConfigError> { new McpConfigError { Error = "bad config" } });

            Assert.Equal("bad config", merged.Single().Error);
        }

        [Fact]
        public void SortsRowsThatNeedActionFirst()
        {
            var merged = McpStatus.Merge(null, null, new List<McpListEntry>
            {
                new McpListEntry { Name = "zeta-ok", Status = McpListStatuses.Connected },
                new McpListEntry { Name = "alpha-ok", Status = McpListStatuses.Connected },
                new McpListEntry { Name = "broken", Status = McpListStatuses.Failed },
                new McpListEntry { Name = "waiting", Status = McpListStatuses.Pending },
            });

            Assert.Equal(new[] { "waiting", "broken", "alpha-ok", "zeta-ok" }, merged.Select(s => s.Name));
        }

        [Theory]
        [InlineData("connected", "connected")]
        [InlineData("failed", "failed")]
        [InlineData("error", "failed")]
        [InlineData("pending", "pending")]
        [InlineData("needs-auth", "pending")]
        [InlineData("something", "unknown")]
        [InlineData(null, "unknown")]
        public void PutsInitStatusOnTheSameScale(string input, string expected)
        {
            Assert.Equal(expected, McpStatus.NormalizeStatus(input));
        }
    }
}
