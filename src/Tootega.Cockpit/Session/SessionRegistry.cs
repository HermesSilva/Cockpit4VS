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
    /// <summary>
    /// Where a handed-over conversation is running now, as far as this machine can tell. Mirrors
    /// the CLI's 2.1.229 Remote Control labels: a cloud/phone peer (<see cref="Cloud"/>) versus a
    /// dropped connection (<see cref="Offline"/>).
    /// </summary>
    internal enum SessionLocation
    {
        /// <summary>A live interactive process on this machine owns it (the terminal we spawned).</summary>
        Local,
        /// <summary>A live non-interactive peer is driving it — cloud or phone.</summary>
        Cloud,
        /// <summary>Nobody is running it: the connection dropped. Transcript intact on disk.</summary>
        Offline,
    }

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
            return await LocateSessionAsync(ids).ConfigureAwait(false) == SessionLocation.Local;
        }

        /// <summary>
        /// Where a handed-over conversation is running now, as far as this machine can tell.
        ///
        ///  - <see cref="SessionLocation.Local"/>: a process on THIS machine owns it (pid alive,
        ///    kind interactive) — the Remote Control terminal we spawned. The handover is active.
        ///  - <see cref="SessionLocation.Cloud"/>: the CLI registered the session with a live
        ///    non-interactive entry — a cloud or phone peer is driving it (the 2.1.229 `cloud`
        ///    label). No local pid owns it.
        ///  - <see cref="SessionLocation.Offline"/>: nobody is running it — the pid died and left
        ///    no other owner (the 2.1.229 `offline` label). The transcript is intact on disk.
        ///
        /// Derived, not read from a field: the local registry file carries only pid/kind/version,
        /// so `cloud` is inferred from a live entry whose kind is not `interactive`. Version-
        /// tolerant — an unknown kind on a live entry is treated as `cloud`, never as a failure.
        /// </summary>
        public async Task<SessionLocation> LocateSessionAsync(params string[] ids)
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids ?? Array.Empty<string>())
            {
                if (!string.IsNullOrEmpty(id)) wanted.Add(id);
            }
            if (wanted.Count == 0) return SessionLocation.Offline;

            var cloud = false;
            foreach (var session in await LiveSessionsAsync().ConfigureAwait(false))
            {
                if (!wanted.Contains(session.SessionId)) continue;
                if (!IsPidAlive(session.Pid)) continue;

                // A live entry is `cloud` ONLY when the CLI explicitly tags it with a kind other
                // than interactive. A missing/empty kind stays local: older CLIs and the default
                // headless case do not tag it, and reading "no kind" as cloud would misreport
                // every ordinary handover (and regress IsSessionLive, which pre-dates cloud).
                if (!string.IsNullOrEmpty(session.Kind) &&
                    !string.Equals(session.Kind, "interactive", StringComparison.Ordinal))
                {
                    cloud = true; // a live entry the CLI does not call interactive: driven elsewhere
                    continue;
                }

                return SessionLocation.Local;
            }

            return cloud ? SessionLocation.Cloud : SessionLocation.Offline;
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
