using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Tootega.Cockpit.Cli
{
    /// <summary>
    /// Compares the installed Claude Code version with the latest published one. Port of
    /// src/cli/CliVersion.ts.
    ///
    /// Best-effort throughout: a network failure yields null, which the UI reads as "up to
    /// date". An update indicator is a convenience, and it must never be the reason a
    /// conversation cannot start.
    /// </summary>
    internal static class CliVersion
    {
        private const string Package = "@anthropic-ai/claude-code";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
        private static readonly Regex SemverPattern = new Regex(@"(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);
        private static readonly object CacheLock = new object();

        private static string _cachedLatest;
        private static DateTime _cachedAt = DateTime.MinValue;

        private static readonly Lazy<HttpClient> Client = new Lazy<HttpClient>(() =>
        {
            // .NET Framework defaults to a protocol set that registry.npmjs.org no longer
            // accepts; without this the request fails as a connection reset.
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch
            {
                // Older frameworks may not know Tls12; the request will simply fail.
            }

            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Add("User-Agent", "Tootega-Cockpit");
            return client;
        });

        /// <summary>Latest published version, or null when unknown. Cached for six hours.</summary>
        public static async Task<string> GetLatestAsync()
        {
            lock (CacheLock)
            {
                if (DateTime.UtcNow - _cachedAt < CacheTtl) return _cachedLatest;
            }

            string latest = null;
            try
            {
                var url = "https://registry.npmjs.org/" + Package + "/latest";
                using (var response = await Client.Value.GetAsync(url).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using (var document = JsonDocument.Parse(body))
                        {
                            if (document.RootElement.TryGetProperty("version", out var version) &&
                                version.ValueKind == JsonValueKind.String)
                            {
                                latest = version.GetString();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Util.Log.Debug("latest version lookup failed: " + ex.Message);
            }

            lock (CacheLock)
            {
                // The timestamp is recorded even on failure, so a machine that is offline
                // does not retry on every single status refresh.
                _cachedLatest = latest;
                _cachedAt = DateTime.UtcNow;
            }

            return latest;
        }

        /// <summary>Extracts "x.y.z" from a string such as "2.1.226 (Claude Code)".</summary>
        public static string ParseSemver(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var match = SemverPattern.Match(text);
            return match.Success ? match.Value : null;
        }

        /// <summary>
        /// True when <paramref name="installed"/> is older than <paramref name="latest"/>.
        /// Missing or unparseable data yields false: showing a spurious "update available"
        /// is worse than showing nothing.
        /// </summary>
        public static bool IsOutdated(string installed, string latest)
        {
            var a = ParseSemver(installed);
            var b = ParseSemver(latest);
            if (a == null || b == null) return false;

            var left = a.Split('.');
            var right = b.Split('.');
            for (var i = 0; i < 3; i++)
            {
                var l = int.Parse(left[i]);
                var r = int.Parse(right[i]);
                if (l < r) return true;
                if (l > r) return false;
            }
            return false;
        }

        /// <summary>Test seam: drops the cached lookup.</summary>
        internal static void ResetCache()
        {
            lock (CacheLock)
            {
                _cachedLatest = null;
                _cachedAt = DateTime.MinValue;
            }
        }
    }
}
