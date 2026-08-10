using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Tootega.Cockpit.Stats;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    /// <summary>What renewing an OPEN context through its live session achieved.</summary>
    internal enum OpenPingResult
    {
        /// <summary>A turn is already running, which keeps the prefix warm on its own.</summary>
        Busy,
        /// <summary>Sent through the session's existing process.</summary>
        Pinged,
        /// <summary>No session is open, so the keeper must spawn an ephemeral one.</summary>
        None,
    }

    /// <summary>
    /// Prompt-cache keep-alive. Port of src/cli/CacheKeeper.ts.
    ///
    /// For each context with the checkbox ticked, it re-sends a minimal request before the 1h
    /// window expires, so the cached prefix does not die and the next real use does not pay for
    /// the cache write again. It works with the tab CLOSED: the persisted stats files carry the
    /// session id and the folder, which is enough to resume.
    ///
    /// The rule that shapes everything here: NEVER revive an already expired cache. Once the
    /// hour has passed the prefix is gone, and re-sending would only re-cache for nothing while
    /// spending tokens. So the keeper renews what is still alive and lets the rest go.
    /// </summary>
    internal sealed class CacheKeeper : IDisposable
    {
        private const int TickMs = 60_000;
        /// <summary>Renew when under five minutes remain, i.e. around the 55-minute mark.</summary>
        private const long RefreshLeadMs = 5 * 60_000L;
        private const int PingTimeoutMs = 90_000;

        /// <summary>
        /// The minimal prompt. It only has to hit the cached prefix to restart the TTL; the
        /// explicit instruction keeps it from doing anything else. Note there is no
        /// --permission-prompt-tool: in headless the CLI denies tools by itself instead of
        /// hanging for an approval nobody will give, which is what used to blow the timeout in
        /// plan mode.
        /// </summary>
        private const string PingPrompt =
            "keep-alive: answer only \"ok\". Do not use tools and do not change files.";

        private readonly StatsStore _stats;
        private readonly Func<string> _claudePath;
        private readonly Func<string, OpenPingResult> _pingOpen;

        /// <summary>Sessions with an ephemeral spawn in progress.</summary>
        private readonly HashSet<string> _inFlight = new HashSet<string>(StringComparer.Ordinal);

        private Timer _timer;
        private bool _disposed;

        /// <param name="pingOpen">
        /// Renews an OPEN context through its live process. Going through the session avoids a
        /// parallel --resume on the same conversation, which would conflict on disk.
        /// </param>
        public CacheKeeper(StatsStore stats, Func<string> claudePath, Func<string, OpenPingResult> pingOpen)
        {
            _stats = stats ?? throw new ArgumentNullException(nameof(stats));
            _claudePath = claudePath ?? throw new ArgumentNullException(nameof(claudePath));
            _pingOpen = pingOpen ?? throw new ArgumentNullException(nameof(pingOpen));
        }

        /// <summary>Starts the periodic sweep. Idempotent.</summary>
        public void Start()
        {
            if (_timer != null || _disposed) return;

            Log.Debug("CacheKeeper started (tick " + Minutes(TickMs) + ", lead " + Minutes(RefreshLeadMs) + ")");
            // Fires immediately as well as periodically, which is what covers reopening VS
            // after being away for a while.
            _timer = new Timer(_ => SafeTick(), null, 0, TickMs);
        }

        public void Stop()
        {
            var timer = _timer;
            _timer = null;
            timer?.Dispose();
        }

        private void SafeTick()
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                // A timer callback that throws would take the process down.
                Log.Error("CacheKeeper tick failed", ex);
            }
        }

        /// <summary>One sweep: renews the ticked contexts that are close to expiring.</summary>
        internal void Tick()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var all = _stats.LoadAll();

            var marked = new List<PersistedStats>();
            foreach (var stats in all)
            {
                if (stats.KeepCacheAlive == true) marked.Add(stats);
            }

            Log.Debug("cache tick: " + all.Count + " contexts, " + marked.Count + " marked keep-alive");

            foreach (var stats in marked)
            {
                var id = stats.SessionId;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(stats.Cwd))
                {
                    Log.Debug("cache skip " + (id ?? "?") + ": no " + (string.IsNullOrEmpty(id) ? "sessionId" : "cwd"));
                    continue;
                }

                lock (_inFlight)
                {
                    if (_inFlight.Contains(id))
                    {
                        Log.Debug("cache skip " + id + ": ping in progress");
                        continue;
                    }
                }

                if (stats.LastTurnTs <= 0)
                {
                    Log.Debug("cache skip " + id + ": no turn yet, nothing to keep alive");
                    continue;
                }

                var expiresIn = stats.LastTurnTs + CostModel.CacheLifeMs - now;
                if (expiresIn <= 0)
                {
                    // The prefix is already gone; reviving it would spend tokens for nothing.
                    Log.Debug("cache skip " + id + ": EXPIRED " + Minutes(-expiresIn) + " ago, not revived");
                    continue;
                }

                if (expiresIn > RefreshLeadMs)
                {
                    Log.Debug("cache skip " + id + ": healthy (expires in " + Minutes(expiresIn) + ")");
                    continue;
                }

                // Cross-instance critical section: several VS windows sweep the same folder, and
                // without this two of them ping the same session on the same tick.
                if (!_stats.AcquireKeepAliveLock(id))
                {
                    Log.Debug("cache skip " + id + ": another instance holds the lock");
                    continue;
                }

                try
                {
                    // Re-read fresh: another instance may have renewed between the sweep and
                    // the lock. The timestamp on disk is the real "already renewed" signal.
                    var fresh = _stats.Load(id)?.LastTurnTs ?? stats.LastTurnTs;
                    var freshExpiresIn = fresh + CostModel.CacheLifeMs - now;
                    if (freshExpiresIn > RefreshLeadMs)
                    {
                        Log.Debug("cache skip " + id + ": already renewed elsewhere (expires in " +
                                  Minutes(freshExpiresIn) + ")");
                        continue;
                    }

                    var open = _pingOpen(id);
                    if (open == OpenPingResult.Busy)
                    {
                        // A real turn in progress will bump the timestamp itself.
                        Log.Debug("cache skip " + id + ": tab busy, an active turn keeps it warm");
                        continue;
                    }

                    // Bump on disk right away, restarting the window at the instant of the ping.
                    // Every instance then reads roughly an hour and backs off. If the ping fails
                    // the cache expires slightly early and is re-cached on next use — acceptable,
                    // and far better than several instances pinging at once.
                    _stats.BumpCacheActivity(id, now);

                    if (open == OpenPingResult.Pinged)
                    {
                        Log.Debug("cache due " + id + ": expires in " + Minutes(expiresIn) +
                                  ", pinged through the open session");
                        continue;
                    }

                    Log.Debug("cache due " + id + ": expires in " + Minutes(expiresIn) +
                              ", ephemeral spawn (closed context)");
                    Refresh(id, stats.Cwd);
                }
                finally
                {
                    _stats.ReleaseKeepAliveLock(id);
                }
            }
        }

        /// <summary>Resumes the context as a headless one-shot and restarts the cache life on success.</summary>
        private void Refresh(string sessionId, string cwd)
        {
            lock (_inFlight)
            {
                if (!_inFlight.Add(sessionId)) return;
            }

            var args = new[]
            {
                "--resume", sessionId,
                "-p", PingPrompt,
                // A one-shot finishes on its own; there is no interactive stream to read.
                "--output-format", "json",
            };

            Process process;
            try
            {
                var info = ProcessLauncher.Build(_claudePath(), args, cwd);
                // stdout is not needed; stderr is kept for diagnostics.
                info.RedirectStandardInput = false;
                info.RedirectStandardOutput = false;

                process = new Process { StartInfo = info, EnableRaisingEvents = true };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) Log.Debug("keep-alive stderr " + sessionId + ": " + e.Data.Trim());
                };

                process.Exited += (s, e) => OnPingExited(process, sessionId);

                if (!process.Start())
                {
                    Log.Error("cache keep-alive: process did not start for " + sessionId);
                    Release(sessionId);
                    return;
                }

                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Log.Error("cache keep-alive spawn failed (" + sessionId + ")", ex);
                Release(sessionId);
                return;
            }

            // A wedged one-shot must not hold the slot forever.
            var killer = new Timer(_ =>
            {
                try
                {
                    ProcessLauncher.KillTree(process);
                }
                catch
                {
                    // Already gone.
                }
            }, null, PingTimeoutMs, Timeout.Infinite);

            process.Exited += (s, e) => killer.Dispose();
        }

        private void OnPingExited(Process process, string sessionId)
        {
            Release(sessionId);

            int code;
            try
            {
                code = process.ExitCode;
            }
            catch
            {
                return;
            }

            if (code == 0)
            {
                _stats.BumpCacheActivity(sessionId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Log.Debug("keep-alive OK " + sessionId + ", 1h window restarted");
            }
            else
            {
                Log.Info("cache keep-alive: " + sessionId + " failed (exit " + code + ")");
            }
        }

        private void Release(string sessionId)
        {
            lock (_inFlight) _inFlight.Remove(sessionId);
        }

        private static string Minutes(long ms)
        {
            return (ms / 60000.0).ToString("F1") + "m";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
