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
    ///
    /// Every operation takes the folder explicitly. Conversations are stored per folder by the
    /// CLI, and each tab has its own folder, so there is no single "current" one to fall back
    /// on — an implicit default would sooner or later delete from the wrong folder.
    /// </summary>
    internal sealed class SessionLibrary
    {
        private const string TitlesKey = "sessionTitles";

        private readonly SessionStore _store;
        private readonly StateStore _state;

        public SessionLibrary(SessionStore store, StateStore state)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        /// <summary>The folder's conversations, with any user rename applied.</summary>
        public IReadOnlyList<SessionInfo> List(string cwd)
        {
            var sessions = _store.ListSessions(cwd);
            var titles = Titles();
            if (titles.Count == 0) return sessions;

            foreach (var session in sessions)
            {
                if (titles.TryGetValue(session.Id, out var renamed)) session.Title = renamed;
            }

            return sessions;
        }

        public string LatestSessionId(string cwd) => _store.LatestSessionId(cwd);

        public IReadOnlyList<HistoryItem> Transcript(string cwd, string sessionId)
        {
            return _store.LoadTranscript(cwd, sessionId);
        }

        public string TitleOf(string cwd, string sessionId)
        {
            if (Titles().TryGetValue(sessionId ?? string.Empty, out var renamed)) return renamed;
            return List(cwd).FirstOrDefault(s => s.Id == sessionId)?.Title;
        }

        /// <summary>
        /// Renames a conversation.
        ///
        /// Stored beside our own state rather than in the transcript: the file and its
        /// generated title belong to the CLI, and rewriting them would put us in the business
        /// of maintaining someone else's format.
        ///
        /// Keyed by session id alone, without the folder: the id is already unique across
        /// folders, and a rename should survive a conversation being resumed from elsewhere.
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

        public bool Delete(string cwd, string sessionId)
        {
            var deleted = _store.DeleteSession(cwd, sessionId);
            if (!deleted) return false;

            var titles = Titles();
            if (titles.Remove(sessionId)) _state.Set(TitlesKey, titles);
            return true;
        }

        /// <summary>
        /// Deletes every conversation of one folder.
        ///
        /// Only that folder's renames are forgotten with it; the other folders' conversations
        /// are still there and must keep their names.
        /// </summary>
        public int DeleteAll(string cwd)
        {
            var ids = List(cwd).Select(s => s.Id).ToList();

            var removed = _store.DeleteAllSessions(cwd);

            var titles = Titles();
            var changed = false;
            foreach (var id in ids) changed |= titles.Remove(id);
            if (changed) _state.Set(TitlesKey, titles);

            return removed;
        }

        /// <summary>
        /// Rewinds a conversation to a prompt, dropping it and everything after.
        ///
        /// Irreversible, and it cuts the CLI's file — so it refuses rather than guessing when
        /// the target cannot be identified.
        /// </summary>
        public bool Rewind(string cwd, string sessionId, string uuid)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(uuid)) return false;
            return _store.TruncateTranscriptAt(cwd, sessionId, uuid);
        }

        private Dictionary<string, string> Titles()
        {
            return _state.Get<Dictionary<string, string>>(TitlesKey)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
