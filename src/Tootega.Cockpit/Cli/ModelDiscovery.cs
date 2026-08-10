using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Tootega.Cockpit.Cli
{
    /// <summary>
    /// A model as /v1/models reports it. Everything the picker shows comes from here — the
    /// extension curates no list of its own, so a model released to the account appears
    /// without an extension update.
    /// </summary>
    internal sealed class DiscoveredModel
    {
        public string Id { get; set; }
        /// <summary>
        /// max_input_tokens: the account's REAL window. Absent on accounts or versions that do
        /// not expose it yet, and absent is not the same as small.
        /// </summary>
        public long? ContextTokens { get; set; }
        /// <summary>The official label, so the id never has to be prettified by guesswork.</summary>
        public string DisplayName { get; set; }
        /// <summary>Release date — the picker's ordering key, newest first.</summary>
        public string CreatedAt { get; set; }
    }

    /// <summary>
    /// Optional model discovery through /v1/models. Port of src/cli/ModelDiscovery.ts.
    ///
    /// The CLI has no `models` subcommand, so this is the only way to know what the account can
    /// actually use. It works with an API key or with the subscription's OAuth token, which is
    /// what lets subscription accounts see new models the day they get them.
    ///
    /// GET /v1/models spends no tokens, so it fits the clean-utility exception: not the agent
    /// loop, no project context, credential read-only and never logged.
    /// </summary>
    internal static class ModelDiscovery
    {
        public const long OneMillion = 1_000_000;

        /// <summary>Returns the models the credential can reach, or an empty list on any failure.</summary>
        public static async Task<IReadOnlyList<DiscoveredModel>> DiscoverAsync(ApiCredentials credentials)
        {
            if (credentials == null || credentials.IsEmpty) return Array.Empty<DiscoveredModel>();

            var body = await AnthropicHttp
                .GetAsync(AnthropicHttp.Host + "/v1/models?limit=1000", credentials)
                .ConfigureAwait(false);

            return body == null ? Array.Empty<DiscoveredModel>() : Parse(body);
        }

        /// <summary>
        /// Parses the /v1/models body. Tolerant: an entry with no id is dropped and every other
        /// field is optional, because an older account may not expose max_input_tokens at all.
        /// </summary>
        public static IReadOnlyList<DiscoveredModel> Parse(string json)
        {
            var models = new List<DiscoveredModel>();
            if (string.IsNullOrWhiteSpace(json)) return models;

            try
            {
                using (var document = JsonDocument.Parse(json))
                {
                    if (!document.RootElement.TryGetProperty("data", out var data) ||
                        data.ValueKind != JsonValueKind.Array) return models;

                    foreach (var entry in data.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object) continue;

                        var id = ReadString(entry, "id");
                        if (string.IsNullOrEmpty(id)) continue;

                        models.Add(new DiscoveredModel
                        {
                            Id = id,
                            ContextTokens = ReadLong(entry, "max_input_tokens"),
                            DisplayName = ReadString(entry, "display_name"),
                            CreatedAt = ReadString(entry, "created_at"),
                        });
                    }
                }
            }
            catch (JsonException)
            {
                return new List<DiscoveredModel>();
            }

            return models;
        }

        /// <summary>
        /// The catalogue as picker ids: newest first, each carrying the [1m] suffix when its
        /// window is 1M.
        ///
        /// The suffix is what makes the CLI open the 1M window on models where it is not the
        /// default, and it is accepted as a no-op on the natively-1M ones. So the rule is
        /// derived from the reported window rather than from a per-model table that would go
        /// stale.
        /// </summary>
        public static IReadOnlyList<string> PickerIds(IEnumerable<DiscoveredModel> models)
        {
            if (models == null) return Array.Empty<string>();

            return models
                .OrderByDescending(m => m.CreatedAt ?? string.Empty, StringComparer.Ordinal)
                .Select(m => m.ContextTokens.GetValueOrDefault() >= OneMillion ? m.Id + "[1m]" : m.Id)
                .ToList();
        }

        private static string ReadString(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        private static long? ReadLong(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
            return value.TryGetInt64(out var number) ? number : (long?)null;
        }
    }
}
