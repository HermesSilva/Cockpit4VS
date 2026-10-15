using System;
using System.IO;
using Tootega.Cockpit.Cli;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// The credential precedence that makes every CLI login type work in the Cockpit — not just
    /// a subscription /login. The bug this guards against: reading only the on-disk credentials
    /// file, which a long-lived `claude setup-token` login never writes (its token is an env
    /// var). Env vars and the config dir are process-wide, so each test saves and restores them.
    /// </summary>
    public class ClaudeHomeTests : IDisposable
    {
        private readonly string _tmp;
        private readonly string _savedConfigDir;
        private readonly string _savedAuthToken;
        private readonly string _savedOauthToken;

        public ClaudeHomeTests()
        {
            _savedConfigDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            _savedAuthToken = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN");
            _savedOauthToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");

            _tmp = Path.Combine(Path.GetTempPath(), "cockpit-creds-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmp);

            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _tmp);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", null);
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _savedConfigDir);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", _savedAuthToken);
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", _savedOauthToken);
            try { Directory.Delete(_tmp, true); } catch { /* ignora */ }
        }

        private void WriteCreds(string json) =>
            File.WriteAllText(Path.Combine(_tmp, ".credentials.json"), json);

        [Fact]
        public void AuthTokenWinsOverEverything()
        {
            WriteCreds("{\"claudeAiOauth\":{\"accessToken\":\"file-tok\"}}");
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "setup-tok");
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "bearer-tok");
            Assert.Equal("bearer-tok", ClaudeHome.ReadOauthToken());
        }

        [Fact]
        public void LongLivedTokenWinsOverFileLogin()
        {
            WriteCreds("{\"claudeAiOauth\":{\"accessToken\":\"file-tok\"}}");
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "setup-tok");
            Assert.Equal("setup-tok", ClaudeHome.ReadOauthToken());
        }

        [Fact]
        public void FallsBackToFileWhenNoEnvToken()
        {
            WriteCreds("{\"claudeAiOauth\":{\"accessToken\":\"file-tok\"}}");
            Assert.Equal("file-tok", ClaudeHome.ReadOauthToken());
        }

        [Fact]
        public void CredentialsFileHonoursConfigDir()
        {
            Assert.Equal(Path.Combine(_tmp, ".credentials.json"), ClaudeHome.CredentialsFile);
        }

        [Fact]
        public void NullWhenNothingResolvable()
        {
            // empty config dir, no file, no env token
            Assert.Null(ClaudeHome.ReadOauthToken());
        }

        [Fact]
        public void BlankEnvTokenIsIgnored()
        {
            WriteCreds("{\"claudeAiOauth\":{\"accessToken\":\"file-tok\"}}");
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "   ");
            Assert.Equal("file-tok", ClaudeHome.ReadOauthToken());
        }

        [Fact]
        public void LoginExpiryReadsRefreshField()
        {
            WriteCreds("{\"claudeAiOauth\":{\"accessToken\":\"t\",\"refreshTokenExpiresAt\":123,\"expiresAt\":99}}");
            Assert.Equal(123L, ClaudeHome.ReadLoginExpiry());
        }

        [Fact]
        public void LoginExpiryFallsBackToExpiresAt()
        {
            WriteCreds("{\"claudeAiOauth\":{\"accessToken\":\"t\",\"expiresAt\":99}}");
            Assert.Equal(99L, ClaudeHome.ReadLoginExpiry());
        }

        [Fact]
        public void LoginExpiryNullWhenEnvTokenShadowsLogin()
        {
            WriteCreds("{\"claudeAiOauth\":{\"accessToken\":\"t\",\"refreshTokenExpiresAt\":123}}");
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "setup-tok");
            Assert.Null(ClaudeHome.ReadLoginExpiry());
        }
    }
}
