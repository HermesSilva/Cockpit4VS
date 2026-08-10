using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tootega.Cockpit.Session;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// The transcript format belongs to the CLI, not to us. These tests work against
    /// fixtures written to a temp folder rather than the developer's real conversations, and
    /// they pin the behaviours that matter when reading someone else's file: every field is
    /// optional, a corrupt line is skipped, and a rewind either cuts cleanly or not at all.
    /// </summary>
    public class SessionStoreTests : IDisposable
    {
        private readonly string _root;
        private readonly SessionStore _store;
        private const string Cwd = @"D:\work\project";

        public SessionStoreTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "cockpit-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _store = new SessionStore(_root);
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

        private void WriteTranscript(string sessionId, params string[] lines)
        {
            var dir = _store.ProjectDirectory(Cwd);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, sessionId + ".jsonl"),
                string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        }

        private static string UserLine(string text, string uuid = null, bool isMeta = false)
        {
            var meta = isMeta ? ",\"isMeta\":true" : string.Empty;
            var id = uuid != null ? ",\"uuid\":\"" + uuid + "\"" : string.Empty;
            return "{\"type\":\"user\"" + id + meta +
                   ",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":" +
                   System.Text.Json.JsonSerializer.Serialize(text) + "}]}}";
        }

        // --- Folder naming ---

        [Theory]
        [InlineData(@"D:\work\project", "D--work-project")]
        [InlineData("/home/user/project", "-home-user-project")]
        [InlineData(@"C:\a", "C--a")]
        [InlineData("", "")]
        public void EncodesCwdTheWayTheCliNamesFolders(string cwd, string expected)
        {
            // This is the CLI's convention, and getting it wrong means finding no sessions
            // at all — which looks like "there are none" rather than "we looked in the
            // wrong place".
            Assert.Equal(expected, SessionStore.EncodeCwd(cwd));
        }

        // --- Listing ---

        [Fact]
        public void ReturnsNothingWhenTheProjectHasNoFolder()
        {
            Assert.Empty(_store.ListSessions(Cwd));
            Assert.Null(_store.LatestSessionId(Cwd));
        }

        [Fact]
        public void PrefersTheAiGeneratedTitle()
        {
            // The same title the /resume picker shows; the latest one wins.
            WriteTranscript("s1",
                UserLine("please refactor the parser"),
                "{\"type\":\"ai-title\",\"aiTitle\":\"First guess\"}",
                "{\"type\":\"ai-title\",\"aiTitle\":\"Refactor the stream parser\"}");

            var session = _store.ListSessions(Cwd).Single();

            Assert.Equal("Refactor the stream parser", session.Title);
        }

        [Fact]
        public void FallsBackToTheFirstUserMessage()
        {
            WriteTranscript("s1", UserLine("fix the login bug"));

            Assert.Equal("fix the login bug", _store.ListSessions(Cwd).Single().Title);
        }

        [Fact]
        public void IgnoresInjectedEntriesWhenChoosingATitle()
        {
            // Command wrappers and system reminders are the CLI talking to itself; a title
            // of "<system-reminder>" would be nonsense.
            WriteTranscript("s1",
                UserLine("<command-name>init</command-name>"),
                UserLine("<system-reminder>be careful</system-reminder>"),
                UserLine("the real question"));

            Assert.Equal("the real question", _store.ListSessions(Cwd).Single().Title);
        }

        [Fact]
        public void CountsMessagesToolsAndModel()
        {
            WriteTranscript("s1",
                UserLine("hello"),
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"model\":\"claude-opus-5\"," +
                "\"content\":[{\"type\":\"text\",\"text\":\"hi\"}," +
                "{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Read\",\"input\":{}}," +
                "{\"type\":\"tool_use\",\"id\":\"t2\",\"name\":\"Edit\",\"input\":{}}]}}",
                UserLine("thanks"));

            var session = _store.ListSessions(Cwd).Single();

            Assert.Equal(2, session.UserCount);
            Assert.Equal(1, session.AssistantCount);
            Assert.Equal(2, session.ToolCount);
            Assert.Equal("claude-opus-5", session.Model);
            Assert.Equal(3, session.MessageCount);
        }

        [Fact]
        public void MetaMessagesDoNotCountAsConversation()
        {
            WriteTranscript("s1", UserLine("real", isMeta: false), UserLine("bookkeeping", isMeta: true));

            var session = _store.ListSessions(Cwd).Single();

            Assert.Equal(1, session.UserCount);
            Assert.Equal(1, session.MessageCount);
        }

        [Fact]
        public void SkipsCorruptLinesWithoutLosingTheRest()
        {
            // A half-written last line is normal while the CLI is running.
            WriteTranscript("s1",
                "this is not json",
                UserLine("still readable"),
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assist");

            var session = _store.ListSessions(Cwd).Single();

            Assert.Equal("still readable", session.Title);
            Assert.Equal(1, session.UserCount);
        }

        [Fact]
        public void ReadsModelFromSystemInitWhenNoAssistantYet()
        {
            WriteTranscript("s1",
                "{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-sonnet-5\"}",
                UserLine("hi"));

            Assert.Equal("claude-sonnet-5", _store.ListSessions(Cwd).Single().Model);
        }

        [Fact]
        public void SortsMostRecentFirstAndHonoursTheLimit()
        {
            WriteTranscript("old", UserLine("old one"));
            System.Threading.Thread.Sleep(20);
            WriteTranscript("recent", UserLine("recent one"));

            var all = _store.ListSessions(Cwd);
            Assert.Equal("recent", all[0].Id);

            Assert.Single(_store.ListSessions(Cwd, 1));
        }

        [Fact]
        public void FindsTheLatestSessionId()
        {
            WriteTranscript("first", UserLine("a"));
            System.Threading.Thread.Sleep(20);
            WriteTranscript("second", UserLine("b"));

            Assert.Equal("second", _store.LatestSessionId(Cwd));
        }

        // --- Titles ---

        [Fact]
        public void CutsTitleAtTheFirstSentence()
        {
            Assert.Equal("Fix the bug.", SessionStore.TruncateTitle("Fix the bug. Then run the tests."));
        }

        [Fact]
        public void CapsLongTitlesOnAWordBoundary()
        {
            // Half a word plus an ellipsis reads as corruption, not as truncation.
            var title = SessionStore.TruncateTitle(new string('x', 20) + " " + new string('y', 80));

            Assert.True(title.Length <= 61, title);
            Assert.EndsWith("…", title);
            Assert.DoesNotContain("yyyy", title);
        }

        [Fact]
        public void CleanRemovesInvisibleCharacters()
        {
            // Zero-width characters ride along in pasted prompts and would make two
            // identical-looking titles differ.
            Assert.Equal("hello world", SessionStore.Clean("hello\u200b \u200dworld\ufeff"));
        }

        [Theory]
        [InlineData("<command-name>x</command-name>", true)]
        [InlineData("<local-command-stdout>x", true)]
        [InlineData("<system-reminder>x", true)]
        [InlineData("<task-notification>x", true)]
        [InlineData("", true)]
        [InlineData("   ", true)]
        [InlineData("a real prompt", false)]
        [InlineData("mentions <system-reminder> midway", false)]
        public void RecognisesInjectedEntries(string text, bool isMeta)
        {
            Assert.Equal(isMeta, SessionStore.IsMetaUserText(text));
        }

        // --- History replay ---

        [Fact]
        public void ReplaysUserAssistantAndToolItems()
        {
            WriteTranscript("s1",
                UserLine("read the file", "u1"),
                "{\"type\":\"assistant\",\"message\":{\"id\":\"a1\",\"role\":\"assistant\"," +
                "\"content\":[{\"type\":\"thinking\",\"thinking\":\"hmm\"}," +
                "{\"type\":\"text\",\"text\":\"sure\"}," +
                "{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Read\",\"input\":{\"file_path\":\"a.cs\"}}]}}",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":" +
                "[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"file body\"}]}}");

            var items = _store.LoadTranscript(Cwd, "s1");

            var user = items.Single(i => i.Kind == "user");
            Assert.Equal("read the file", user.Text);
            Assert.Equal("u1", user.Id);

            var assistant = items.Single(i => i.Kind == "assistant");
            Assert.Equal("sure", assistant.Text);
            Assert.Equal("hmm", assistant.Thinking);

            var tool = items.Single(i => i.Kind == "tool");
            Assert.Equal("Read", tool.Name);
            Assert.Equal("file body", tool.Result.Value.GetString());
        }

        [Fact]
        public void MergesAssistantFragmentsSharingAnId()
        {
            // One logical message can span several transcript lines; the bubble must be one
            // piece of text, not a run of fragments.
            WriteTranscript("s1",
                "{\"type\":\"assistant\",\"message\":{\"id\":\"a1\",\"role\":\"assistant\"," +
                "\"content\":[{\"type\":\"text\",\"text\":\"Hello \"}]}}",
                "{\"type\":\"assistant\",\"message\":{\"id\":\"a1\",\"role\":\"assistant\"," +
                "\"content\":[{\"type\":\"text\",\"text\":\"world\"}]}}");

            var assistant = _store.LoadTranscript(Cwd, "s1").Single(i => i.Kind == "assistant");

            Assert.Equal("Hello world", assistant.Text);
        }

        [Fact]
        public void MarksToolErrors()
        {
            WriteTranscript("s1",
                "{\"type\":\"assistant\",\"message\":{\"id\":\"a1\",\"role\":\"assistant\"," +
                "\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Bash\",\"input\":{}}]}}",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":" +
                "[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"boom\",\"is_error\":true}]}}");

            Assert.True(_store.LoadTranscript(Cwd, "s1").Single(i => i.Kind == "tool").IsError);
        }

        [Fact]
        public void RebuildsPastedImagesAsDataUris()
        {
            WriteTranscript("s1",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[" +
                "{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/jpeg\",\"data\":\"QUJD\"}}]}}");

            var user = _store.LoadTranscript(Cwd, "s1").Single();

            Assert.Equal("data:image/jpeg;base64,QUJD", user.Images.Single());
            // An image-only message still belongs in the timeline even with no text.
            Assert.Equal(string.Empty, user.Text);
        }

        [Fact]
        public void KeepsLineBreaksInUserText()
        {
            // Clean() collapses whitespace, but that is for titles: a prompt's shape matters.
            WriteTranscript("s1", UserLine("line one\nline two"));

            Assert.Equal("line one\nline two", _store.LoadTranscript(Cwd, "s1").Single().Text);
        }

        [Fact]
        public void SkipsInjectedAndMetaLinesOnReplay()
        {
            WriteTranscript("s1",
                UserLine("<system-reminder>hidden</system-reminder>"),
                UserLine("bookkeeping", isMeta: true),
                UserLine("visible"));

            var items = _store.LoadTranscript(Cwd, "s1");

            Assert.Single(items);
            Assert.Equal("visible", items[0].Text);
        }

        [Fact]
        public void ReplayOfAMissingTranscriptIsEmpty()
        {
            Assert.Empty(_store.LoadTranscript(Cwd, "does-not-exist"));
        }

        // --- Rewind and delete ---

        [Fact]
        public void RewindDropsTheTargetLineAndEverythingAfter()
        {
            WriteTranscript("s1",
                UserLine("first", "u1"),
                UserLine("second", "u2"),
                UserLine("third", "u3"));

            Assert.True(_store.TruncateTranscriptAt(Cwd, "s1", "u2"));

            var items = _store.LoadTranscript(Cwd, "s1");
            Assert.Single(items);
            Assert.Equal("first", items[0].Text);
        }

        [Fact]
        public void RewindLeavesTheFileIntactWhenTheUuidIsUnknown()
        {
            // Cutting at a guess would destroy the conversation; refusing is the safe answer.
            WriteTranscript("s1", UserLine("first", "u1"));

            Assert.False(_store.TruncateTranscriptAt(Cwd, "s1", "nope"));
            Assert.Single(_store.LoadTranscript(Cwd, "s1"));
        }

        [Fact]
        public void RewindOnAMissingTranscriptFails()
        {
            Assert.False(_store.TruncateTranscriptAt(Cwd, "ghost", "u1"));
        }

        [Fact]
        public void DeletesOneSession()
        {
            WriteTranscript("s1", UserLine("bye"));

            Assert.True(_store.DeleteSession(Cwd, "s1"));
            Assert.Empty(_store.ListSessions(Cwd));
            Assert.False(_store.DeleteSession(Cwd, "s1"));
        }

        [Fact]
        public void DeletesEverySessionAndReportsTheCount()
        {
            WriteTranscript("s1", UserLine("a"));
            WriteTranscript("s2", UserLine("b"));

            Assert.Equal(2, _store.DeleteAllSessions(Cwd));
            Assert.Empty(_store.ListSessions(Cwd));
            Assert.Equal(0, _store.DeleteAllSessions(Cwd));
        }
    }
}
