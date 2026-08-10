using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Tootega.Cockpit.Protocol;

namespace Tootega.Cockpit.Stats
{
    /// <summary>Prices per 1M tokens, in USD. An estimate, and labelled as one in the UI.</summary>
    internal sealed class TokenPrice
    {
        public double Input { get; set; }
        public double Output { get; set; }
        /// <summary>Roughly 1.25x input.</summary>
        public double CacheWrite { get; set; }
        /// <summary>Roughly 0.1x input.</summary>
        public double CacheRead { get; set; }
    }

    /// <summary>
    /// Cost estimation and context-window derivation. Extracted from
    /// src/stats/StatsAggregator.ts, where the price table and the limit rules lived inline.
    ///
    /// Both are estimates built from what can be known locally, and both are deliberate about
    /// it: the cost is a table lookup shown as "estimated", never presented as an invoice, and
    /// the context limit prefers a real value discovered from /v1/models over any pattern
    /// match on the model id.
    /// </summary>
    internal static class CostModel
    {
        /// <summary>
        /// Prompt cache life — Claude Code's extended 1h TTL. After this much idle time the
        /// cached prefix expires and the next turn rewrites everything, a cold reset. Every
        /// request that hits the prefix RESTARTS this window, which is what makes a keep-alive
        /// possible.
        /// </summary>
        public const long CacheLifeMs = 60 * 60 * 1000L;

        private static readonly List<KeyValuePair<Regex, TokenPrice>> Prices =
            new List<KeyValuePair<Regex, TokenPrice>>
            {
                Price("opus", 5, 25, 6.25, 0.5),
                Price("sonnet", 3, 15, 3.75, 0.3),
                Price("haiku", 1, 5, 1.25, 0.1),
                Price("fable|mythos", 10, 50, 12.5, 1),
            };

        private static readonly TokenPrice DefaultPrice =
            new TokenPrice { Input = 5, Output = 25, CacheWrite = 6.25, CacheRead = 0.5 };

        private static KeyValuePair<Regex, TokenPrice> Price(string pattern, double input, double output,
                                                             double cacheWrite, double cacheRead)
        {
            return new KeyValuePair<Regex, TokenPrice>(
                new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled),
                new TokenPrice { Input = input, Output = output, CacheWrite = cacheWrite, CacheRead = cacheRead });
        }

        public static TokenPrice PriceFor(string model)
        {
            if (string.IsNullOrEmpty(model)) return DefaultPrice;
            foreach (var entry in Prices)
            {
                if (entry.Key.IsMatch(model)) return entry.Value;
            }
            // An unrecognised model is priced as the most expensive family rather than as
            // free: under-reporting cost is the worse error for a transparency panel.
            return DefaultPrice;
        }

        /// <summary>Estimated cost of a usage block, from the model's price table.</summary>
        public static double EstimateCost(Usage usage, string model)
        {
            if (usage == null) return 0;
            var price = PriceFor(model);
            return (Num(usage.InputTokens) * price.Input
                    + Num(usage.CacheCreationInputTokens) * price.CacheWrite
                    + Num(usage.CacheReadInputTokens) * price.CacheRead
                    + Num(usage.OutputTokens) * price.Output) / 1_000_000.0;
        }

        /// <summary>
        /// Defensive coercion for a token count off the stream: a finite value above zero, or
        /// zero. A malformed usage block must not make the panel show negative or NaN.
        /// </summary>
        public static long Num(long? value)
        {
            return value.HasValue && value.Value > 0 ? value.Value : 0;
        }

        // --- Context window ---

        private static readonly Regex OneMSuffix = new Regex(@"\[1m\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RepeatedOneM = new Regex(@"(\[1m\])(\[1m\])+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Claude5Family = new Regex(@"(?:fable|sonnet|opus|haiku|mythos|spark)-5\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MuseSpark = new Regex(@"muse-spark", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly object Gate = new object();

        /// <summary>
        /// Real context windows per model, populated by discovery from /v1/models. This is the
        /// source of truth for models that are natively 1M but carry no [1m] suffix, which no
        /// pattern match can reliably guess.
        /// </summary>
        private static readonly Dictionary<string, long> KnownContextLimits =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether the engine we spawn has the 1M window disabled
        /// (CLAUDE_CODE_DISABLE_1M_CONTEXT). Injected by the host, because this type does no
        /// I/O. Since CLI 2.1.223 it caps EVERY 1M model at 200K.
        /// </summary>
        private static bool _oneMDisabled;

        public static void SetOneMContextDisabled(bool disabled)
        {
            lock (Gate) _oneMDisabled = disabled;
        }

        /// <summary>Records a model's real context window. Invalid values are ignored.</summary>
        public static void RegisterModelContext(string model, long? tokens)
        {
            if (string.IsNullOrEmpty(model) || !tokens.HasValue || tokens.Value <= 0) return;
            lock (Gate) KnownContextLimits[ContextKey(model)] = tokens.Value;
        }

        /// <summary>Test seam: forgets everything discovery recorded.</summary>
        internal static void ResetDiscoveredContexts()
        {
            lock (Gate)
            {
                KnownContextLimits.Clear();
                _oneMDisabled = false;
            }
        }

        /// <summary>Lookup key: the id without its [1m] suffix.</summary>
        private static string ContextKey(string model) => OneMSuffix.Replace(model, string.Empty);

        /// <summary>
        /// Claude Code's effective limit:
        ///  - a [1m] suffix means 1M;
        ///  - a real context discovered from /v1/models wins over any guess;
        ///  - the Claude 5 family is natively 1M even without the suffix (the fallback used
        ///    before discovery has answered);
        ///  - otherwise 200K.
        ///
        /// With the 1M window disabled everything is capped at 200K, because that is where the
        /// CLI auto-compacts whatever the model's own window says.
        /// </summary>
        public static long DeriveContextLimit(string model)
        {
            if (string.IsNullOrEmpty(model)) return 200_000;

            bool disabled;
            long discovered;
            lock (Gate)
            {
                disabled = _oneMDisabled;
                KnownContextLimits.TryGetValue(ContextKey(model), out discovered);
            }

            long Cap(long value) => disabled ? Math.Min(value, 200_000) : value;

            if (OneMSuffix.IsMatch(model)) return Cap(1_000_000);
            if (discovered > 0) return Cap(discovered);
            if (Claude5Family.IsMatch(model)) return Cap(1_000_000);
            // Not capped: this one is 1M by construction, not by the [1m] opt-in.
            if (MuseSpark.IsMatch(model)) return 1_000_000;
            return 200_000;
        }

        /// <summary>
        /// Collapses repeated [1m] suffixes.
        ///
        /// The CLI normalizes this itself now, but a resumed old session can still carry the
        /// duplicated id — and left alone it would both display wrong and confuse the price
        /// lookup.
        /// </summary>
        public static string NormalizeModel(string model)
        {
            return string.IsNullOrEmpty(model) ? model : RepeatedOneM.Replace(model, "$1");
        }
    }
}
