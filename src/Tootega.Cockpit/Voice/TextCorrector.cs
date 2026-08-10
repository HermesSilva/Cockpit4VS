using System;
using System.Threading.Tasks;
using Tootega.Cockpit.Cli;

namespace Tootega.Cockpit.Voice
{
    /// <summary>
    /// Spelling and grammar correction of dictated text. Port of src/cli/TextCorrector.ts.
    ///
    /// Opt-in and off by default, because it spends tokens — a small amount, but the user
    /// should choose. It is a clean one-shot: instruction plus text, nothing else.
    /// </summary>
    internal sealed class TextCorrector
    {
        /// <summary>
        /// The instruction is in English but the ANSWER must stay in the user's language, said
        /// explicitly so the model never translates what was dictated. Asking for the text
        /// alone matters too: a model that adds "Here is the corrected text:" would put that
        /// straight into the composer.
        /// </summary>
        private const string SystemPrompt =
            "Fix only spelling, accentuation and grammar mistakes in the user's text. " +
            "Keep exactly the same language, meaning and tone — never translate. " +
            "Answer ONLY with the corrected text — no comments, no quotes, no prefixes.";

        private readonly AiClient _ai;

        public TextCorrector(AiClient ai)
        {
            _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        }

        /// <summary>
        /// Corrects the text. <paramref name="hints"/> comes from the dictation dictionary and
        /// steers the model to keep jargon and apply replacements.
        ///
        /// Returns null on failure, which the caller reads as "keep the original" — a failed
        /// correction must never lose what the user dictated.
        /// </summary>
        public Task<string> CorrectAsync(string text, string hints = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return Task.FromResult<string>(null);

            return _ai.AskAsync(new AskOptions
            {
                System = string.IsNullOrEmpty(hints) ? SystemPrompt : SystemPrompt + " " + hints,
                Prompt = text,
                MaxTokens = MaxTokensFor(text),
            });
        }

        /// <summary>
        /// Budgets the reply against the input. A correction is about the same length as its
        /// input, so a fixed ceiling would either truncate a long dictation or over-reserve for
        /// a short one.
        /// </summary>
        internal static int MaxTokensFor(string text)
        {
            var estimate = (int)Math.Ceiling((text ?? string.Empty).Length / 2.0) + 256;
            return Math.Min(4096, Math.Max(256, estimate));
        }
    }
}
