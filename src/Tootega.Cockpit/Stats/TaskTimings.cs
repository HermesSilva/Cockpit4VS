using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Stats
{
    /// <summary>
    /// Average duration per task, segmented by (model, effort, verbosity, type). Port of
    /// src/stats/TaskTimings.ts.
    ///
    /// The segmentation is the point: the same task takes wildly different times depending on
    /// the model and the effort, so one global average per task type would calibrate the
    /// activity gauge to a number that is wrong for every combination. The file is global
    /// (~/.claude/tootega) because the calibration is a property of the machine, not of a
    /// project or a tab.
    ///
    /// Three robustness rules, all learned the hard way:
    ///  - each key stores an average AND a sample count, and is only exposed after a minimum
    ///    number of samples. An average of one is not an average.
    ///  - the average is a true mean while samples are few and an EMA afterwards, so it
    ///    adapts without throwing away history.
    ///  - writes are debounced and go through a lock: several windows share this file, so a
    ///    flush re-reads the disk and merges rather than overwriting what others recorded.
    /// </summary>
    internal sealed class TaskTimings : IDisposable
    {
        /// <summary>Bumped when the key format changes; an older file recalibrates from scratch.</summary>
        private const int Version = 4;

        /// <summary>Weight of a new sample once the average has stabilized.</summary>
        private const double EmaAlpha = 0.3;

        /// <summary>Below this is noise — a near-instant restart, not a task.</summary>
        private const int MinMs = 150;

        /// <summary>Above this is an outlier — a wedged process, not a slow task.</summary>
        private const int MaxMs = 30 * 60 * 1000;

        /// <summary>Samples needed before an average is trustworthy enough to expose.</summary>
        private const int MinSamples = 3;

        private const string Separator = " :: ";
        private const int FlushMs = 5000;
        private const int LockRetryMs = 250;

        private sealed class Stat
        {
            [JsonPropertyName("ms")] public double Ms { get; set; }
            [JsonPropertyName("n")] public int N { get; set; }
        }

        private sealed class Store
        {
            [JsonPropertyName("version")] public int Version { get; set; }
            [JsonPropertyName("stats")] public Dictionary<string, Stat> Stats { get; set; } = new Dictionary<string, Stat>();
        }

        private readonly string _file;
        private readonly string _lockFile;
        private readonly object _gate = new object();

        /// <summary>Samples not yet persisted. Applied to disk on flush.</summary>
        private readonly List<KeyValuePair<string, double>> _pending = new List<KeyValuePair<string, double>>();

        /// <summary>In-memory mirror for fast reads; refreshed with the merged state on flush.</summary>
        private Store _cache;

        private Timer _flushTimer;
        private bool _disposed;

        public TaskTimings(string directory = null)
        {
            var dir = directory ?? ClaudeHome.CockpitDir;
            _file = Path.Combine(dir, "task-timings.json");
            _lockFile = Path.Combine(dir, "task-timings.lock");
        }

        /// <summary>Readable composite key: "&lt;model&gt; :: &lt;effort&gt; :: &lt;verbosity&gt; :: &lt;type&gt;".</summary>
        private static string KeyOf(string model, string effort, string verbosity, string type)
        {
            return model + Separator + effort + Separator + verbosity + Separator + type;
        }

        /// <summary>
        /// Queues a sample. Persisted debounced, so a busy turn does not rewrite the file
        /// dozens of times.
        /// </summary>
        public void Record(string model, string effort, string verbosity, string type, double ms)
        {
            if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(type)) return;
            if (double.IsNaN(ms) || double.IsInfinity(ms) || ms < MinMs || ms > MaxMs) return;

            var key = KeyOf(model,
                string.IsNullOrEmpty(effort) ? "default" : effort,
                string.IsNullOrEmpty(verbosity) ? "verbose" : verbosity,
                type);

            lock (_gate)
            {
                _pending.Add(new KeyValuePair<string, double>(key, ms));
                ScheduleFlush(FlushMs);
            }
        }

        /// <summary>
        /// Averages for one scope, keyed by plain type. The webview asks by type
        /// (tool:Read, assistant) without knowing the scope, so the prefix is stripped here.
        /// Only keys with enough samples are included — an unreliable number would calibrate
        /// the gauge worse than the default does.
        /// </summary>
        public IReadOnlyDictionary<string, double> Scoped(string model, string effort, string verbosity)
        {
            var prefix = model + Separator + effort + Separator + verbosity + Separator;
            var result = new Dictionary<string, double>();

            foreach (var entry in Load().Stats)
            {
                if (entry.Value == null || entry.Value.N < MinSamples) continue;
                if (!entry.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                result[entry.Key.Substring(prefix.Length)] = entry.Value.Ms;
            }

            return result;
        }

        private Store Load()
        {
            lock (_gate)
            {
                return _cache ?? (_cache = ReadStore());
            }
        }

        /// <summary>Reads from disk without caching — the base a flush merges into.</summary>
        private Store ReadStore()
        {
            var raw = FileStore.ReadAllTextOrNull(_file);
            if (raw != null)
            {
                try
                {
                    var parsed = Json.TryDeserialize<Store>(raw);
                    // A file from an older key format is discarded rather than migrated: the
                    // averages would be meaningless under the new segmentation anyway.
                    if (parsed?.Stats != null && parsed.Version == Version) return parsed;
                }
                catch (JsonException)
                {
                    // Corrupt: recalibrates from scratch.
                }
            }

            return new Store { Version = Version, Stats = new Dictionary<string, Stat>() };
        }

        /// <summary>True mean while samples are few (alpha = 1/n), EMA once stabilized.</summary>
        private static void ApplySample(Store store, string key, double ms)
        {
            if (!store.Stats.TryGetValue(key, out var stat) || stat == null)
            {
                store.Stats[key] = new Stat { Ms = ms, N = 1 };
                return;
            }

            var n = stat.N + 1;
            var alpha = Math.Max(EmaAlpha, 1.0 / n);
            stat.Ms = stat.Ms + (ms - stat.Ms) * alpha;
            stat.N = n;
        }

        /// <summary>Caller must hold <see cref="_gate"/>.</summary>
        private void ScheduleFlush(int delayMs)
        {
            if (_disposed || _flushTimer != null) return;
            _flushTimer = new Timer(_ => Flush(), null, delayMs, Timeout.Infinite);
        }

        /// <summary>
        /// Persists the buffer: take the lock, re-read the disk, merge, write atomically.
        /// Re-reading is what lets several windows share the file without one clobbering the
        /// other's samples.
        /// </summary>
        public void Flush()
        {
            List<KeyValuePair<string, double>> batch;
            lock (_gate)
            {
                DisposeTimer();
                if (_pending.Count == 0) return;

                // Take the batch now; samples arriving during the flush go to the next one.
                batch = new List<KeyValuePair<string, double>>(_pending);
                _pending.Clear();
            }

            using (var fileLock = FileStore.Lock.TryAcquire(_lockFile))
            {
                if (fileLock == null)
                {
                    // Busy: retry soon without losing the batch.
                    lock (_gate)
                    {
                        _pending.InsertRange(0, batch);
                        ScheduleFlush(LockRetryMs);
                    }
                    return;
                }

                var merged = ReadStore();
                foreach (var sample in batch) ApplySample(merged, sample.Key, sample.Value);

                if (FileStore.WriteAtomic(_file, Json.Serialize(merged)))
                {
                    lock (_gate) _cache = merged;
                }
                else
                {
                    lock (_gate)
                    {
                        _pending.InsertRange(0, batch);
                        ScheduleFlush(LockRetryMs);
                    }
                }
            }
        }

        private void DisposeTimer()
        {
            var timer = _flushTimer;
            _flushTimer = null;
            timer?.Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Flush first: pending samples are cheap to write and lost otherwise.
            Flush();
            lock (_gate) DisposeTimer();
        }
    }
}
