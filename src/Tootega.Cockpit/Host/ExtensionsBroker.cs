using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;
using CockpitSession = Tootega.Cockpit.Session.Session;

namespace Tootega.Cockpit.Host
{
    /// <summary>
    /// The three extensibility panels: plugins, MCP servers and skills.
    ///
    /// They are grouped because they share one shape — a modal asks, the CLI is consulted, the
    /// answer is posted — and one rule: the busy flag is always cleared, including on failure.
    /// A modal left spinning forever is the worst outcome here, since the user cannot tell it
    /// from a slow CLI.
    /// </summary>
    internal sealed class ExtensionsBroker
    {
        /// <summary>Skill listing overrides are per folder, keyed inside this state entry.</summary>
        private const string OverridesKey = "skillOverrides";

        private readonly PluginManager _plugins;
        private readonly StateStore _state;
        private readonly Func<string> _claudePath;
        private readonly Action<HostMessage, string> _post;

        public ExtensionsBroker(PluginManager plugins, StateStore state, Func<string> claudePath,
                                Action<HostMessage, string> post)
        {
            _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _claudePath = claudePath ?? throw new ArgumentNullException(nameof(claudePath));
            _post = post ?? throw new ArgumentNullException(nameof(post));
        }

        // ---- Plugins ----

        /// <param name="force">Re-validates the marketplace URLs, which costs a model call.</param>
        public async Task SendPluginsAsync(string tabId, bool force)
        {
            _post(HostMessages.PluginsBusy(true), tabId);

            try
            {
                _post(HostMessages.PluginsData(await _plugins.ListAsync(_claudePath(), force)), tabId);
            }
            catch (Exception ex)
            {
                _post(HostMessages.PluginsError(ex.Message), tabId);
            }
            finally
            {
                _post(HostMessages.PluginsBusy(false), tabId);
            }
        }

        /// <summary>
        /// Runs a plugin action and reloads the list afterwards.
        ///
        /// The reload is unconditional, even when the action reported failure: a half-applied
        /// install leaves state on disk, and showing the previous list would hide it.
        /// </summary>
        public async Task RunPluginActionAsync(string tabId, string action, string argument, string scope)
        {
            _post(HostMessages.PluginsBusy(true, action + " " + argument), tabId);

            try
            {
                var result = await _plugins.ActionAsync(_claudePath(), action, argument, scope);
                if (!result.Ok) _post(HostMessages.PluginsError(result.Message ?? "failed"), tabId);

                _post(HostMessages.PluginsData(await _plugins.ListAsync(_claudePath())), tabId);
            }
            catch (Exception ex)
            {
                _post(HostMessages.PluginsError(ex.Message), tabId);
            }
            finally
            {
                _post(HostMessages.PluginsBusy(false), tabId);
            }
        }

        // ---- MCP ----

        /// <summary>
        /// The MCP panel: the session's own inventory merged with what `claude mcp list` says.
        ///
        /// Neither source is enough alone — the session knows which tools each server actually
        /// exposed, and only the list knows about servers waiting for approval, which never
        /// start and so never appear in a session.
        /// </summary>
        public async Task SendMcpAsync(string tabId, CockpitSession session)
        {
            _post(HostMessages.McpBusy(true), tabId);

            try
            {
                var list = await McpStatus.FetchListAsync(_claudePath());
                var servers = McpStatus.Merge(session.LastTools, session.LastMcpServers, list, session.LastMcpErrors);

                _post(HostMessages.McpData(new McpData
                {
                    Servers = new List<McpServerInfo>(servers),
                    GeneratedAt = DateTime.UtcNow.ToString("o"),
                }), tabId);
            }
            catch (Exception ex)
            {
                Log.Debug("mcp: " + ex.Message);

                // An empty panel that says "generated now" is honest; a spinner is not.
                _post(HostMessages.McpData(new McpData
                {
                    Servers = new List<McpServerInfo>(),
                    GeneratedAt = DateTime.UtcNow.ToString("o"),
                }), tabId);
            }
            finally
            {
                _post(HostMessages.McpBusy(false), tabId);
            }
        }

        // ---- Skills ----

        /// <summary>
        /// Re-reads the session's skill metadata.
        ///
        /// It goes through a control request, so it costs no turn and no tokens — which is why
        /// the panel can afford a refresh button at all.
        /// </summary>
        public async Task SendSkillsAsync(string tabId, CockpitSession session)
        {
            _post(HostMessages.SkillsBusy(true), tabId);

            try
            {
                await session.RefreshSkillsAsync();
            }
            catch (Exception ex)
            {
                Log.Debug("skills: " + ex.Message);
            }
            finally
            {
                _post(HostMessages.SkillsBusy(false), tabId);
            }
        }

        /// <summary>
        /// The skill listing overrides of one folder.
        ///
        /// Per folder because <c>.claude/skills/</c> belongs to the project: an override set
        /// here must not follow the user into an unrelated repository. They are applied through
        /// the CLI's <c>--settings</c> at spawn, so the user's own settings.json is never
        /// touched and the CLI outside the Cockpit keeps behaving as they configured it.
        /// </summary>
        public Dictionary<string, string> OverridesFor(string cwd)
        {
            var all = AllOverrides();
            return all.TryGetValue(cwd ?? string.Empty, out var map)
                ? new Dictionary<string, string>(map, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Changes one skill's override and tells every session on that folder.
        ///
        /// The sessions restart on the next send, because the listing is decided at start-up.
        /// </summary>
        public void SetOverride(string cwd, string name, string value, IEnumerable<CockpitSession> sessions)
        {
            if (string.IsNullOrEmpty(name)) return;

            var all = AllOverrides();
            var key = cwd ?? string.Empty;

            var map = all.TryGetValue(key, out var existing)
                ? new Dictionary<string, string>(existing, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            // 'on' is the default, so it is stored as the absence of an override rather than as
            // a value — otherwise the file grows a record for every skill the user ever looked at.
            if (string.Equals(value, "on", StringComparison.Ordinal)) map.Remove(name);
            else map[name] = value;

            all[key] = map;
            _state.Set(OverridesKey, all);

            if (sessions == null) return;
            foreach (var session in sessions) session.SetSkillOverride(name, value);
        }

        private Dictionary<string, Dictionary<string, string>> AllOverrides()
        {
            return _state.Get<Dictionary<string, Dictionary<string, string>>>(OverridesKey)
                   ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
