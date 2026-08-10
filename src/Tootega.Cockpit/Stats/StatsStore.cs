using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Stats
{
    internal sealed class ToolDecisionCounts
    {
        public int Allow { get; set; }
        public int AllowAlways { get; set; }
        public int Deny { get; set; }
    }

    /// <summary>
    /// Serializable state of one session — mirrors the StatsAggregator accumulators.
    ///
    /// The field names and version match the VS Code Cockpit's format on purpose. Both
    /// extensions store under ~/.claude/tootega/stats, so keeping them compatible means a
    /// conversation continued in the other editor keeps its history instead of resetting to
    /// zero.
    /// </summary>
    internal sealed class PersistedStats
    {
        public int Version { get; set; }
        public string SessionId { get; set; }
        /// <summary>Working folder — lets the CacheKeeper resume with the tab closed.</summary>
        public string Cwd { get; set; }
        public bool? KeepCacheAlive { get; set; }
        public string Model { get; set; }
        public string Mode { get; set; }
        public long ContextLimit { get; set; }
        public bool AutoLimit { get; set; }
        public long? SessionStartTs { get; set; }

        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheCreateTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public double SessionCostUsd { get; set; }
        public bool CostIsEstimate { get; set; }

        public int TurnCount { get; set; }
        public int CacheResetCount { get; set; }
        public double CacheRecacheCostUsd { get; set; }
        public int CompactionCount { get; set; }
        public int ReopenCount { get; set; }
        public long PeakContextUsed { get; set; }
        public long? PeakContextTs { get; set; }
        public long? PeakCacheTokens { get; set; }
        /// <summary>Real execution time: the prompts summed, without idleness.</summary>
        public long ActiveMs { get; set; }

        // State for between-turn detection. Not displayed, but it has to survive a reopen or
        // the first turn after resuming would look like a cache reset.
        public long LastContextUsed { get; set; }
        public long LastCacheRead { get; set; }
        public long LastTurnTs { get; set; }

        public Dictionary<string, ModelUsage> PerModel { get; set; }
        public Dictionary<string, ToolDecisionCounts> ToolDecisions { get; set; }
        public List<DenialEvent> Denials { get; set; }
        public List<TimelineSample> Timeline { get; set; }
        public List<CompactionEvent> Compactions { get; set; }
        public string UpdatedAt { get; set; }
    }

    /// <summary>
    /// Persistence of the per-session statistics. Port of src/stats/StatsStore.ts.
    ///
    /// This exists because the numbers cannot be re-derived. The CLI does not re-emit the
    /// usage of old turns on --resume, so a reopened context would show zero tokens and zero
    /// cost for work that really happened. Persisting is the only way the figures stay
    /// coherent across a reopen.
    ///
    /// Writes are debounced (not once per token) and atomic. A context is owned by the window
    /// that has it open; in the rare case of two windows on one session the last writer wins,
    /// which is accepted rather than solved — a cross-process merge here would cost more than
    /// the inconsistency it prevents.
    /// </summary>
    internal sealed class StatsStore : IDisposable
    {
        public const int StatsVersion = 1;

        private const int FlushMs = 4000;
        /// <summary>Timeline samples kept per session; older ones are decimated.</summary>
        private const int TimelineCap = 400;
        private const int KeepAliveLockStaleMs = 30000;

        private static readonly Regex UnsafeFileChars = new Regex(@"[^A-Za-z0-9._-]", RegexOptions.Compiled);

        private readonly string _directory;
        private readonly object _gate = new object();

        /// <summary>One pending state per session; the most recent wins.</summary>
        private readonly Dictionary<string, PersistedStats> _pending = new Dictionary<string, PersistedStats>();

        private readonly Dictionary<string, FileStore.Lock> _heldLocks = new Dictionary<string, FileStore.Lock>();

        private Timer _flushTimer;
        private bool _disposed;

        public StatsStore(string directory = null)
        {
            _directory = directory ?? Path.Combine(ClaudeHome.CockpitDir, "stats");
        }

        private string FileFor(string sessionId)
        {
            // The session id is already a safe uuid, but it arrives from disk, so it is
            // normalized rather than trusted.
            return Path.Combine(_directory, UnsafeFileChars.Replace(sessionId, "_") + ".json");
        }

        /// <summary>Reads a session's persisted state, or null when missing or incompatible.</summary>
        public PersistedStats Load(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;

            // The freshest state may still be in our own buffer.
            lock (_gate)
            {
                if (_pending.TryGetValue(sessionId, out var buffered)) return buffered;
            }

            var raw = FileStore.ReadAllTextOrNull(FileFor(sessionId));
            if (raw == null) return null;

            var parsed = Json.TryDeserialize<PersistedStats>(raw);
            // A file from an older version starts this session from scratch rather than
            // being half-read into wrong numbers.
            return parsed != null && parsed.Version == StatsVersion && !string.IsNullOrEmpty(parsed.SessionId)
                ? parsed
                : null;
        }

        /// <summary>Reads every session's state, for the CacheKeeper sweep.</summary>
        public IReadOnlyList<PersistedStats> LoadAll()
        {
            var all = new List<PersistedStats>();

            string[] files;
            try
            {
                if (!Directory.Exists(_directory)) return all;
                files = Directory.GetFiles(_directory, "*.json");
            }
            catch
            {
                return all;
            }

            foreach (var file in files)
            {
                var loaded = Load(Path.GetFileNameWithoutExtension(file));
                if (loaded != null) all.Add(loaded);
            }

            return all;
        }

        /// <summary>Queues a session's state; the write is debounced and atomic.</summary>
        public void Save(PersistedStats data)
        {
            if (string.IsNullOrEmpty(data?.SessionId)) return;

            lock (_gate)
            {
                _pending[data.SessionId] = data;
                if (_flushTimer == null && !_disposed)
                    _flushTimer = new Timer(_ => Flush(), null, FlushMs, Timeout.Infinite);
            }
        }

        /// <summary>Writes everything pending right away. Called when the package shuts down.</summary>
        public void Flush()
        {
            List<PersistedStats> batch;
            lock (_gate)
            {
                var timer = _flushTimer;
                _flushTimer = null;
                timer?.Dispose();

                if (_pending.Count == 0) return;
                batch = _pending.Values.ToList();
                _pending.Clear();
            }

            foreach (var data in batch)
            {
                if (!FileStore.WriteAtomic(FileFor(data.SessionId), Json.Serialize(data)))
                    Log.Debug("stats flush failed for " + data.SessionId);
            }

            Log.Debug("stats flush: " + batch.Count + " session(s)");
        }

        /// <summary>
        /// Restarts a session's cache life after a successful keep-alive. Writes immediately:
        /// the keeper needs the fresh timestamp on disk before its next tick, or another
        /// window would ping the same session again. Touches nothing else.
        /// </summary>
        public void BumpCacheActivity(string sessionId, long timestampMs)
        {
            var stats = Load(sessionId);
            if (stats == null) return;

            stats.LastTurnTs = timestampMs;
            stats.UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime.ToString("o");

            // Through the buffer so a pending Save cannot regress the bump.
            lock (_gate) _pending[sessionId] = stats;
            Flush();
        }

        // --- Per-session keep-alive lock, coordinating several VS instances ---
        // Every instance sweeps the same directory, so without coordination two of them ping
        // the same session on the same tick. The lock covers only the critical section:
        // re-read fresh, decide, bump. Whoever loses it skips; the real signal between
        // instances is the LastTurnTs on disk.

        /// <summary>Takes exclusive ownership of this session's keep-alive.</summary>
        public bool AcquireKeepAliveLock(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;

            lock (_gate)
            {
                if (_heldLocks.ContainsKey(sessionId)) return false;

                var acquired = FileStore.Lock.TryAcquire(FileFor(sessionId) + ".lock", KeepAliveLockStaleMs);
                if (acquired == null) return false;

                _heldLocks[sessionId] = acquired;
                return true;
            }
        }

        /// <summary>Releases this session's keep-alive lock. No-op when not held.</summary>
        public void ReleaseKeepAliveLock(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;

            lock (_gate)
            {
                if (!_heldLocks.TryGetValue(sessionId, out var held)) return;
                _heldLocks.Remove(sessionId);
                held.Dispose();
            }
        }

        /// <summary>
        /// Decimates the timeline, keeping recent samples dense and thinning old ones.
        ///
        /// The recent half is what the user is looking at; the old half only has to show the
        /// shape of the session, so dropping every other sample there costs nothing visible
        /// and keeps the file from growing without bound.
        /// </summary>
        public static List<TimelineSample> CapTimeline(List<TimelineSample> timeline)
        {
            if (timeline == null || timeline.Count <= TimelineCap) return timeline;

            var half = timeline.Count / 2;
            var result = new List<TimelineSample>(timeline.Count);
            for (var i = 0; i < half; i += 2) result.Add(timeline[i]);
            result.AddRange(timeline.Skip(half));
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Pending statistics are cheap to write and gone otherwise.
            Flush();

            lock (_gate)
            {
                foreach (var held in _heldLocks.Values) held.Dispose();
                _heldLocks.Clear();

                var timer = _flushTimer;
                _flushTimer = null;
                timer?.Dispose();
            }
        }
    }
}
