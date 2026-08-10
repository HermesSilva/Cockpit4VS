using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Spell;
using Tootega.Cockpit.Util;
using Tootega.Cockpit.Voice;

namespace Tootega.Cockpit.Host
{
    /// <summary>
    /// The composer's spell-checker.
    ///
    /// It is built on the first word checked, not at start-up: the dictionaries are megabytes
    /// of Hunspell data, and a user who never types in the composer should never pay for them.
    ///
    /// The added words live in the same per-machine file as the dictation dictionary. That is
    /// deliberate — a term the user taught the dictation is not a typo either, and keeping two
    /// lists would mean teaching the same word twice.
    /// </summary>
    internal sealed class SpellingService
    {
        private readonly VoiceDictionary _dictionary;
        private readonly WorkspaceTerms _terms;

        private Speller _speller;
        private string _termsFolder;

        public SpellingService(VoiceDictionary dictionary, WorkspaceTerms terms)
        {
            _dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
            _terms = terms ?? throw new ArgumentNullException(nameof(terms));
        }

        /// <summary>
        /// The words the misspelling check should ignore, for one folder.
        ///
        /// A project's dependencies and glossary are not typos, and neither are the dictation
        /// terms. They are recomputed when the folder changes, because the jargon of one
        /// project is not the jargon of the next.
        /// </summary>
        private Speller For(string cwd)
        {
            if (_speller == null)
            {
                var dictionary = _dictionary.Load();

                // No folder passed: the Speller finds the shipped dictionaries beside its own
                // assembly, and it already handles the case a test runner creates by
                // shadow-copying that assembly.
                _speller = new Speller(null, dictionary.SpellWords ?? new List<string>());
            }

            if (!string.Equals(_termsFolder, cwd, StringComparison.OrdinalIgnoreCase))
            {
                _termsFolder = cwd;
                RefreshProjectTerms(cwd);
            }

            return _speller;
        }

        private void RefreshProjectTerms(string cwd)
        {
            var dictionary = _dictionary.Load();
            _speller.SetProjectTerms(_terms.For(cwd).Concat(dictionary.Terms ?? new List<string>()));
        }

        // ---- Handlers ----

        /// <summary>Checks a batch of words and answers with the wrong ones.</summary>
        public async Task<HostMessage> CheckAsync(string cwd, IReadOnlyList<string> words)
        {
            var speller = For(cwd);
            await speller.EnsureAsync();
            return HostMessages.SpellResult(speller.Check(words ?? new List<string>()));
        }

        /// <summary>Suggestions for one word, in both languages the composer offers.</summary>
        public async Task<HostMessage> SuggestAsync(string cwd, string requestId, string word)
        {
            var speller = For(cwd);
            await speller.EnsureAsync();

            var suggestions = speller.Suggest(word);
            return HostMessages.SpellSuggestResult(requestId, word, suggestions.Pt, suggestions.En);
        }

        /// <summary>
        /// Teaches the checker a word, for good.
        ///
        /// Written through the shared file rather than kept in memory: "add to dictionary" that
        /// forgets on restart is worse than no such button.
        /// </summary>
        public void Add(string cwd, string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return;

            var speller = For(cwd);
            speller.AddWord(word);

            var dictionary = _dictionary.Load();
            dictionary.SpellWords = speller.UserDictionary().ToList();
            _dictionary.Save(dictionary);

            Log.Debug("spell: '" + word + "' added to the dictionary");
        }

        /// <summary>
        /// Replaces the whole added-words list, as the dictionary modal does, and re-reads the
        /// project terms so a term added there stops being flagged immediately.
        /// </summary>
        public IReadOnlyList<string> ReplaceUserDictionary(string cwd, IEnumerable<string> words)
        {
            var speller = For(cwd);
            if (words != null) speller.SetUserDictionary(words);

            RefreshProjectTerms(cwd);
            return speller.UserDictionary();
        }
    }
}
