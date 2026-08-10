using System;
using System.Collections.Generic;
using System.Linq;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.UI;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Host
{
    /// <summary>A message a surface sent, with the conversation it came from.</summary>
    internal sealed class SurfaceMessage
    {
        public SurfaceMessage(string tabId, string json)
        {
            TabId = tabId;
            Json = json;
        }

        /// <summary>The sending window's tab, or null when it was the hub.</summary>
        public string TabId { get; }

        public string Json { get; }
    }

    /// <summary>
    /// The set of live webviews, and the one way to reach them.
    ///
    /// There is one window per conversation, plus the hub. A message tagged with a tab goes only
    /// to that conversation's window — with several open, broadcasting would make every window
    /// repaint on every token of every other one. Untagged messages are global (config, CLI
    /// status, the tab list) and go everywhere; the hub takes everything, since it renders
    /// whichever conversation is active.
    /// </summary>
    internal sealed class SurfaceBroadcaster
    {
        private readonly List<CockpitWebView> _surfaces = new List<CockpitWebView>();
        private readonly object _gate = new object();

        /// <summary>Raised for every message a surface sends, with its origin.</summary>
        public event EventHandler<SurfaceMessage> MessageReceived;

        public void Register(CockpitWebView view)
        {
            if (view == null) return;

            lock (_gate)
            {
                if (_surfaces.Contains(view)) return;
                _surfaces.Add(view);
            }

            view.MessageReceived += OnSurfaceMessage;
        }

        public void Unregister(CockpitWebView view)
        {
            if (view == null) return;

            view.MessageReceived -= OnSurfaceMessage;
            lock (_gate) _surfaces.Remove(view);
        }

        private void OnSurfaceMessage(object sender, string json)
        {
            var view = sender as CockpitWebView;
            MessageReceived?.Invoke(this, new SurfaceMessage(view?.TabId, json));
        }

        /// <summary>
        /// Sends a message to the surfaces it concerns. A null tab means it is global.
        /// </summary>
        public void Post(HostMessage message, string tabId = null)
        {
            if (message == null) return;

            string json;
            try
            {
                json = message.WithTab(tabId).ToJson();
            }
            catch (Exception ex)
            {
                // A message that cannot be serialized is a bug, but it must not take the
                // conversation with it.
                Log.Error("host: could not serialize a '" + message.Kind + "' message", ex);
                return;
            }

            List<CockpitWebView> surfaces;
            lock (_gate) surfaces = _surfaces.Where(s => s.Accepts(tabId)).ToList();

            foreach (var surface in surfaces) surface.PostMessage(json);
        }

        public bool HasSurfaces
        {
            get { lock (_gate) return _surfaces.Count > 0; }
        }

        /// <summary>Whether a conversation has a window of its own open.</summary>
        public bool HasWindowFor(string tabId)
        {
            if (tabId == null) return false;

            lock (_gate)
            {
                return _surfaces.Any(s => string.Equals(s.TabId, tabId, StringComparison.Ordinal));
            }
        }
    }
}
