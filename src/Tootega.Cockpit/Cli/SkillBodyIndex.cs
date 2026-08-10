using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Tootega.Cockpit.Cli
{
    /// <summary>
    /// Recognises a SKILL.md body inside text a HOOK injected. Port of
    /// src/cli/SkillBodyIndex.ts.
    ///
    /// Why this exists: a hook (SessionStart, UserPromptSubmit) can dump a skill's content
    /// straight into the context. That path emits no `Skill` tool_use and goes through no
    /// /name, so the panel would show the skill as light while its body was already weighing
    /// on the prompt. The stream's hook_response carries the text but NOT the skill's name —
    /// the hook command is usually a script of its own — so the only possible link is the
    /// CONTENT: comparing the injected text against the SKILL.md files on disk.
    ///
    /// It is an inference, and the UI labels it as one. A false positive is unlikely: the
    /// signature is hundreds of literal characters of the skill's own body.
    /// </summary>
    internal sealed class SkillBodyIndex
    {
        /// <summary>
        /// How much of the body is used as a signature. Long enough not to collide between
        /// skills, short enough to survive a hook that injects only the beginning of the file.
        /// </summary>
        private const int SignatureChars = 200;

        /// <summary>Below this a body is too short or too generic to identify anything.</summary>
        private const int SignatureMin = 60;

        /// <summary>A hook can dump an entire file; scanning megabytes is not worth it.</summary>
        private const int TextMax = 200_000;

        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex Frontmatter =
            new Regex(@"^---\r?\n[\s\S]*?\r?\n---\r?\n", RegexOptions.Compiled);

        private readonly string _userRoot;
        private readonly object _gate = new object();

        /// <summary>Signatures already read, including the absent ones, so disk is read once.</summary>
        private readonly Dictionary<string, string> _signatures = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _signatureMisses = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _discovered =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <param name="userRoot">The ~/.claude root. Injectable for tests.</param>
        public SkillBodyIndex(string userRoot = null)
        {
            _userRoot = userRoot ?? ClaudeHome.Root;
        }

        /// <summary>Forgets everything read — a workspace switch, or an edited skill.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _signatures.Clear();
                _signatureMisses.Clear();
                _discovered.Clear();
            }
        }

        /// <summary>
        /// The name of the skill whose body appears in <paramref name="text"/>, or null.
        ///
        /// <paramref name="names"/> is the list the session announced. Built-in skills have no
        /// file on disk and therefore never match — their injection is still accounted as hook
        /// context, just without a name, which is honest rather than convenient.
        /// </summary>
        public string Match(string text, IReadOnlyList<string> names, string cwd = null)
        {
            if (string.IsNullOrEmpty(text) || names == null || names.Count == 0) return null;

            var haystack = Normalize(text.Length > TextMax ? text.Substring(0, TextMax) : text);
            if (haystack.Length < SignatureMin) return null;

            foreach (var name in names)
            {
                var signature = Signature(name, cwd);
                if (signature != null && haystack.IndexOf(signature, StringComparison.Ordinal) >= 0) return name;
            }

            return null;
        }

        /// <summary>Normalized signature of a skill, or null when there is no readable file.</summary>
        public string Signature(string name, string cwd = null)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var key = (cwd ?? string.Empty) + " " + name;

            lock (_gate)
            {
                if (_signatures.TryGetValue(key, out var cached)) return cached;
                if (_signatureMisses.Contains(key)) return null;
            }

            string signature = null;
            foreach (var path in CandidatePaths(name, cwd))
            {
                string content;
                try
                {
                    if (!File.Exists(path)) continue;
                    content = File.ReadAllText(path);
                }
                catch
                {
                    continue;
                }

                var normalized = Normalize(BodyOf(content));
                if (normalized.Length >= SignatureMin)
                    signature = normalized.Substring(0, Math.Min(SignatureChars, normalized.Length));

                // The file was found in this scope. Whether or not it is long enough to serve
                // as a signature, a narrower scope wins over a wider one — so we stop here
                // rather than falling through to the user-level copy of a project skill.
                break;
            }

            lock (_gate)
            {
                if (signature != null) _signatures[key] = signature;
                else _signatureMisses.Add(key);
            }

            return signature;
        }

        /// <summary>
        /// Skill names that exist on disk, project and user scope.
        ///
        /// Needed because SessionStart hooks fire BEFORE the init event announces the skill
        /// list — and that first injection is precisely the one carrying the whole body, so
        /// without this it would go unrecognised.
        /// </summary>
        public IReadOnlyList<string> NamesOnDisk(string cwd = null)
        {
            var key = cwd ?? string.Empty;

            lock (_gate)
            {
                if (_discovered.TryGetValue(key, out var cached)) return cached;
            }

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var root in SkillRoots(cwd))
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var directory in Directory.GetDirectories(root))
                    {
                        var name = Path.GetFileName(directory);
                        if (!string.IsNullOrEmpty(name) && seen.Add(name)) names.Add(name);
                    }
                }
                catch
                {
                    // A scope with no skills directory simply contributes nothing.
                }
            }

            lock (_gate) _discovered[key] = names;
            return names;
        }

        /// <summary>Where a SKILL.md can live, in the order the CLI resolves them.</summary>
        private IEnumerable<string> CandidatePaths(string name, string cwd)
        {
            var relative = Path.Combine("skills", name, "SKILL.md");
            if (!string.IsNullOrEmpty(cwd)) yield return Path.Combine(cwd, ".claude", relative);
            yield return Path.Combine(_userRoot, relative);
        }

        private IEnumerable<string> SkillRoots(string cwd)
        {
            if (!string.IsNullOrEmpty(cwd)) yield return Path.Combine(cwd, ".claude", "skills");
            yield return Path.Combine(_userRoot, "skills");
        }

        /// <summary>
        /// Whitespace collapsed and case folded: a hook may reformat what it copied (CRLF,
        /// re-indentation), and the signature has to survive that.
        /// </summary>
        internal static string Normalize(string text)
        {
            return string.IsNullOrEmpty(text)
                ? string.Empty
                : Whitespace.Replace(text, " ").Trim().ToLowerInvariant();
        }

        /// <summary>The SKILL.md body without its YAML frontmatter, which the listing already covers.</summary>
        internal static string BodyOf(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return string.Empty;
            var match = Frontmatter.Match(markdown);
            return match.Success ? markdown.Substring(match.Length) : markdown;
        }
    }
}
