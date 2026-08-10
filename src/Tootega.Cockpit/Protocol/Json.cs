using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tootega.Cockpit.Protocol
{
    /// <summary>
    /// Shared serializer settings for both wires: the CLI's stream-json and the
    /// host&lt;-&gt;webview protocol.
    ///
    /// The contract is deliberately tolerant, mirroring the rule the TypeScript port
    /// follows: an unknown field is ignored rather than fatal, so the UI survives a CLI
    /// upgrade that adds or drops keys. Nulls are omitted on the way out because the
    /// webview's types treat "absent" and "null" the same, and omitting keeps the
    /// per-token stream messages small.
    /// </summary>
    internal static class Json
    {
        public static readonly JsonSerializerOptions Options = Create();

        private static JsonSerializerOptions Create()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                // A malformed line from the CLI must not take the session down.
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            return options;
        }

        public static string Serialize<T>(T value)
        {
            return JsonSerializer.Serialize(value, Options);
        }

        /// <summary>Deserializes, returning default instead of throwing on malformed input.</summary>
        public static T TryDeserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return default;
            try
            {
                return JsonSerializer.Deserialize<T>(json, Options);
            }
            catch (JsonException)
            {
                return default;
            }
        }

        /// <summary>Deserializes a sub-element, returning default instead of throwing.</summary>
        public static T TryDeserialize<T>(JsonElement element)
        {
            try
            {
                return element.Deserialize<T>(Options);
            }
            catch (JsonException)
            {
                return default;
            }
        }
    }
}
