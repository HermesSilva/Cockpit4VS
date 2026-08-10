using System;
using System.Collections.Generic;
using Tootega.Cockpit.Settings;

namespace Tootega.Cockpit.Cli
{
    /// <summary>
    /// Which engine backs a session: the official Claude Code CLI, or the Tootega agent
    /// (a local model, no account, no cost). Port of src/cli/Engine.ts.
    ///
    /// Both speak the SAME process contract — stream-json over stdin/stdout, the shapes in
    /// Protocol/Events.cs. That is the point: the UI does not reimplement orchestration for
    /// either one, it just spawns a different binary.
    ///
    /// What differs is only what exists *around* the conversation. Claude brings an account,
    /// subscription limits, plugins, skills and MCP; Tootega runs locally, so those panels
    /// have nothing to show. <see cref="EngineCaps"/> is the single place that says so,
    /// instead of scattering engine checks across the UI.
    /// </summary>
    internal static class EngineIds
    {
        public const string Claude = "claude";
        public const string Tootega = "tootega";
    }

    internal sealed class EngineCaps
    {
        /// <summary>Anthropic account, /usage, statusline, rate limits.</summary>
        public bool Account { get; private set; }
        /// <summary>Plugins, skills, MCP servers, subagents.</summary>
        public bool Extensions { get; private set; }
        /// <summary>Whether a dollar cost is meaningful. Tootega always reports zero.</summary>
        public bool Cost { get; private set; }

        public static readonly EngineCaps Claude = new EngineCaps { Account = true, Extensions = true, Cost = true };
        public static readonly EngineCaps Tootega = new EngineCaps { Account = false, Extensions = false, Cost = false };
    }

    internal sealed class Engines
    {
        private readonly ICockpitSettings _settings;

        public Engines(ICockpitSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Master switch for the local engine, off by default. Off means Tootega does not
        /// exist for this installation: nothing spawns agent.exe, the picker stays hidden
        /// and the Engine setting is ignored. Single place to answer "is the local engine in
        /// play?", so nothing else has to second-guess a stale setting.
        /// </summary>
        public bool TootegaEnabled => _settings.TootegaEnabled;

        /// <summary>Engines this installation offers. One entry while the switch is off —
        /// and a one-option picker is noise, so the UI hides it.</summary>
        public IReadOnlyList<string> Available =>
            TootegaEnabled
                ? new[] { EngineIds.Claude, EngineIds.Tootega }
                : new[] { EngineIds.Claude };

        public string Current =>
            TootegaEnabled && string.Equals(_settings.Engine, EngineIds.Tootega, StringComparison.OrdinalIgnoreCase)
                ? EngineIds.Tootega
                : EngineIds.Claude;

        public EngineCaps Caps() => CapsFor(Current);

        public static EngineCaps CapsFor(string engine) =>
            engine == EngineIds.Tootega ? EngineCaps.Tootega : EngineCaps.Claude;

        public static string LabelFor(string engine) =>
            engine == EngineIds.Tootega ? "Tootega Code CLI" : "Claude Code CLI";

        public string Label() => LabelFor(Current);

        /// <summary>Configured executable path for the given engine.</summary>
        public string PathFor(string engine)
        {
            var configured = engine == EngineIds.Tootega ? _settings.TootegaPath : _settings.ClaudePath;
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            return engine == EngineIds.Tootega ? "agent.exe" : "claude";
        }

        public string Path() => PathFor(Current);

        /// <summary>host:port of the Tootega engine server. Empty means the agent's default.</summary>
        public string TootegaServer => (_settings.TootegaServer ?? string.Empty).Trim();
    }
}
