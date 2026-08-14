using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tootega.Cockpit.Session;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// The registry exists so the UI can say what is KNOWN rather than what was hoped for.
    /// The property under test is therefore not "does it read JSON" but "does a stale entry
    /// ever read as live" — assuming success is the bug this file prevents.
    /// </summary>
    public class SessionRegistryTests : IDisposable
    {
        private readonly string _dir;
        private readonly SessionRegistry _registry;

        public SessionRegistryTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cockpit-registry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _registry = new SessionRegistry(_dir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch
            {
            }
        }

        private void WriteEntry(string fileName, string json)
        {
            File.WriteAllText(Path.Combine(_dir, fileName), json, new UTF8Encoding(false));
        }

        /// <summary>A pid that is certainly running: this test process.</summary>
        private static int OwnPid => Process.GetCurrentProcess().Id;

        [Fact]
        public async Task MissingDirectoryMeansNothingIsRunning()
        {
            var registry = new SessionRegistry(Path.Combine(_dir, "not-created"));

            Assert.Empty(await registry.LiveSessionsAsync());
            Assert.False(await registry.IsSessionLiveAsync("any"));
        }

        [Fact]
        public async Task ReadsRegisteredSessions()
        {
            WriteEntry("1234.json",
                "{\"pid\":1234,\"sessionId\":\"abc\",\"cwd\":\"D:/work\",\"kind\":\"interactive\",\"version\":\"2.1.226\"}");

            var session = (await _registry.LiveSessionsAsync()).Single();

            Assert.Equal(1234, session.Pid);
            Assert.Equal("abc", session.SessionId);
            Assert.Equal("interactive", session.Kind);
            Assert.Equal("2.1.226", session.Version);
        }

        [Fact]
        public async Task SkipsEntriesThatAreNotUsableYet()
        {
            // A file mid-write, or one whose shape we do not understand, is skipped rather
            // than guessed at.
            WriteEntry("bad.json", "{ half written");
            WriteEntry("no-pid.json", "{\"sessionId\":\"abc\"}");
            WriteEntry("no-session.json", "{\"pid\":42}");
            WriteEntry("zero-pid.json", "{\"pid\":0,\"sessionId\":\"abc\"}");
            WriteEntry("ignored.txt", "{\"pid\":1,\"sessionId\":\"abc\"}");

            Assert.Empty(await _registry.LiveSessionsAsync());
        }

        [Fact]
        public async Task KeepsUnknownFieldsFromBreakingTheRead()
        {
            // Version tolerance: a CLI release adding fields must not blind us to the entry.
            WriteEntry("1.json", "{\"pid\":1,\"sessionId\":\"abc\",\"brandNewField\":{\"nested\":true}}");

            Assert.Single(await _registry.LiveSessionsAsync());
        }

        [Fact]
        public async Task ALiveProcessMakesTheSessionLive()
        {
            WriteEntry("own.json", "{\"pid\":" + OwnPid + ",\"sessionId\":\"mine\"}");

            Assert.True(await _registry.IsSessionLiveAsync("mine"));
        }

        [Fact]
        public async Task AStaleEntryDoesNotCountAsLive()
        {
            // A process killed hard leaves its file behind. Trusting the file alone is
            // exactly how a dead session reads as connected.
            WriteEntry("dead.json", "{\"pid\":999999,\"sessionId\":\"ghost\"}");

            Assert.False(await _registry.IsSessionLiveAsync("ghost"));
        }

        [Fact]
        public async Task MatchesAnyOfTheKnownIdsForOneConversation()
        {
            // sessionId and the resume id diverge after a resume, and the caller may only
            // know one of them.
            WriteEntry("own.json", "{\"pid\":" + OwnPid + ",\"sessionId\":\"resumed-id\"}");

            Assert.True(await _registry.IsSessionLiveAsync("original-id", "resumed-id"));
        }

        [Fact]
        public async Task NoIdsMeansNotLive()
        {
            WriteEntry("own.json", "{\"pid\":" + OwnPid + ",\"sessionId\":\"mine\"}");

            Assert.False(await _registry.IsSessionLiveAsync());
            Assert.False(await _registry.IsSessionLiveAsync(null, string.Empty));
        }

        [Fact]
        public async Task DoesNotMatchAnUnrelatedSession()
        {
            WriteEntry("own.json", "{\"pid\":" + OwnPid + ",\"sessionId\":\"someone-else\"}");

            Assert.False(await _registry.IsSessionLiveAsync("mine"));
        }

        [Fact]
        public async Task LocatesALiveInteractiveSessionAsLocal()
        {
            // The interactive terminal we handed the session to: a live pid with kind interactive.
            WriteEntry("own.json",
                "{\"pid\":" + OwnPid + ",\"sessionId\":\"mine\",\"kind\":\"interactive\"}");

            Assert.Equal(SessionLocation.Local, await _registry.LocateSessionAsync("mine"));
        }

        [Fact]
        public async Task LocatesALiveNonInteractiveSessionAsCloud()
        {
            // A live entry the CLI does not call interactive: a cloud or phone peer is driving it.
            WriteEntry("own.json",
                "{\"pid\":" + OwnPid + ",\"sessionId\":\"mine\",\"kind\":\"cloud\"}");

            Assert.Equal(SessionLocation.Cloud, await _registry.LocateSessionAsync("mine"));
        }

        [Fact]
        public async Task LocatesAStaleSessionAsOffline()
        {
            // The pid died and left no other owner: the connection dropped.
            WriteEntry("dead.json",
                "{\"pid\":999999,\"sessionId\":\"ghost\",\"kind\":\"interactive\"}");

            Assert.Equal(SessionLocation.Offline, await _registry.LocateSessionAsync("ghost"));
        }

        [Fact]
        public async Task LocatesAnUnknownSessionAsOffline()
        {
            Assert.Equal(SessionLocation.Offline, await _registry.LocateSessionAsync("nobody"));
        }

        [Fact]
        public async Task PrefersLocalOverCloudWhenBothArePresent()
        {
            // A conversation can show up as both a cloud peer and our local terminal (e.g. right
            // after taking it back). Owning it locally is the stronger, actionable truth.
            WriteEntry("cloud.json",
                "{\"pid\":" + OwnPid + ",\"sessionId\":\"mine\",\"kind\":\"cloud\"}");
            WriteEntry("local.json",
                "{\"pid\":" + OwnPid + ",\"sessionId\":\"mine\",\"kind\":\"interactive\"}");

            Assert.Equal(SessionLocation.Local, await _registry.LocateSessionAsync("mine"));
        }

        [Fact]
        public void RecognisesTheCurrentProcessAsAlive()
        {
            Assert.True(SessionRegistry.IsPidAlive(OwnPid));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(999999)]
        public void RejectsInvalidOrDeadPids(int pid)
        {
            Assert.False(SessionRegistry.IsPidAlive(pid));
        }
    }
}
