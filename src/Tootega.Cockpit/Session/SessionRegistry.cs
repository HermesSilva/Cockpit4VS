using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;

namespace Tootega.Cockpit.Session
{
    /// <summary>One entry of the CLI's live-session registry.</summary>
    internal sealed class LiveSession
    {
        public int Pid { get; set; }
        public string SessionId { get; set; }
        public string Cwd { get; set; }
        public long? StartedAt { get; set; }
        /// <summary>interactive | … — not pinned, since the CLI may add kinds.</summary>
        public string Kind { get; set; }
        public string Entrypoint { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
    }

    /// <summary>
    /// The live-session registry the CLI keeps in ~/.claude/sessions/&lt;pid&gt;.json — one file
    /// per running process, written at startup and removed on exit. Port of
    /// src/cli/SessionRegistry.ts.
    ///
    /// We read it for ONE reason: to know whether a session we handed to another process is
    /// really up. Without it the UI can only assume — spawn a terminal and declare success —
    /// which is exactly the bug the official extension fixed in 2.1.224, showing Remote
    /// Control as connected after the connection had failed.
    ///
    /// Version-tolerant like the stream parser: unknown fields are ignored, a malformed or
    /// half-written file is skipped, and a missing directory just means nothing is running.
    /// </summary>
    internal sealed class SessionRegistry
    {
        private readonly string _directory;

        public SessionRegistry(string directory = null)
        {
            _directory = directory ?? ClaudeHome.SessionsDir;
        }

        /// <summary>Every session the CLI currently reports as running. Never throws.</summary>
        public Task<IReadOnlyList<LiveSession>> LiveSessionsAsync()
        {
            return Task.Run<IReadOnlyList<LiveSession>>(() =>
            {
                var sessions = new List<LiveSession>();

                string[] files;
                try
                {
                    if (!Directory.Exists(_directory)) return sessions;
                    files = Directory.GetFiles(_directory, "*.json");
                }
                catch
                {
                    // No registry yet: nothing is running.
                    return sessions;
                }

                foreach (var file in files)
                {
                    try
                    {
                        var parsed = Json.TryDeserialize<LiveSession>(File.ReadAllText(file));
                        // A file still being written, or one whose shape we do not
                        // understand, is skipped rather than guessed at.
                        if (parsed == null || string.IsNullOrEmpty(parsed.SessionId) || parsed.Pid <= 0) continue;
                        sessions.Add(parsed);
                    }
                    catch
                    {
                        // Locked or mid-write; the next poll will see it.
                    }
                }

                return sessions;
            });
        }

        /// <summary>
        /// Is this conversation owned by a live process?
        ///
        /// Accepts several ids for the same conversation, because sessionId and the resume id
        /// diverge after a resume and the caller may only know one of them. The pid is
        /// confirmed alive: a process killed hard leaves its registry file behind, and
        /// trusting the file alone is how a dead session reads as connected.
        /// </summary>
        public async Task<bool> IsSessionLiveAsync(params string[] ids)
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids ?? Array.Empty<string>())
            {
                if (!string.IsNullOrEmpty(id)) wanted.Add(id);
            }
            if (wanted.Count == 0) return false;

            foreach (var session in await LiveSessionsAsync().ConfigureAwait(false))
            {
                if (!wanted.Contains(session.SessionId)) continue;
                if (IsPidAlive(session.Pid)) return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a pid is a running process. The .NET equivalent of `kill(pid, 0)`:
        /// existence is what is being checked, and nothing is signalled.
        /// </summary>
        internal static bool IsPidAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    return !process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                // No such process.
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch
            {
                // Anything else (typically a permission problem) means the process exists
                // but is not ours — alive, from the caller's point of view.
                return true;
            }
        }
    }
}
