using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Host;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Secrets;
using Tootega.Cockpit.Session;
using Tootega.Cockpit.Util;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// The auxiliary panels: the export, the native diff, the skill overrides and the vault.
    ///
    /// Their logic is small but each has one behaviour that is destructive or misleading when
    /// wrong — an export that overwrites the previous document, a diff preview that does not
    /// match what the tool will actually write, an override leaking into another project, an
    /// error message echoing a secret. Those are what these tests hold in place.
    /// </summary>
    public class AuxiliarySurfaceTests : IDisposable
    {
        private readonly string _root;

        public AuxiliarySurfaceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "cockpit-aux-" + Guid.NewGuid().ToString("N"));
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
                // A leftover temp folder is not worth failing a test over.
            }
        }

        // ---- Export ----

        [Fact]
        public void ExportingTwiceNeverOverwritesTheFirstDocument()
        {
            var first = Path.Combine(_root, "conversation.md");
            File.WriteAllText(first, "the first export");

            var second = ConversationExporter.UniquePath(first);
            Assert.Equal(Path.Combine(_root, "conversation-2.md"), second);

            File.WriteAllText(second, "the second export");
            Assert.Equal(Path.Combine(_root, "conversation-3.md"), ConversationExporter.UniquePath(first));

            // And the original is still there, which is the whole point.
            Assert.Equal("the first export", File.ReadAllText(first));
        }

        [Fact]
        public void TheDocumentIsReadFromTheCliResultEnvelope()
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["type"] = "result",
                ["result"] = "# Title\n\nThe document.",
            });

            Assert.Equal("# Title\n\nThe document.", ConversationExporter.ExtractResult(json));
        }

        [Fact]
        public void AnArrayEnvelopeIsAcceptedToo()
        {
            // The wrapper shape has changed between CLI versions; tolerance here costs nothing
            // and losing a document that is right there costs the user their tokens.
            var json = "[{\"type\":\"system\"},{\"type\":\"result\",\"result\":\"The document.\"}]";
            Assert.Equal("The document.", ConversationExporter.ExtractResult(json));
        }

        [Fact]
        public void NonJsonOutputIsUsedAsTheDocument()
        {
            Assert.Equal("Just markdown.", ConversationExporter.ExtractResult("Just markdown."));
        }

        [Fact]
        public void AFenceWrappingTheWholeDocumentIsRemovedButInnerOnesSurvive()
        {
            var text = "```markdown\n# Title\n\n```csharp\nvar x = 1;\n```\n\nEnd.\n```";

            var stripped = ConversationExporter.StripFence(text);

            Assert.StartsWith("# Title", stripped);
            Assert.Contains("```csharp", stripped);
            Assert.Contains("var x = 1;", stripped);
            Assert.EndsWith("End.", stripped);
        }

        [Fact]
        public void TextWithNoWrappingFenceIsLeftAlone()
        {
            const string text = "# Title\n\nNo fence here.";
            Assert.Equal(text, ConversationExporter.StripFence(text));
        }

        // ---- Native diff ----

        private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

        [Fact]
        public void WriteProposesItsWholeContent()
        {
            var after = DiffLauncher.Apply("Write", "old body", Input("{\"content\":\"new body\"}"));
            Assert.Equal("new body", after);
        }

        [Fact]
        public void EditProposesTheSameSubstitutionTheToolWillMake()
        {
            var after = DiffLauncher.Apply("Edit", "a b a",
                Input("{\"old_string\":\"a\",\"new_string\":\"z\"}"));

            // Every occurrence, exactly as the CLI applies it: a preview that differs from the
            // real edit is worse than no preview at all.
            Assert.Equal("z b z", after);
        }

        [Fact]
        public void MultiEditAppliesTheEditsInOrder()
        {
            var after = DiffLauncher.Apply("MultiEdit", "one two",
                Input("{\"edits\":[{\"old_string\":\"one\",\"new_string\":\"1\"}," +
                      "{\"old_string\":\"two\",\"new_string\":\"2\"}]}"));

            Assert.Equal("1 2", after);
        }

        [Fact]
        public void ToolsWithNothingToDiffAreRefusedRatherThanShownEmpty()
        {
            Assert.Null(DiffLauncher.Apply("Bash", "body", Input("{\"command\":\"ls\"}")));
            Assert.Null(DiffLauncher.Apply("Edit", "body", Input("{\"new_string\":\"z\"}")));
            Assert.Null(DiffLauncher.Apply("Write", "body", null));
        }

        // ---- Skill overrides ----

        private ExtensionsBroker NewBroker()
        {
            var state = new StateStore(Path.Combine(_root, "state"));

            return new ExtensionsBroker(
                new PluginManager(new AiClient(), Path.Combine(_root, "plugins")),
                state,
                () => "claude",
                (message, tab) => { });
        }

        [Fact]
        public void ASkillOverrideBelongsToItsFolderAlone()
        {
            var broker = NewBroker();
            var a = Path.Combine(_root, "project-a");
            var b = Path.Combine(_root, "project-b");

            broker.SetOverride(a, "pdf", "off", null);

            Assert.Equal("off", broker.OverridesFor(a)["pdf"]);

            // `.claude/skills/` belongs to the project, so an override set in one repository
            // must not follow the user into another.
            Assert.Empty(broker.OverridesFor(b));
        }

        [Fact]
        public void TurningASkillBackOnStoresNothingRatherThanTheDefault()
        {
            var broker = NewBroker();
            var cwd = Path.Combine(_root, "project");

            broker.SetOverride(cwd, "pdf", "off", null);
            broker.SetOverride(cwd, "pdf", "on", null);

            // 'on' is the default; recording it would grow a file entry for every skill the
            // user ever looked at.
            Assert.Empty(broker.OverridesFor(cwd));
        }

        [Fact]
        public void OverridesSurviveANewBroker()
        {
            var cwd = Path.Combine(_root, "project");
            NewBroker().SetOverride(cwd, "pdf", "off", null);

            Assert.Equal("off", NewBroker().OverridesFor(cwd)["pdf"]);
        }

        // ---- Usage sources ----

        private static LimitWindow Window(double? pct) => new LimitWindow { UsedPct = pct };

        [Fact]
        public void TheAccountApiWinsOverEverythingElse()
        {
            var api = new ApiUsage { FiveHour = Window(0.42) };
            var cached = new RealLimits { FiveHour = Window(0.10), AgeMs = 0 };
            var local = new LocalUsage { FiveHourUsd = 3 };

            var chosen = UsageMonitor.Select(api, cached, local);

            // It is the same source the CLI's own /usage reads, so it matches exactly.
            Assert.Equal("api", chosen.UsageSource);
            Assert.Equal(0.42, chosen.Limits.FiveHour.UsedPct);
        }

        [Fact]
        public void AFreshStatuslineCacheIsUsedWhenTheApiSaysNothing()
        {
            var chosen = UsageMonitor.Select(null, new RealLimits { FiveHour = Window(0.10), AgeMs = 1000 }, null);

            Assert.Equal("statusline", chosen.UsageSource);
            Assert.Equal("real", chosen.LimitsSource);
        }

        [Fact]
        public void AStaleStatuslineCacheIsNotTrusted()
        {
            var stale = new RealLimits { FiveHour = Window(0.99), AgeMs = 60 * 60 * 1000 };

            Assert.False(UsageMonitor.Fresh(stale));

            // Showing an hours-old percentage as the current one is the failure this guards.
            var chosen = UsageMonitor.Select(null, stale, new LocalUsage { FiveHourUsd = 3, FiveHourTokens = 900 });

            Assert.Equal("estimate", chosen.UsageSource);
            Assert.Null(chosen.Limits.FiveHour.UsedPct);
        }

        [Fact]
        public void ACacheWithNoTimestampIsStillBelieved()
        {
            // The field postdates some payloads; discarding a real reading over a missing
            // timestamp would throw away the percentage for nothing.
            Assert.True(UsageMonitor.Fresh(new RealLimits { SevenDay = Window(0.2) }));
        }

        [Fact]
        public void TheLocalEstimateCarriesCostAndTokensButNeverAPercentage()
        {
            var local = new LocalUsage
            {
                FiveHourUsd = 1.5,
                FiveHourTokens = 1200,
                SevenDayUsd = 9,
                SevenDayTokens = 40_000,
            };

            var chosen = UsageMonitor.Select(null, null, local);

            Assert.Equal("estimate", chosen.UsageSource);
            Assert.Equal(1.5, chosen.Limits.FiveHour.Usd);
            Assert.Equal(40_000, chosen.Limits.SevenDay.Tokens);

            // This machine cannot see the user's other devices, so it does not know the share
            // of the limit that has been used.
            Assert.Null(chosen.Limits.FiveHour.UsedPct);
            Assert.Null(chosen.Limits.SevenDay.UsedPct);
            Assert.Null(chosen.Scoped);
        }

        [Fact]
        public void AnEmptyApiAnswerIsNotASource()
        {
            Assert.False(UsageMonitor.Usable(new ApiUsage()));
            Assert.False(UsageMonitor.Usable(null));
        }

        // ---- Vault ----

        private sealed class Surface
        {
            public readonly List<HostMessage> Messages = new List<HostMessage>();

            public void Post(HostMessage message, string tabId) => Messages.Add(message);

            public HostMessage Last(string kind) => Messages.LastOrDefault(m => m.Kind == kind);

            public string Json(string kind) => Last(kind)?.ToJson() ?? string.Empty;
        }

        private static WebviewMessage Message(string kind, params (string Key, object Value)[] fields)
        {
            var payload = new Dictionary<string, object> { ["kind"] = kind };
            foreach (var field in fields) payload[field.Key] = field.Value;

            return WebviewMessage.Parse(Protocol.Json.Serialize(payload));
        }

        [Fact]
        public void AVaultWithNoStorageSaysSoInsteadOfFailingSilently()
        {
            var surface = new Surface();
            new VaultBroker(null, surface.Post).Handle("tab-1", Message(WebviewMessageKinds.CredsLoad));

            Assert.NotNull(surface.Last("credsError"));
        }

        [Fact]
        public void ACredentialIsStoredAndReadBackOnlyWithAValidCode()
        {
            var storage = new InMemorySecretStorage();
            var store = new CredentialsStore(storage);
            var surface = new Surface();
            var vault = new VaultBroker(store, surface.Post);

            // Enrolment: the secret comes back once, and the first code confirms it.
            vault.Handle("tab-1", Message(WebviewMessageKinds.CredsEnrollBegin));
            var setup = surface.Last("credsSetup");
            Assert.NotNull(setup);

            var secret = SecretOf(setup);
            vault.Handle("tab-1", Message(WebviewMessageKinds.CredsEnrollConfirm, ("code", Code(secret))));
            Assert.Contains("\"ok\":true", surface.Json("credsResult"));

            vault.Handle("tab-1", Message(WebviewMessageKinds.CredsAdd,
                ("code", Code(secret)), ("name", "Staging DB"), ("value", "s3cr3t"), ("username", "svc")));
            Assert.Contains("\"ok\":true", surface.Json("credsResult"));

            var id = store.List().Single().Id;

            // A wrong code returns a refusal, never the value.
            vault.Handle("tab-1", Message(WebviewMessageKinds.CredsUse, ("code", "000000"), ("id", id)));
            Assert.Contains("\"ok\":false", surface.Json("credsResult"));
            Assert.Null(surface.Last("credsValue"));

            vault.Handle("tab-1", Message(WebviewMessageKinds.CredsUse, ("code", Code(secret)), ("id", id)));
            Assert.Contains("s3cr3t", surface.Json("credsValue"));
        }

        [Fact]
        public void ARefusalNeverEchoesTheValueBack()
        {
            var store = new CredentialsStore(new InMemorySecretStorage());
            var surface = new Surface();
            var vault = new VaultBroker(store, surface.Post);

            // Not enrolled, so this cannot succeed — and the message must not carry the secret
            // the caller just tried to store.
            vault.Handle("tab-1", Message(WebviewMessageKinds.CredsAdd,
                ("code", "123456"), ("name", "Prod"), ("value", "do-not-leak-me")));

            var everything = string.Join(" ", surface.Messages.Select(m => m.ToJson()));
            Assert.DoesNotContain("do-not-leak-me", everything);
        }

        private static string SecretOf(HostMessage setup)
        {
            using (var document = JsonDocument.Parse(setup.ToJson()))
            {
                return document.RootElement.GetProperty("secret").GetString();
            }
        }

        private static string Code(string secret)
        {
            return Totp.Hotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        }
    }
}
