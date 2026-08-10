using System.Collections.Generic;
using System.Text.Json;
using Tootega.Cockpit.Protocol;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// Guards the wire format. Every name here is a contract with the React webview or with
    /// the Claude Code CLI — a typo produces no build error and no exception, just a value
    /// the other side silently reads as undefined. These tests are the only place that
    /// catches that.
    /// </summary>
    public class ProtocolWireTests
    {
        private static JsonElement Parse(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                return doc.RootElement.Clone();
            }
        }

        // --- Host -> webview ---

        [Fact]
        public void HostMessage_CarriesKind()
        {
            var json = Parse(HostMessages.AssistantText("m1", "hello").ToJson());

            Assert.Equal("assistantText", json.GetProperty("kind").GetString());
            Assert.Equal("m1", json.GetProperty("id").GetString());
            Assert.Equal("hello", json.GetProperty("delta").GetString());
        }

        [Fact]
        public void HostMessage_OmitsNullFields()
        {
            // "absent" and "null" are the same thing to the webview's types, and omitting
            // matters: these messages go out once per streamed token.
            var json = Parse(HostMessages.TurnComplete().ToJson());

            Assert.Equal("turnComplete", json.GetProperty("kind").GetString());
            Assert.False(json.TryGetProperty("costUsd", out _));
            Assert.False(json.TryGetProperty("usage", out _));
        }

        [Fact]
        public void HostMessage_WithTab_AddsRoutingKey()
        {
            var json = Parse(HostMessages.Busy(true).WithTab("tab-2").ToJson());

            Assert.Equal("tab-2", json.GetProperty("tab").GetString());
            Assert.True(json.GetProperty("busy").GetBoolean());
        }

        [Fact]
        public void HostMessage_WithTab_IgnoresEmptyTab()
        {
            // Global messages (config, sessions, locale) travel without a tab.
            var json = Parse(HostMessages.Busy(false).WithTab(null).ToJson());

            Assert.False(json.TryGetProperty("tab", out _));
        }

        [Fact]
        public void Selection_UsesRefKey()
        {
            // The webview reads `ref`, which is not a legal C# member name — this is
            // exactly the kind of mapping that silently breaks.
            var json = Parse(HostMessages.Selection("src/a.cs#10-20").ToJson());

            Assert.Equal("src/a.cs#10-20", json.GetProperty("ref").GetString());
        }

        [Fact]
        public void HookInjected_UsesEventKey()
        {
            // `event` is a C# keyword; the parameter is @event and must still serialize bare.
            var json = Parse(HostMessages.HookInjected("SessionStart:startup", "SessionStart", tokens: 120).ToJson());

            Assert.Equal("SessionStart", json.GetProperty("event").GetString());
            Assert.Equal(120, json.GetProperty("tokens").GetInt64());
        }

        // --- DTO casing ---

        [Fact]
        public void StatsSnapshot_SerializesToCamelCase()
        {
            var stats = new StatsSnapshot
            {
                ContextUsed = 1234,
                ContextLimit = 200000,
                CacheHitRate = 0.95,
                SessionCostUsd = 1.25,
                CostIsEstimate = true,
                LimitsSource = "statusline",
            };

            var json = Parse(HostMessages.Stats(stats).ToJson()).GetProperty("stats");

            Assert.Equal(1234, json.GetProperty("contextUsed").GetInt64());
            Assert.Equal(200000, json.GetProperty("contextLimit").GetInt64());
            Assert.Equal(0.95, json.GetProperty("cacheHitRate").GetDouble(), 6);
            Assert.Equal(1.25, json.GetProperty("sessionCostUsd").GetDouble(), 6);
            Assert.True(json.GetProperty("costIsEstimate").GetBoolean());
            Assert.Equal("statusline", json.GetProperty("limitsSource").GetString());
        }

        [Fact]
        public void ModelMeta_KeepsAcronymCasing()
        {
            // inMTok / outMTok are the names the model picker reads. A naive camelCase of
            // "InMTok" could plausibly come out "inMTok" or "inmTok"; pin it.
            var config = new SessionConfig
            {
                ModelMeta = new Dictionary<string, ModelMeta>
                {
                    ["claude-opus-5"] = new ModelMeta { InMTok = 15, OutMTok = 75, ContextTokens = 200000, PriceMult = 1 },
                },
            };

            var meta = Parse(HostMessages.Config(config).ToJson())
                .GetProperty("config").GetProperty("modelMeta").GetProperty("claude-opus-5");

            Assert.Equal(15, meta.GetProperty("inMTok").GetDouble());
            Assert.Equal(75, meta.GetProperty("outMTok").GetDouble());
            Assert.Equal(200000, meta.GetProperty("contextTokens").GetInt64());
            Assert.Equal(1, meta.GetProperty("priceMult").GetDouble());
        }

        [Fact]
        public void LimitWindow_UsesUsedPct()
        {
            var stats = new StatsSnapshot
            {
                Limits = new LimitsBlock
                {
                    FiveHour = new LimitWindow { UsedPct = 0.42, ResetsAt = "2026-08-09T12:00:00Z", Status = "allowed" },
                },
            };

            var five = Parse(HostMessages.Stats(stats).ToJson())
                .GetProperty("stats").GetProperty("limits").GetProperty("fiveHour");

            Assert.Equal(0.42, five.GetProperty("usedPct").GetDouble(), 6);
            Assert.Equal("2026-08-09T12:00:00Z", five.GetProperty("resetsAt").GetString());
        }

        [Fact]
        public void ScopedBucket_SerializesInheritedMembers()
        {
            var data = new UsageData
            {
                Buckets = new UsageBuckets
                {
                    WeeklyScoped = new List<ScopedBucket>
                    {
                        new ScopedBucket { Label = "Opus", UsedPct = 0.6, Usd = 3.5 },
                    },
                },
                Source = "api",
                GeneratedAt = "2026-08-09T00:00:00Z",
            };

            var scoped = Parse(HostMessages.UsageData(data).ToJson())
                .GetProperty("data").GetProperty("buckets").GetProperty("weeklyScoped")[0];

            Assert.Equal("Opus", scoped.GetProperty("label").GetString());
            Assert.Equal(0.6, scoped.GetProperty("usedPct").GetDouble(), 6);
            Assert.Equal(3.5, scoped.GetProperty("usd").GetDouble(), 6);
        }

        // --- Webview -> host ---

        [Fact]
        public void WebviewMessage_ParsesKindAndScalars()
        {
            var msg = WebviewMessage.Parse("{\"kind\":\"setModel\",\"model\":\"claude-opus-5\"}");

            Assert.NotNull(msg);
            Assert.Equal(WebviewMessageKinds.SetModel, msg.Kind);
            Assert.Equal("claude-opus-5", msg.GetString("model"));
        }

        [Fact]
        public void WebviewMessage_ParsesTypedPayload()
        {
            var msg = WebviewMessage.Parse(
                "{\"kind\":\"sendMessage\",\"text\":\"hi\",\"force\":true," +
                "\"images\":[{\"mediaType\":\"image/png\",\"data\":\"AAA\"}]}");

            var payload = msg.As<SendMessagePayload>();

            Assert.Equal("hi", payload.Text);
            Assert.True(payload.Force);
            Assert.Single(payload.Images);
            Assert.Equal("image/png", payload.Images[0].MediaType);
            Assert.Equal("AAA", payload.Images[0].Data);
        }

        [Fact]
        public void WebviewMessage_ToleratesUnknownFields()
        {
            // A newer webview may send fields this host does not model yet.
            var msg = WebviewMessage.Parse("{\"kind\":\"interrupt\",\"somethingNew\":42}");

            Assert.NotNull(msg);
            Assert.Equal(WebviewMessageKinds.Interrupt, msg.Kind);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json")]
        [InlineData("[1,2,3]")]
        [InlineData("{\"no\":\"kind\"}")]
        [InlineData("{\"kind\":42}")]
        public void WebviewMessage_ReturnsNullForUnusableInput(string json)
        {
            // A malformed message must not throw into the WebView2 event handler.
            Assert.Null(WebviewMessage.Parse(json));
        }

        [Fact]
        public void WebviewMessage_MissingScalarsFallBack()
        {
            var msg = WebviewMessage.Parse("{\"kind\":\"rewind\"}");

            Assert.Equal(-1, msg.GetInt("index", -1));
            Assert.True(msg.GetBool("force", true));
            Assert.Null(msg.GetString("text"));
            Assert.Null(msg.GetStringList("absPaths"));
        }

        [Fact]
        public void AskResponse_ParsesAnswerMap()
        {
            var msg = WebviewMessage.Parse(
                "{\"kind\":\"askResponse\",\"requestId\":\"r1\",\"answers\":{\"Which one?\":\"A\"}}");

            var payload = msg.As<AskResponsePayload>();

            Assert.Equal("r1", payload.RequestId);
            Assert.Equal("A", payload.Answers["Which one?"]);
        }
    }
}
