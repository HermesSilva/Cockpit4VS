using System;
using System.Collections.Generic;
using System.Linq;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;
using CockpitSession = Tootega.Cockpit.Session.Session;

namespace Tootega.Cockpit.Host
{
    /// <summary>
    /// The open tabs and the session behind each one.
    ///
    /// Tabs exist per conversation rather than per window because that is the intended use:
    /// several conversations side by side, each with its own process, model and statistics.
    /// This class owns their identity and ordering; it knows nothing about what a session does.
    /// </summary>
    internal sealed class TabRegistry : IDisposable
    {
        private sealed class TabMeta
        {
            public string Title;
            public string Status = "idle";
        }

        private readonly Func<string, CockpitSession> _createSession;
        private readonly Dictionary<string, CockpitSession> _sessions = new Dictionary<string, CockpitSession>(StringComparer.Ordinal);
        private readonly Dictionary<string, TabMeta> _meta = new Dictionary<string, TabMeta>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();

        /// <summary>The composer draft, mirrored so a dead renderer does not lose what was typed.</summary>
        private readonly Dictionary<string, string> _drafts = new Dictionary<string, string>(StringComparer.Ordinal);

        private int _sequence;

        /// <param name="createSession">Builds a session bound to the given tab id.</param>
        public TabRegistry(Func<string, CockpitSession> createSession)
        {
            _createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
        }

        public string ActiveTab { get; private set; }

        public int Count => _order.Count;

        public IReadOnlyList<string> Order => _order;

        public IEnumerable<CockpitSession> Sessions => _sessions.Values;

        public IEnumerable<KeyValuePair<string, CockpitSession>> Entries => _sessions;

        /// <summary>Raised when the tab list or any tab's title/status changed.</summary>
        public event EventHandler Changed;

        public string CreateTab()
        {
            var tabId = "tab-" + (++_sequence);

            _sessions[tabId] = _createSession(tabId);
            _meta[tabId] = new TabMeta();
            _order.Add(tabId);
            ActiveTab = tabId;

            Changed?.Invoke(this, EventArgs.Empty);
            return tabId;
        }

        /// <summary>The active tab, creating one when there is none.</summary>
        public string EnsureActiveTab()
        {
            if (ActiveTab != null && _sessions.ContainsKey(ActiveTab)) return ActiveTab;
            return CreateTab();
        }

        public CockpitSession Active() => _sessions[EnsureActiveTab()];

        public CockpitSession SessionFor(string tabId)
        {
            return tabId != null && _sessions.TryGetValue(tabId, out var session) ? session : Active();
        }

        public bool Has(string tabId) => tabId != null && _sessions.ContainsKey(tabId);

        public bool SetActive(string tabId)
        {
            if (!Has(tabId)) return false;
            ActiveTab = tabId;
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>
        /// Closes a tab, or empties it when it is the last one.
        ///
        /// A Cockpit with no conversation has nothing to show and no way back except a
        /// command, so the last tab is cleared rather than removed.
        /// </summary>
        public bool Close(string tabId, out bool emptiedInstead)
        {
            emptiedInstead = false;
            if (!_sessions.TryGetValue(tabId, out var session)) return false;

            if (_order.Count <= 1)
            {
                session.ClearConversation();
                SetTitle(tabId, null);
                emptiedInstead = true;
                return true;
            }

            try
            {
                session.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug("tabs: disposing a session failed: " + ex.Message);
            }

            _sessions.Remove(tabId);
            _meta.Remove(tabId);
            _order.Remove(tabId);
            _drafts.Remove(tabId);

            if (ActiveTab == tabId) ActiveTab = _order.LastOrDefault();

            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public void SetStatus(string tabId, string status)
        {
            if (!_meta.TryGetValue(tabId, out var meta) || meta.Status == status) return;
            meta.Status = status;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public string Title(string tabId)
        {
            return _meta.TryGetValue(tabId, out var meta) ? meta.Title : null;
        }

        public void SetTitle(string tabId, string title)
        {
            if (!_meta.TryGetValue(tabId, out var meta)) return;
            meta.Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public bool AnyBusy() => _sessions.Values.Any(s => s.Busy);

        // --- Drafts ---

        public string Draft(string tabId)
        {
            return _drafts.TryGetValue(tabId, out var draft) ? draft : null;
        }

        public void SetDraft(string tabId, string text)
        {
            if (string.IsNullOrEmpty(text)) _drafts.Remove(tabId);
            else _drafts[tabId] = text;
        }

        public void ClearDraft(string tabId) => _drafts.Remove(tabId);

        /// <summary>The tab list as the webview renders it.</summary>
        public List<TabInfo> Snapshot()
        {
            return _order
                .Where(id => _sessions.ContainsKey(id))
                .Select(id => new TabInfo
                {
                    Id = id,
                    Title = _meta.TryGetValue(id, out var meta) ? meta.Title : null,
                    Status = _meta.TryGetValue(id, out var m) ? m.Status : "idle",
                    SessionId = _sessions[id].SessionId ?? _sessions[id].ResumeId,
                })
                .ToList();
        }

        public void Dispose()
        {
            foreach (var session in _sessions.Values)
            {
                try
                {
                    session.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Debug("tabs: disposing a session failed: " + ex.Message);
                }
            }

            _sessions.Clear();
            _meta.Clear();
            _order.Clear();
            _drafts.Clear();
        }
    }
}
