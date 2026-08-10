using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Tootega.Cockpit.Cli;

namespace Tootega.Cockpit.Util
{
    /// <summary>
    /// Small persistent key/value store — the stand-in for VS Code's globalState.
    ///
    /// It holds the handful of facts that must survive a restart but are not user settings:
    /// whether a one-off offer has already been made, the last successful model list, the
    /// statusline command we replaced. VS has a settings store of its own, but it lives behind
    /// the shell, and using it here would make all of this untestable outside the IDE for no
    /// gain.
    ///
    /// Written atomically. Every read is best-effort: a corrupt file behaves as an empty store,
    /// which at worst re-offers something once.
    /// </summary>
    internal sealed class StateStore
    {
        private readonly string _file;
        private readonly object _gate = new object();
        private Dictionary<string, JsonElement> _values;

        public StateStore(string file = null)
        {
            _file = file ?? Path.Combine(ClaudeHome.CockpitDir, "vs-state.json");
        }

        private Dictionary<string, JsonElement> Values
        {
            get
            {
                if (_values != null) return _values;

                var raw = FileStore.ReadAllTextOrNull(_file);
                if (raw != null)
                {
                    var parsed = Protocol.Json.TryDeserialize<Dictionary<string, JsonElement>>(raw);
                    if (parsed != null)
                    {
                        _values = parsed;
                        return _values;
                    }
                }

                _values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                return _values;
            }
        }

        public string GetString(string key, string fallback = null)
        {
            lock (_gate)
            {
                return Values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : fallback;
            }
        }

        public bool GetBool(string key, bool fallback = false)
        {
            lock (_gate)
            {
                if (!Values.TryGetValue(key, out var value)) return fallback;
                if (value.ValueKind == JsonValueKind.True) return true;
                if (value.ValueKind == JsonValueKind.False) return false;
                return fallback;
            }
        }

        public T Get<T>(string key)
        {
            lock (_gate)
            {
                return Values.TryGetValue(key, out var value) ? Protocol.Json.TryDeserialize<T>(value) : default;
            }
        }

        public void Set<T>(string key, T value)
        {
            lock (_gate)
            {
                try
                {
                    using (var document = JsonDocument.Parse(Protocol.Json.Serialize(value)))
                    {
                        Values[key] = document.RootElement.Clone();
                    }
                }
                catch (JsonException ex)
                {
                    Log.Debug("could not store state key " + key + ": " + ex.Message);
                    return;
                }

                FileStore.WriteAtomic(_file, Protocol.Json.Serialize(Values));
            }
        }

        public void Remove(string key)
        {
            lock (_gate)
            {
                if (!Values.Remove(key)) return;
                FileStore.WriteAtomic(_file, Protocol.Json.Serialize(Values));
            }
        }
    }
}
