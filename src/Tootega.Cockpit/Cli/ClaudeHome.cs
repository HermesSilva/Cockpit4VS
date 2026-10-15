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

        /// <summary>
        /// Where the CLI would keep the credentials file: $CLAUDE_CONFIG_DIR when set, else
        /// ~/.claude. Reading a fixed path missed the login entirely when the user ran with
        /// CLAUDE_CONFIG_DIR pointed elsewhere.
        /// </summary>
        public static string CredentialsFile
        {
            get
            {
                var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")?.Trim();
                var baseDir = string.IsNullOrEmpty(dir) ? Root : dir;
                return Path.Combine(baseDir, ".credentials.json");
            }
        }

        /// <summary>Where the CLI registers every running process, used for remote-control liveness.</summary>
        public static string SessionsDir => Path.Combine(Root, "sessions");

        public static string ProjectsDir => Path.Combine(Root, "projects");

        /// <summary>The Cockpit's own per-machine data (dictionaries, caches).</summary>
        public static string CockpitDir => Path.Combine(Root, "tootega");

        /// <summary>
        /// The OAuth bearer for direct Anthropic calls, following the CLI's login precedence so
        /// EVERY login type works here — not just a subscription /login:
        ///   1. ANTHROPIC_AUTH_TOKEN      (env) — gateway/proxy bearer
        ///   2. CLAUDE_CODE_OAUTH_TOKEN   (env) — the long-lived `claude setup-token` token,
        ///      which the CLI never writes to disk (it lives only in this env var)
        ///   3. claudeAiOauth.accessToken (file) — the on-disk subscription login
        /// Read-only. Null only when nothing is resolvable. ANTHROPIC_API_KEY is intentionally
        /// not here — it is an x-api-key credential, resolved by AnthropicHttp.ResolveCredentials.
        /// </summary>
        public static string ReadOauthToken()
        {
            var fromEnv = EnvToken("ANTHROPIC_AUTH_TOKEN") ?? EnvToken("CLAUDE_CODE_OAUTH_TOKEN");
            if (fromEnv != null) return fromEnv;

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
        ///
        /// Also null when an env token (ANTHROPIC_AUTH_TOKEN / CLAUDE_CODE_OAUTH_TOKEN) is the
        /// credential in use: that token shadows the file login and carries no on-disk expiry,
        /// so a stale file date would not describe the active credential (the CLI shows no
        /// expiry warning for those logins either).
        /// </summary>
        public static long? ReadLoginExpiry()
        {
            if (EnvToken("ANTHROPIC_AUTH_TOKEN") != null || EnvToken("CLAUDE_CODE_OAUTH_TOKEN") != null)
                return null;

            var oauth = ReadOauthSection();
            if (oauth == null) return null;

            var value = ReadPositiveLong(oauth.Value, "refreshTokenExpiresAt")
                        ?? ReadPositiveLong(oauth.Value, "expiresAt");
            return value;
        }

        private static string EnvToken(string name) => Nullify(Environment.GetEnvironmentVariable(name)?.Trim());

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
