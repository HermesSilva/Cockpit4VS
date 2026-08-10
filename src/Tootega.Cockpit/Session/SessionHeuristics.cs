using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tootega.Cockpit.Session
{
    internal sealed class EngineNotice
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public string Topic { get; set; }
    }

    /// <summary>
    /// The judgement calls Session makes about a stream it does not control: is this error
    /// fatal or transient, is this an auth problem, is this unknown system event worth showing.
    ///
    /// Kept apart from the state machine because these are the parts most likely to be wrong,
    /// and the only ones that can be checked against real CLI output without a process.
    /// </summary>
    internal static class SessionHeuristics
    {
        /// <summary>
        /// A transient failure — a dropped connection, a stall, a retry, a 5xx. NOT fatal: the
        /// modern CLI preserves the partial response and retries.
        ///
        /// Telling this apart from a real error is what keeps the UI from showing alarming
        /// noise and, worse, triggering a sign-in flow over a network hiccup.
        /// </summary>
        private static readonly Regex TransientText = new Regex(
            @"error_during_execution|stream (disconnect|stall|error)|connection (drop|reset|closed|error)" +
            @"|ECONNRESET|ETIMEDOUT|socket hang up|premature close|waiting for api response|will retry" +
            @"|overloaded|\b5\d{2}\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TransientSubtype = new Regex(
            @"error_during_execution|max_turns", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Conservative: a headless session cannot sign in, so a false positive here sends the
        /// user through an auth flow they did not need.
        /// </summary>
        private static readonly Regex AuthText = new Regex(
            @"please run /login|/login|not authenticated|authentication (failed|required|error)" +
            @"|invalid api key|unauthorized|\b401\b|oauth|please (log ?in|sign ?in)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Subtypes that carry a warning, matched by shape rather than exact name.</summary>
        private static readonly Regex WarningishSubtype = new Regex(
            @"warn|notice|credit|limit|restrict|degrad|fallback",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SlashPrefix = new Regex(@"^/", RegexOptions.Compiled);

        /// <summary>The `system` subtypes Session handles itself. Never a notice.</summary>
        private static readonly HashSet<string> KnownSystemSubtypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "init",
            "background_tasks_changed",
            "task_started",
            "task_updated",
            "task_notification",
            "compact_boundary",
            "status",
            "hook_response",
        };

        public static bool IsTransientError(string text, string subtype = null)
        {
            return TransientText.IsMatch(text ?? string.Empty) || TransientSubtype.IsMatch(subtype ?? string.Empty);
        }

        public static bool IsAuthError(string text)
        {
            return AuthText.IsMatch(text ?? string.Empty);
        }

        /// <summary>
        /// A tool label from the engine's task_type. Used only when a task appears without us
        /// having seen the tool_use that launched it — a resumed session, or a subagent.
        /// </summary>
        public static string TaskTool(string taskType)
        {
            switch (taskType)
            {
                case "local_bash": return "Bash";
                case "workflow": return "Workflow";
                default: return string.IsNullOrEmpty(taskType) ? "Task" : taskType;
            }
        }

        /// <summary>
        /// A mid-session warning carried by a `system` event, recognised by SHAPE rather than by
        /// an exact subtype.
        ///
        /// Fast mode running out of usage credits and a restricted subagent model falling back
        /// to the parent's both arrived this way in different releases, and the next one will
        /// add more. Anything with a warning-ish subtype or an explicit warning field is
        /// surfaced; every other unknown event keeps being ignored, as the tolerant stream
        /// contract requires.
        /// </summary>
        public static EngineNotice ReadEngineNotice(JsonElement system)
        {
            if (system.ValueKind != JsonValueKind.Object) return null;

            var subtype = ReadString(system, "subtype");
            if (string.IsNullOrEmpty(subtype) || KnownSystemSubtypes.Contains(subtype)) return null;

            var explicitWarning = ReadString(system, "warning");
            if (!WarningishSubtype.IsMatch(subtype) && explicitWarning == null) return null;

            var text = explicitWarning
                       ?? ReadString(system, "message")
                       ?? ReadString(system, "text")
                       // Last resort: the subtype itself, made readable. Better than an empty
                       // banner that says something happened without saying what.
                       ?? subtype.Replace('_', ' ');

            var id = subtype + ":" + text;
            return new EngineNotice
            {
                Id = id.Length > 200 ? id.Substring(0, 200) : id,
                Text = text,
                Topic = subtype,
            };
        }

        /// <summary>
        /// Slash-command names from the initialize handshake.
        ///
        /// This matters because the handshake answers BEFORE the first message, while the init
        /// event only arrives after one — so without it a fresh tab would have no command
        /// autocomplete until the user had already sent something. The key names vary between
        /// CLI versions, hence the several accepted spellings.
        /// </summary>
        public static IReadOnlyList<string> ExtractSlashCommands(JsonElement? payload)
        {
            var commands = new List<string>();
            if (payload?.ValueKind != JsonValueKind.Object) return commands;

            JsonElement array = default;
            var found = false;
            foreach (var key in new[] { "commands", "slash_commands", "slashCommands", "available_commands" })
            {
                if (!payload.Value.TryGetProperty(key, out array) || array.ValueKind != JsonValueKind.Array) continue;
                found = true;
                break;
            }
            if (!found) return commands;

            foreach (var entry in array.EnumerateArray())
            {
                string name = null;
                if (entry.ValueKind == JsonValueKind.String) name = entry.GetString();
                else if (entry.ValueKind == JsonValueKind.Object) name = ReadString(entry, "name");

                if (string.IsNullOrEmpty(name)) continue;
                commands.Add(SlashPrefix.Replace(name, string.Empty));
            }

            return commands;
        }

        /// <summary>
        /// Parses a tool input assembled from streamed JSON fragments.
        ///
        /// A truncated stream leaves invalid JSON, and the raw text is kept under a marker
        /// rather than discarded: showing the user what the tool was about to receive beats
        /// showing an empty card.
        /// </summary>
        public static JsonElement SafeJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return ParseOrEmpty("{}");

            try
            {
                using (var document = JsonDocument.Parse(json))
                {
                    return document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                return ParseOrEmpty("{\"_raw\":" + JsonSerializer.Serialize(json) + "}");
            }
        }

        private static JsonElement ParseOrEmpty(string json)
        {
            using (var document = JsonDocument.Parse(json))
            {
                return document.RootElement.Clone();
            }
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
