using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Settings;
using Tootega.Cockpit.Util;
using Tootega.Cockpit.Voice;

namespace Tootega.Cockpit.Host
{
    /// <summary>
    /// Voice dictation into the composer, and the dictionary behind it.
    ///
    /// The microphone is captured in the HOST, not in the webview: a WebView2 in a tool window
    /// has no user-granted media permission to lean on, so the audio comes from ffmpeg and the
    /// transcription rides a socket the host owns. That also means only one dictation can be
    /// live at a time — starting a second one ends the first, which is what a single microphone
    /// implies.
    /// </summary>
    internal sealed class DictationService : IDisposable
    {
        private readonly ICockpitSettings _settings;
        private readonly VoiceDictionary _dictionary;
        private readonly WorkspaceTerms _terms;
        private readonly SpellingService _spelling;
        private readonly TextCorrector _corrector;
        private readonly Action<HostMessage, string> _post;

        private VoiceSession _voice;
        private AudioCapture _capture;

        public DictationService(ICockpitSettings settings, VoiceDictionary dictionary, WorkspaceTerms terms,
                                SpellingService spelling, TextCorrector corrector,
                                Action<HostMessage, string> post)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
            _terms = terms ?? throw new ArgumentNullException(nameof(terms));
            _spelling = spelling ?? throw new ArgumentNullException(nameof(spelling));
            _corrector = corrector ?? throw new ArgumentNullException(nameof(corrector));
            _post = post ?? throw new ArgumentNullException(nameof(post));
        }

        /// <summary>
        /// Starts dictating into a tab.
        ///
        /// The dictionary is re-read here rather than cached: the user may have just edited it
        /// in the modal, and dictating with the previous terms would look like the edit was
        /// ignored.
        /// </summary>
        public async Task StartAsync(string tabId, string cwd, string requested)
        {
            Stop();

            var dictionary = _dictionary.Load();

            // Keyterms anchor the spelling of jargon. The STT runs monolingual, so English
            // terms dictated inside another language only come out right when they are listed.
            var extras = new List<string> { SafeFolderName(cwd) };
            extras.AddRange(_terms.For(cwd));
            var keyterms = VoiceDictionary.BuildKeyterms(dictionary, extras);

            var language = ResolveLanguage(requested);

            var capture = new AudioCapture(CockpitHostService.Blank(_settings.FfmpegPath));
            _capture = capture;

            // Readiness is the first real audio frame, not the socket opening: announcing it
            // earlier makes the user start speaking into a microphone that is not recording
            // yet, and the first words are simply lost.
            var announced = false;

            _voice = new VoiceSession(language, keyterms, new VoiceCallbacks
            {
                // The socket is up: start the microphone. This runs on the socket's reader
                // thread, so launching ffmpeg is handed to the pool rather than blocking the
                // very loop that carries the transcriptions back.
                OnOpen = () => _ = Task.Run(() => CaptureAsync(capture, tabId, () => announced, () => announced = true)),

                OnTranscript = (text, isFinal) =>
                {
                    var fixedText = VoiceDictionary.ApplyReplacements(text, dictionary);
                    _post(HostMessages.VoiceTranscript(fixedText, isFinal), tabId);
                },

                OnError = message => _post(HostMessages.VoiceError(message), tabId),

                OnClose = () =>
                {
                    _capture?.Stop();
                    _capture = null;
                    _voice = null;
                    _post(HostMessages.VoiceClosed(), tabId);
                },
            });

            Log.Debug("voice: starting dictation (" + language + ")");
            await _voice.StartAsync();
        }

        /// <summary>
        /// Pumps microphone frames into the transcription socket until one of them stops.
        /// </summary>
        private async Task CaptureAsync(AudioCapture capture, string tabId,
                                        Func<bool> announced, Action announce)
        {
            try
            {
                await capture.StartAsync(
                    frame =>
                    {
                        if (!announced())
                        {
                            announce();
                            _post(HostMessages.VoiceReady(), tabId);
                        }

                        var session = _voice;
                        if (session == null) return;

                        // Not awaited: this callback runs on ffmpeg's reader thread, and a frame
                        // held up waiting on the socket is a gap in what the user said.
                        _ = session.PushAudioAsync(frame);
                    },
                    message =>
                    {
                        _post(HostMessages.VoiceError(message), tabId);
                        Stop();
                    },
                    () =>
                    {
                        // ffmpeg exited; closing the socket finishes the teardown.
                    });
            }
            catch (Exception ex)
            {
                Log.Error("voice: capture failed", ex);
                _post(HostMessages.VoiceError(ex.Message), tabId);
                Stop();
            }
        }

        public void Stop()
        {
            _capture?.Stop();
            _capture = null;

            var voice = _voice;
            if (voice == null) return;

            // Not awaited: stopping is a courtesy to the server, and the caller — a webview
            // message handler — should not block on a socket that may already be gone.
            _ = voice.StopAsync();
        }

        /// <summary>
        /// Cleans up dictated text through the small model.
        ///
        /// The dictionary replacements are applied FIRST, and the model is told about the
        /// account's terms, so it does not "correct" a proper noun into a common word — which
        /// is the one failure that makes such a feature untrustworthy.
        /// </summary>
        public async Task CorrectAsync(string tabId, string text)
        {
            var trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                _post(HostMessages.VoiceCorrectError(), tabId);
                return;
            }

            var dictionary = _dictionary.Load();
            var pre = VoiceDictionary.ApplyReplacements(trimmed, dictionary);

            var corrected = await _corrector.CorrectAsync(pre, VoiceDictionary.CorrectorHints(dictionary));

            // A model that did not answer still leaves the replacements, which is better than
            // handing back the raw transcription.
            _post(HostMessages.VoiceCorrected(string.IsNullOrWhiteSpace(corrected) ? pre : corrected), tabId);
        }

        // ---- The dictionary modal ----

        public void Send(string tabId, string cwd)
        {
            var dictionary = _dictionary.Load();
            var data = VoiceDictionary.ToWire(dictionary);
            data.SpellWords = _spelling.ReplaceUserDictionary(cwd, null).ToList();
            _post(HostMessages.VoiceDict(data), tabId);
        }

        /// <summary>
        /// Saves the dictionary the modal edited.
        ///
        /// The two lists are kept in one file because they answer the same question — "this is
        /// a real word here" — for dictation and for the spell-checker alike.
        /// </summary>
        public void Save(string tabId, string cwd, VoiceDictData data)
        {
            var dictionary = VoiceDictionary.FromWire(data) ?? new VoiceDict();

            var words = _spelling.ReplaceUserDictionary(cwd, data?.SpellWords);
            dictionary.SpellWords = words.ToList();

            _dictionary.Save(dictionary);

            var wire = VoiceDictionary.ToWire(dictionary);
            wire.SpellWords = words.ToList();
            _post(HostMessages.VoiceDict(wire), tabId);
        }

        /// <summary>
        /// The dictation language: an explicit setting wins over what the webview asked for.
        /// Normalized to the short code the STT expects (pt-BR becomes pt).
        /// </summary>
        private string ResolveLanguage(string requested)
        {
            var forced = CockpitHostService.Blank(_settings.VoiceLanguage);
            var value = forced ?? CockpitHostService.Blank(requested) ?? "en";
            var shortCode = value.Split('-')[0];
            return string.IsNullOrEmpty(shortCode) ? "en" : shortCode.ToLowerInvariant();
        }

        private static string SafeFolderName(string cwd)
        {
            try
            {
                return Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Dispose()
        {
            Stop();
            _voice?.Dispose();
            _capture?.Dispose();
        }
    }
}
