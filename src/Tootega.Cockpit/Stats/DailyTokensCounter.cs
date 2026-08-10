using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Stats
{
    /// <summary>
    /// Global token counter (sent / received / total), aggregated per day. Port of
    /// src/stats/DailyTokens.ts.
    ///
    /// The source is the CLI's own transcripts under ~/.claude/projects, which every context
    /// and every editor instance on the machine writes to. That is why the number is
    /// naturally global: any window that ran a turn left its trace there.
    ///
    ///   sent     = input + cache_read + cache_creation (everything sent to the model)
    ///   received = output
    ///
    /// Scanning the whole history on every open would be expensive, so an incremental rollup
    /// is kept beside it: per file, its mtime and size plus the day map, and a file is only
    /// re-read when it changed. The rollup is a derived CACHE — the transcripts are the truth
    /// — so it is written atomically with last-write-wins and needs no lock.
    /// </summary>
    internal sealed class DailyTokensCounter
    {
        /// <summary>
        /// v2 stopped summing repeated lines of one response. The bump is what discards an
        /// old rollup, which holds inflated totals — leaving it would keep showing them.
        /// </summary>
        private const int RollupVersion = 2;

        /// <summary>Internal rather than private so the parser can be tested directly.</summary>
        internal sealed class DayTotals
        {
            [JsonPropertyName("s")] public long Sent { get; set; }
            [JsonPropertyName("r")] public long Received { get; set; }
        }

        private sealed class FileEntry
        {
            [JsonPropertyName("mtimeMs")] public long MtimeMs { get; set; }
            [JsonPropertyName("size")] public long Size { get; set; }
            [JsonPropertyName("days")] public Dictionary<string, DayTotals> Days { get; set; }
        }

        private sealed class Rollup
        {
            [JsonPropertyName("version")] public int Version { get; set; }
            [JsonPropertyName("files")] public Dictionary<string, FileEntry> Files { get; set; }
        }

        private readonly string _projectsRoot;
        private readonly string _rollupFile;

        public DailyTokensCounter(string projectsRoot = null, string rollupFile = null)
        {
            _projectsRoot = projectsRoot ?? ClaudeHome.ProjectsDir;
            _rollupFile = rollupFile ?? Path.Combine(ClaudeHome.CockpitDir, "tokens-rollup.json");
        }

        /// <summary>
        /// Aggregates tokens per day across the whole machine. <paramref name="maxDays"/>
        /// limits only the per-day slice returned for display; the totals are all-time.
        /// </summary>
        public Task<TokenTotals> ComputeAsync(int maxDays = 30)
        {
            return Task.Run(() => Compute(maxDays));
        }

        private TokenTotals Compute(int maxDays)
        {
            var empty = new TokenTotals { Sent = 0, Received = 0, Total = 0, Days = new List<DailyTokens>() };

            string[] projectDirs;
            try
            {
                if (!Directory.Exists(_projectsRoot)) return empty;
                projectDirs = Directory.GetDirectories(_projectsRoot);
            }
            catch
            {
                // No history yet.
                return empty;
            }

            var previous = LoadRollup();
            var next = new Rollup { Version = RollupVersion, Files = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase) };

            foreach (var dir in projectDirs)
            {
                string[] transcripts;
                try
                {
                    transcripts = Directory.GetFiles(dir, "*.jsonl");
                }
                catch
                {
                    continue;
                }

                foreach (var file in transcripts)
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (!info.Exists) continue;

                        var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();

                        // Unchanged: reuse the aggregate instead of re-reading the file.
                        if (previous.Files != null &&
                            previous.Files.TryGetValue(file, out var cached) &&
                            cached != null &&
                            cached.MtimeMs == mtime &&
                            cached.Size == info.Length)
                        {
                            next.Files[file] = cached;
                            continue;
                        }

                        next.Files[file] = new FileEntry
                        {
                            MtimeMs = mtime,
                            Size = info.Length,
                            Days = ParseFile(File.ReadAllText(file)),
                        };
                    }
                    catch
                    {
                        // A problematic file is skipped; its stale entry simply falls out of
                        // the new rollup.
                    }
                }
            }

            SaveRollup(next);

            var byDay = new Dictionary<string, DayTotals>(StringComparer.Ordinal);
            long sent = 0;
            long received = 0;

            foreach (var entry in next.Files.Values)
            {
                if (entry?.Days == null) continue;
                foreach (var day in entry.Days)
                {
                    if (day.Value == null) continue;
                    if (!byDay.TryGetValue(day.Key, out var slot))
                    {
                        slot = new DayTotals();
                        byDay[day.Key] = slot;
                    }
                    slot.Sent += day.Value.Sent;
                    slot.Received += day.Value.Received;
                    sent += day.Value.Sent;
                    received += day.Value.Received;
                }
            }

            var days = byDay
                .Select(kv => new DailyTokens { Date = kv.Key, Sent = kv.Value.Sent, Received = kv.Value.Received })
                .OrderByDescending(d => d.Date, StringComparer.Ordinal)
                .Take(maxDays)
                .ToList();

            return new TokenTotals { Sent = sent, Received = received, Total = sent + received, Days = days };
        }

        /// <summary>Day map of the assistant lines in one transcript that carry usage.</summary>
        internal static Dictionary<string, DayTotals> ParseFile(string content)
        {
            var days = new Dictionary<string, DayTotals>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(content)) return days;

            // Responses already counted in THIS file. The duplication never crosses files,
            // so a per-file set is enough.
            var counted = new HashSet<string>(StringComparer.Ordinal);

            foreach (var line in content.Split('\n'))
            {
                // Cheap prefilter: most lines are user messages or tool results, and parsing
                // every one of them over a long history is the expensive part.
                if (line.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0) continue;
                if (line.IndexOf("usage", StringComparison.Ordinal) < 0) continue;

                JsonElement root;
                try
                {
                    using (var document = JsonDocument.Parse(line))
                    {
                        root = document.RootElement.Clone();
                    }
                }
                catch (JsonException)
                {
                    continue;
                }

                if (!root.TryGetProperty("type", out var type) || type.GetString() != "assistant") continue;
                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                if (!message.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object) continue;

                if (!root.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String) continue;
                if (!DateTimeOffset.TryParse(timestamp.GetString(), out var when)) continue;

                var key = UsageKey.For(root);
                // Same response, another block: its usage was already counted.
                if (key != null && !counted.Add(key)) continue;

                var usage = Json.TryDeserialize<Usage>(usageElement);
                if (usage == null) continue;

                var lineSent = usage.InputTokens.GetValueOrDefault()
                               + usage.CacheReadInputTokens.GetValueOrDefault()
                               + usage.CacheCreationInputTokens.GetValueOrDefault();
                var lineReceived = usage.OutputTokens.GetValueOrDefault();

                var day = LocalDay(when);
                if (!days.TryGetValue(day, out var slot))
                {
                    slot = new DayTotals();
                    days[day] = slot;
                }
                slot.Sent += lineSent;
                slot.Received += lineReceived;
            }

            return days;
        }

        /// <summary>
        /// YYYY-MM-DD in LOCAL time. "Per day" means the user's day, so a turn at 23:00 must
        /// not land on tomorrow because UTC says so.
        /// </summary>
        internal static string LocalDay(DateTimeOffset when)
        {
            return when.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private Rollup LoadRollup()
        {
            var raw = FileStore.ReadAllTextOrNull(_rollupFile);
            if (raw != null)
            {
                var parsed = Json.TryDeserialize<Rollup>(raw);
                if (parsed?.Files != null && parsed.Version == RollupVersion) return parsed;
            }

            return new Rollup { Version = RollupVersion, Files = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase) };
        }

        private void SaveRollup(Rollup rollup)
        {
            // A cache, so a failed write costs a rescan rather than data.
            FileStore.WriteAtomic(_rollupFile, Json.Serialize(rollup));
        }
    }
}
