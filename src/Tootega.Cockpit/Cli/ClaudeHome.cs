using System;
using System.IO;
using System.Text.Json;

namespace Tootega.Cockpit.Cli
{
    /// <summary>
    /// Locations under ~/.claude, and the two read-only facts the Cockpit needs from the
    /// credentials file.
    ///
    /// Credentials are READ-only here and are never written, echoed or logged — not even at
    /// debug level, and not truncated "just to identify it". The only things exposed are the
    /// token (to callers that need to authenticate) and the login expiry (to warn before a
    /// long session is interrupted).
    /// </summary>
    internal static class ClaudeHome
    {
        public static string Root =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

        /// <summary>User-level CLI settings, shared with the CLI itself. Never rewritten
        /// wholesale — see the statusline and UTF-8 hook installers.</summary>
        public static string SettingsFile => Path.Combine(Root, "settings.json");

        public static string CredentialsFile => Path.Combine(Root, ".credentials.json");

        /// <summary>Where the CLI registers every running process, used for remote-control liveness.</summary>
        public static string SessionsDir => Path.Combine(Root, "sessions");

        public static string ProjectsDir => Path.Combine(Root, "projects");

        /// <summary>The Cockpit's own per-machine data (dictionaries, caches).</summary>
        public static string CockpitDir => Path.Combine(Root, "tootega");

        /// <summary>OAuth access token, read-only. Null when missing or unreadable.</summary>
        public static string ReadOauthToken()
        {
            var oauth = ReadOauthSection();
            if (oauth == null) return null;

            return oauth.Value.TryGetProperty("accessToken", out var token) && token.ValueKind == JsonValueKind.String
                ? Nullify(token.GetString())
                : null;
        }

        /// <summary>
        /// LOGIN validity (epoch ms), read-only.
        ///
        /// This is refreshTokenExpiresAt: `expiresAt` belongs to the access token (hours),
        /// which the CLI renews by itself. What actually expires the login and forces a
        /// /login is the refresh token. Falls back to expiresAt only when the refresh field
        /// is absent, and returns null for accounts that keep no OAuth file at all (an API
        /// key, or a credential in the OS keychain).
        /// </summary>
        public static long? ReadLoginExpiry()
        {
            var oauth = ReadOauthSection();
            if (oauth == null) return null;

            var value = ReadPositiveLong(oauth.Value, "refreshTokenExpiresAt")
                        ?? ReadPositiveLong(oauth.Value, "expiresAt");
            return value;
        }

        private static JsonElement? ReadOauthSection()
        {
            try
            {
                var path = CredentialsFile;
                if (!File.Exists(path)) return null;

                using (var document = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;
                    return oauth.ValueKind == JsonValueKind.Object ? oauth.Clone() : (JsonElement?)null;
                }
            }
            catch
            {
                // Unreadable or malformed: treated as "no OAuth credential". Never logged —
                // the message could carry file content.
                return null;
            }
        }

        private static long? ReadPositiveLong(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind != JsonValueKind.Number) return null;
            return value.TryGetInt64(out var number) && number > 0 ? number : (long?)null;
        }

        private static string Nullify(string value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
