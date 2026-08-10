using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Voice
{
    /// <summary>
    /// Harvests technical terms from the workspace to feed the speech keyterms. Port of
    /// src/cli/WorkspaceTerms.ts.
    ///
    /// Why it is needed: the speech proxy does not accept a multilingual mode, so recognition
    /// runs monolingual and bends English technical words into the dictation language's
    /// phonemes. Keyterms is the lever that anchors the literal spelling of names and jargon —
    /// the more relevant project terms it carries, the better "deploy", "WebSocket" and
    /// dependency names come back intact.
    ///
    /// The sources are deliberately cheap: dependency names from package.json plus a fixed
    /// glossary. No parsing, no scanning of source files.
    /// </summary>
    internal sealed class WorkspaceTerms
    {
        /// <summary>
        /// Neutral technical glossary — words a monolingual model tends to warp and that turn
        /// up in developer dictation. Project names are deliberately absent: those come from
        /// the workspace itself.
        /// </summary>
        private static readonly string[] TechGlossary =
        {
            "TypeScript", "JavaScript", "Node", "npm", "React", "Vite", "WebSocket",
            "API", "JSON", "HTTP", "HTTPS", "URL", "CLI", "GUI", "SDK", "UUID",
            "commit", "push", "pull", "merge", "rebase", "branch", "deploy", "build",
            "debug", "log", "lint", "token", "cache", "buffer", "stream", "thread",
            "async", "await", "callback", "promise", "endpoint", "payload", "header",
            "webview", "extension", "workspace", "frontend", "backend", "runtime",
            "Claude", "Anthropic", "Deepgram", "OpenTelemetry", "OTEL",
            // This port's own vocabulary, which the VS Code original had no reason to carry.
            "Visual", "Studio", "VSIX", "csproj", "solution", "NuGet", "MSBuild", "dotnet",
        };

        /// <summary>Short, because it exists to catch a package.json edited during the session.</summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

        private readonly object _gate = new object();
        private string _cachedCwd;
        private DateTime _cachedAt = DateTime.MinValue;
        private List<string> _cachedTerms;

        /// <summary>
        /// Terms harvested from the workspace plus the glossary, deduplicated. These go in as
        /// keyterms EXTRAS, after the user's own dictionary, so the character budget trims the
        /// automatic ones first.
        /// </summary>
        public IReadOnlyList<string> For(string cwd)
        {
            lock (_gate)
            {
                if (_cachedTerms != null &&
                    string.Equals(_cachedCwd, cwd, StringComparison.OrdinalIgnoreCase) &&
                    DateTime.UtcNow - _cachedAt < CacheTtl)
                {
                    return _cachedTerms;
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var terms = new List<string>();

            foreach (var term in HarvestPackageJson(cwd).Concat(TechGlossary))
            {
                if (string.IsNullOrWhiteSpace(term)) continue;
                if (seen.Add(term)) terms.Add(term);
            }

            lock (_gate)
            {
                _cachedCwd = cwd;
                _cachedAt = DateTime.UtcNow;
                _cachedTerms = terms;
            }

            Log.Debug("voice: harvested " + terms.Count + " workspace terms");
            return terms;
        }

        /// <summary>Dependency names from package.json. Empty when there is none.</summary>
        internal static IEnumerable<string> HarvestPackageJson(string cwd)
        {
            var terms = new List<string>();
            if (string.IsNullOrEmpty(cwd)) return terms;

            try
            {
                var path = Path.Combine(cwd, "package.json");
                if (!File.Exists(path)) return terms;

                using (var document = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    foreach (var section in new[] { "dependencies", "devDependencies" })
                    {
                        if (!document.RootElement.TryGetProperty(section, out var deps) ||
                            deps.ValueKind != JsonValueKind.Object) continue;

                        foreach (var dependency in deps.EnumerateObject())
                        {
                            terms.AddRange(DependencyToTerms(dependency.Name));
                        }
                    }
                }
            }
            catch
            {
                // No package.json, or an unreadable one: the glossary still applies.
            }

            return terms;
        }

        /// <summary>
        /// Splits a dependency name into pronounceable parts: "@scope/pkg-name" becomes
        /// "scope", "pkg", "name". Fragments under three characters are dropped — they are
        /// noise in a keyterms list, not words anyone dictates.
        /// </summary>
        internal static IEnumerable<string> DependencyToTerms(string name)
        {
            if (string.IsNullOrEmpty(name)) yield break;

            var withoutScope = name.TrimStart('@').Replace('/', '-');

            foreach (var part in withoutScope.Split('-', '_', '.'))
            {
                if (part.Length < 3) continue;
                if (!part.Any(char.IsLetter)) continue;
                yield return part;
            }
        }
    }
}
