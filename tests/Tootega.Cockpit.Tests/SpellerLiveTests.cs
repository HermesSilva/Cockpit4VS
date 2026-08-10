using System.Linq;
using System.Threading.Tasks;
using Tootega.Cockpit.Spell;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// The spell-checker against the real PT-BR and EN dictionaries.
    ///
    /// This is the check that matters for the port: the VS Code original ran Hunspell as WASM,
    /// which has no .NET analogue, so the engine was replaced. Unit tests can prove the
    /// bookkeeping; only loading the actual .aff/.dic pairs proves the replacement behaves the
    /// same way on real words.
    /// </summary>
    public class SpellerLiveTests
    {
        private static async Task<Speller> ReadySpellerAsync()
        {
            var speller = new Speller();
            await speller.EnsureAsync();
            return speller;
        }

        [SkippableFact]
        public async Task LoadsBothDictionaries()
        {
            var speller = await ReadySpellerAsync();
            Skip.IfNot(speller.IsReady, "Dictionaries are not present beside the test assembly.");

            Assert.True(speller.IsReady);
        }

        [SkippableFact]
        public async Task AcceptsWordsFromEitherLanguage()
        {
            // The whole design: a word is only wrong when BOTH dictionaries reject it.
            // Otherwise a developer writing mixed Portuguese and English would see half of
            // every sentence underlined, which teaches people to ignore the marks.
            var speller = await ReadySpellerAsync();
            Skip.IfNot(speller.IsReady, "Dictionaries are not present beside the test assembly.");

            Assert.Empty(speller.Check(new[] { "hello", "world", "session" }));
            Assert.Empty(speller.Check(new[] { "sessão", "código", "arquivo" }));
            // A sentence mixing both must come back clean.
            Assert.Empty(speller.Check(new[] { "abrir", "session", "novo", "commit" }));
        }

        [SkippableFact]
        public async Task FlagsWordsNeitherLanguageKnows()
        {
            var speller = await ReadySpellerAsync();
            Skip.IfNot(speller.IsReady, "Dictionaries are not present beside the test assembly.");

            var bad = speller.Check(new[] { "hello", "zzqqxxwv", "sessão" });

            Assert.Equal(new[] { "zzqqxxwv" }, bad);
        }

        [SkippableFact]
        public async Task SuggestsPerLanguage()
        {
            // Grouping by language is what makes a bilingual suggestion list readable rather
            // than a jumble the user has to decode.
            var speller = await ReadySpellerAsync();
            Skip.IfNot(speller.IsReady, "Dictionaries are not present beside the test assembly.");

            var suggestions = speller.Suggest("cormputer");

            Assert.NotEmpty(suggestions.En);
            Assert.Contains("computer", suggestions.En);
        }

        [SkippableFact]
        public async Task ProjectAndUserWordsSuppressTheMark()
        {
            var speller = await ReadySpellerAsync();
            Skip.IfNot(speller.IsReady, "Dictionaries are not present beside the test assembly.");

            Assert.NotEmpty(speller.Check(new[] { "Tootega" }));

            speller.SetUserDictionary(new[] { "Tootega" });
            speller.SetProjectTerms(new[] { "VSIX" });

            Assert.Empty(speller.Check(new[] { "Tootega", "VSIX" }));
            // Case-insensitive, since dictation and typing disagree on capitalisation.
            Assert.Empty(speller.Check(new[] { "tootega", "vsix" }));
        }

        [SkippableFact]
        public async Task NeverFlagsPathologicalTokens()
        {
            // URL glue and base64 are always "wrong"; underlining them helps nobody.
            var speller = await ReadySpellerAsync();
            Skip.IfNot(speller.IsReady, "Dictionaries are not present beside the test assembly.");

            Assert.Empty(speller.Check(new[] { new string('x', 200) }));
            Assert.Empty(speller.Suggest(new string('x', 200)).En);
        }
    }
}
