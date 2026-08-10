namespace Tootega.Cockpit
{
    /// <summary>
    /// The orchestration surface the menu commands drive. It is the C# counterpart of
    /// ChatViewProvider in the VS Code extension: the commands know what they want done,
    /// not how the CLI session is managed.
    ///
    /// The implementation is registered by the orchestration layer once a session exists;
    /// while it is absent, the commands that need it report themselves as unavailable
    /// instead of failing silently when clicked.
    /// </summary>
    internal interface ICockpitHost
    {
        void NewSession();

        /// <summary>
        /// Brings the current conversation forward, starting one when there is none. What "Open
        /// Cockpit" means, and deliberately not "open another empty conversation".
        /// </summary>
        void OpenOrFocusConversation();

        /// <summary>Reloads the active conversation's renderer — the recovery for a dead webview.</summary>
        void ReloadActiveView();

        /// <summary>
        /// Asks for a folder and moves the active conversation to it — the same operation the
        /// webview's folder chip performs, reachable from the tool window toolbar.
        /// </summary>
        void ChangeFolder();

        /// <summary>
        /// The active conversation's folder, or null when there is no conversation yet. Read
        /// by the toolbar to show where the window is pointed.
        /// </summary>
        string CurrentFolder { get; }

        void Interrupt();
        void OpenSessions();
        void ReopenClosed();
        void LoginCli();
        void LogoutCli();
        void SetApiKeyInteractive();
        void ClearApiKey();
        void EnableUsageTracking();
        void DisableUsageTracking();
        void EnableUtf8Fix();
        void DisableUtf8Fix();
    }

    internal static class CockpitHost
    {
        /// <summary>Null until the orchestration layer comes up.</summary>
        public static ICockpitHost Instance { get; set; }
    }
}
