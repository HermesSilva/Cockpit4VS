using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Secrets;
using Tootega.Cockpit.Spell;
using Tootega.Cockpit.Voice;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    public class VoiceAndVaultTests : IDisposable
    {
        private readonly string _root;

        public VoiceAndVaultTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "cockpit-voice-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
            }
        }

        // --- Dictation dictionary ---

        private static VoiceDict Dict(IEnumerable<string> terms = null,
                                      IEnumerable<VoiceReplacement> replacements = null)
        {
            return new VoiceDict
            {
                Terms = (terms ?? Enumerable.Empty<string>()).ToList(),
                Replacements = (replacements ?? Enumerable.Empty<VoiceReplacement>()).ToList(),
            };
        }

        [Fact]
        public void PersistsAndReloadsTheDictionary()
        {
            var dictionary = new VoiceDictionary(_root);

            dictionary.Save(new VoiceDict
            {
                Terms = new List<string> { "Tootega", "Cockpit" },
                Replacements = new List<VoiceReplacement> { new VoiceReplacement { From = "dase", To = "DASE" } },
                SpellWords = new List<string> { "VSIX" },
            });

            var loaded = new VoiceDictionary(_root).Load();

            Assert.Equal(new[] { "Tootega", "Cockpit" }, loaded.Terms);
            Assert.Equal("DASE", loaded.Replacements.Single().To);
            Assert.Equal(new[] { "VSIX" }, loaded.SpellWords);
        }

        [Fact]
        public void NormalisesOnSave()
        {
            var dictionary = new VoiceDictionary(_root);

            dictionary.Save(new VoiceDict
            {
                Terms = new List<string> { " Tootega ", "tootega", string.Empty, "Cockpit" },
                Replacements = new List<VoiceReplacement>
                {
                    new VoiceReplacement { From = "  ", To = "x" },   // no left side: matches everything
                    new VoiceReplacement { From = " a ", To = " b " },
                },
            });

            var loaded = new VoiceDictionary(_root).Load();

            // Case-insensitive dedupe keeping the first spelling seen.
            Assert.Equal(new[] { "Tootega", "Cockpit" }, loaded.Terms);
            Assert.Equal("a", loaded.Replacements.Single().From);
            Assert.Equal("b", loaded.Replacements.Single().To);
        }

        [Fact]
        public void AMissingDictionaryLoadsEmpty()
        {
            var loaded = new VoiceDictionary(Path.Combine(_root, "nothing")).Load();

            Assert.Empty(loaded.Terms);
            Assert.Empty(loaded.Replacements);
        }

        [Fact]
        public void MergesTheLegacyPerAccountDictionaries()
        {
            // Otherwise the user would have to retype their whole vocabulary after the
            // per-machine change.
            var legacy = Path.Combine(_root, "voice-dictionary");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "a.json"),
                "{\"terms\":[\"FromA\"],\"replacements\":[]}", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(legacy, "b.json"),
                "{\"terms\":[\"FromB\"],\"replacements\":[]}", new UTF8Encoding(false));

            var loaded = new VoiceDictionary(_root).Load();

            Assert.Contains("FromA", loaded.Terms);
            Assert.Contains("FromB", loaded.Terms);
        }

        [Fact]
        public void UserTermsComeBeforeHarvestedOnes()
        {
            // Order is priority, and the budget truncates the tail — so what the user curated
            // by hand is never dropped in favour of something automatic.
            var keyterms = VoiceDictionary.BuildKeyterms(Dict(new[] { "MyProduct" }), new[] { "React", "MyProduct" });

            Assert.StartsWith("MyProduct", keyterms);
            // Deduplicated across both sources.
            Assert.Equal(new[] { "MyProduct", "React" }, keyterms.Split(','));
        }

        [Fact]
        public void KeytermsRespectTheCharacterBudget()
        {
            var many = Enumerable.Range(0, 500).Select(i => "term" + i.ToString("D4")).ToList();

            var keyterms = VoiceDictionary.BuildKeyterms(Dict(many));

            Assert.True(keyterms.Length <= 2000, "keyterms was " + keyterms.Length + " chars");
            Assert.StartsWith("term0000", keyterms);
        }

        [Fact]
        public void ReplacesWholeWordsOnly()
        {
            // A rule for "dase" must not rewrite the middle of "database".
            var dictionary = Dict(replacements: new[] { new VoiceReplacement { From = "dase", To = "DASE" } });

            Assert.Equal("open DASE now", VoiceDictionary.ApplyReplacements("open dase now", dictionary));
            Assert.Equal("the database", VoiceDictionary.ApplyReplacements("the database", dictionary));
        }

        [Fact]
        public void ReplacementIsCaseInsensitiveButKeepsTheTargetCasing()
        {
            // The point is usually restoring a proper noun's spelling.
            var dictionary = Dict(replacements: new[] { new VoiceReplacement { From = "tootega", To = "Tootega" } });

            Assert.Equal("Tootega and Tootega",
                VoiceDictionary.ApplyReplacements("TOOTEGA and tootega", dictionary));
        }

        [Fact]
        public void ReplacementHandlesAccentedBoundaries()
        {
            var dictionary = Dict(replacements: new[] { new VoiceReplacement { From = "sessao", To = "sessão" } });

            Assert.Equal("a sessão terminou",
                VoiceDictionary.ApplyReplacements("a sessao terminou", dictionary));
        }

        [Fact]
        public void ARuleThatCannotCompileIsSkipped()
        {
            // A stray bracket in a user-typed rule must not fail the whole pass.
            var dictionary = Dict(replacements: new[]
            {
                new VoiceReplacement { From = "a(b", To = "x" },
                new VoiceReplacement { From = "ok", To = "fine" },
            });

            Assert.Equal("fine", VoiceDictionary.ApplyReplacements("ok", dictionary));
        }

        [Fact]
        public void ReplacementLiteralsAreNotTreatedAsSubstitutions()
        {
            // "$1" in the target would otherwise be read as a capture reference.
            var dictionary = Dict(replacements: new[] { new VoiceReplacement { From = "price", To = "$1 each" } });

            Assert.Equal("$1 each", VoiceDictionary.ApplyReplacements("price", dictionary));
        }

        [Fact]
        public void CorrectorHintsCoverTermsAndReplacements()
        {
            // Without them a corrector asked to fix spelling will "fix" a product name into a
            // real word — exactly what the dictionary exists to prevent.
            var hints = VoiceDictionary.CorrectorHints(Dict(
                new[] { "Tootega" },
                new[] { new VoiceReplacement { From = "dase", To = "DASE" } }));

            Assert.Contains("Tootega", hints);
            Assert.Contains("dase", hints);
            Assert.Contains("DASE", hints);
        }

        [Fact]
        public void NoDictionaryMeansNoHints()
        {
            Assert.Null(VoiceDictionary.CorrectorHints(Dict()));
        }

        // --- Workspace terms ---

        [Fact]
        public void HarvestsDependencyNames()
        {
            var project = Path.Combine(_root, "project");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "package.json"),
                "{\"dependencies\":{\"@anthropic-ai/sdk\":\"1\"},\"devDependencies\":{\"vitest\":\"2\"}}",
                new UTF8Encoding(false));

            var terms = new WorkspaceTerms().For(project);

            Assert.Contains("anthropic", terms);
            Assert.Contains("sdk", terms);
            Assert.Contains("vitest", terms);
            // The fixed glossary is always present.
            Assert.Contains("WebSocket", terms);
        }

        [Theory]
        [InlineData("@scope/pkg-name", new[] { "scope", "pkg", "name" })]
        [InlineData("react", new[] { "react" })]
        [InlineData("a-b-c", new string[0])]              // fragments under three chars are noise
        [InlineData("lodash.debounce", new[] { "lodash", "debounce" })]
        public void SplitsDependencyNamesIntoPronounceableParts(string name, string[] expected)
        {
            Assert.Equal(expected, WorkspaceTerms.DependencyToTerms(name).ToArray());
        }

        [Fact]
        public void NoPackageJsonStillYieldsTheGlossary()
        {
            var terms = new WorkspaceTerms().For(Path.Combine(_root, "empty-project"));

            Assert.Contains("commit", terms);
            Assert.DoesNotContain(terms, t => t == "vitest");
        }

        // --- Text corrector budget ---

        [Theory]
        [InlineData(null, 256)]
        [InlineData("", 256)]
        [InlineData("short", 259)]   // 256 floor plus half the input, rounded up
        public void ShortTextGetsAtLeastTheMinimumBudget(string text, int expected)
        {
            Assert.Equal(expected, TextCorrector.MaxTokensFor(text));
        }

        [Fact]
        public void BudgetGrowsWithTheInputAndIsCapped()
        {
            // A correction is about as long as its input, so a fixed ceiling would truncate a
            // long dictation and over-reserve for a short one.
            var medium = TextCorrector.MaxTokensFor(new string('x', 2000));
            Assert.True(medium > 256 && medium < 4096, "got " + medium);

            Assert.Equal(4096, TextCorrector.MaxTokensFor(new string('x', 100_000)));
        }

        // --- Audio capture ---

        [Fact]
        public void BuildsFfmpegArgumentsForPcm16()
        {
            var args = new AudioCapture().BuildArgs("Microphone (Realtek)");
            var line = string.Join(" ", args);

            Assert.Contains("-hide_banner", args);
            // 16 kHz mono signed 16-bit little-endian on stdout is what the speech socket wants.
            Assert.Equal("16000", args[Array.IndexOf(args, "-ar") + 1]);
            Assert.Equal("1", args[Array.IndexOf(args, "-ac") + 1]);
            Assert.Contains("-f s16le", line);
            Assert.Equal("pipe:1", args[args.Length - 1]);
        }

        [Fact]
        public void PicksTheFirstDeviceAfterTheAudioHeader()
        {
            // Video devices are listed first; taking the first quoted name overall would
            // select a webcam.
            const string stderr =
                "[dshow @ 1] DirectShow video devices (some may be both video and audio devices)\n" +
                "[dshow @ 1]  \"HD Webcam\"\n" +
                "[dshow @ 1] DirectShow audio devices\n" +
                "[dshow @ 1]  \"Microphone (Realtek Audio)\"\n" +
                "[dshow @ 1]  \"Line In\"\n";

            Assert.Equal("Microphone (Realtek Audio)", AudioCapture.FirstAudioDevice(stderr));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("DirectShow video devices\n \"Webcam\"\n")]
        public void NoAudioDeviceIsReportedAsAbsent(string stderr)
        {
            Assert.Null(AudioCapture.FirstAudioDevice(stderr));
        }

        // --- Voice session URL ---

        [Fact]
        public void BuildsTheSpeechStreamUrl()
        {
            var session = new VoiceSession("pt", "termo1,termo2", new VoiceCallbacks());

            var url = session.BuildUrl();

            Assert.Contains("/api/ws/speech_to_text/voice_stream", url);
            Assert.Contains("encoding=linear16", url);
            Assert.Contains("sample_rate=16000", url);
            Assert.Contains("language=pt", url);
            // Interim forwarding is what makes results appear while the user is still speaking.
            Assert.Contains("forward_interims=typed", url);
        }

        [Fact]
        public void DefaultsTheDictationLanguage()
        {
            Assert.Contains("language=en", new VoiceSession(null, null, new VoiceCallbacks()).BuildUrl());
        }

        [Fact]
        public void PinsTheLastInterimWhenAnUtteranceEndsWithoutAFinal()
        {
            // The service sometimes closes an utterance without a final; dropping the interim
            // would silently swallow what the user just said.
            var transcripts = new List<(string Text, bool Final)>();
            var session = new VoiceSession("en", null, new VoiceCallbacks
            {
                OnTranscript = (text, final) => transcripts.Add((text, final)),
            });

            session.HandleMessage("{\"type\":\"TranscriptInterim\",\"data\":\"hello wor\"}");
            session.HandleMessage("{\"type\":\"TranscriptEndpoint\"}");

            Assert.Equal(2, transcripts.Count);
            Assert.False(transcripts[0].Final);
            Assert.Equal(("hello wor", true), transcripts[1]);
        }

        [Fact]
        public void AFinalClearsThePendingInterim()
        {
            var transcripts = new List<(string Text, bool Final)>();
            var session = new VoiceSession("en", null, new VoiceCallbacks
            {
                OnTranscript = (text, final) => transcripts.Add((text, final)),
            });

            session.HandleMessage("{\"type\":\"TranscriptInterim\",\"data\":\"hello wor\"}");
            session.HandleMessage("{\"type\":\"TranscriptText\",\"data\":\"hello world\"}");
            // Nothing left to pin, so the endpoint must not re-emit anything.
            session.HandleMessage("{\"type\":\"TranscriptEndpoint\"}");

            Assert.Equal(2, transcripts.Count);
            Assert.Equal(("hello world", true), transcripts[1]);
        }

        [Fact]
        public void ReportsServerErrors()
        {
            string reported = null;
            var session = new VoiceSession("en", null, new VoiceCallbacks { OnError = m => reported = m });

            session.HandleMessage("{\"type\":\"error\",\"error\":{\"message\":\"bad audio format\"}}");

            Assert.Equal("bad audio format", reported);
        }

        [Fact]
        public void IgnoresMessagesItDoesNotUnderstand()
        {
            var calls = 0;
            var session = new VoiceSession("en", null, new VoiceCallbacks
            {
                OnTranscript = (t, f) => calls++,
                OnError = m => calls++,
            });

            session.HandleMessage("not json");
            session.HandleMessage("{\"type\":\"SomethingNew\"}");
            session.HandleMessage("[]");

            Assert.Equal(0, calls);
        }

        // --- TOTP ---

        [Fact]
        public void GeneratesUsableSecrets()
        {
            var secret = Totp.GenerateSecret();

            Assert.Equal(32, secret.Length);   // 20 bytes in base32
            Assert.All(secret, c => Assert.Contains(c, "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"));
            Assert.NotEqual(secret, Totp.GenerateSecret());
        }

        [Fact]
        public void MatchesTheRfc4648Base32Vectors()
        {
            Assert.Equal("MZXW6YTB", Totp.Base32Encode(Encoding.ASCII.GetBytes("fooba")));
            Assert.Equal("fooba", Encoding.ASCII.GetString(Totp.Base32Decode("MZXW6YTB")));
        }

        [Fact]
        public void Base32DecodeIgnoresPaddingAndSpacing()
        {
            // Users retype these by hand, often with the spacing the app displayed.
            Assert.Equal(Totp.Base32Decode("MZXW6YTB"), Totp.Base32Decode("mzxw 6ytb=="));
        }

        [Fact]
        public void MatchesTheRfc6238TestVector()
        {
            // The published vector for the ASCII secret "12345678901234567890" at T=59.
            var secret = Totp.Base32Encode(Encoding.ASCII.GetBytes("12345678901234567890"));

            Assert.Equal("287082", Totp.Hotp(secret, 59 / 30));
        }

        [Fact]
        public void AcceptsTheCurrentCode()
        {
            var secret = Totp.GenerateSecret();
            var now = DateTimeOffset.UtcNow;
            var code = Totp.Hotp(secret, now.ToUnixTimeSeconds() / 30);

            Assert.True(Totp.Verify(secret, code, now));
        }

        [Fact]
        public void ToleratesOneStepOfClockDrift()
        {
            // The phone and the machine are never exactly in sync.
            var secret = Totp.GenerateSecret();
            var now = DateTimeOffset.UtcNow;

            var previous = Totp.Hotp(secret, now.ToUnixTimeSeconds() / 30 - 1);
            var next = Totp.Hotp(secret, now.ToUnixTimeSeconds() / 30 + 1);

            Assert.True(Totp.Verify(secret, previous, now));
            Assert.True(Totp.Verify(secret, next, now));
        }

        [Fact]
        public void RejectsCodesFurtherOut()
        {
            var secret = Totp.GenerateSecret();
            var now = DateTimeOffset.UtcNow;
            var stale = Totp.Hotp(secret, now.ToUnixTimeSeconds() / 30 - 5);

            Assert.False(Totp.Verify(secret, stale, now));
        }

        [Theory]
        [InlineData("")]
        [InlineData("12345")]
        [InlineData("1234567")]
        [InlineData("abcdef")]
        [InlineData(null)]
        public void RejectsMalformedCodes(string code)
        {
            Assert.False(Totp.Verify(Totp.GenerateSecret(), code));
        }

        [Fact]
        public void AcceptsACodeWithSpacing()
        {
            var secret = Totp.GenerateSecret();
            var now = DateTimeOffset.UtcNow;
            var code = Totp.Hotp(secret, now.ToUnixTimeSeconds() / 30);

            Assert.True(Totp.Verify(secret, code.Substring(0, 3) + " " + code.Substring(3), now));
        }

        [Fact]
        public void BuildsAScannableUri()
        {
            var uri = Totp.BuildUri("ABCDEFGH");

            Assert.StartsWith("otpauth://totp/", uri);
            Assert.Contains("secret=ABCDEFGH", uri);
            Assert.Contains("algorithm=SHA1", uri);
            Assert.Contains("digits=6", uri);
            Assert.Contains("period=30", uri);
        }

        // --- Credential vault ---

        private static (CredentialsStore Store, string Secret) EnrolledVault()
        {
            var store = new CredentialsStore(new InMemorySecretStorage());
            var challenge = store.BeginEnroll();
            Assert.True(store.ConfirmEnroll(CurrentCode(challenge.Secret)));
            return (store, challenge.Secret);
        }

        private static string CurrentCode(string secret)
        {
            return Totp.Hotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        }

        [Fact]
        public void EnrolmentOnlyPersistsAfterAValidCode()
        {
            // Storing before the user proved they can generate codes would lock them out of
            // their own vault.
            var store = new CredentialsStore(new InMemorySecretStorage());
            Assert.False(store.IsEnrolled());

            var challenge = store.BeginEnroll();
            Assert.False(store.ConfirmEnroll("000000"));
            Assert.False(store.IsEnrolled());

            Assert.True(store.ConfirmEnroll(CurrentCode(challenge.Secret)));
            Assert.True(store.IsEnrolled());
        }

        [Fact]
        public void EnrolmentOffersAQrAndTheSecret()
        {
            var challenge = new CredentialsStore(new InMemorySecretStorage()).BeginEnroll();

            Assert.False(string.IsNullOrEmpty(challenge.Secret));
            Assert.StartsWith("otpauth://", challenge.Uri);
            // The QR is inline SVG so the secret never touches disk.
            Assert.Contains("<svg", challenge.QrSvg);
        }

        [Fact]
        public void StoresAndRetrievesACredential()
        {
            var (store, secret) = EnrolledVault();

            Assert.True(store.Add(CurrentCode(secret), "GitHub", "ghp_secret", "hermes", "work account").Ok);

            var meta = store.List().Single();
            Assert.Equal("GitHub", meta.Name);
            Assert.Equal("hermes", meta.Username);
            // Listing metadata must never carry the value.
            Assert.DoesNotContain("ghp_secret", Json.Serialize(meta));

            var used = store.Use(CurrentCode(secret), meta.Id);
            Assert.True(used.Ok);
            Assert.Equal("ghp_secret", used.Value);
        }

        [Fact]
        public void EverySensitiveOperationRequiresAValidCode()
        {
            var (store, secret) = EnrolledVault();
            store.Add(CurrentCode(secret), "GitHub", "ghp_secret");
            var id = store.List().Single().Id;

            Assert.Equal(VaultFailure.Totp, store.Add("000000", "X", "v").Reason);
            Assert.Equal(VaultFailure.Totp, store.Use("000000", id).Reason);
            Assert.Equal(VaultFailure.Totp, store.Edit("000000", id, "X").Reason);
            Assert.Equal(VaultFailure.Totp, store.Remove("000000", id).Reason);

            // Nothing was changed by any of the refused attempts.
            Assert.Single(store.List());
            Assert.Equal("ghp_secret", store.Use(CurrentCode(secret), id).Value);
        }

        [Fact]
        public void AnUnenrolledVaultRefusesEverything()
        {
            var store = new CredentialsStore(new InMemorySecretStorage());

            Assert.Equal(VaultFailure.Totp, store.Add("000000", "X", "v").Reason);
            Assert.Empty(store.List());
        }

        [Fact]
        public void EditKeepsTheValueWhenNoneIsGiven()
        {
            // This is what lets the user fix a label without retyping the secret.
            var (store, secret) = EnrolledVault();
            store.Add(CurrentCode(secret), "GitHub", "ghp_secret");
            var id = store.List().Single().Id;

            Assert.True(store.Edit(CurrentCode(secret), id, "GitHub (work)", "hermes").Ok);

            Assert.Equal("GitHub (work)", store.List().Single().Name);
            Assert.Equal("ghp_secret", store.Use(CurrentCode(secret), id).Value);
        }

        [Fact]
        public void EditReplacesTheValueWhenOneIsGiven()
        {
            var (store, secret) = EnrolledVault();
            store.Add(CurrentCode(secret), "GitHub", "old");
            var id = store.List().Single().Id;

            store.Edit(CurrentCode(secret), id, "GitHub", value: "new");

            Assert.Equal("new", store.Use(CurrentCode(secret), id).Value);
        }

        [Fact]
        public void RefusesIncompleteInput()
        {
            var (store, secret) = EnrolledVault();

            Assert.Equal(VaultFailure.Input, store.Add(CurrentCode(secret), "  ", "value").Reason);
            Assert.Equal(VaultFailure.Input, store.Add(CurrentCode(secret), "name", "").Reason);
            Assert.Equal(VaultFailure.Input, store.Edit(CurrentCode(secret), "no-such-id", "name").Reason);
        }

        [Fact]
        public void RemovesACredentialAndItsValue()
        {
            var (store, secret) = EnrolledVault();
            store.Add(CurrentCode(secret), "GitHub", "ghp_secret");
            var id = store.List().Single().Id;

            Assert.True(store.Remove(CurrentCode(secret), id).Ok);

            Assert.Empty(store.List());
            Assert.Equal(string.Empty, store.Use(CurrentCode(secret), id).Value);
        }

        [Fact]
        public void CredentialsGetDistinctIds()
        {
            var (store, secret) = EnrolledVault();
            store.Add(CurrentCode(secret), "A", "1");
            store.Add(CurrentCode(secret), "B", "2");

            var items = store.List();
            Assert.Equal(2, items.Count);
            Assert.NotEqual(items[0].Id, items[1].Id);
            Assert.Equal("1", store.Use(CurrentCode(secret), items[0].Id).Value);
            Assert.Equal("2", store.Use(CurrentCode(secret), items[1].Id).Value);
        }

        // --- Spell checker ---

        [Theory]
        [InlineData("hello", true)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void DecidesWhatIsWorthChecking(string word, bool checkable)
        {
            Assert.Equal(checkable, Speller.IsCheckable(word));
        }

        [Fact]
        public void SkipsPathologicalTokens()
        {
            // URL glue, base64 and hashes are always "wrong" and underlining them helps nobody.
            Assert.False(Speller.IsCheckable(new string('x', 100)));
            Assert.False(Speller.IsCheckable("has\u0000null"));
            Assert.False(Speller.IsCheckable("has\u0007bell"));
            Assert.False(Speller.IsCheckable("has\u007Fdelete"));
        }

        [Fact]
        public void KnownWordsAreNeverFlagged()
        {
            // No dictionaries are loaded here, so this exercises the known-word path alone.
            var speller = new Speller(Path.Combine(_root, "no-dicts"));
            speller.SetUserDictionary(new[] { "Tootega" });
            speller.SetProjectTerms(new[] { "VSIX" });

            Assert.Contains("Tootega", speller.UserDictionary());
            Assert.Empty(speller.Check(new[] { "Tootega", "VSIX" }));
        }

        [Fact]
        public void WithoutDictionariesNothingIsFlagged()
        {
            // Failing to load must degrade to "no marks", never to everything underlined.
            var speller = new Speller(Path.Combine(_root, "no-dicts"));

            Assert.False(speller.IsReady);
            Assert.Empty(speller.Check(new[] { "zzzznotaword" }));
            Assert.Empty(speller.Suggest("zzzznotaword").Pt);
        }

        [Fact]
        public void AddedWordsJoinTheUserDictionary()
        {
            var speller = new Speller(Path.Combine(_root, "no-dicts"));

            speller.AddWord("  Cockpit  ");
            speller.AddWord("   ");

            Assert.Equal(new[] { "Cockpit" }, speller.UserDictionary());
        }
    }
}
