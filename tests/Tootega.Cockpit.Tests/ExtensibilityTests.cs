using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Session;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    public class ExtensibilityTests : IDisposable
    {
        private readonly string _root;

        public ExtensibilityTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "cockpit-ext-" + Guid.NewGuid().ToString("N"));
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

        // --- MCP inventory ---

        [Fact]
        public void SplitsToolNamesOnTheFirstSeparator()
        {
            // A sanitized server name never contains a double underscore, so the FIRST "__"
            // after the prefix is the boundary. Splitting on the last one would cut a tool
            // name like sql_execute_query in the wrong place.
            var inventory = McpInventory.ParseInventory(
                new[] { "mcp__plugin_mssql-localdb-mcp_mssql-localdb__sql_execute_query", "Read" },
                null);

            var server = inventory.Servers.Single();
            Assert.Equal("plugin_mssql-localdb-mcp_mssql-localdb", server.Key);
            Assert.Equal("sql_execute_query", server.Tools.Single());
            Assert.Equal(new[] { "Read" }, inventory.NativeTools);
        }

        [Fact]
        public void KeepsServersAnnouncedWithNoTools()
        {
            var inventory = McpInventory.ParseInventory(
                new[] { "Read" },
                new List<McpServerRef> { new McpServerRef { Name = "weather", Status = "failed" } });

            var server = inventory.Servers.Single();
            Assert.Equal("weather", server.Name);
            Assert.Equal("failed", server.Status);
            Assert.Empty(server.Tools);
        }

        [Fact]
        public void MatchesToolsToTheirAnnouncedServerThroughSanitisation()
        {
            // A plugin id contains characters the tool prefix cannot carry, so the announced
            // name and the prefix differ; sanitising is what links them.
            var inventory = McpInventory.ParseInventory(
                new[] { "mcp__my_plugin_server__do_thing" },
                new List<McpServerRef> { new McpServerRef { Name = "my:plugin:server", Status = "connected" } });

            var server = inventory.Servers.Single();
            Assert.Equal("my:plugin:server", server.Name);
            Assert.Equal("connected", server.Status);
            Assert.Equal("do_thing", server.Tools.Single());
        }

        [Fact]
        public void InventsAServerForToolsItDidNotAnnounce()
        {
            // The tools are demonstrably there, so dropping them would hide real context cost.
            var inventory = McpInventory.ParseInventory(new[] { "mcp__ghost__tool" }, null);

            Assert.Equal("ghost", inventory.Servers.Single().Name);
        }

        [Fact]
        public void DeduplicatesRepeatedTools()
        {
            var inventory = McpInventory.ParseInventory(
                new[] { "mcp__s__a", "mcp__s__a", "mcp__s__b" }, null);

            Assert.Equal(new[] { "a", "b" }, inventory.Servers.Single().Tools);
        }

        [Fact]
        public void ToleratesEmptyInput()
        {
            var inventory = McpInventory.ParseInventory(null, null);

            Assert.Empty(inventory.Servers);
            Assert.Empty(inventory.NativeTools);
        }

        // --- `claude mcp list` ---

        [Fact]
        public void ParsesTheThreeListShapes()
        {
            var entries = McpInventory.ParseList(
                "Checking MCP server health...\n" +
                "\n" +
                "dase: node D:\\tools\\dase.js  - \u2714 Connected\n" +
                "remote: https://example.com/mcp (HTTP)  - \u2714 Connected\n" +
                "broken:  (SSE)  - \u2717 Failed to connect\n" +
                "waiting: node x.js  - \u23f8 Pending approval (run /mcp)\n");

            Assert.Equal(4, entries.Count);

            Assert.Equal("dase", entries[0].Name);
            Assert.Equal(@"node D:\tools\dase.js", entries[0].Target);
            Assert.Null(entries[0].Transport);       // stdio has no suffix
            Assert.Equal(McpListStatuses.Connected, entries[0].Status);

            Assert.Equal("https://example.com/mcp", entries[1].Target);
            Assert.Equal("HTTP", entries[1].Transport);

            // A remote declared with no URL leaves only the transport behind.
            Assert.Null(entries[2].Target);
            Assert.Equal("SSE", entries[2].Transport);
            Assert.True(entries[2].NotConfigured);
            Assert.Equal(McpListStatuses.Failed, entries[2].Status);

            Assert.Equal(McpListStatuses.Pending, entries[3].Status);
        }

        [Theory]
        [InlineData("\u2714 Connected", "connected")]
        [InlineData("\u221a Connected", "connected")]      // the glyph changed between versions
        [InlineData("Connected", "connected")]
        [InlineData("\u2717 Failed to connect", "failed")]
        [InlineData("error starting server", "failed")]
        [InlineData("\u23f8 Pending approval", "pending")]
        [InlineData("something else entirely", "unknown")]
        public void ReadsStatusByWordNotByGlyph(string tail, string expected)
        {
            // Pinning to the symbol would silently break the panel on a CLI upgrade — it has
            // already changed once.
            Assert.Equal(expected, McpInventory.ListStatus(tail));
        }

        [Fact]
        public void IgnoresListNoise()
        {
            var entries = McpInventory.ParseList("Checking MCP server health...\n\nno colon here\n");

            Assert.Empty(entries);
        }

        // --- init mcp_server_errors ---

        private static JsonElement Element(string json)
        {
            using (var document = JsonDocument.Parse(json))
            {
                return document.RootElement.Clone();
            }
        }

        [Fact]
        public void NormalisesBothErrorShapes()
        {
            var errors = McpInventory.ParseErrors(Element(
                "[\"weather: invalid url\"," +
                "{\"name\":\"db\",\"error\":\"command not found\"}," +
                "{\"server\":\"api\",\"message\":\"timeout\"}," +
                "\"just a message with no name structure that is quite long indeed\"]"));

            Assert.Equal(4, errors.Count);
            Assert.Equal("weather", errors[0].Name);
            Assert.Equal("invalid url", errors[0].Error);
            Assert.Equal("db", errors[1].Name);
            Assert.Equal("api", errors[2].Name);
            Assert.Equal("timeout", errors[2].Error);
            Assert.Null(errors[3].Name);
        }

        [Fact]
        public void AnErrorWithoutAReasonStillCounts()
        {
            // Something went wrong even if the CLI did not say what.
            var errors = McpInventory.ParseErrors(Element("[{\"name\":\"db\"}]"));

            Assert.Equal("error", errors.Single().Error);
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("[]")]
        [InlineData("[\"\",\"   \"]")]
        public void UnusableErrorPayloadsYieldNothing(string json)
        {
            Assert.Empty(McpInventory.ParseErrors(Element(json)));
        }

        // --- Skill body recognition ---

        private void WriteSkill(string scopeRoot, string name, string body)
        {
            var dir = Path.Combine(scopeRoot, "skills", name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), body, new UTF8Encoding(false));
        }

        private static string LongBody(string marker)
        {
            return marker + " " + string.Join(" ", Enumerable.Repeat("distinctive body text", 20));
        }

        [Fact]
        public void RecognisesAnInjectedSkillBody()
        {
            var user = Path.Combine(_root, "user");
            WriteSkill(user, "caveman", "---\nname: caveman\n---\n" + LongBody("CAVEMAN"));

            var index = new SkillBodyIndex(user);
            var injected = "Some hook preamble.\n" + LongBody("CAVEMAN");

            Assert.Equal("caveman", index.Match(injected, new[] { "caveman" }));
        }

        [Fact]
        public void SurvivesReformattingByTheHook()
        {
            // A hook may re-indent or change line endings on what it copied.
            var user = Path.Combine(_root, "user");
            WriteSkill(user, "caveman", "---\nx: 1\n---\n" + LongBody("CAVEMAN"));

            var index = new SkillBodyIndex(user);
            var reformatted = "   " + LongBody("CAVEMAN").Replace(" ", "\r\n\t").ToUpperInvariant();

            Assert.Equal("caveman", index.Match(reformatted, new[] { "caveman" }));
        }

        [Fact]
        public void IgnoresTheFrontmatterWhenSigning()
        {
            // The frontmatter is what the listing already accounts for; matching on it would
            // fire on a metadata-only injection.
            var user = Path.Combine(_root, "user");
            WriteSkill(user, "caveman", "---\nname: caveman\ndescription: something\n---\n" + LongBody("BODY"));

            var index = new SkillBodyIndex(user);

            Assert.Null(index.Match("---\nname: caveman\ndescription: something\n---\n", new[] { "caveman" }));
        }

        [Fact]
        public void ShortBodiesAreNotUsedAsSignatures()
        {
            // Too short is too generic, and a false positive would mislabel hook context as a
            // loaded skill.
            var user = Path.Combine(_root, "user");
            WriteSkill(user, "tiny", "---\nx: 1\n---\nshort");

            var index = new SkillBodyIndex(user);

            Assert.Null(index.Signature("tiny"));
            Assert.Null(index.Match("short", new[] { "tiny" }));
        }

        [Fact]
        public void BuiltInSkillsNeverMatch()
        {
            // They have no file on disk, so their injection stays accounted as hook context
            // without a name — honest rather than convenient.
            var index = new SkillBodyIndex(Path.Combine(_root, "user"));

            Assert.Null(index.Match(LongBody("ANYTHING"), new[] { "some-builtin" }));
        }

        [Fact]
        public void ProjectScopeWinsOverUserScope()
        {
            var user = Path.Combine(_root, "user");
            var project = Path.Combine(_root, "project");
            WriteSkill(user, "shared", "---\nx: 1\n---\n" + LongBody("USERVERSION"));
            WriteSkill(Path.Combine(project, ".claude"), "shared", "---\nx: 1\n---\n" + LongBody("PROJECTVERSION"));

            var index = new SkillBodyIndex(user);

            Assert.Equal("shared", index.Match(LongBody("PROJECTVERSION"), new[] { "shared" }, project));
            Assert.Null(index.Match(LongBody("USERVERSION"), new[] { "shared" }, project));
        }

        [Fact]
        public void ListsSkillsOnDiskAcrossScopes()
        {
            // SessionStart hooks fire before init announces the skill list, and that first
            // injection is the one carrying the whole body.
            var user = Path.Combine(_root, "user");
            var project = Path.Combine(_root, "project");
            WriteSkill(user, "from-user", "---\nx: 1\n---\nbody");
            WriteSkill(Path.Combine(project, ".claude"), "from-project", "---\nx: 1\n---\nbody");

            var names = new SkillBodyIndex(user).NamesOnDisk(project);

            Assert.Contains("from-user", names);
            Assert.Contains("from-project", names);
        }

        [Fact]
        public void MatchIsSafeWithNoCandidates()
        {
            var index = new SkillBodyIndex(Path.Combine(_root, "user"));

            Assert.Null(index.Match("text", new string[0]));
            Assert.Null(index.Match(null, new[] { "x" }));
            Assert.Null(index.Match("too short", new[] { "x" }));
        }

        // --- Repository directives ---

        [Theory]
        [InlineData("<!-- **enffort=max** -->", "max")]
        [InlineData("<!-- enffort=high -->", "high")]
        [InlineData("<!-- effort = medium -->", "medium")]
        [InlineData("<!-- enfor=**low** -->", "low")]
        [InlineData("text before <!-- **enffort = xhigh** --> text after", "xhigh")]
        [InlineData("ENFFORT=MAX", "max")]
        public void ReadsTheMinimumEffortTag(string text, string expected)
        {
            // The spelling variants exist because this is typed by hand; a directive that
            // fails on a typo is worse than accepting three spellings.
            Assert.Equal(expected, RepoDirectives.ParseMinEffort(text));
        }

        [Theory]
        [InlineData("no tag here")]
        [InlineData("<!-- enffort=turbo -->")]
        [InlineData("")]
        [InlineData(null)]
        public void IgnoresAnythingThatIsNotAKnownLevel(string text)
        {
            Assert.Null(RepoDirectives.ParseMinEffort(text));
        }

        [Fact]
        public void TheDeepestDeclarationWins()
        {
            // A subtree can reasonably demand more than the repository as a whole.
            var root = Path.Combine(_root, "repo");
            var nested = Path.Combine(root, "src", "critical");
            Directory.CreateDirectory(nested);

            File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "<!-- enffort=low -->", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(nested, "CLAUDE.md"), "<!-- enffort=max -->", new UTF8Encoding(false));

            Assert.Equal("max", RepoDirectives.ResolveMinEffort(nested, root));
            Assert.Equal("low", RepoDirectives.ResolveMinEffort(root, root));
        }

        [Fact]
        public void FindsTheTagInTheDotClaudeFolderToo()
        {
            var root = Path.Combine(_root, "repo2");
            Directory.CreateDirectory(Path.Combine(root, ".claude"));
            File.WriteAllText(Path.Combine(root, ".claude", "CLAUDE.md"), "<!-- enffort=high -->", new UTF8Encoding(false));

            Assert.Equal("high", RepoDirectives.ResolveMinEffort(root, root));
        }

        [Fact]
        public void NoDeclarationAnywhereMeansNoFloor()
        {
            var root = Path.Combine(_root, "repo3");
            Directory.CreateDirectory(root);

            Assert.Null(RepoDirectives.ResolveMinEffort(root, root));
            Assert.Null(RepoDirectives.ResolveMinEffort(null, root));
        }

        [Theory]
        [InlineData("low", "max", true)]
        [InlineData("high", "max", true)]
        [InlineData("max", "max", false)]
        [InlineData("max", "low", false)]
        [InlineData("high", null, false)]
        [InlineData(null, "max", false)]        // unknown selection: let it through
        [InlineData("default", "max", false)]   // 'default' is not a level we can rank
        [InlineData("high", "turbo", false)]    // unknown floor: cannot enforce
        public void ComparesEffortAgainstTheFloor(string selected, string minimum, bool below)
        {
            // Refusing to send because a value was not recognised would be worse than
            // letting it through.
            Assert.Equal(below, RepoDirectives.IsBelow(selected, minimum));
        }

        // --- System prompt template ---

        private static ShellEnvironment FullEnvironment() => new ShellEnvironment
        {
            DefaultShell = "PowerShell 7.6.4",
            PsVersion = "7.6.4",
            GitBash = true,
            Wsl = "Ubuntu",
            WinPathStyle = @"Windows (C:\...)",
        };

        private static ShellEnvironment BareEnvironment() => new ShellEnvironment
        {
            DefaultShell = "cmd.exe",
            GitBash = false,
            Wsl = null,
            WinPathStyle = @"Windows (C:\...)",
        };

        [Fact]
        public void SubstitutesResolvedPlaceholders()
        {
            var vars = SystemPromptTemplate.BuildVars(@"D:\work\project", FullEnvironment());

            var expanded = SystemPromptTemplate.Expand(
                "Shell: ${defaultShell}\nProject: ${projectPathWin}\nBash: ${projectPathGitBash}", vars);

            Assert.Contains("Shell: PowerShell 7.6.4", expanded);
            Assert.Contains(@"Project: D:\work\project", expanded);
            Assert.Contains("Bash: /d/work/project", expanded);
        }

        [Fact]
        public void DropsTheWholeLineWhenADependencyIsMissing()
        {
            // A table row describing a shell the machine does not have is worse than no row:
            // it actively misleads the agent into writing commands that cannot run.
            var vars = SystemPromptTemplate.BuildVars(@"D:\work", BareEnvironment());

            var expanded = SystemPromptTemplate.Expand(
                "| Shell | Path |\n" +
                "| PowerShell | ${projectPathWin} |\n" +
                "| Git Bash | ${projectPathGitBash} |\n" +
                "${wslRow}\n" +
                "End.", vars);

            Assert.Contains("| PowerShell |", expanded);
            Assert.DoesNotContain("Git Bash", expanded);
            Assert.DoesNotContain("WSL", expanded);
            Assert.Contains("End.", expanded);
        }

        [Fact]
        public void BuildsTheWslRowOnlyWhenADistributionExists()
        {
            var withWsl = SystemPromptTemplate.BuildVars(@"D:\work", FullEnvironment());
            Assert.Contains("/mnt/d/work", withWsl["wslRow"]);
            Assert.Equal("/mnt/d/work", withWsl["projectPathWsl"]);

            var withoutWsl = SystemPromptTemplate.BuildVars(@"D:\work", BareEnvironment());
            Assert.Null(withoutWsl["wslRow"]);
            Assert.Null(withoutWsl["projectPathWsl"]);
        }

        [Fact]
        public void KeepsUnknownPlaceholdersVerbatim()
        {
            // Inventing a value would be worse than showing the user their own typo.
            var vars = SystemPromptTemplate.BuildVars(@"D:\work", FullEnvironment());

            Assert.Contains("${somethingElse}", SystemPromptTemplate.Expand("Value: ${somethingElse}", vars));
        }

        [Fact]
        public void CollapsesTheGapLeftByARemovedLine()
        {
            var vars = SystemPromptTemplate.BuildVars(@"D:\work", BareEnvironment());

            var expanded = SystemPromptTemplate.Expand("A\n\n${projectPathGitBash}\n\nB", vars);

            Assert.DoesNotContain("\n\n\n", expanded);
        }

        [Theory]
        [InlineData(@"D:\a\b", "/d/a/b")]
        [InlineData(@"C:\Program Files\x", "/c/Program Files/x")]
        [InlineData("/already/posix", "/already/posix")]
        public void ConvertsPathsForGitBash(string input, string expected)
        {
            Assert.Equal(expected, SystemPromptTemplate.ToGitBashPath(input));
        }

        [Theory]
        [InlineData(@"D:\a\b", "/mnt/d/a/b")]
        [InlineData("/already/posix", "/already/posix")]
        public void ConvertsPathsForWsl(string input, string expected)
        {
            Assert.Equal(expected, SystemPromptTemplate.ToWslPath(input));
        }

        [Fact]
        public void BuildReturnsNothingWhenThereIsNothingToInject()
        {
            Assert.Null(SystemPromptTemplate.Build(null, @"D:\work"));
            Assert.Null(SystemPromptTemplate.Build("   ", @"D:\work"));
        }

        [Fact]
        public void BuildReturnsNothingWhenEveryLineWasDropped()
        {
            // The template validated away entirely, which is not the same as it being empty —
            // but the outcome for the CLI is: send no flag rather than an empty one.
            SystemPromptTemplate.ResetEnvironmentCache();
            var vars = SystemPromptTemplate.BuildVars(@"D:\work", BareEnvironment());

            Assert.Equal(string.Empty, SystemPromptTemplate.Expand("${wslRow}\n${projectPathGitBash}", vars));
        }
    }
}
