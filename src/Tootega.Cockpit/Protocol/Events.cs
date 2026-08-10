using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tootega.Cockpit.Protocol
{
    /// <summary>
    /// Schemas of the stream-json events the Claude Code CLI emits, one JSON object per
    /// line. Port of shared/events.ts.
    ///
    /// The contract is tolerant on purpose: every type keeps an Extra bag for keys we do
    /// not model, and unions are flattened into one class with optional members rather
    /// than a discriminated hierarchy. A CLI release that adds a field must not require an
    /// extension release, and one that drops a field must degrade to "absent", not throw.
    /// </summary>
    internal sealed class Usage
    {
        [JsonPropertyName("input_tokens")] public long? InputTokens { get; set; }
        [JsonPropertyName("output_tokens")] public long? OutputTokens { get; set; }
        [JsonPropertyName("cache_creation_input_tokens")] public long? CacheCreationInputTokens { get; set; }
        [JsonPropertyName("cache_read_input_tokens")] public long? CacheReadInputTokens { get; set; }
    }

    /// <summary>
    /// One block of message content. text / thinking / tool_use / tool_result are folded
    /// into a single shape because the parser branches on <see cref="Type"/> anyway, and a
    /// block type we have never seen still needs to round-trip without losing data.
    /// </summary>
    internal sealed class ContentBlock
    {
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("text")] public string Text { get; set; }
        [JsonPropertyName("thinking")] public string Thinking { get; set; }

        // tool_use
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("input")] public JsonElement? Input { get; set; }

        // tool_result
        [JsonPropertyName("tool_use_id")] public string ToolUseId { get; set; }
        [JsonPropertyName("content")] public JsonElement? Content { get; set; }
        [JsonPropertyName("is_error")] public bool? IsError { get; set; }

        [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }
    }

    internal sealed class McpServerRef
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; }
    }

    /// <summary>
    /// A server the CLI skipped during config validation. The shape is tolerant: it can
    /// arrive as a bare string or as an object carrying the name plus a message under one
    /// of several keys, so all of them are accepted and normalized at the call site.
    /// </summary>
    internal sealed class McpServerError
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("server")] public string Server { get; set; }
        [JsonPropertyName("error")] public string Error { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; }

        public string ServerName => !string.IsNullOrEmpty(Name) ? Name : Server;
        public string Reason => !string.IsNullOrEmpty(Error) ? Error : Message;
    }

    /// <summary>A denial made by the ENGINE (auto mode / missing permission), not by the user.</summary>
    internal sealed class PermissionDenial
    {
        [JsonPropertyName("tool_name")] public string ToolName { get; set; }
        [JsonPropertyName("tool_use_id")] public string ToolUseId { get; set; }
        [JsonPropertyName("tool_input")] public JsonElement? ToolInput { get; set; }
    }

    /// <summary>
    /// Account usage limits, emitted when a bucket's status changes. `utilization` only
    /// arrives once the bucket crosses the warning threshold — at low usage the event
    /// carries status/resetsAt/rateLimitType alone (claude-code #50518).
    /// </summary>
    internal sealed class RateLimitInfo
    {
        [JsonPropertyName("status")] public string Status { get; set; }
        [JsonPropertyName("resetsAt")] public double? ResetsAt { get; set; }
        [JsonPropertyName("rateLimitType")] public string RateLimitType { get; set; }
        [JsonPropertyName("utilization")] public double? Utilization { get; set; }
        [JsonPropertyName("overageStatus")] public string OverageStatus { get; set; }
        [JsonPropertyName("isUsingOverage")] public bool? IsUsingOverage { get; set; }
    }

    internal sealed class AssistantMessage
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("role")] public string Role { get; set; }
        [JsonPropertyName("model")] public string Model { get; set; }
        [JsonPropertyName("content")] public List<ContentBlock> Content { get; set; }
        [JsonPropertyName("usage")] public Usage Usage { get; set; }
        [JsonPropertyName("stop_reason")] public string StopReason { get; set; }
    }

    /// <summary>
    /// A user event's content is either a block list or a bare string, so it is kept raw
    /// and normalized by the parser instead of forcing one of the two shapes here.
    /// </summary>
    internal sealed class UserMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; }
        [JsonPropertyName("content")] public JsonElement? Content { get; set; }
    }

    internal sealed class ControlRequestBody
    {
        [JsonPropertyName("subtype")] public string Subtype { get; set; }
        [JsonPropertyName("tool_name")] public string ToolName { get; set; }
        [JsonPropertyName("input")] public JsonElement? Input { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }
    }

    /// <summary>
    /// One stream-json line, in the flattened form the parser consumes. Every high-level
    /// event type of shared/events.ts maps onto this: <see cref="Type"/> (and
    /// <see cref="Subtype"/>) select which members are meaningful.
    /// </summary>
    internal sealed class ClaudeEvent
    {
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("subtype")] public string Subtype { get; set; }
        [JsonPropertyName("session_id")] public string SessionId { get; set; }
        [JsonPropertyName("uuid")] public string Uuid { get; set; }

        // system / init
        [JsonPropertyName("cwd")] public string Cwd { get; set; }
        [JsonPropertyName("model")] public string Model { get; set; }
        [JsonPropertyName("tools")] public List<string> Tools { get; set; }
        [JsonPropertyName("mcp_servers")] public List<McpServerRef> McpServers { get; set; }
        [JsonPropertyName("mcp_server_errors")] public JsonElement? McpServerErrors { get; set; }
        [JsonPropertyName("permissionMode")] public string PermissionMode { get; set; }
        [JsonPropertyName("slash_commands")] public List<string> SlashCommands { get; set; }

        /// <summary>
        /// Real context window, when the engine knows it. Tootega reports the server's
        /// --ctx; the Claude CLI does not send this, so it is usually absent and the limit
        /// is derived from the active model instead.
        /// </summary>
        [JsonPropertyName("context_window")] public long? ContextWindow { get; set; }

        // assistant / user
        [JsonPropertyName("message")] public JsonElement? Message { get; set; }

        // result
        [JsonPropertyName("is_error")] public bool? IsError { get; set; }
        [JsonPropertyName("result")] public string Result { get; set; }
        [JsonPropertyName("total_cost_usd")] public double? TotalCostUsd { get; set; }
        [JsonPropertyName("usage")] public Usage Usage { get; set; }
        [JsonPropertyName("num_turns")] public int? NumTurns { get; set; }
        [JsonPropertyName("duration_ms")] public double? DurationMs { get; set; }

        /// <summary>
        /// Denials the engine decided on its own. Only the tool and its input — the REASON
        /// arrives in the error tool_result carrying the same tool_use_id.
        /// </summary>
        [JsonPropertyName("permission_denials")] public List<PermissionDenial> PermissionDenials { get; set; }

        // stream_event (--include-partial-messages)
        [JsonPropertyName("event")] public JsonElement? Event { get; set; }

        // rate_limit_event
        [JsonPropertyName("rate_limit_info")] public RateLimitInfo RateLimitInfo { get; set; }

        // control_request
        [JsonPropertyName("request_id")] public string RequestId { get; set; }
        [JsonPropertyName("request")] public ControlRequestBody Request { get; set; }

        [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }

        public AssistantMessage AsAssistantMessage()
        {
            return Message.HasValue ? Json.TryDeserialize<AssistantMessage>(Message.Value) : null;
        }

        public UserMessage AsUserMessage()
        {
            return Message.HasValue ? Json.TryDeserialize<UserMessage>(Message.Value) : null;
        }
    }

    /// <summary>Well-known values of <see cref="ClaudeEvent.Type"/>, kept as constants
    /// rather than an enum so an unrecognized type stays representable.</summary>
    internal static class EventTypes
    {
        public const string System = "system";
        public const string Assistant = "assistant";
        public const string User = "user";
        public const string Result = "result";
        public const string StreamEvent = "stream_event";
        public const string ControlRequest = "control_request";
        public const string ControlResponse = "control_response";
        public const string RateLimitEvent = "rate_limit_event";
    }

    /// <summary>Rate-limit bucket names. `seven_day_&lt;model&gt;` variants exist too, so
    /// comparisons should tolerate names not listed here.</summary>
    internal static class RateLimitBuckets
    {
        public const string FiveHour = "five_hour";
        public const string SevenDay = "seven_day";
        public const string SevenDayOpus = "seven_day_opus";
        public const string SevenDaySonnet = "seven_day_sonnet";
        public const string Overage = "overage";
    }
}
