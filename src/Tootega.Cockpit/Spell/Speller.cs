using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Tootega.Cockpit.Util;
using WeCantSpell.Hunspell;

namespace Tootega.Cockpit.Spell
{
    internal sealed class SpellSuggestions
    {
        public List<string> Pt { get; set; } = new List<string>();
        public List<string> En { get; set; } = new List<string>();
    }

    /// <summary>
    /// Bilingual spell-checker (PT-BR + EN). Port of src/spell/Speller.ts.
    ///
    /// A word is only flagged when BOTH dictionaries reject it. That is the whole design: a
    /// developer writing mixed Portuguese and English would otherwise see half of every
    /// sentence underlined, which teaches people to ignore the marks.
    ///
    /// It marks and suggests; it never auto-corrects. The original ran Hunspell as WASM, which
    /// has no .NET analogue — this uses a fully managed implementation reading the same
    /// .aff/.dic files, so there are no native binaries to ship per architecture.
    /// </summary>
    internal sealed class Speller
    {
        /// <summary>
        /// No real word is longer than this. Bigger tokens are junk — URL glue, base64, a
        /// hash — and checking them is pure cost for a result that is always "wrong".
        /// </summary>
        private const int MaxWordLength = 64;

        private readonly string _dictionaryDirectory;
        private readonly object _gate = new object();

        private WordList _en;
        private WordList _pt;
        private Task _loading;
        private volatile bool _ready;

        private HashSet<string> _userWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Project vocabulary — dependency names, glossary, dictation terms. Treated as known
        /// so they are not flagged, but never persisted: they are derived from the workspace
        /// and recomputed each session, and writing them into the user's dictionary would
        /// silently accumulate another project's jargon.
        /// </summary>
        private HashSet<string> _projectWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public Speller(string dictionaryDirectory = null, IEnumerable<string> initialUserWords = null)
        {
            _dictionaryDirectory = dictionaryDirectory ?? DefaultDictionaryDirectory();

            if (initialUserWords == null) return;
            foreach (var word in initialUserWords)
            {
                if (!string.IsNullOrWhiteSpace(word)) _userWords.Add(word.Trim());
            }
        }

        /// <summary>
        /// Finds the dictionaries beside the assembly.
        ///
        /// Several candidates rather than one, because Assembly.Location points at a shadow
        /// copy under some hosts — the test runner among them — where the content files were
        /// never copied. Falling back to the code base and the app-domain directory covers
        /// that without special-casing any particular host.
        /// </summary>
        private static string DefaultDictionaryDirectory()
        {
            foreach (var directory in CandidateDirectories())
            {
                if (string.IsNullOrEmpty(directory)) continue;

                var candidate = Path.Combine(directory, "Dictionaries");
                if (Directory.Exists(candidate)) return candidate;
            }

            // Nothing found: return the primary guess so the log names a real path.
            var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(assemblyDirectory ?? string.Empty, "Dictionaries");
        }

        private static IEnumerable<string> CandidateDirectories()
        {
            var assembly = Assembly.GetExecutingAssembly();

            yield return SafeDirectory(() => Path.GetDirectoryName(assembly.Location));

            yield return SafeDirectory(() =>
            {
                var codeBase = assembly.CodeBase;
                if (string.IsNullOrEmpty(codeBase)) return null;
                return Path.GetDirectoryName(new Uri(codeBase).LocalPath);
            });

            yield return SafeDirectory(() => AppDomain.CurrentDomain.BaseDirectory);
        }

        private static string SafeDirectory(Func<string> resolve)
        {
            try
            {
                return resolve();
            }
            catch
            {
                return null;
            }
        }

        public bool IsReady => _ready;

        /// <summary>
        /// Loads the dictionaries in the background. Idempotent, and safe to call before every
        /// check — the composer does exactly that.
        /// </summary>
        public Task EnsureAsync()
        {
            lock (_gate)
            {
                return _loading ?? (_loading = Task.Run(() => Load()));
            }
        }

        private void Load()
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();

                var en = LoadPair("en");
                var pt = LoadPair("pt-br");

                lock (_gate)
                {
                    _en = en;
                    _pt = pt;
                    _ready = en != null && pt != null;
                }

                if (_ready) Log.Info("spell: dictionaries loaded in " + stopwatch.ElapsedMilliseconds + " ms");
                else Log.Info("spell: dictionaries unavailable in " + _dictionaryDirectory);
            }
            catch (Exception ex)
            {
                // Spell checking is an assist; failing to load it must not affect anything else.
                Log.Error("spell: failed to load dictionaries", ex);
            }
        }

        private WordList LoadPair(string baseName)
        {
            try
            {
                var aff = Path.Combine(_dictionaryDirectory, baseName + ".aff");
                var dic = Path.Combine(_dictionaryDirectory, baseName + ".dic");
                if (!File.Exists(aff) || !File.Exists(dic)) return null;

                return WordList.CreateFromFiles(dic, aff);
            }
            catch (Exception ex)
            {
                Log.Debug("spell: could not load " + baseName + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>Sets the project's technical terms. Not persisted.</summary>
        public void SetProjectTerms(IEnumerable<string> words)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var word in words ?? Enumerable.Empty<string>())
            {
                var trimmed = (word ?? string.Empty).Trim();
                if (trimmed.Length > 0) set.Add(trimmed);
            }

            lock (_gate) _projectWords = set;
        }

        /// <summary>The subset of <paramref name="words"/> that both dictionaries reject.</summary>
        public IReadOnlyList<string> Check(IEnumerable<string> words)
        {
            var bad = new List<string>();
            if (!_ready) return bad;

            WordList en, pt;
            lock (_gate)
            {
                en = _en;
                pt = _pt;
            }
            if (en == null || pt == null) return bad;

            foreach (var word in words ?? Enumerable.Empty<string>())
            {
                if (IsKnown(word)) continue;
                // Not worth checking, and treated as correct rather than flagged: an
                // underlined base64 blob helps nobody.
                if (!IsCheckable(word)) continue;

                if (!en.Check(word) && !pt.Check(word)) bad.Add(word);
            }

            return bad;
        }

        /// <summary>
        /// Suggestions grouped by language, so the dropdown can say where each came from —
        /// which is what makes a bilingual list readable instead of a jumble.
        /// </summary>
        public SpellSuggestions Suggest(string word, int max = 7)
        {
            var suggestions = new SpellSuggestions();
            if (!_ready || !IsCheckable(word)) return suggestions;

            WordList en, pt;
            lock (_gate)
            {
                en = _en;
                pt = _pt;
            }
            if (en == null || pt == null) return suggestions;

            try
            {
                suggestions.Pt = pt.Suggest(word).Take(max).ToList();
                suggestions.En = en.Suggest(word).Take(max).ToList();
            }
            catch (Exception ex)
            {
                Log.Debug("spell: suggest failed: " + ex.Message);
            }

            return suggestions;
        }

        public void AddWord(string word)
        {
            var trimmed = (word ?? string.Empty).Trim();
            if (trimmed.Length == 0) return;
            lock (_gate) _userWords.Add(trimmed);
        }

        /// <summary>Replaces the user dictionary, as edited in the modal.</summary>
        public void SetUserDictionary(IEnumerable<string> words)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var word in words ?? Enumerable.Empty<string>())
            {
                var trimmed = (word ?? string.Empty).Trim();
                if (trimmed.Length > 0) set.Add(trimmed);
            }

            lock (_gate) _userWords = set;
        }

        public IReadOnlyList<string> UserDictionary()
        {
            lock (_gate) return _userWords.ToList();
        }

        private bool IsKnown(string word)
        {
            if (string.IsNullOrEmpty(word)) return true;
            lock (_gate) return _userWords.Contains(word) || _projectWords.Contains(word);
        }

        /// <summary>
        /// Whether a token is worth checking: within a plausible word length and free of
        /// control characters.
        /// </summary>
        internal static bool IsCheckable(string word)
        {
            if (string.IsNullOrEmpty(word) || word.Length > MaxWordLength) return false;

            foreach (var c in word)
            {
                if (c < 32 || c == 127) return false;
            }

            return true;
        }
    }
}
