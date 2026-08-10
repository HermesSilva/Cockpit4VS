using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Voice
{
    internal sealed class VoiceCallbacks
    {
        /// <summary>The socket is ready — capture may start.</summary>
        public Action OnOpen { get; set; }
        public Action<string, bool> OnTranscript { get; set; }
        public Action<string> OnError { get; set; }
        public Action OnClose { get; set; }
    }

    /// <summary>
    /// One dictation session over the same speech service Claude Code's own /voice uses. Port
    /// of src/cli/VoiceStream.ts.
    ///
    /// It is an OAuth WebSocket and spends NO tokens, which is what puts it inside the recorded
    /// exception. The credential is read-only and never written or logged.
    ///
    /// Audio goes up as linear16 PCM, 16 kHz mono, in binary frames; transcriptions come back
    /// as JSON, interim while you speak and final when an utterance closes.
    /// </summary>
    internal sealed class VoiceSession : IDisposable
    {
        private const string DefaultBase = "wss://api.anthropic.com";
        private const string Path = "/api/ws/speech_to_text/voice_stream";
        private const int KeepAliveMs = 10_000;

        /// <summary>100 ms of PCM16 at 16 kHz mono.</summary>
        private const int SilenceFrameBytes = 3200;

        private readonly string _language;
        private readonly string _keyterms;
        private readonly VoiceCallbacks _callbacks;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private ClientWebSocket _socket;
        private CancellationTokenSource _cancellation;
        private Timer _keepAlive;
        private bool _closed;
        private bool _disposed;

        /// <summary>The most recent interim of the utterance in flight.</summary>
        private string _lastInterim = string.Empty;

        private int _chunks;
        private long _bytes;

        public VoiceSession(string language, string keyterms, VoiceCallbacks callbacks)
        {
            _language = string.IsNullOrWhiteSpace(language) ? "en" : language;
            _keyterms = keyterms;
            _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        }

        public async Task StartAsync()
        {
            var token = ClaudeHome.ReadOauthToken();
            if (string.IsNullOrEmpty(token))
            {
                // A stable identifier, not prose: the UI explains what to do about it.
                _callbacks.OnError?.Invoke("no-oauth-token");
                _callbacks.OnClose?.Invoke();
                return;
            }

            var socket = new ClientWebSocket();
            try
            {
                socket.Options.SetRequestHeader("authorization", "Bearer " + token);
                socket.Options.SetRequestHeader("anthropic-beta", AnthropicHttp.OauthBeta);
                socket.Options.SetRequestHeader("anthropic-version", AnthropicHttp.ApiVersion);

                // The terms the recogniser should prioritise. Sent as a header because the
                // service takes it at connection time, not per utterance.
                if (!string.IsNullOrEmpty(_keyterms)) socket.Options.SetRequestHeader("x-config-keyterms", _keyterms);

                _socket = socket;
                _cancellation = new CancellationTokenSource();

                Log.Debug("voice: connecting, language=" + _language);
                await socket.ConnectAsync(new Uri(BuildUrl()), _cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug("voice: connection failed: " + ex.Message);
                _callbacks.OnError?.Invoke("ws-connect: " + ex.Message);
                _callbacks.OnClose?.Invoke();
                Cleanup();
                return;
            }

            _callbacks.OnOpen?.Invoke();

            _keepAlive = new Timer(_ => SendKeepAlive(), null, KeepAliveMs, KeepAliveMs);
            _ = Task.Run(() => ReceiveLoopAsync(socket, _cancellation.Token));
        }

        internal string BuildUrl()
        {
            var baseUrl = Environment.GetEnvironmentVariable("VOICE_STREAM_BASE_URL");
            if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = DefaultBase;

            // The same parameters the official CLI uses. endpointing and utterance_end give
            // the segmentation on pauses; forward_interims is what makes results appear while
            // the user is still speaking rather than only at the end.
            var query =
                "encoding=linear16" +
                "&sample_rate=16000" +
                "&channels=1" +
                "&endpointing_ms=300" +
                "&utterance_end_ms=1000" +
                "&language=" + Uri.EscapeDataString(_language) +
                "&use_conversation_engine=true" +
                "&forward_interims=typed" +
                "&stt_provider=deepgram-nova3";

            return baseUrl + Path + "?" + query;
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellation)
        {
            var buffer = new byte[16 * 1024];
            var message = new StringBuilder();

            try
            {
                while (socket.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
                {
                    var result = await socket
                        .ReceiveAsync(new ArraySegment<byte>(buffer), cancellation)
                        .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    // The protocol only sends text; a binary frame is not ours to interpret.
                    if (result.MessageType != WebSocketMessageType.Text) continue;

                    message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;

                    HandleMessage(message.ToString());
                    message.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped on purpose.
            }
            catch (Exception ex)
            {
                Log.Debug("voice: receive ended: " + ex.Message);
                _callbacks.OnError?.Invoke(ex.Message);
            }
            finally
            {
                Cleanup();
                _callbacks.OnClose?.Invoke();
            }
        }

        /// <summary>
        /// Interprets one server message.
        ///
        /// The shapes that matter:
        ///   TranscriptInterim — a cumulative partial, replacing the previous one;
        ///   TranscriptText    — the FINAL text of an utterance;
        ///   TranscriptEndpoint— the utterance ended.
        ///
        /// The endpoint case is what stops words being lost: when the service closes an
        /// utterance without sending a final, the last interim IS the result, and dropping it
        /// would silently swallow what the user just said.
        /// </summary>
        internal void HandleMessage(string raw)
        {
            JsonElement root;
            try
            {
                using (var document = JsonDocument.Parse(raw))
                {
                    root = document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                Log.Debug("voice: non-json message");
                return;
            }

            if (root.ValueKind != JsonValueKind.Object) return;

            var type = ReadString(root, "type");

            if (type == "error")
            {
                var message = "server error";
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    message = ReadString(error, "message") ?? ReadString(error, "type") ?? message;
                }

                Log.Debug("voice: server error: " + message);
                _callbacks.OnError?.Invoke(message);
                return;
            }

            var data = ReadString(root, "data");

            switch (type)
            {
                case "TranscriptInterim":
                    if (data == null) return;
                    _lastInterim = data;
                    _callbacks.OnTranscript?.Invoke(data, false);
                    return;

                case "TranscriptText":
                    if (data == null) return;
                    _lastInterim = string.Empty;
                    _callbacks.OnTranscript?.Invoke(data, true);
                    return;

                case "TranscriptEndpoint":
                    if (string.IsNullOrEmpty(_lastInterim)) return;
                    // No final arrived, so the last interim is the utterance.
                    _callbacks.OnTranscript?.Invoke(_lastInterim, true);
                    _lastInterim = string.Empty;
                    return;
            }
        }

        /// <summary>Pushes a PCM16 frame to the server.</summary>
        public async Task PushAudioAsync(byte[] frame)
        {
            var socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open || frame == null || frame.Length == 0) return;

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (socket.State != WebSocketState.Open) return;

                await socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true,
                                       CancellationToken.None).ConfigureAwait(false);

                _chunks++;
                _bytes += frame.Length;
                if (_chunks == 1) Log.Debug("voice: first audio frame, " + frame.Length + "B");
            }
            catch (Exception ex)
            {
                Log.Debug("voice: dropped a frame: " + ex.Message);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void SendKeepAlive()
        {
            _ = SendTextAsync("{\"type\":\"KeepAlive\"}");
        }

        private async Task SendTextAsync(string json)
        {
            var socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open) return;

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (socket.State != WebSocketState.Open) return;
                var bytes = Encoding.UTF8.GetBytes(json);
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                                       CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug("voice: send failed: " + ex.Message);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Ends the session: a short silence flush, then CloseStream.
        ///
        /// The silence matters. Without a trailing pause the recogniser sometimes closes
        /// without emitting the final transcript, losing the last thing said — so roughly
        /// 600 ms of zeroed PCM is sent to give it the pause it is waiting for.
        ///
        /// The socket is NOT closed immediately: the server still has audio to process and
        /// will close on its own once it has sent the final text. The forced close is only a
        /// safety net.
        /// </summary>
        public async Task StopAsync()
        {
            if (_closed) return;
            _closed = true;

            Log.Debug("voice: stopping after " + _chunks + " frames / " + _bytes + "B");

            var silence = new byte[SilenceFrameBytes];
            for (var i = 0; i < 6; i++) await PushAudioAsync(silence).ConfigureAwait(false);

            await SendTextAsync("{\"type\":\"CloseStream\"}").ConfigureAwait(false);

            _ = ForceCloseAfterGraceAsync();
        }

        private async Task ForceCloseAfterGraceAsync()
        {
            await Task.Delay(6000).ConfigureAwait(false);

            var socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open) return;

            Log.Debug("voice: forcing close, the server did not close after CloseStream");
            Cleanup();
        }

        private void Cleanup()
        {
            var keepAlive = _keepAlive;
            _keepAlive = null;
            keepAlive?.Dispose();

            var socket = _socket;
            _socket = null;

            try
            {
                _cancellation?.Cancel();
            }
            catch
            {
            }

            if (socket == null) return;

            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    _ = socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                }
            }
            catch
            {
            }
            finally
            {
                socket.Dispose();
            }
        }

        private static string ReadString(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
            return value.GetString();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Cleanup();
            _cancellation?.Dispose();
            _sendLock.Dispose();
        }
    }
}
