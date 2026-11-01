using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Tootega.Cockpit.Session
{
    /// <summary>Context-guard configuration. <see cref="Threshold"/> is a fraction (0.8 = 80%).</summary>
    internal sealed class GuardConfig
    {
        public bool Enabled { get; set; }
        public double Threshold { get; set; } = 0.8;
    }

    /// <summary>One phase of a guard run, for the webview banner. Wire names are the TS ones.</summary>
    internal sealed class GuardNotice
    {
        public string Id { get; set; }
        /// <summary>saving | retrying | compacting | done | failed | canceled</summary>
        public string Phase { get; set; }
        public int Pct { get; set; }
        public int Threshold { get; set; }
        public long? Pre { get; set; }
        public long? Post { get; set; }
        public bool Resumed { get; set; }
        /// <summary>no-confirmation | turn-error | compact-error | no-compaction | interrupted | restarted</summary>
        public string Reason { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>What the guard needs from the Session. See <see cref="ContextGuard"/>.</summary>
    internal interface IGuardIO
    {
        /// <summary>
        /// Writes a user message to the live CLI. <paramref name="newTurn"/>: the session is idle
        /// and this message opens a turn (account for it: busy + stopwatch). Otherwise a turn is
        /// running and the message rides inside it.
        /// </summary>
        void Send(string text, bool newTurn);
        void Notify(GuardNotice notice);
        string Nonce();
        void Log(string message);
    }

    internal enum GuardResultAction
    {
        None,
        /// <summary>The request is still in the CLI's queue and runs as the next turn: own it.</summary>
        ExpectQueuedTurn,
    }

    /// <summary>
    /// Port of src/session/ContextGuard.ts — keep the two state machines identical.
    ///
    /// When the context window passes the threshold (80% by default), make the model save what it
    /// needs to resume, PROVE it did, and only then compact. Every step rests on behaviour measured
    /// against the real CLI (2.1.316, stream-json):
    ///  - A user message written to stdin while a turn runs is delivered at the NEXT TOOL BOUNDARY,
    ///    inside the same turn (one result). With no further tool call the CLI queues it and runs
    ///    it as the next turn on its own — a turn the session did not start.
    ///  - /compact written mid-turn is NOT injected: it waits for the turn to end, so it is only
    ///    ever sent with the session idle.
    ///  - A compaction is proven by compact_result "success" plus a compact_boundary whose
    ///    post_tokens &lt; pre_tokens. The turn's result proves nothing: a PreCompact hook that blocks
    ///    the compaction still ends the turn with a successful result.
    ///
    /// "Done" is a fact: the reply must end with a line whose format and value the guard dictated —
    /// COCKPIT-CONTEXT-SAVED &lt;nonce&gt;, with a nonce minted for this run. Nothing is ever
    /// compacted without it.
    /// </summary>
    internal sealed class ContextGuard
    {
        public const string Sentinel = "COCKPIT-CONTEXT-SAVED";
        /// <summary>Extra fill before trying again after a failure.</summary>
        public const double RetryStep = 0.05;
        private const int MaxAttempts = 2; // the request + one stricter retry

        public const string CompactCommand = "/compact Keep the saved state and the next steps of the paused task.";

        public const string ResumePrompt =
            "[Cockpit context guard] The context was compacted. Resume the task you paused, starting " +
            "from the state you saved.";

        public static string SavePrompt(int pct, int threshold, string nonce) =>
            $"[Cockpit context guard] The context window is {pct}% full (limit {threshold}%). " +
            "Pause the current task now and prepare for a context compaction:\n" +
            "1. Save everything needed to resume this work — what is done, what is in progress, what " +
            "comes next, decisions taken and open questions — following this repository's directives " +
            "for memory and documentation. If the repository provides a skill for saving context or " +
            "preparing a compaction, use it.\n" +
            "2. Do not continue the task in this turn.\n" +
            "3. End your reply with exactly this line, alone, as the very last line:\n" +
            $"{Sentinel} {nonce}";

        public static string RetryPrompt(string nonce) =>
            "[Cockpit context guard] Your previous reply did not end with the required confirmation " +
            "line. If the state is not fully saved yet, save it first. Then end your reply with exactly " +
            "this line, alone, as the very last line:\n" +
            $"{Sentinel} {nonce}";

        private static readonly Regex EdgeMarkup = new Regex(@"^[`*_]+|[`*_]+$", RegexOptions.Compiled);

        /// <summary>
        /// True when the reply's last non-empty line is exactly the sentinel with this nonce.
        /// Markdown around a lone line (backticks, bold) is tolerated; anything else is not.
        /// </summary>
        public static bool Confirms(string reply, string nonce)
        {
            var last = (reply ?? string.Empty).Split('\n')
                .Select(l => l.Trim()).Where(l => l.Length > 0).LastOrDefault();
            if (last == null) return false;
            var bare = EdgeMarkup.Replace(last, string.Empty).Trim();
            return bare == $"{Sentinel} {nonce}";
        }

        /// <summary>Keeps the threshold where a save turn still fits before the window fills.</summary>
        public static double ClampThreshold(double t)
        {
            if (double.IsNaN(t) || double.IsInfinity(t)) return 0.8;
            return Math.Min(0.95, Math.Max(0.5, t));
        }

        private enum Kind { Idle, Saving, Compacting }

        private readonly object _gate = new object();
        private readonly IGuardIO _io;
        private readonly Func<GuardConfig> _config;

        private Kind _kind = Kind.Idle;
        private string _id;
        private string _nonce;
        private int _pct;
        private int _attempts;
        private bool _midTurn;
        private bool _delivered;
        private bool _autoCompacted;
        private bool? _compactOk;
        private string _compactError;
        private long? _pre;
        private long? _post;

        private bool _requireBelow;
        private double _retryFloor;
        private double _lastFill;
        private int _seq;

        public ContextGuard(IGuardIO io, Func<GuardConfig> config)
        {
            _io = io ?? throw new ArgumentNullException(nameof(io));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public bool Active => _kind != Kind.Idle;

        /// <summary>idle | saving | compacting — for diagnostics and tests.</summary>
        public string Phase => _kind.ToString().ToLowerInvariant();

        /// <summary>A new API call (message_start): a mid-turn request reached the model with it.</summary>
        public void OnApiCall()
        {
            lock (_gate)
            {
                if (_kind == Kind.Saving && _midTurn) _delivered = true;
            }
        }

        /// <summary>Every context measurement: on each API call and at the end of each turn.</summary>
        public void OnContext(long used, long limit, bool busy, bool compacting = false)
        {
            lock (_gate)
            {
                if (limit <= 0 || used < 0) return;
                var fill = (double)used / limit;
                _lastFill = fill;
                var cfg = _config() ?? new GuardConfig();
                var threshold = ClampThreshold(cfg.Threshold);
                if (fill < threshold)
                {
                    _requireBelow = false;
                    _retryFloor = 0;
                }
                if (_kind != Kind.Idle || !cfg.Enabled || compacting) return;
                if (fill < threshold || _requireBelow) return;
                if (_retryFloor > 0 && fill < _retryFloor) return;
                Start(fill, threshold, busy);
            }
        }

        private void Start(double fill, double threshold, bool busy)
        {
            _nonce = _io.Nonce();
            _id = $"g{++_seq}_{_nonce}";
            _pct = (int)Math.Floor(fill * 100);
            _attempts = 1;
            _midTurn = busy;
            _delivered = false;
            _autoCompacted = false;
            _pre = _post = null;
            _kind = Kind.Saving;
            var th = Pct100(threshold);
            _io.Log($"context guard: {_pct}% >= {th}% -> asking to save ({(busy ? "mid-turn" : "new turn")}) nonce={_nonce}");
            _io.Notify(new GuardNotice { Id = _id, Phase = "saving", Pct = _pct, Threshold = th });
            _io.Send(SavePrompt(_pct, th, _nonce), !busy);
        }

        /// <summary>
        /// The turn ended; <paramref name="reply"/> is its final text. Returns
        /// <see cref="GuardResultAction.ExpectQueuedTurn"/> when the request is still in the CLI's
        /// queue and will run as the next turn.
        /// </summary>
        public GuardResultAction OnResult(string reply, bool isError)
        {
            lock (_gate)
            {
                if (_kind == Kind.Saving)
                {
                    if (isError) return Fail("turn-error");
                    if (Confirms(reply, _nonce)) return Confirmed();
                    if (_midTurn && !_delivered)
                    {
                        _delivered = true;
                        _io.Log("context guard: request queued by the CLI; it runs as the next turn");
                        return GuardResultAction.ExpectQueuedTurn;
                    }
                    if (_attempts >= MaxAttempts) return Fail("no-confirmation");
                    _attempts++;
                    _io.Log($"context guard: confirmation missing, retry {_attempts}/{MaxAttempts}");
                    _io.Notify(new GuardNotice { Id = _id, Phase = "retrying", Pct = _pct, Threshold = ThresholdPct() });
                    _io.Send(RetryPrompt(_nonce), true);
                    return GuardResultAction.None;
                }
                if (_kind == Kind.Compacting)
                {
                    if (_compactOk == false) return Fail("compact-error", _compactError);
                    var saved = _pre.HasValue && _post.HasValue && _post.Value < _pre.Value;
                    if (_compactOk != true || !saved) return Fail("no-compaction");
                    Finish(_pre, _post);
                }
                return GuardResultAction.None;
            }
        }

        private GuardResultAction Confirmed()
        {
            if (_autoCompacted)
            {
                // The CLI already compacted while the model was saving: a second compaction would
                // only re-pay the cache. The state is saved, so go straight to resuming.
                _io.Log("context guard: confirmed; the CLI had already compacted on its own");
                Finish(_pre, _post);
                return GuardResultAction.None;
            }
            _io.Log("context guard: confirmed -> /compact");
            _kind = Kind.Compacting;
            _compactOk = null;
            _compactError = null;
            _pre = _post = null;
            _io.Notify(new GuardNotice { Id = _id, Phase = "compacting", Pct = _pct, Threshold = ThresholdPct() });
            _io.Send(CompactCommand, true);
            return GuardResultAction.None;
        }

        /// <summary>system/status with compact_result / compact_error.</summary>
        public void OnCompactStatus(bool ok, string error = null)
        {
            lock (_gate)
            {
                if (_kind != Kind.Compacting) return;
                _compactOk = ok;
                if (!ok) _compactError = error;
            }
        }

        /// <summary>system/compact_boundary.</summary>
        public void OnCompactBoundary(string trigger, long? pre, long? post)
        {
            lock (_gate)
            {
                if (_kind == Kind.Compacting)
                {
                    if (_compactOk == null) _compactOk = true;
                    _pre = pre;
                    _post = post;
                    return;
                }
                if (_kind == Kind.Saving && trigger == "auto")
                {
                    _autoCompacted = true;
                    _pre = pre;
                    _post = post;
                    _io.Log("context guard: the CLI auto-compacted during the save");
                }
            }
        }

        /// <summary>The user pressed Stop.</summary>
        public void OnUserInterrupt()
        {
            lock (_gate)
            {
                if (_kind == Kind.Idle) return;
                Fail("interrupted");
            }
        }

        /// <summary>The process was restarted or the conversation changed under the guard.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                if (_kind != Kind.Idle) Fail("restarted");
                _requireBelow = false;
                _retryFloor = 0;
            }
        }

        private void Finish(long? pre, long? post)
        {
            var resume = _midTurn;
            _kind = Kind.Idle;
            _requireBelow = true;
            _retryFloor = 0;
            _io.Log($"context guard: done {(pre?.ToString() ?? "?")} -> {(post?.ToString() ?? "?")}{(resume ? ", resuming the task" : string.Empty)}");
            _io.Notify(new GuardNotice { Id = _id, Phase = "done", Pct = _pct, Threshold = ThresholdPct(), Pre = pre, Post = post, Resumed = resume });
            if (resume) _io.Send(ResumePrompt, true);
        }

        private GuardResultAction Fail(string reason, string detail = null)
        {
            _kind = Kind.Idle;
            // Never loop on a failure: wait for the fill to grow a few more points (or to drop
            // below the threshold — the CLI's own auto-compaction is still the safety net).
            _retryFloor = Math.Max(_lastFill, ClampThreshold((_config() ?? new GuardConfig()).Threshold)) + RetryStep;
            var phase = reason == "interrupted" || reason == "restarted" ? "canceled" : "failed";
            _io.Log($"context guard: {phase} ({reason}{(detail != null ? ": " + detail : string.Empty)})");
            _io.Notify(new GuardNotice { Id = _id, Phase = phase, Pct = _pct, Threshold = ThresholdPct(), Reason = reason, Detail = detail });
            return GuardResultAction.None;
        }

        private int ThresholdPct() => Pct100(ClampThreshold((_config() ?? new GuardConfig()).Threshold));

        private static int Pct100(double f) => (int)Math.Round(f * 100, MidpointRounding.AwayFromZero);
    }
}
