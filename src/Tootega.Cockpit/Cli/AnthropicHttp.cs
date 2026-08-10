using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    /// <summary>
    /// The credential a clean utility call authenticates with.
    ///
    /// Either an API key or a bearer token, never both. The distinction matters because the
    /// OAuth path also needs the beta header, and sending an API key with it would be
    /// rejected.
    /// </summary>
    internal sealed class ApiCredentials
    {
        public string ApiKey { get; set; }
        public string AuthToken { get; set; }

        public bool IsEmpty => string.IsNullOrEmpty(ApiKey) && string.IsNullOrEmpty(AuthToken);
    }

    /// <summary>
    /// HTTP for the Cockpit's own utility calls.
    ///
    /// These are the narrow exception to the "everything goes through the CLI" rule: reads of
    /// public docs, and clean isolated calls carrying only what the task needs — no agent
    /// system prompt, no tools, no MCP, no project context. They are never part of the agent
    /// loop, and the credential is read-only and never logged.
    ///
    /// Every method is best-effort: a failure returns null or empty rather than throwing. None
    /// of this is required for a conversation to work, so none of it may be able to stop one.
    /// </summary>
    internal static class AnthropicHttp
    {
        public const string Host = "https://api.anthropic.com";
        public const string ApiVersion = "2023-06-01";
        /// <summary>Required when authenticating with the subscription's OAuth token.</summary>
        public const string OauthBeta = "oauth-2025-04-20";

        private static readonly Lazy<HttpClient> Shared = new Lazy<HttpClient>(CreateClient);

        private static HttpClient CreateClient()
        {
            try
            {
                // .NET Framework defaults to a protocol set these endpoints no longer accept;
                // without this the request fails as a connection reset rather than a 4xx.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch
            {
                // An older framework may not know Tls12; the request will simply fail.
            }

            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Add("User-Agent", "Tootega-Cockpit");
            return client;
        }

        /// <summary>
        /// Resolves the credential for a clean call, in the order that respects the user's
        /// intent: an explicit key first, then the environment, and only then the CLI's own
        /// OAuth token — which is what lets a subscription account with no API key still use
        /// these endpoints.
        /// </summary>
        public static ApiCredentials ResolveCredentials(string configuredApiKey)
        {
            var apiKey = configuredApiKey?.Trim();
            if (string.IsNullOrEmpty(apiKey)) apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (!string.IsNullOrEmpty(apiKey)) return new ApiCredentials { ApiKey = apiKey };

            var authToken = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN");
            if (!string.IsNullOrEmpty(authToken)) return new ApiCredentials { AuthToken = authToken };

            var oauth = ClaudeHome.ReadOauthToken();
            return string.IsNullOrEmpty(oauth) ? null : new ApiCredentials { AuthToken = oauth };
        }

        /// <summary>
        /// GET returning the body, or null on any failure. <paramref name="credentials"/> may be
        /// null for unauthenticated reads such as the public pricing docs.
        /// </summary>
        public static async Task<string> GetAsync(string url, ApiCredentials credentials = null,
                                                  int timeoutMs = 8000, string accept = null)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                Apply(request, credentials, accept);
                return await SendAsync(request, timeoutMs).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// GET returning the status alongside the body.
        ///
        /// Callers that need to distinguish a transient failure from a permanent one use this:
        /// a 429 or 5xx is worth retrying, a 401 is not, and "null body" cannot tell them
        /// apart. Status 0 means the request never got an answer.
        /// </summary>
        public static async Task<(int Status, string Body, string Error)> GetWithStatusAsync(
            string url, ApiCredentials credentials, int timeoutMs = 8000)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                Apply(request, credentials, null);

                try
                {
                    using (var cancellation = new System.Threading.CancellationTokenSource(timeoutMs))
                    using (var response = await Shared.Value
                               .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellation.Token)
                               .ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return ((int)response.StatusCode, body, null);
                    }
                }
                catch (OperationCanceledException)
                {
                    return (0, null, "timeout (" + timeoutMs + " ms)");
                }
                catch (Exception ex)
                {
                    return (0, null, "network: " + ex.Message);
                }
            }
        }

        /// <summary>POST of a JSON body, returning the response body or null on failure.</summary>
        public static async Task<string> PostJsonAsync(string url, string json, ApiCredentials credentials,
                                                       int timeoutMs = 20000)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(json ?? string.Empty, new UTF8Encoding(false), "application/json");
                Apply(request, credentials, null);
                return await SendAsync(request, timeoutMs).ConfigureAwait(false);
            }
        }

        private static void Apply(HttpRequestMessage request, ApiCredentials credentials, string accept)
        {
            if (!string.IsNullOrEmpty(accept)) request.Headers.TryAddWithoutValidation("Accept", accept);
            if (credentials == null || credentials.IsEmpty) return;

            request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);

            if (!string.IsNullOrEmpty(credentials.ApiKey))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", credentials.ApiKey);
            }
            else
            {
                request.Headers.TryAddWithoutValidation("authorization", "Bearer " + credentials.AuthToken);
                request.Headers.TryAddWithoutValidation("anthropic-beta", OauthBeta);
            }
        }

        private static async Task<string> SendAsync(HttpRequestMessage request, int timeoutMs)
        {
            try
            {
                using (var cancellation = new System.Threading.CancellationTokenSource(timeoutMs))
                using (var response = await Shared.Value
                           .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellation.Token)
                           .ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.IsSuccessStatusCode) return body;

                    // The status is worth logging; the body is not, since an auth error can
                    // echo parts of the credential back.
                    Log.Debug("GET/POST " + request.RequestUri.AbsolutePath + " -> HTTP " + (int)response.StatusCode);
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("request to " + request.RequestUri.AbsolutePath + " failed: " + ex.Message);
                return null;
            }
        }
    }
}
