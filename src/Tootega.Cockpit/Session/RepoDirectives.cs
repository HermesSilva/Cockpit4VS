using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Tootega.Cockpit.Session
{
    /// <summary>
    /// Directives the Cockpit reads from a project's CLAUDE.md. Port of
    /// src/session/RepoDirectives.ts.
    ///
    /// This is a different thing from the CLAUDE.md content the CLI injects into the context.
    /// That is instruction for the model, with no enforcement. Here the Cockpit reads the file
    /// to ENFORCE something in the UI — today, a minimum reasoning effort, which is asked about
    /// before a send rather than silently applied.
    ///
    /// The tag lives inside a markdown comment so it does not pollute the prose:
    ///   &lt;!-- **enffort=max** --&gt;
    /// </summary>
    internal static class RepoDirectives
    {
        /// <summary>Effort levels in order, so two values can be compared.</summary>
        public static readonly IReadOnlyDictionary<string, int> EffortRank =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["low"] = 1,
                ["medium"] = 2,
                ["high"] = 3,
                ["xhigh"] = 4,
                ["max"] = 5,
            };

        /// <summary>
        /// Deliberately forgiving: the spelling variants exist because the tag is typed by hand,
        /// and a directive that fails to apply because of a typo is worse than accepting three
        /// spellings. The level itself must be a known one.
        /// </summary>
        private static readonly Regex Tag = new Regex(
            @"(?:enffort|effort|enfor)\s*=\s*\*{0,2}\s*(low|medium|high|xhigh|max)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Extracts the minimum effort from CLAUDE.md text, or null.</summary>
        public static string ParseMinEffort(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var match = Tag.Match(text);
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }

        public static string ReadMinEffortFromFile(string absolutePath)
        {
            try
            {
                return File.Exists(absolutePath) ? ParseMinEffort(File.ReadAllText(absolutePath)) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Minimum effort declared in the CLAUDE.md at the project root, or null.</summary>
        public static string ReadMinEffort(string cwd)
        {
            return string.IsNullOrEmpty(cwd) ? null : ReadMinEffortFromFile(Path.Combine(cwd, "CLAUDE.md"));
        }

        /// <summary>
        /// Resolves the minimum effort for a folder by walking up to the root.
        ///
        /// The DEEPEST declaration wins, because a subtree can reasonably demand more than the
        /// repository as a whole. Both CLAUDE.md and .claude/CLAUDE.md are checked at each
        /// level, since the CLI honours either.
        /// </summary>
        public static string ResolveMinEffort(string directory, string root)
        {
            if (string.IsNullOrEmpty(directory)) return null;

            string current;
            string stop;
            try
            {
                current = Path.GetFullPath(directory);
                stop = string.IsNullOrEmpty(root) ? current : Path.GetFullPath(root);
            }
            catch
            {
                return null;
            }

            // Bounded against loops on odd paths (a mapped drive pointing at itself).
            for (var depth = 0; depth < 64; depth++)
            {
                var level = ReadMinEffortFromFile(Path.Combine(current, "CLAUDE.md"))
                            ?? ReadMinEffortFromFile(Path.Combine(current, ".claude", "CLAUDE.md"));
                if (level != null) return level;

                if (string.Equals(current, stop, StringComparison.OrdinalIgnoreCase)) break;

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current) break;
                current = parent;
            }

            return null;
        }

        /// <summary>
        /// Whether <paramref name="selected"/> falls below the folder's floor.
        ///
        /// An unknown level on either side answers false: refusing to send because a value was
        /// not recognised would be worse than letting it through.
        /// </summary>
        public static bool IsBelow(string selected, string minimum)
        {
            if (string.IsNullOrEmpty(minimum)) return false;
            if (!EffortRank.TryGetValue(minimum, out var floor)) return false;
            if (string.IsNullOrEmpty(selected) || !EffortRank.TryGetValue(selected, out var chosen)) return false;
            return chosen < floor;
        }
    }
}
