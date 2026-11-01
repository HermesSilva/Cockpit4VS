using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Session;
using Tootega.Cockpit.Stats;
using Xunit;
using Xunit.Abstractions;
using CockpitSession = Tootega.Cockpit.Session.Session;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// The context guard end to end: this port's real Session driving the real Claude CLI.
    ///
    /// The window is shrunk in the statistics so 80% is crossed mid-turn, right after the model
    /// reads a large file — the guard must ask for the save inside that turn, accept only the
    /// dictated confirmation, compact (proved by the boundary) and resume the task to the end.
    ///
    /// It spends real tokens (Haiku, a few turns), so it only runs when
    /// COCKPIT_LIVE_GUARD=1. Skipped, not failed, otherwise.
    /// </summary>
    public class ContextGuardLiveTests
    {
        private readonly ITestOutputHelper _out;
        public ContextGuardLiveTests(ITestOutputHelper output) { _out = output; }

        [SkippableFact]
        public void SavesConfirmsCompactsAndResumesMidTurn()
        {
            Skip.IfNot(Environment.GetEnvironmentVariable("COCKPIT_LIVE_GUARD") == "1",
                "Set COCKPIT_LIVE_GUARD=1 to run (spends real tokens).");

            var root = Path.Combine(Path.GetTempPath(), "cockpit-guard-live-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var big = Environment.GetEnvironmentVariable("COCKPIT_LIVE_GUARD_BIGFILE");
            Skip.If(string.IsNullOrEmpty(big) || !File.Exists(big), "COCKPIT_LIVE_GUARD_BIGFILE must point at a ~50 KB text file.");
            File.Copy(big, Path.Combine(root, "big.txt"));

            var phases = new List<string>();
            var texts = new Dictionary<string, string>();
            var gate = new object();

            var hooks = new SessionHooks
            {
                Emit = m =>
                {
                    using (var doc = JsonDocument.Parse(m.ToJson()))
                    {
                        var e = doc.RootElement;
                        var kind = e.GetProperty("kind").GetString();
                        lock (gate)
                        {
                            if (kind == "contextGuard")
                            {
                                var phase = e.GetProperty("phase").GetString();
                                phases.Add(phase);
                                _out.WriteLine("GUARD " + e.GetRawText());
                            }
                            else if (kind == "assistantText")
                            {
                                var id = e.GetProperty("id").GetString();
                                texts.TryGetValue(id, out var t);
                                texts[id] = t + e.GetProperty("delta").GetString();
                            }
                            else if (kind == "compaction" && e.TryGetProperty("pre", out _))
                            {
                                _out.WriteLine("COMPACTION " + e.GetRawText());
                            }
                        }
                    }
                },
                OnBusy = b => { },
                Cwd = () => root,
                ClaudePath = _ => "claude",
                Settings = () => new SessionDefaults { Model = "haiku", Effort = "default", Permission = "bypassPermissions" },
                ContextGuard = () => new GuardConfig { Enabled = true, Threshold = 0.8 },
            };
            var session = new CockpitSession(hooks, new StatsStore(Path.Combine(root, "stats")),
                new Tootega.Cockpit.Cli.SkillBodyIndex(Path.Combine(root, "claude")));
            session.Stats.SetContextLimit(45000);

            session.Send(
                "Do these steps in order, one tool call each:\n" +
                "(1) Read the file big.txt in full and count how many lines contain the word \"function\". " +
                "Append \"STEP1 <count>\" to progress.txt.\n" +
                "(2) Bash: sleep 3 && echo STEP2 >> progress.txt\n" +
                "(3) Bash: sleep 3 && echo STEP3 >> progress.txt\n" +
                "Do not read big.txt again after step 1. When all steps are done, reply \"ALL DONE\".");

            var deadline = DateTime.UtcNow.AddMinutes(8);
            bool Finished()
            {
                lock (gate)
                {
                    if (phases.Contains("failed") || phases.Contains("canceled")) return true;
                    return phases.Contains("done") && texts.Values.Any(v => v.Contains("ALL DONE")) && !session.Busy;
                }
            }
            while (!Finished() && DateTime.UtcNow < deadline) Thread.Sleep(500);
            Thread.Sleep(1000);
            session.Stop();

            var progress = File.Exists(Path.Combine(root, "progress.txt")) ? File.ReadAllText(Path.Combine(root, "progress.txt")) : string.Empty;
            _out.WriteLine("phases: " + string.Join(" > ", phases));
            _out.WriteLine("progress.txt: " + progress.Replace("\n", " | "));

            Assert.Equal(new[] { "saving", "compacting", "done" }, phases.ToArray());
            Assert.Contains("STEP1", progress);
            Assert.Contains("STEP2", progress);
            Assert.Contains("STEP3", progress);
        }
    }
}
