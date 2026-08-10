using System;
using System.Threading.Tasks;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Host
{
    /// <summary>
    /// Reports whether the engine is present, which version it is, and whether the account is
    /// signed in.
    ///
    /// All of it is best-effort and none of it blocks a conversation. The published version is
    /// looked up once and cached for hours: an update badge is a convenience, and it must never
    /// be the reason the status takes a second to appear.
    /// </summary>
    internal sealed class CliStatusReporter
    {
        private readonly Engines _engines;
        private string _latestVersion;

        public CliStatusReporter(Engines engines)
        {
            _engines = engines ?? throw new ArgumentNullException(nameof(engines));
        }

        /// <summary>The last resolved binary, so callers spawn what was actually found.</summary>
        public CliDetection Detection { get; private set; }

        public async Task<HostMessage> BuildStatusAsync()
        {
            var engine = _engines.Current;
            var detection = await CliDetector.ResolveAsync(_engines.PathFor(engine), engine).ConfigureAwait(false);
            Detection = detection;

            if (detection.Ok && _latestVersion == null)
                _latestVersion = await CliVersion.GetLatestAsync().ConfigureAwait(false);

            if (!detection.Ok) Log.Info("cli: not usable (" + detection.Error + ")");

            return HostMessages.CliStatus(
                detection.Ok,
                detection.Version,
                // The error is only meaningful when it failed; sending it otherwise would
                // make a healthy status carry a stale message.
                detection.Ok ? null : detection.Error,
                _latestVersion,
                ExtensionVersion());
        }

        public async Task<HostMessage> BuildAuthAsync()
        {
            var account = await AuthStatus.FetchAsync(_engines.PathFor(EngineIds.Claude)).ConfigureAwait(false);
            return HostMessages.Auth(account?.LoggedIn ?? false);
        }

        private static string ExtensionVersion()
        {
            try
            {
                return typeof(CliStatusReporter).Assembly.GetName().Version?.ToString(3);
            }
            catch
            {
                return null;
            }
        }
    }
}
