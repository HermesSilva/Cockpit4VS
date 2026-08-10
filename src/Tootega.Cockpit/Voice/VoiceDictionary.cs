using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Voice
{
    internal sealed class VoiceDict
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 2;
        [JsonPropertyName("terms")] public List<string> Terms { get; set; } = new List<string>();
        [JsonPropertyName("replacements")] public List<VoiceReplacement> Replacements { get; set; } = new List<VoiceReplacement>();
        /// <summary>Words the user added to, or told to ignore in, the spell-checker.</summary>
        [JsonPropertyName("spellWords")] public List<string> SpellWords { get; set; } = new List<string>();
    }

    /// <summary>
    /// The dictation dictionary: terms to recognise and preserve, plus heard-to-written
    /// replacements. Port of src/cli/VoiceDictionary.ts.
    ///
    /// It is used in two places. Live, the terms become the keyterms header the speech service
    /// prioritises during recognition. After dictation, the replacements are applied to the
    /// text and the terms steer the corrector to PRESERVE them rather than "fixing" proper
    /// nouns and jargon into ordinary words.
    ///
    /// Stored per MACHINE rather than per Claude account: it is the OS user's vocabulary, and
    /// splitting it by account meant re-teaching the same jargon after a re-login.
    /// </summary>
    internal sealed class VoiceDictionary
    {
        /// <summary>Cap so the keyterms header cannot grow without bound.</summary>
        private const int MaxTerms = 200;
        private const int MaxKeytermsChars = 2000;

        private static readonly Regex RegexSpecials = new Regex(@"[.*+?^${}()|[\]\\]", RegexOptions.Compiled);
        private static readonly Regex UnsafeSlugChars = new Regex(@"[^a-z0-9._-]+", RegexOptions.Compiled);

        private readonly string _file;
        private readonly string _legacyDirectory;

        public VoiceDictionary(string directory = null)
        {
            var root = directory ?? ClaudeHome.CockpitDir;
            _file = Path.Combine(root, "dictionaries.json");
            _legacyDirectory = Path.Combine(root, "voice-dictionary");
        }

        /// <summary>Filename-safe slug from an account e-mail. Kept for the legacy migration.</summary>
        internal static string AccountSlug(string email)
        {
            var slug = UnsafeSlugChars.Replace((email ?? string.Empty).Trim().ToLowerInvariant(), "_");
            return slug.Length > 0 ? slug : "default";
        }

        /// <summary>Reads the dictionary. Missing or corrupt reads as empty.</summary>
        public VoiceDict Load()
        {
            var raw = FileStore.ReadAllTextOrNull(_file);
            if (raw != null)
            {
                var parsed = Normalize(Json.TryDeserialize<VoiceDict>(raw));
                if (parsed != null) return parsed;
            }

            // First run after the split-by-account era: merge whatever was there rather than
            // making the user retype their vocabulary.
            return MigrateLegacy();
        }

        /// <summary>Writes the dictionary, normalized and deduplicated.</summary>
        public void Save(VoiceDict dictionary)
        {
            var normalized = new VoiceDict
            {
                Version = 2,
                Terms = Dedupe(dictionary?.Terms).Take(MaxTerms).ToList(),
                Replacements = (dictionary?.Replacements ?? new List<VoiceReplacement>())
                    .Select(r => new VoiceReplacement { From = (r?.From ?? string.Empty).Trim(), To = (r?.To ?? string.Empty).Trim() })
                    // A rule with no left-hand side matches everything; dropping it is the
                    // only safe reading.
                    .Where(r => r.From.Length > 0)
                    .ToList(),
                SpellWords = Dedupe(dictionary?.SpellWords).ToList(),
            };

            FileStore.WriteAtomic(_file, Json.Serialize(normalized));
        }

        private VoiceDict MigrateLegacy()
        {
            var merged = new VoiceDict();

            try
            {
                if (!Directory.Exists(_legacyDirectory)) return merged;

                foreach (var file in Directory.GetFiles(_legacyDirectory, "*.json"))
                {
                    var parsed = Normalize(Json.TryDeserialize<VoiceDict>(FileStore.ReadAllTextOrNull(file)));
                    if (parsed == null) continue;

                    merged.Terms.AddRange(parsed.Terms);
                    merged.Replacements.AddRange(parsed.Replacements);
                    merged.SpellWords.AddRange(parsed.SpellWords);
                }
            }
            catch
            {
                // No legacy directory, or an unreadable one: nothing to migrate.
            }

            return merged;
        }

        /// <summary>Drops entries that cannot be used, so the rest of the code need not re-check.</summary>
        private static VoiceDict Normalize(VoiceDict dictionary)
        {
            if (dictionary == null) return null;

            return new VoiceDict
            {
                Version = dictionary.Version,
                Terms = (dictionary.Terms ?? new List<string>())
                    .Where(t => !string.IsNullOrWhiteSpace(t)).ToList(),
                Replacements = (dictionary.Replacements ?? new List<VoiceReplacement>())
                    .Where(r => r != null && !string.IsNullOrWhiteSpace(r.From) && r.To != null)
                    .ToList(),
                SpellWords = (dictionary.SpellWords ?? new List<string>())
                    .Where(w => !string.IsNullOrWhiteSpace(w)).ToList(),
            };
        }

        /// <summary>
        /// The keyterms string for the speech service.
        ///
        /// Order is PRIORITY: the user's own terms come first, then extras harvested from the
        /// workspace. The character budget truncates the tail, so what the user curated by
        /// hand is never dropped in favour of something automatic.
        /// </summary>
        public static string BuildKeyterms(VoiceDict dictionary, IEnumerable<string> extras = null)
        {
            var all = Dedupe((dictionary?.Terms ?? new List<string>())
                .Concat(extras ?? Enumerable.Empty<string>()));

            var result = string.Empty;
            foreach (var term in all)
            {
                var next = result.Length == 0 ? term : result + "," + term;
                if (next.Length > MaxKeytermsChars) break;
                result = next;
            }

            return result;
        }

        /// <summary>
        /// Applies the heard-to-written replacements.
        ///
        /// Matched case-insensitively and bounded by non-letters, so a rule for "dase" does not
        /// rewrite the middle of "database". The replacement's own casing is preserved, since
        /// the point is usually to restore a proper noun's spelling.
        /// </summary>
        public static string ApplyReplacements(string text, VoiceDict dictionary)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var replacements = dictionary?.Replacements;
            if (replacements == null || replacements.Count == 0) return text;

            var result = text;
            foreach (var replacement in replacements)
            {
                if (string.IsNullOrEmpty(replacement?.From)) continue;

                try
                {
                    var pattern = @"(?<!\p{L})" + RegexSpecials.Replace(replacement.From, @"\$&") + @"(?!\p{L})";
                    result = Regex.Replace(result, pattern, (replacement.To ?? string.Empty).Replace("$", "$$"),
                                           RegexOptions.IgnoreCase);
                }
                catch (ArgumentException)
                {
                    // A rule that will not compile is skipped rather than failing the whole pass.
                }
            }

            return result;
        }

        /// <summary>
        /// The instruction fragment that steers the corrector.
        ///
        /// Without it, a corrector asked to fix spelling will happily "fix" a product name into
        /// a real word — which is exactly what the dictionary exists to prevent.
        /// </summary>
        public static string CorrectorHints(VoiceDict dictionary)
        {
            var parts = new List<string>();

            if (dictionary?.Terms != null && dictionary.Terms.Count > 0)
            {
                parts.Add("Preserve these terms EXACTLY (names/jargon), without changing their spelling: " +
                          string.Join(", ", dictionary.Terms) + ".");
            }

            if (dictionary?.Replacements != null && dictionary.Replacements.Count > 0)
            {
                var map = string.Join("; ", dictionary.Replacements.Select(r => "\"" + r.From + "\" → \"" + r.To + "\""));
                parts.Add("Apply these replacements whenever they appear: " + map + ".");
            }

            return parts.Count > 0 ? string.Join(" ", parts) : null;
        }

        /// <summary>Case-insensitive dedupe that keeps the first spelling seen.</summary>
        private static IEnumerable<string> Dedupe(IEnumerable<string> values)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values ?? Enumerable.Empty<string>())
            {
                var trimmed = (value ?? string.Empty).Trim();
                if (trimmed.Length == 0) continue;
                if (seen.Add(trimmed)) yield return trimmed;
            }
        }

        /// <summary>Converts to the wire shape the dictionary modal reads.</summary>
        public static VoiceDictData ToWire(VoiceDict dictionary, string account = null)
        {
            return new VoiceDictData
            {
                Terms = dictionary?.Terms ?? new List<string>(),
                Replacements = dictionary?.Replacements ?? new List<VoiceReplacement>(),
                SpellWords = dictionary?.SpellWords ?? new List<string>(),
                Account = account,
            };
        }

        public static VoiceDict FromWire(VoiceDictData data)
        {
            return Normalize(new VoiceDict
            {
                Terms = data?.Terms ?? new List<string>(),
                Replacements = data?.Replacements ?? new List<VoiceReplacement>(),
                SpellWords = data?.SpellWords ?? new List<string>(),
            }) ?? new VoiceDict();
        }
    }
}
