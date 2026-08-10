using System;
using System.Collections.Generic;
using System.Linq;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.UI;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Host
{
    /// <summary>
    /// The set of live webviews, and the one way to reach them.
    ///
    /// There can be two at once — the chat window and the hub — showing different views of the
    /// same state. Messages are broadcast to both rather than routed to one: the webview
    /// already filters by tab id, and getting that filtering right in two places is harder
    /// than sending one extra message.
    /// </summary>
    internal sealed class SurfaceBroadcaster
    {
        private readonly List<CockpitWebView> _surfaces = new List<CockpitWebView>();
        private readonly object _gate = new object();

        /// <summary>Raised with the raw JSON of every message a surface sends.</summary>
        public event EventHandler<string> MessageReceived;

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
            MessageReceived?.Invoke(this, json);
        }

        /// <summary>
        /// Sends a message to every surface, tagged with the tab it belongs to. A null tab
        /// means the message is global — config, sessions, CLI status.
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
            lock (_gate) surfaces = _surfaces.ToList();

            foreach (var surface in surfaces) surface.PostMessage(json);
        }

        public bool HasSurfaces
        {
            get { lock (_gate) return _surfaces.Count > 0; }
        }
    }
}
