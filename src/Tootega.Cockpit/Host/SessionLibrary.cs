using System;
using System.Collections.Generic;
using System.Linq;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Session;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Host
{
    /// <summary>
    /// The saved conversations: listing them, replaying one into a tab, and the destructive
    /// operations on them.
    ///
    /// The transcripts belong to the CLI. This class only reads them, and cuts them when the
    /// user asks — which is why renames are stored on our side instead of being written into
    /// someone else's file.
    /// </summary>
    internal sealed class SessionLibrary
    {
        private const string TitlesKey = "sessionTitles";

        private readonly SessionStore _store;
        private readonly StateStore _state;
        private readonly Func<string> _cwd;

        public SessionLibrary(SessionStore store, StateStore state, Func<string> cwd)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _cwd = cwd ?? throw new ArgumentNullException(nameof(cwd));
        }

        /// <summary>The folder's conversations, with any user rename applied.</summary>
        public IReadOnlyList<SessionInfo> List()
        {
            var sessions = _store.ListSessions(_cwd());
            var titles = Titles();
            if (titles.Count == 0) return sessions;

            foreach (var session in sessions)
            {
                if (titles.TryGetValue(session.Id, out var renamed)) session.Title = renamed;
            }

            return sessions;
        }

        public string LatestSessionId() => _store.LatestSessionId(_cwd());

        public IReadOnlyList<HistoryItem> Transcript(string sessionId)
        {
            return _store.LoadTranscript(_cwd(), sessionId);
        }

        public string TitleOf(string sessionId)
        {
            if (Titles().TryGetValue(sessionId ?? string.Empty, out var renamed)) return renamed;
            return List().FirstOrDefault(s => s.Id == sessionId)?.Title;
        }

        /// <summary>
        /// Renames a conversation.
        ///
        /// Stored beside our own state rather than in the transcript: the file and its
        /// generated title belong to the CLI, and rewriting them would put us in the business
        /// of maintaining someone else's format.
        /// </summary>
        public void Rename(string sessionId, string name)
        {
            if (string.IsNullOrEmpty(sessionId)) return;

            var titles = Titles();
            // An empty name is a reset, restoring whatever the CLI generated.
            if (string.IsNullOrWhiteSpace(name)) titles.Remove(sessionId);
            else titles[sessionId] = name.Trim();

            _state.Set(TitlesKey, titles);
        }

        public bool Delete(string sessionId)
        {
            var deleted = _store.DeleteSession(_cwd(), sessionId);
            if (!deleted) return false;

            var titles = Titles();
            if (titles.Remove(sessionId)) _state.Set(TitlesKey, titles);
            return true;
        }

        public int DeleteAll()
        {
            var removed = _store.DeleteAllSessions(_cwd());
            _state.Set(TitlesKey, new Dictionary<string, string>(StringComparer.Ordinal));
            return removed;
        }

        /// <summary>
        /// Rewinds a conversation to a prompt, dropping it and everything after.
        ///
        /// Irreversible, and it cuts the CLI's file — so it refuses rather than guessing when
        /// the target cannot be identified.
        /// </summary>
        public bool Rewind(string sessionId, string uuid)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(uuid)) return false;
            return _store.TruncateTranscriptAt(_cwd(), sessionId, uuid);
        }

        private Dictionary<string, string> Titles()
        {
            return _state.Get<Dictionary<string, string>>(TitlesKey)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
