using System;
using System.IO;
using System.Text;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Host;
using Tootega.Cockpit.Session;
using Tootega.Cockpit.Stats;
using Tootega.Cockpit.Util;
using Xunit;
using CockpitSession = Tootega.Cockpit.Session.Session;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// The folder belongs to the tab, not to the window.
    ///
    /// This is the rule that keeps two conversations side by side from contaminating each
    /// other, and the failures it prevents are the expensive kind: a conversation resumed
    /// against the wrong transcript, a mass delete reaching into a folder the user was not
    /// looking at, an agent started in a directory nobody chose. Each of those is silent until
    /// it has already happened, so they are pinned here.
    /// </summary>
    public class TabFolderTests : IDisposable
    {
        private readonly string _root;
        private readonly string _folderA;
        private readonly string _folderB;

        public TabFolderTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "cockpit-tabs-" + Guid.NewGuid().ToString("N"));
            _folderA = Path.Combine(_root, "project-a");
            _folderB = Path.Combine(_root, "project-b");

            Directory.CreateDirectory(_folderA);
            Directory.CreateDirectory(_folderB);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
                // A leftover temp folder is not worth failing a test over.
            }
        }

        // ---- TabRegistry ----

        /// <summary>
        /// Builds a registry whose sessions record the folder they were told to run in, so the
        /// test can assert what the CLI would actually have been started with.
        /// </summary>
        private TabRegistry NewRegistry(out Func<string, string> cwdSeenBySession)
        {
            var stats = new StatsStore(Path.Combine(_root, "stats"));
            var skills = new SkillBodyIndex(Path.Combine(_root, "claude"));
            var hooksByTab = new System.Collections.Generic.Dictionary<string, SessionHooks>(StringComparer.Ordinal);

            TabRegistry registry = null;

            CockpitSession Create(string tabId)
            {
                var hooks = new SessionHooks
                {
                    Emit = _ => { },
                    // Read through the registry, exactly as the host wires it.
                    Cwd = () => registry.Cwd(tabId),
                    ClaudePath = _ => "claude",
                    Settings = () => new SessionDefaults
                    {
                        Model = "default",
                        Effort = "default",
                        Permission = "default",
                    },
                };

                hooksByTab[tabId] = hooks;
                return new CockpitSession(hooks, stats, skills);
            }

            registry = new TabRegistry(Create, () => _folderA);
            cwdSeenBySession = tabId => hooksByTab[tabId].Cwd();
            return registry;
        }

        [Fact]
        public void EachTabKeepsItsOwnFolder()
        {
            using (var tabs = NewRegistry(out var sessionCwd))
            {
                var a = tabs.CreateTab();
                var b = tabs.CreateTab(_folderB);

                Assert.Equal(_folderA, tabs.Cwd(a));
                Assert.Equal(_folderB, tabs.Cwd(b));

                // What the session — and therefore the CLI — actually sees.
                Assert.Equal(_folderA, sessionCwd(a));
                Assert.Equal(_folderB, sessionCwd(b));
            }
        }

        [Fact]
        public void DroppingATabRemovesItEvenWhenItIsTheLastOne()
        {
            using (var tabs = NewRegistry(out _))
            {
                var only = tabs.CreateTab(_folderA);

                // A conversation IS its window here, so closing that window leaves nothing to
                // keep an empty tab for — the transcript is still on disk either way.
                Assert.True(tabs.Drop(only));

                Assert.Equal(0, tabs.Count);
                Assert.False(tabs.Has(only));
                Assert.Empty(tabs.Snapshot());
            }
        }

        [Fact]
        public void DroppingTheActiveTabMovesTheActiveMarkerToAnotherOne()
        {
            using (var tabs = NewRegistry(out _))
            {
                var first = tabs.CreateTab(_folderA);
                var second = tabs.CreateTab(_folderB);

                Assert.Equal(second, tabs.ActiveTab);
                tabs.Drop(second);

                // Something has to be active for the hub to have anything to show.
                Assert.Equal(first, tabs.ActiveTab);
            }
        }

        [Fact]
        public void SwitchingToTheAlreadyActiveTabAnnouncesNothing()
        {
            using (var tabs = NewRegistry(out _))
            {
                var tabId = tabs.CreateTab(_folderA);

                var changes = 0;
                tabs.Changed += (s, e) => changes++;

                // Every message a window sends re-asserts its tab as active; announcing each
                // one would repaint the hub on every heartbeat.
                Assert.True(tabs.SetActive(tabId));
                Assert.Equal(0, changes);
            }
        }

        [Fact]
        public void ANewTabWithNoFolderFallsBackToTheWindows()
        {
            using (var tabs = NewRegistry(out _))
            {
                Assert.Equal(_folderA, tabs.Cwd(tabs.CreateTab()));
            }
        }

        [Fact]
        public void AFolderThatDoesNotExistFallsBackRatherThanFailing()
        {
            using (var tabs = NewRegistry(out _))
            {
                // Refusing to open a tab because a folder was renamed would be worse than
                // starting somewhere real.
                var tabId = tabs.CreateTab(Path.Combine(_root, "was-renamed"));
                Assert.Equal(_folderA, tabs.Cwd(tabId));
            }
        }

        [Fact]
        public void MovingATabClearsItsConversationAndTitle()
        {
            using (var tabs = NewRegistry(out var sessionCwd))
            {
                var tabId = tabs.CreateTab(_folderA);
                tabs.SetTitle(tabId, "Renaming the parser");
                tabs.SessionFor(tabId).Resume("session-in-a");

                Assert.True(tabs.SetCwd(tabId, _folderB));

                Assert.Equal(_folderB, tabs.Cwd(tabId));
                Assert.Equal(_folderB, sessionCwd(tabId));

                // The transcript and the title described a conversation that lives in the
                // folder the tab just left.
                Assert.Null(tabs.SessionFor(tabId).ResumeId);
                Assert.Null(tabs.SessionFor(tabId).SessionId);
                Assert.Null(tabs.Title(tabId));
            }
        }

        [Fact]
        public void MovingATabToWhereItAlreadyIsChangesNothing()
        {
            using (var tabs = NewRegistry(out _))
            {
                var tabId = tabs.CreateTab(_folderB);
                tabs.SetTitle(tabId, "Still here");

                // Reported as "no change" so the host does not wipe a live conversation over a
                // click that meant nothing.
                Assert.False(tabs.SetCwd(tabId, _folderB));
                Assert.Equal("Still here", tabs.Title(tabId));
            }
        }

        [Fact]
        public void TheTabListCarriesTheFolderOfEachTab()
        {
            using (var tabs = NewRegistry(out _))
            {
                var a = tabs.CreateTab(_folderA);
                var b = tabs.CreateTab(_folderB);

                var snapshot = tabs.Snapshot();

                Assert.Equal(_folderA, snapshot.Find(t => t.Id == a).Cwd);
                Assert.Equal(_folderB, snapshot.Find(t => t.Id == b).Cwd);
            }
        }

        [Fact]
        public void FoldersListsEachFolderOnceInTabOrder()
        {
            using (var tabs = NewRegistry(out _))
            {
                tabs.CreateTab(_folderB);
                tabs.CreateTab(_folderA);
                tabs.CreateTab(_folderB);

                Assert.Equal(new[] { _folderB, _folderA }, tabs.Folders());
            }
        }

        // ---- SessionLibrary ----

        private SessionStore _store;

        private SessionLibrary NewLibrary()
        {
            _store = new SessionStore(Path.Combine(_root, "projects"));
            return new SessionLibrary(_store, new StateStore(Path.Combine(_root, "state")));
        }

        private void WriteConversation(string cwd, string sessionId, string prompt)
        {
            var dir = _store.ProjectDirectory(cwd);
            Directory.CreateDirectory(dir);

            var line = "{\"type\":\"user\",\"uuid\":\"u1\",\"message\":{\"role\":\"user\"," +
                       "\"content\":[{\"type\":\"text\",\"text\":" +
                       System.Text.Json.JsonSerializer.Serialize(prompt) + "}]}}";

            File.WriteAllText(Path.Combine(dir, sessionId + ".jsonl"), line + "\n", new UTF8Encoding(false));
        }

        [Fact]
        public void EachFolderListsOnlyItsOwnConversations()
        {
            var library = NewLibrary();

            WriteConversation(_folderA, "aaa", "in project a");
            WriteConversation(_folderB, "bbb", "in project b");

            Assert.Equal(new[] { "aaa" }, System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Select(library.List(_folderA), s => s.Id)));

            Assert.Equal(new[] { "bbb" }, System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Select(library.List(_folderB), s => s.Id)));
        }

        [Fact]
        public void DeletingEveryConversationOfOneFolderLeavesTheOtherFolderAlone()
        {
            var library = NewLibrary();

            WriteConversation(_folderA, "aaa", "in project a");
            WriteConversation(_folderB, "bbb", "in project b");
            library.Rename("bbb", "The other project");

            Assert.Equal(1, library.DeleteAll(_folderA));

            Assert.Empty(library.List(_folderA));

            // The other folder keeps both its conversation and the name the user gave it: a
            // mass delete is scoped to one folder, including the renames it forgets.
            var remaining = library.List(_folderB);
            Assert.Single(remaining);
            Assert.Equal("The other project", remaining[0].Title);
        }
    }
}
