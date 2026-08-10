using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    internal sealed class AskOptions
    {
        /// <summary>The user message. This is the entire context the call gets.</summary>
        public string Prompt { get; set; }
        public string System { get; set; }
        /// <summary>Overrides the configured internal model for this call.</summary>
        public string Model { get; set; }
        public int? MaxTokens { get; set; }
        public int? TimeoutMs { get; set; }
    }

    /// <summary>
    /// The Cockpit's own one-shot helper for utility calls. Port of src/cli/AiClient.ts.
    ///
    /// This is NOT the agent loop — that stays entirely in the CLI. It is the narrow exception
    /// recorded in CLAUDE.md: an isolated call carrying only what the task needs, with no agent
    /// system prompt, no tools, no MCP and no project context.
    ///
    /// The reason it exists rather than shelling out to the CLI for these: a `claude -p`
    /// one-shot has a cold start of several seconds and loads the full system prompt plus
    /// tools and MCP on every invocation. For correcting a dictated sentence or labelling a
    /// slash command, that is both far slower and far dirtier than sending the instruction and
    /// the text alone.
    ///
    /// The token is read-only and never written or logged. Every failure returns null; a
    /// utility helper must never be able to break a conversation.
    /// </summary>
    internal sealed class AiClient
    {
        private const string DefaultModel = "claude-haiku-4-5";
        private const int DefaultMaxTokens = 4096;
        private const int DefaultTimeoutMs = 20_000;

        private string _internalModel = DefaultModel;

        /// <summary>
        /// The model used for internal calls, injected from settings. Empty falls back to
        /// Haiku, which is the point: these calls should be cheap and fast.
        /// </summary>
        public void SetInternalModel(string model)
        {
            _internalModel = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        }

        public string InternalModel => _internalModel;

        /// <summary>
        /// Asks a one-shot question and returns the response text, or null on any failure —
        /// no token, non-2xx, timeout, unparseable reply. Never throws.
        /// </summary>
        public async Task<string> AskAsync(AskOptions options)
        {
            if (options == null || string.IsNullOrWhiteSpace(options.Prompt)) return null;

            var token = ClaudeHome.ReadOauthToken();
            if (string.IsNullOrEmpty(token))
            {
                Log.Debug("ai: no oauth token available");
                return null;
            }

            var payload = new Dictionary<string, object>
            {
                ["model"] = string.IsNullOrWhiteSpace(options.Model) ? _internalModel : options.Model,
                ["max_tokens"] = options.MaxTokens ?? DefaultMaxTokens,
                ["messages"] = new[]
                {
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = options.Prompt },
                },
            };

            if (!string.IsNullOrWhiteSpace(options.System)) payload["system"] = options.System;

            var body = await AnthropicHttp.PostJsonAsync(
                AnthropicHttp.Host + "/v1/messages",
                Json.Serialize(payload),
                new ApiCredentials { AuthToken = token },
                options.TimeoutMs ?? DefaultTimeoutMs).ConfigureAwait(false);

            return body == null ? null : ExtractText(body);
        }

        /// <summary>First text block of a Messages response, trimmed. Null when there is none.</summary>
        internal static string ExtractText(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using (var document = JsonDocument.Parse(json))
                {
                    if (!document.RootElement.TryGetProperty("content", out var content) ||
                        content.ValueKind != JsonValueKind.Array) return null;

                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.ValueKind != JsonValueKind.Object) continue;
                        if (!block.TryGetProperty("type", out var type) || type.GetString() != "text") continue;
                        if (!block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) continue;

                        var value = text.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(value)) return value;
                    }
                }
            }
            catch (JsonException ex)
            {
                Log.Debug("ai: could not parse the response: " + ex.Message);
            }

            return null;
        }
    }
}
