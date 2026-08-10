using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Stats;

namespace Tootega.Cockpit.Session
{
    internal sealed class LocalUsage
    {
        public double FiveHourUsd { get; set; }
        public double SevenDayUsd { get; set; }
        /// <summary>NEW tokens, excluding cache reads.</summary>
        public long FiveHourTokens { get; set; }
        public long SevenDayTokens { get; set; }
        public long FiveHourCacheRead { get; set; }
        public long SevenDayCacheRead { get; set; }
        public UsageBreakdown Breakdown { get; set; }
        public UsageAttribution Attribution { get; set; }
    }

    /// <summary>
    /// Estimates local usage over the 5h and 7-day windows by scanning the CLI's transcripts.
    /// Port of src/session/UsageAggregator.ts.
    ///
    /// It is an estimate, and for THIS machine only — it cannot see other devices or claude.ai.
    /// That is the same limitation the official /usage breakdown has, and the UI labels it as
    /// estimated rather than implying otherwise.
    ///
    /// The distinction that makes these numbers readable: "new" tokens (input, output,
    /// cache-create) are counted apart from cache reads. A cache read is the context re-read on
    /// every turn and can be ~97% of the raw total, so mixing them would drown the figure that
    /// actually reflects work done.
    /// </summary>
    internal sealed class UsageAggregator
    {
        /// <summary>Context above which a turn counts as long context — the same cut /usage uses.</summary>
        private const long LongContextTokens = 150_000;

        /// <summary>A tool_result arrives as text: four characters is about a token.</summary>
        private const int CharsPerToken = 4;

        private const long HourMs = 3_600_000L;

        private readonly string _projectsRoot;

        public UsageAggregator(string projectsRoot = null)
        {
            _projectsRoot = projectsRoot ?? ClaudeHome.ProjectsDir;
        }

        /// <summary>Raw accumulation for the attribution, turned into percentages at the end.</summary>
        private sealed class Attribution
        {
            public long LongContextTokens;
            public long SubagentTokens;
            public long CacheRead;
            public long CacheCreate;
            public readonly Dictionary<string, ToolContextSlice> ByTool =
                new Dictionary<string, ToolContextSlice>(StringComparer.Ordinal);
        }

        public Task<LocalUsage> ComputeAsync(long nowMs)
        {
            return Task.Run(() => Compute(nowMs));
        }

        internal LocalUsage Compute(long nowMs)
        {
            var usage = new LocalUsage
            {
                Breakdown = new UsageBreakdown { ByModel = new List<UsageSlice>(), BySource = new List<UsageSlice>() },
                Attribution = new UsageAttribution { ByTool = new List<ToolContextSlice>() },
            };

            var since7d = nowMs - 7 * 24 * HourMs;
            var since5h = nowMs - 5 * HourMs;

            string[] projectDirs;
            try
            {
                if (!Directory.Exists(_projectsRoot)) return usage;
                projectDirs = Directory.GetDirectories(_projectsRoot);
            }
            catch
            {
                return usage;
            }

            var byModel = new Dictionary<string, UsageSlice>(StringComparer.Ordinal);
            var bySource = new Dictionary<string, UsageSlice>(StringComparer.Ordinal);
            var attribution = new Attribution();

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
                        // A file untouched since before the window can only hold old data.
                        if (!info.Exists) continue;
                        if (new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() < since7d) continue;

                        Accumulate(File.ReadAllText(file), since7d, since5h, usage, byModel, bySource, attribution);
                    }
                    catch
                    {
                        // A problematic file is skipped; the rest of the estimate still stands.
                    }
                }
            }

            // Largest first by cost. `<synthetic>` is a CLI marker for turns without a real
            // call, not a model, so it is excluded rather than shown as one.
            usage.Breakdown.ByModel = byModel.Values
                .Where(s => s.Key != "<synthetic>" && (s.Tokens > 0 || s.CacheRead > 0))
                .OrderByDescending(s => s.Usd)
                .ToList();

            // Fixed main-then-subagent order, so the block reads the same way every time.
            usage.Breakdown.BySource = new[] { "main", "subagent" }
                .Select(key => bySource.TryGetValue(key, out var slice) ? slice : null)
                .Where(slice => slice != null && slice.Tokens > 0)
                .ToList();

            var denominator = usage.SevenDayTokens > 0 ? usage.SevenDayTokens : 1;
            var cacheTotal = attribution.CacheRead + attribution.CacheCreate;

            usage.Attribution = new UsageAttribution
            {
                LongContextPct = (double)attribution.LongContextTokens / denominator,
                SubagentPct = (double)attribution.SubagentTokens / denominator,
                // Absent rather than zero when there was no cache activity at all.
                CacheHitPct = cacheTotal > 0 ? (double)attribution.CacheRead / cacheTotal : (double?)null,
                ByTool = attribution.ByTool.Values
                    .Where(s => s.Tokens > 0)
                    .OrderByDescending(s => s.Tokens)
                    .ToList(),
            };

            return usage;
        }

        private static void Accumulate(string content, long since7d, long since5h, LocalUsage usage,
                                       Dictionary<string, UsageSlice> byModel,
                                       Dictionary<string, UsageSlice> bySource,
                                       Attribution attribution)
        {
            // Responses already counted in THIS file — see UsageKey.
            var counted = new HashSet<string>(StringComparer.Ordinal);

            // tool_use_id to the tool's bucket. The tool_result arrives on a later user line
            // without the tool's name, and the link only exists inside one file.
            var toolOf = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var line in content.Split('\n'))
            {
                // Cheap prefilter: over a week of history, parsing every line is the cost.
                var isAssistant = line.IndexOf("\"assistant\"", StringComparison.Ordinal) >= 0;
                if (!isAssistant && line.IndexOf("\"tool_result\"", StringComparison.Ordinal) < 0) continue;

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

                if (!root.TryGetProperty("timestamp", out var timestamp) ||
                    timestamp.ValueKind != JsonValueKind.String) continue;
                if (!DateTimeOffset.TryParse(timestamp.GetString(), out var when)) continue;

                var ts = when.ToUnixTimeMilliseconds();
                if (ts < since7d) continue;

                var type = ReadString(root, "type");

                if (type == "user")
                {
                    // A user line only matters for its tool_results: how much the tool injected.
                    AccumulateToolResults(root, toolOf, attribution);
                    continue;
                }

                if (type != "assistant") continue;
                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                if (!message.TryGetProperty("usage", out var usageElement) ||
                    usageElement.ValueKind != JsonValueKind.Object) continue;

                // Record each tool_use's name before the dedup check: the result may be matched
                // from a line whose usage was already counted.
                RecordToolUses(message, toolOf);

                var key = UsageKey.For(root);
                if (key != null && !counted.Add(key)) continue;

                var parsed = Json.TryDeserialize<Usage>(usageElement);
                if (parsed == null) continue;

                var model = ReadString(message, "model");
                var cost = CostModel.EstimateCost(parsed, model);

                var input = CostModel.Num(parsed.InputTokens);
                var output = CostModel.Num(parsed.OutputTokens);
                var cacheCreate = CostModel.Num(parsed.CacheCreationInputTokens);
                var cacheRead = CostModel.Num(parsed.CacheReadInputTokens);

                var newTokens = input + output + cacheCreate;

                usage.SevenDayUsd += cost;
                usage.SevenDayTokens += newTokens;
                usage.SevenDayCacheRead += cacheRead;

                // The turn's context is everything the model read to answer.
                var context = input + cacheRead + cacheCreate;
                if (context > LongContextTokens) attribution.LongContextTokens += newTokens;

                var isSidechain = root.TryGetProperty("isSidechain", out var sidechain) &&
                                  sidechain.ValueKind == JsonValueKind.True;
                if (isSidechain) attribution.SubagentTokens += newTokens;

                attribution.CacheRead += cacheRead;
                attribution.CacheCreate += cacheCreate;

                Bump(byModel, CostModel.NormalizeModel(model) ?? "unknown", cost, newTokens, cacheRead);
                Bump(bySource, isSidechain ? "subagent" : "main", cost, newTokens, cacheRead);

                if (ts >= since5h)
                {
                    usage.FiveHourUsd += cost;
                    usage.FiveHourTokens += newTokens;
                    usage.FiveHourCacheRead += cacheRead;
                }
            }
        }

        private static void RecordToolUses(JsonElement message, Dictionary<string, string> toolOf)
        {
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (ReadString(block, "type") != "tool_use") continue;

                var id = ReadString(block, "id");
                if (id == null) continue;

                var name = ReadString(block, "name") ?? "?";
                var input = block.TryGetProperty("input", out var inputElement) ? inputElement : (JsonElement?)null;
                toolOf[id] = ToolBucket(name, input);
            }
        }

        private static void AccumulateToolResults(JsonElement root, Dictionary<string, string> toolOf,
                                                  Attribution attribution)
        {
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (ReadString(block, "type") != "tool_result") continue;

                var toolUseId = ReadString(block, "tool_use_id");
                if (toolUseId == null) continue;

                // A tool_use from outside this window or file cannot be attributed, so the
                // result is skipped rather than filed under a guess.
                if (!toolOf.TryGetValue(toolUseId, out var bucket)) continue;

                if (!attribution.ByTool.TryGetValue(bucket, out var slice))
                {
                    slice = new ToolContextSlice { Key = bucket };
                    attribution.ByTool[bucket] = slice;
                }

                slice.Calls++;
                var chars = block.TryGetProperty("content", out var resultContent) ? ResultChars(resultContent) : 0;
                slice.Tokens += (long)Math.Round((double)chars / CharsPerToken);
            }
        }

        /// <summary>
        /// Groups a tool for attribution.
        ///
        /// MCP tools collapse to their server, because what matters is which server inflates
        /// the context rather than which of its tools did; skills collapse to the skill name.
        /// Everything else keeps its own name.
        /// </summary>
        internal static string ToolBucket(string name, JsonElement? input)
        {
            if (name == null) return "?";

            if (name.StartsWith("mcp__", StringComparison.Ordinal))
            {
                var parts = name.Split(new[] { "__" }, StringSplitOptions.None);
                return "mcp:" + (parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? parts[1] : "?");
            }

            if (name == "Skill" && input?.ValueKind == JsonValueKind.Object &&
                input.Value.TryGetProperty("skill", out var skill) && skill.ValueKind == JsonValueKind.String)
            {
                var skillName = skill.GetString();
                if (!string.IsNullOrEmpty(skillName)) return "skill:" + skillName;
            }

            return name;
        }

        /// <summary>Size in characters of a tool_result's content — plain text or blocks.</summary>
        internal static long ResultChars(JsonElement content)
        {
            if (content.ValueKind == JsonValueKind.String) return content.GetString()?.Length ?? 0;
            if (content.ValueKind != JsonValueKind.Array) return 0;

            long total = 0;
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.String)
                {
                    total += block.GetString()?.Length ?? 0;
                }
                else if (block.ValueKind == JsonValueKind.Object &&
                         block.TryGetProperty("text", out var text) &&
                         text.ValueKind == JsonValueKind.String)
                {
                    total += text.GetString()?.Length ?? 0;
                }
            }
            return total;
        }

        private static void Bump(Dictionary<string, UsageSlice> map, string key, double usd, long tokens, long cacheRead)
        {
            if (!map.TryGetValue(key, out var slice))
            {
                slice = new UsageSlice { Key = key };
                map[key] = slice;
            }

            slice.Usd += usd;
            slice.Tokens += tokens;
            slice.CacheRead += cacheRead;
        }

        private static string ReadString(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }
    }
}
