using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    /// <summary>
    /// The subscription account, via `claude auth status --json` — the CLI's own official
    /// source. Port of src/cli/AuthStatus.ts.
    ///
    /// Equivalent to the ACCOUNT block of /usage: auth method, e-mail, org and plan.
    /// Tolerant: a failure or timeout reports "not logged in" rather than an error, because
    /// the account panel is informative and must not block a conversation.
    /// </summary>
    internal static class AuthStatus
    {
        private const int TimeoutMs = 8000;

        public static async Task<UsageAccount> FetchAsync(string claudePath)
        {
            try
            {
                var info = ProcessLauncher.Build(claudePath, new[] { "auth", "status", "--json" }, null);
                using (var process = Process.Start(info))
                {
                    if (process == null) return NotLoggedIn();

                    var stdout = process.StandardOutput.ReadToEndAsync();
                    // Drained so a chatty process cannot fill the pipe and deadlock.
                    var stderr = process.StandardError.ReadToEndAsync();

                    // Both pipes closing is the real end-of-output signal; WaitForExit alone
                    // can return before the last bytes have been read.
                    var reading = Task.WhenAll(stdout, stderr);
                    var finished = await Task.WhenAny(reading, Task.Delay(TimeoutMs)).ConfigureAwait(false);
                    if (finished != reading)
                    {
                        ProcessLauncher.KillTree(process);
                        return NotLoggedIn();
                    }

                    return Parse(await stdout.ConfigureAwait(false));
                }
            }
            catch (Exception ex)
            {
                Log.Debug("auth status failed: " + ex.Message);
                return NotLoggedIn();
            }
        }

        private static UsageAccount Parse(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return NotLoggedIn();

            try
            {
                using (var document = JsonDocument.Parse(output.Trim()))
                {
                    var root = document.RootElement;
                    return new UsageAccount
                    {
                        LoggedIn = ReadBool(root, "loggedIn"),
                        AuthMethod = ReadString(root, "authMethod"),
                        ApiProvider = ReadString(root, "apiProvider"),
                        Email = ReadString(root, "email"),
                        OrgName = ReadString(root, "orgName"),
                        Plan = ReadString(root, "subscriptionType"),
                        // Not part of `auth status --json`: read from the credentials file,
                        // so the UI can warn before the login expires mid-session.
                        LoginExpiresAt = ClaudeHome.ReadLoginExpiry(),
                    };
                }
            }
            catch (JsonException)
            {
                return NotLoggedIn();
            }
        }

        private static UsageAccount NotLoggedIn() => new UsageAccount { LoggedIn = false };

        private static bool ReadBool(JsonElement parent, string name)
        {
            return parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        }

        private static string ReadString(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }
    }
}
