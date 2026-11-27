using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Session;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// Reopening a session must show the context guard and the compaction the way the live session
    /// did: banners, not the guard's messages as prompts the user never typed. The lines mirror a
    /// real CLI transcript (2.1.3xx). Port of test/TranscriptGuard.test.ts in the base repo.
    /// </summary>
    public class TranscriptGuardTests
    {
        private readonly SessionStore _store = new SessionStore();

        private static string User(string uuid, string text, bool compactSummary = false) =>
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["type"] = "user",
                ["uuid"] = uuid,
                ["timestamp"] = "2026-11-26T01:50:42.148Z",
                ["message"] = new { role = "user", content = text },
                ["isCompactSummary"] = compactSummary,
            });

        private static string Assistant(string id, string text) =>
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                uuid = "u-" + id,
                message = new { id, role = "assistant", content = new[] { new { type = "text", text } } },
            });

        private static string Boundary(string uuid) =>
            JsonSerializer.Serialize(new
            {
                type = "system",
                subtype = "compact_boundary",
                uuid,
                timestamp = "2026-11-26T01:52:14.982Z",
                content = "Conversation compacted",
                compactMetadata = new { trigger = "manual", preTokens = 804640, postTokens = 17492, durationMs = 65127 },
            });

        private static string Queued(string uuid, string text) =>
            JsonSerializer.Serialize(new
            {
                type = "attachment",
                uuid,
                attachment = new { type = "queued_command", prompt = new[] { new { type = "text", text } } },
            });

        [Fact]
        public void GuardRunThatCompactedIsOneGuardBannerPlusTheCompactionBanner()
        {
            var items = _store.ParseTranscript(new[]
            {
                User("p1", "do the task"),
                Assistant("a1", "working"),
                User("g1", ContextGuard.SavePrompt(80, 80, "109cc5b4")),
                Assistant("a2", "saved\nCOCKPIT-CONTEXT-SAVED 109cc5b4"),
                Boundary("b1"),
                User("s1", "This session is being continued from a previous conversation…", compactSummary: true),
                User("c1", "<command-name>/compact</command-name>"),
                User("r1", ContextGuard.ResumePrompt),
                Assistant("a3", "resuming"),
            });

            Assert.Equal(new[] { "do the task" }, items.Where(i => i.Kind == "user").Select(i => i.Text));
            Assert.Equal(new[] { "user", "assistant", "notice", "assistant", "notice", "assistant" }, items.Select(i => i.Kind));

            var guard = items[2].Guard;
            Assert.Equal("done", guard.Phase);
            Assert.Equal(80, guard.Pct);
            Assert.Equal(80, guard.Threshold);
            Assert.Equal(804640, guard.Pre);
            Assert.Equal(17492, guard.Post);
            Assert.True(guard.Resumed);

            var compaction = items[4];
            Assert.Equal("compact_boundary", compaction.Topic);
            Assert.Equal(804640, compaction.Compaction.Pre);
            Assert.Equal(17492, compaction.Compaction.Post);
            Assert.Equal("manual", compaction.Compaction.Trigger);
            Assert.Equal(65127d, compaction.Compaction.DurationMs);
        }

        [Fact]
        public void MidTurnSaveRequestOpensTheRunButAQuotedGuardTextInAToolResultDoesNot()
        {
            var toolUse = JsonSerializer.Serialize(new
            {
                type = "assistant",
                uuid = "u-t",
                message = new { id = "a1", content = new[] { new { type = "tool_use", id = "toolu_1", name = "Bash" } } },
            });
            var toolResult = JsonSerializer.Serialize(new
            {
                type = "user",
                uuid = "tr",
                message = new
                {
                    content = new[]
                    {
                        new { type = "tool_result", tool_use_id = "toolu_1", content = "log: " + ContextGuard.SavePrompt(90, 80, "zzzz") },
                    },
                },
            });

            var items = _store.ParseTranscript(new[] { toolUse, toolResult, Queued("q1", ContextGuard.SavePrompt(83, 80, "abcd1234")) });

            var notice = Assert.Single(items, i => i.Kind == "notice");
            // Nothing compacted after it: it ended without a compaction, not "still saving" forever.
            Assert.Equal("failed", notice.Guard.Phase);
            Assert.Equal(83, notice.Guard.Pct);
            Assert.Equal("no-compaction", notice.Guard.Reason);
        }

        [Fact]
        public void RetryIsNotAPromptAndTheSameRequestRecordedTwiceIsOneBanner()
        {
            var save = ContextGuard.SavePrompt(80, 80, "n1");
            var items = _store.ParseTranscript(new[] { User("g1", save), Queued("q1", save), User("g2", ContextGuard.RetryPrompt("n1")), Boundary("b1") });

            Assert.DoesNotContain(items, i => i.Kind == "user");
            Assert.Single(items, i => i.Guard != null);
        }

        [Fact]
        public void ManualCompactionIsACompactionBannerWithNoGuardBanner()
        {
            var items = _store.ParseTranscript(new[] { User("p1", "hi"), Boundary("b1"), User("c1", "<command-name>/compact</command-name>") });

            Assert.Equal(new[] { "user", "compact_boundary" }, items.Select(i => i.Kind == "notice" ? i.Topic : i.Kind));
        }

        [Fact]
        public void NoticeSerializesWithTheWireShapeOfTheBase()
        {
            var items = _store.ParseTranscript(new[] { User("g1", ContextGuard.SavePrompt(80, 80, "n1")), Boundary("b1") });
            var json = JsonSerializer.Serialize(items[0], Json.Options);

            Assert.Contains("\"kind\":\"notice\"", json);
            Assert.Contains("\"guard\":{\"phase\":\"done\",\"pct\":80,\"threshold\":80,\"pre\":804640,\"post\":17492}", json);
            Assert.DoesNotContain("resumed", json);
        }
    }
}
