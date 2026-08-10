using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Stats;

namespace Tootega.Cockpit.Cli
{
    /// <summary>
    /// Aggregated telemetry state, fed by OTLP data points. Port of the OtelState half of
    /// src/cli/OtelReceiver.ts.
    ///
    /// Kept apart from the server so the parsing — the part with all the shape assumptions —
    /// can be tested without opening a socket.
    /// </summary>
    internal sealed class OtelState
    {
        public long SinceTs { get; set; }
        public long LinesAdded { get; set; }
        public long LinesRemoved { get; set; }

        /// <summary>model to lines added.</summary>
        public Dictionary<string, long> LocByModel { get; } = new Dictionary<string, long>(StringComparer.Ordinal);
        /// <summary>model to REAL USD, from claude_code.cost.usage.</summary>
        public Dictionary<string, double> CostByModel { get; } = new Dictionary<string, double>(StringComparer.Ordinal);
        /// <summary>model to REAL tokens, from claude_code.token.usage.</summary>
        public Dictionary<string, long> TokensByModel { get; } = new Dictionary<string, long>(StringComparer.Ordinal);

        public long SessionCount { get; set; }
        public long CommitCount { get; set; }
        public long PrCount { get; set; }

        public Dictionary<string, OtelToolDecision> Decisions { get; } =
            new Dictionary<string, OtelToolDecision>(StringComparer.Ordinal);

        /// <summary>
        /// Cost and tokens per workflow RUN. Since CLI 2.1.202 the agents a workflow spawns
        /// carry workflow.run_id and workflow.name in their attributes, which is what makes it
        /// possible to total what a whole run spent — stream-json does not expose that. The
        /// effort attribute arrived in 2.1.214.
        /// </summary>
        public Dictionary<string, WorkflowAccumulator> Workflows { get; } =
            new Dictionary<string, WorkflowAccumulator>(StringComparer.Ordinal);

        public OtelState(long nowMs)
        {
            SinceTs = nowMs;
        }
    }

    internal sealed class WorkflowAccumulator
    {
        public string Name { get; set; }
        public double Usd { get; set; }
        public long Tokens { get; set; }
        /// <summary>A run can mix agents at different efforts, so the set is collected.</summary>
        public HashSet<string> Efforts { get; } = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Parses OTLP metric payloads and turns the aggregate into an <see cref="OtelStats"/>.
    ///
    /// This is the clean, structured source for data the event stream does not carry — lines of
    /// code per model, edit decisions, real per-model cost — with no transcript scanning.
    /// Tolerant throughout: an unrecognised metric or shape is ignored rather than fatal.
    /// </summary>
    internal static class OtelMetrics
    {
        /// <summary>Canonical order for displaying a run's efforts, lowest to highest.</summary>
        private static readonly string[] EffortOrder = { "low", "medium", "high", "xhigh", "max" };

        /// <summary>Aggregates an OTLP ExportMetricsServiceRequest (JSON) into the state.</summary>
        public static void Ingest(JsonElement body, OtelState state)
        {
            if (body.ValueKind != JsonValueKind.Object) return;
            if (!body.TryGetProperty("resourceMetrics", out var resourceMetrics) ||
                resourceMetrics.ValueKind != JsonValueKind.Array) return;

            foreach (var resource in resourceMetrics.EnumerateArray())
            {
                foreach (var scope in EnumerateArray(resource, "scopeMetrics"))
                {
                    foreach (var metric in EnumerateArray(scope, "metrics"))
                    {
                        IngestMetric(metric, state);
                    }
                }
            }
        }

        public static void Ingest(string json, OtelState state)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                using (var document = JsonDocument.Parse(json))
                {
                    Ingest(document.RootElement, state);
                }
            }
            catch (JsonException)
            {
                // A malformed export is dropped; the next one will arrive in ten seconds.
            }
        }

        private static void IngestMetric(JsonElement metric, OtelState state)
        {
            var name = ReadString(metric, "name");
            if (name == null) return;

            foreach (var point in DataPoints(metric))
            {
                var value = PointValue(point);
                // Zero and negative points carry no information for counters.
                if (value <= 0) continue;

                var attributes = Attributes(point);

                switch (name)
                {
                    case "claude_code.lines_of_code.count":
                        if (Attribute(attributes, "type") == "removed")
                        {
                            state.LinesRemoved += (long)value;
                        }
                        else
                        {
                            state.LinesAdded += (long)value;
                            Add(state.LocByModel, ModelOf(attributes), (long)value);
                        }
                        break;

                    case "claude_code.cost.usage":
                        // REAL cost, reported by the CLI itself.
                        Add(state.CostByModel, ModelOf(attributes), value);
                        AddWorkflow(state, attributes, value, 0);
                        break;

                    case "claude_code.token.usage":
                        Add(state.TokensByModel, ModelOf(attributes), (long)value);
                        AddWorkflow(state, attributes, 0, (long)value);
                        break;

                    case "claude_code.session.count":
                        state.SessionCount += (long)value;
                        break;

                    case "claude_code.commit.count":
                        state.CommitCount += (long)value;
                        break;

                    case "claude_code.pull_request.count":
                        state.PrCount += (long)value;
                        break;

                    case "claude_code.code_edit_tool.decision":
                        var tool = Attribute(attributes, "tool_name") ?? Attribute(attributes, "tool") ?? "tool";
                        if (!state.Decisions.TryGetValue(tool, out var decision))
                        {
                            decision = new OtelToolDecision { Tool = tool };
                            state.Decisions[tool] = decision;
                        }
                        if (Attribute(attributes, "decision") == "reject") decision.Reject += (int)value;
                        else decision.Accept += (int)value;
                        break;
                }
            }
        }

        private static void AddWorkflow(OtelState state, Dictionary<string, string> attributes, double usd, long tokens)
        {
            var runId = Attribute(attributes, "workflow.run_id");
            if (runId == null) return;

            var name = Attribute(attributes, "workflow.name");
            if (!state.Workflows.TryGetValue(runId, out var run))
            {
                run = new WorkflowAccumulator { Name = name ?? runId };
                state.Workflows[runId] = run;
            }

            // The name may only arrive on some points; the first real one wins over the id.
            if ((string.IsNullOrEmpty(run.Name) || run.Name == runId) && !string.IsNullOrEmpty(name))
                run.Name = name;

            run.Usd += usd;
            run.Tokens += tokens;

            var effort = Attribute(attributes, "effort");
            if (!string.IsNullOrEmpty(effort)) run.Efforts.Add(effort);
        }

        /// <summary>Builds the snapshot the Usage modal renders.</summary>
        public static OtelStats ToStats(OtelState state, bool enabled, string endpoint)
        {
            // OTEL telemetry does not separate cache reads from the rest, so cacheRead stays
            // zero here rather than being invented.
            var locByModel = state.LocByModel
                .Select(kv => new UsageSlice { Key = kv.Key, Usd = 0, Tokens = kv.Value, CacheRead = 0 })
                .OrderByDescending(s => s.Tokens)
                .ToList();

            var costByModel = state.CostByModel
                .Select(kv => new UsageSlice
                {
                    Key = kv.Key,
                    Usd = kv.Value,
                    Tokens = state.TokensByModel.TryGetValue(kv.Key, out var tokens) ? tokens : 0,
                    CacheRead = 0,
                })
                .OrderByDescending(s => s.Usd)
                .ToList();

            var decisions = state.Decisions.Values
                .OrderByDescending(d => d.Accept + d.Reject)
                .ToList();

            var workflows = state.Workflows
                .Select(kv => new WorkflowRun
                {
                    RunId = kv.Key,
                    Name = kv.Value.Name,
                    Usd = kv.Value.Usd,
                    Tokens = kv.Value.Tokens,
                    Effort = kv.Value.Efforts.Count > 0 ? string.Join(" · ", SortEfforts(kv.Value.Efforts)) : null,
                })
                .OrderByDescending(w => w.Usd)
                .ToList();

            // Empty collections and zero counters are reported as absent, so the panel shows
            // nothing instead of a row of zeroes that looks like measured data.
            return new OtelStats
            {
                Enabled = enabled,
                Endpoint = endpoint,
                SinceTs = state.SinceTs,
                LinesAdded = state.LinesAdded,
                LinesRemoved = state.LinesRemoved,
                LocByModel = locByModel.Count > 0 ? locByModel : null,
                CostByModel = costByModel.Count > 0 ? costByModel : null,
                SessionCount = state.SessionCount > 0 ? (int)state.SessionCount : (int?)null,
                CommitCount = state.CommitCount > 0 ? (int)state.CommitCount : (int?)null,
                PrCount = state.PrCount > 0 ? (int)state.PrCount : (int?)null,
                ToolDecisions = decisions.Count > 0 ? decisions : null,
                Workflows = workflows.Count > 0 ? workflows : null,
            };
        }

        internal static IEnumerable<string> SortEfforts(IEnumerable<string> efforts)
        {
            return efforts.OrderBy(e =>
            {
                var index = Array.IndexOf(EffortOrder, e);
                // An effort we do not know sorts last rather than first.
                return index < 0 ? 99 : index;
            });
        }

        /// <summary>
        /// Numeric value of a data point. asInt arrives as a STRING in OTLP/JSON, which is the
        /// detail that silently zeroes every counter if missed.
        /// </summary>
        internal static double PointValue(JsonElement point)
        {
            if (point.ValueKind != JsonValueKind.Object) return 0;

            if (point.TryGetProperty("asInt", out var asInt))
            {
                if (asInt.ValueKind == JsonValueKind.Number && asInt.TryGetDouble(out var number)) return number;
                if (asInt.ValueKind == JsonValueKind.String &&
                    double.TryParse(asInt.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }

            if (point.TryGetProperty("asDouble", out var asDouble) &&
                asDouble.ValueKind == JsonValueKind.Number &&
                asDouble.TryGetDouble(out var value) && !double.IsNaN(value))
            {
                return value;
            }

            return 0;
        }

        /// <summary>Flattens the OTLP attribute list into a plain map.</summary>
        internal static Dictionary<string, string> Attributes(JsonElement point)
        {
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var attribute in EnumerateArray(point, "attributes"))
            {
                var key = ReadString(attribute, "key");
                if (key == null) continue;
                if (!attribute.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object) continue;

                var text = ReadString(value, "stringValue")
                           ?? ReadScalarAsString(value, "intValue")
                           ?? ReadScalarAsString(value, "doubleValue")
                           ?? ReadScalarAsString(value, "boolValue");

                if (text != null) attributes[key] = text;
            }

            return attributes;
        }

        private static IEnumerable<JsonElement> DataPoints(JsonElement metric)
        {
            // A counter arrives as `sum`, an instantaneous value as `gauge`.
            foreach (var container in new[] { "sum", "gauge" })
            {
                if (!metric.TryGetProperty(container, out var wrapper) || wrapper.ValueKind != JsonValueKind.Object) continue;
                foreach (var point in EnumerateArray(wrapper, "dataPoints")) yield return point;
            }
        }

        private static IEnumerable<JsonElement> EnumerateArray(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object) yield break;
            if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) yield break;
            foreach (var item in array.EnumerateArray()) yield return item;
        }

        private static string ModelOf(Dictionary<string, string> attributes)
        {
            return CostModel.NormalizeModel(Attribute(attributes, "model")) ?? "unknown";
        }

        private static string Attribute(Dictionary<string, string> attributes, string key)
        {
            return attributes.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;
        }

        private static void Add(Dictionary<string, long> map, string key, long value)
        {
            map.TryGetValue(key, out var current);
            map[key] = current + value;
        }

        private static void Add(Dictionary<string, double> map, string key, double value)
        {
            map.TryGetValue(key, out var current);
            map[key] = current + value;
        }

        private static string ReadString(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        private static string ReadScalarAsString(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value)) return null;

            switch (value.ValueKind)
            {
                case JsonValueKind.String: return value.GetString();
                case JsonValueKind.Number: return value.ToString();
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                default: return null;
            }
        }
    }
}
