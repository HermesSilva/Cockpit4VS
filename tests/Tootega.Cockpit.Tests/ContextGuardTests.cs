using System.Collections.Generic;
using System.Linq;
using Tootega.Cockpit.Session;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// The context guard's whole contract, against a fake session — the same scenarios as the
    /// base repo's test/ContextGuard.test.ts, so the two state machines are held to one spec.
    /// </summary>
    public class ContextGuardTests
    {
        private sealed class FakeIO : IGuardIO
        {
            public readonly List<(string Text, bool NewTurn)> Sent = new List<(string, bool)>();
            public readonly List<GuardNotice> Notices = new List<GuardNotice>();
            private int _n;
            public void Send(string text, bool newTurn) => Sent.Add((text, newTurn));
            public void Notify(GuardNotice notice) => Notices.Add(notice);
            public string Nonce() => "n" + (++_n);
            public void Log(string message) { }
            public GuardNotice Last => Notices[Notices.Count - 1];
        }

        private const long Limit = 1_000_000;
        private static long At(double pct) => (long)(pct / 100 * Limit);

        private static (ContextGuard G, FakeIO IO) Harness(bool enabled = true, double threshold = 0.8)
        {
            var io = new FakeIO();
            return (new ContextGuard(io, () => new GuardConfig { Enabled = enabled, Threshold = threshold }), io);
        }

        private static string Ok(string nonce) => ContextGuard.Sentinel + " " + nonce;

        [Fact]
        public void Sentinel_AcceptsOnlyTheExactLastLineWithThisNonce()
        {
            Assert.True(ContextGuard.Confirms("Saved to memory.\n\n" + Ok("abc"), "abc"));
            Assert.True(ContextGuard.Confirms(Ok("abc") + "\r\n", "abc"));
            Assert.True(ContextGuard.Confirms("`" + Ok("abc") + "`", "abc"));
            Assert.False(ContextGuard.Confirms(Ok("abd"), "abc"));
            Assert.False(ContextGuard.Confirms(Ok("abc") + "\nand then I continued", "abc"));
            Assert.False(ContextGuard.Confirms("ok " + Ok("abc"), "abc"));
            Assert.False(ContextGuard.Confirms("", "abc"));
            Assert.False(ContextGuard.Confirms(null, "abc"));
        }

        [Fact]
        public void Trigger_NothingBelowThresholdDisabledCompactingOrUnknownWindow()
        {
            var h = Harness();
            h.G.OnContext(At(79.9), Limit, true);
            Assert.Empty(h.IO.Sent);
            h = Harness(enabled: false);
            h.G.OnContext(At(90), Limit, true);
            Assert.Empty(h.IO.Sent);
            h = Harness();
            h.G.OnContext(At(90), Limit, true, compacting: true);
            Assert.Empty(h.IO.Sent);
            h.G.OnContext(At(90), 0, true);
            Assert.Empty(h.IO.Sent);
        }

        [Fact]
        public void Trigger_MidTurnWritesIntoTheRunningTurn()
        {
            var h = Harness();
            h.G.OnContext(At(81), Limit, true);
            Assert.Single(h.IO.Sent);
            Assert.False(h.IO.Sent[0].NewTurn);
            Assert.Contains(Ok("n1"), h.IO.Sent[0].Text);
            Assert.Contains("81%", h.IO.Sent[0].Text);
            Assert.Equal("saving", h.IO.Last.Phase);
            Assert.Equal(81, h.IO.Last.Pct);
            Assert.Equal(80, h.IO.Last.Threshold);
            h.G.OnContext(At(85), Limit, true);
            Assert.Single(h.IO.Sent);
        }

        [Fact]
        public void Trigger_IdleOpensItsOwnTurn()
        {
            var h = Harness();
            h.G.OnContext(At(83), Limit, false);
            Assert.True(h.IO.Sent[0].NewTurn);
        }

        [Fact]
        public void HappyPath_ConfirmedCompactBoundaryResume()
        {
            var h = Harness();
            h.G.OnContext(At(82), Limit, true);
            h.G.OnApiCall();
            Assert.Equal(GuardResultAction.None, h.G.OnResult("State saved.\n" + Ok("n1"), false));
            Assert.Equal((ContextGuard.CompactCommand, true), h.IO.Sent[1]);
            Assert.Equal("compacting", h.IO.Last.Phase);

            h.G.OnCompactStatus(true);
            h.G.OnCompactBoundary("manual", 820_000, 96_000);
            h.G.OnResult(string.Empty, false);
            Assert.Equal("done", h.IO.Last.Phase);
            Assert.Equal(820_000, h.IO.Last.Pre);
            Assert.Equal(96_000, h.IO.Last.Post);
            Assert.True(h.IO.Last.Resumed);
            Assert.Equal((ContextGuard.ResumePrompt, true), h.IO.Sent[2]);
            Assert.False(h.G.Active);
        }

        [Fact]
        public void HappyPath_NoRetriggerUntilBelowThreshold()
        {
            var h = Harness();
            h.G.OnContext(At(82), Limit, true);
            h.G.OnApiCall();
            h.G.OnResult(Ok("n1"), false);
            h.G.OnCompactBoundary("manual", 820_000, 96_000);
            h.G.OnResult(string.Empty, false);
            var before = h.IO.Sent.Count;
            h.G.OnContext(At(85), Limit, true);
            Assert.Equal(before, h.IO.Sent.Count);
            h.G.OnContext(At(10), Limit, true);
            h.G.OnContext(At(81), Limit, true);
            Assert.Equal(before + 1, h.IO.Sent.Count);
            Assert.Contains(Ok("n2"), h.IO.Sent[before].Text);
        }

        [Fact]
        public void HappyPath_TriggeredIdleDoesNotResume()
        {
            var h = Harness();
            h.G.OnContext(At(82), Limit, false);
            h.G.OnResult(Ok("n1"), false);
            h.G.OnCompactBoundary("manual", 800, 100);
            h.G.OnResult(string.Empty, false);
            Assert.Equal("done", h.IO.Last.Phase);
            Assert.False(h.IO.Last.Resumed);
            Assert.DoesNotContain(h.IO.Sent, s => s.Text == ContextGuard.ResumePrompt);
        }

        [Fact]
        public void Queued_TurnEndsWithoutToolBoundary_ExpectsTheQueuedTurnThenVerifiesIt()
        {
            var h = Harness();
            h.G.OnContext(At(82), Limit, true);
            Assert.Equal(GuardResultAction.ExpectQueuedTurn, h.G.OnResult("Here is the final answer.", false));
            Assert.Single(h.IO.Sent);
            Assert.Equal(GuardResultAction.None, h.G.OnResult(Ok("n1"), false));
            Assert.Equal(ContextGuard.CompactCommand, h.IO.Sent[1].Text);
        }

        [Fact]
        public void NoProof_OneStricterRetryThenFails()
        {
            var h = Harness();
            h.G.OnContext(At(82), Limit, false);
            h.G.OnResult("I saved some notes.", false);
            Assert.Equal("retrying", h.IO.Last.Phase);
            Assert.True(h.IO.Sent[1].NewTurn);
            Assert.Contains(Ok("n1"), h.IO.Sent[1].Text);
            h.G.OnResult("Done!", false);
            Assert.Equal("failed", h.IO.Last.Phase);
            Assert.Equal("no-confirmation", h.IO.Last.Reason);
            Assert.DoesNotContain(h.IO.Sent, s => s.Text == ContextGuard.CompactCommand);
        }

        [Fact]
        public void NoProof_StaleNonceIsNotAConfirmation()
        {
            var h = Harness();
            h.G.OnContext(At(82), Limit, false);
            h.G.OnResult(Ok("n0"), false);
            Assert.Equal("retrying", h.IO.Last.Phase);
        }

        [Fact]
        public void NoProof_SaveTurnEndedWithError()
        {
            var h = Harness();
            h.G.OnContext(At(82), Limit, false);
            h.G.OnResult(Ok("n1"), true);
            Assert.Equal("turn-error", h.IO.Last.Reason);
            Assert.Single(h.IO.Sent);
        }

        [Fact]
        public void NoProof_CompactionBlockedEvenWithSuccessfulResult()
        {
            var h = Harness();
            h.G.OnContext(At(82), Limit, false);
            h.G.OnResult(Ok("n1"), false);
            h.G.OnCompactStatus(false, "skipped: Compaction blocked by PreCompact hook");
            h.G.OnResult("Compaction blocked by PreCompact hook", false);
            Assert.Equal("failed", h.IO.Last.Phase);
            Assert.Equal("compact-error", h.IO.Last.Reason);
            Assert.Contains("PreCompact", h.IO.Last.Detail);
            Assert.DoesNotContain(h.IO.Sent, s => s.Text == ContextGuard.ResumePrompt);
        }

        [Fact]
        public void NoProof_NoBoundaryOrNothingSavedIsNotACompaction()
        {
            var h = Harness();
            h.G.OnContext(At(82), Limit, false);
            h.G.OnResult(Ok("n1"), false);
            h.G.OnResult(string.Empty, false);
            Assert.Equal("no-compaction", h.IO.Last.Reason);

            h = Harness();
            h.G.OnContext(At(82), Limit, false);
            h.G.OnResult(Ok("n1"), false);
            h.G.OnCompactBoundary("manual", 500, 500);
            h.G.OnResult(string.Empty, false);
            Assert.Equal("no-compaction", h.IO.Last.Reason);
        }

        [Fact]
        public void AfterFailure_WaitsFiveMorePoints()
        {
            var h = Harness();
            h.G.OnContext(At(82), Limit, false);
            h.G.OnResult("nope", false);
            h.G.OnResult("nope", false);
            var n = h.IO.Sent.Count;
            h.G.OnContext(At(86), Limit, false);
            Assert.Equal(n, h.IO.Sent.Count);
            h.G.OnContext(At(87.5), Limit, false);
            Assert.Equal(n + 1, h.IO.Sent.Count);
        }

        [Fact]
        public void AutoCompactionDuringSave_SkipsSecondCompactionAndResumes()
        {
            var h = Harness();
            h.G.OnContext(At(82), Limit, true);
            h.G.OnApiCall();
            h.G.OnCompactBoundary("auto", 900_000, 120_000);
            h.G.OnResult(Ok("n1"), false);
            Assert.DoesNotContain(h.IO.Sent, s => s.Text == ContextGuard.CompactCommand);
            Assert.Equal("done", h.IO.Last.Phase);
            Assert.Equal(900_000, h.IO.Last.Pre);
            Assert.True(h.IO.Last.Resumed);
            Assert.Equal(ContextGuard.ResumePrompt, h.IO.Sent.Last().Text);
        }

        [Fact]
        public void Cancel_StopCancelsAndNothingElseIsSent()
        {
            var h = Harness();
            h.G.OnContext(At(82), Limit, true);
            h.G.OnUserInterrupt();
            Assert.Equal("canceled", h.IO.Last.Phase);
            Assert.Equal("interrupted", h.IO.Last.Reason);
            Assert.False(h.G.Active);
            h.G.OnResult(Ok("n1"), false);
            Assert.Single(h.IO.Sent);
        }

        [Fact]
        public void Cancel_ResetCancelsAndClearsFloors()
        {
            var h = Harness();
            h.G.OnContext(At(82), Limit, false);
            h.G.Reset();
            Assert.Equal("restarted", h.IO.Last.Reason);
            h.G.OnContext(At(82), Limit, false);
            Assert.Equal(2, h.IO.Sent.Count);
        }
    }
}
