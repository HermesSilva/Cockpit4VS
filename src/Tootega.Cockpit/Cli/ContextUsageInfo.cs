using System.Collections.Generic;

namespace Tootega.Cockpit.Cli
{
    /// <summary>One skill as the engine reports it in get_context_usage.</summary>
    internal sealed class ContextUsageSkill
    {
        public string Name { get; set; }
        /// <summary>projectSettings | userSettings | built-in | plugin…</summary>
        public string Source { get; set; }
        /// <summary>Listing cost of this skill, MEASURED by the engine rather than estimated.</summary>
        public long? Tokens { get; set; }
    }

    /// <summary>
    /// The answer to the get_context_usage control request.
    ///
    /// It is a LOCAL computation inside the engine: it creates no turn, spends no tokens and
    /// adds no transcript line, and it answers even before the first message. That is what
    /// makes per-skill cost showable at all — the alternative would be running /context, which
    /// costs a turn and pollutes the conversation.
    ///
    /// The type lives here (rather than with its parser) because StatsAggregator consumes it
    /// and does no I/O of its own.
    /// </summary>
    internal sealed class ContextUsageInfo
    {
        public List<ContextUsageSkill> Skills { get; set; } = new List<ContextUsageSkill>();

        /// <summary>The "Skills" category total — metadata only, not loaded bodies.</summary>
        public long? ListingTokens { get; set; }

        /// <summary>Skills the engine knows, before overrides.</summary>
        public int? TotalSkills { get; set; }

        /// <summary>Skills that actually entered the listing, after overrides.</summary>
        public int? IncludedSkills { get; set; }
    }
}
