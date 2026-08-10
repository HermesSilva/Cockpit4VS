using System.Collections.Generic;

namespace Tootega.Cockpit.Protocol
{
    /// <summary>
    /// One host -&gt; webview message.
    ///
    /// The TypeScript side is a discriminated union of ~60 shapes keyed by `kind`.
    /// Modelling that as 60 C# classes would be ceremony without benefit: the messages are
    /// write-only here and read once on the other side. Instead each message is a small
    /// field bag built by a named factory in <see cref="HostMessages"/>, which keeps the
    /// call sites typed and the wire format exact.
    ///
    /// Keys are written literally rather than through a naming policy, because they are a
    /// contract with the webview, not a C# naming preference.
    /// </summary>
    internal sealed class HostMessage
    {
        private readonly Dictionary<string, object> _fields;

        internal HostMessage(string kind, params (string Key, object Value)[] fields)
        {
            _fields = new Dictionary<string, object> { ["kind"] = kind };
            foreach (var field in fields)
            {
                // Null means "absent" on this wire; omitting keeps the per-token
                // streaming messages small.
                if (field.Value != null) _fields[field.Key] = field.Value;
            }
        }

        public string Kind => (string)_fields["kind"];

        /// <summary>
        /// Routes the message to a tab's state. Conversation and stats messages carry it;
        /// global ones (ready/config/cliStatus/locale/sessions/tabs) do not.
        /// </summary>
        public HostMessage WithTab(string tabId)
        {
            if (!string.IsNullOrEmpty(tabId)) _fields["tab"] = tabId;
            return this;
        }

        public string ToJson()
        {
            return Json.Serialize(_fields);
        }

        public override string ToString() => ToJson();
    }
}
