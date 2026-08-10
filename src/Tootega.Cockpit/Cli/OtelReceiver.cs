using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    /// <summary>
    /// A local OTLP/HTTP receiver for Claude Code's opt-in telemetry. Port of
    /// src/cli/OtelReceiver.ts.
    ///
    /// It accepts /v1/metrics and /v1/logs on the loopback interface and aggregates the
    /// claude_code.* counters. This is the clean structured source for things the event stream
    /// does not carry — lines of code per model, edit decisions, real per-model cost — without
    /// scanning transcripts.
    ///
    /// Opt-in and off by default. When enabled, the OTLP export variables are set on this
    /// process so the `claude` children inherit them and export here.
    ///
    /// Implemented over a raw TcpListener rather than HttpListener: HttpListener needs a URL
    /// reservation on Windows, which would mean asking the user to run an elevated command
    /// before a convenience feature works. The protocol surface needed here is one POST and a
    /// fixed reply, so a minimal server is the smaller cost.
    /// </summary>
    internal sealed class OtelReceiver : IDisposable
    {
        private const string Loopback = "127.0.0.1";

        /// <summary>Defensive cap: a single export should never approach this.</summary>
        private const int MaxBodyBytes = 8 * 1024 * 1024;

        private static readonly byte[] OkResponse = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");

        private static readonly byte[] NotFoundResponse = Encoding.ASCII.GetBytes(
            "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        /// <summary>
        /// The export variables. Two of them are privacy decisions, not configuration:
        /// prompts and assistant responses are pinned off, because since CLI 2.1.193 the
        /// response text rides along in telemetry and follows the prompt flag when unset —
        /// anyone already logging prompts would silently start logging responses. The receiver
        /// discards /v1/logs anyway, but this keeps the text from leaving the CLI at all.
        /// </summary>
        private static readonly string[] ManagedVariables =
        {
            "CLAUDE_CODE_ENABLE_TELEMETRY",
            "OTEL_METRICS_EXPORTER",
            "OTEL_LOGS_EXPORTER",
            "OTEL_EXPORTER_OTLP_PROTOCOL",
            "OTEL_EXPORTER_OTLP_ENDPOINT",
            "OTEL_METRIC_EXPORT_INTERVAL",
            "OTEL_LOG_USER_PROMPTS",
            "OTEL_LOG_ASSISTANT_RESPONSES",
            "OTEL_LOG_TOOL_DETAILS",
        };

        private readonly int _port;
        private readonly object _gate = new object();
        private OtelState _state;
        private TcpListener _listener;
        private CancellationTokenSource _cancellation;
        private Task _acceptLoop;
        private bool _running;
        private bool _disposed;

        public OtelReceiver(int port = 4318)
        {
            _port = port;
            _state = new OtelState(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public string Endpoint => "http://" + Loopback + ":" + _port;

        public bool IsRunning
        {
            get { lock (_gate) return _running; }
        }

        /// <summary>Starts the receiver and exports the environment. Idempotent.</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_running || _disposed) return;

                try
                {
                    _listener = new TcpListener(IPAddress.Parse(Loopback), _port);
                    _listener.Start();
                }
                catch (Exception ex)
                {
                    // Usually the port is taken by another instance. Telemetry is a
                    // convenience, so this is reported and dropped rather than surfaced.
                    Log.Error("otel: could not listen on " + Endpoint, ex);
                    _listener = null;
                    return;
                }

                _cancellation = new CancellationTokenSource();
                _running = true;
                _acceptLoop = Task.Run(() => AcceptLoopAsync(_listener, _cancellation.Token));

                ApplyEnvironment();
                Log.Info("otel receiver at " + Endpoint + " (opt-in)");
            }
        }

        /// <summary>Stops the receiver and removes the exported variables.</summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (!_running) return;
                _running = false;

                try
                {
                    _cancellation?.Cancel();
                    _listener?.Stop();
                }
                catch
                {
                    // Already down.
                }

                _listener = null;
                _cancellation = null;
                _acceptLoop = null;

                ClearEnvironment();
            }
        }

        /// <summary>Aggregates a payload directly. The seam tests and callers use.</summary>
        public void Ingest(string metricsJson)
        {
            lock (_gate) OtelMetrics.Ingest(metricsJson, _state);
        }

        /// <summary>Aggregated snapshot for the Usage modal.</summary>
        public OtelStats Stats()
        {
            lock (_gate) return OtelMetrics.ToStats(_state, _running, Endpoint);
        }

        /// <summary>Forgets everything collected so far, keeping the receiver up.</summary>
        public void Reset()
        {
            lock (_gate) _state = new OtelState(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                // Each connection is handled independently: one malformed export must not
                // stop the receiver from accepting the next.
                _ = Task.Run(() => HandleClientAsync(client), cancellation);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    client.ReceiveTimeout = 15000;
                    client.SendTimeout = 15000;

                    var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                    if (request == null)
                    {
                        await WriteAsync(stream, NotFoundResponse).ConfigureAwait(false);
                        return;
                    }

                    var isMetrics = request.Value.Path.EndsWith("/v1/metrics", StringComparison.Ordinal);
                    var isLogs = request.Value.Path.EndsWith("/v1/logs", StringComparison.Ordinal);

                    if (request.Value.Method != "POST" || (!isMetrics && !isLogs))
                    {
                        await WriteAsync(stream, NotFoundResponse).ConfigureAwait(false);
                        return;
                    }

                    if (isMetrics) Ingest(request.Value.Body);

                    // /v1/logs is accepted and discarded. Assistant responses can carry
                    // conversation text, and none of it is retained — receipt is acknowledged
                    // so the exporter does not retry forever.
                    await WriteAsync(stream, OkResponse).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("otel: connection failed: " + ex.Message);
            }
        }

        private struct HttpRequest
        {
            public string Method;
            public string Path;
            public string Body;
        }

        /// <summary>
        /// Reads one HTTP request. Only what an OTLP exporter actually sends is supported:
        /// a request line, headers, and a body delimited by Content-Length or chunked encoding.
        /// </summary>
        private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream)
        {
            var reader = new ByteReader(stream);

            var requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(requestLine)) return null;

            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return null;

            var contentLength = -1;
            var chunked = false;

            while (true)
            {
                var header = await reader.ReadLineAsync().ConfigureAwait(false);
                if (header == null) return null;
                if (header.Length == 0) break;

                var separator = header.IndexOf(':');
                if (separator <= 0) continue;

                var name = header.Substring(0, separator).Trim();
                var value = header.Substring(separator + 1).Trim();

                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(value, out contentLength);
                else if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    chunked = value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            string body;
            if (chunked) body = await reader.ReadChunkedBodyAsync(MaxBodyBytes).ConfigureAwait(false);
            else if (contentLength > 0) body = await reader.ReadBodyAsync(Math.Min(contentLength, MaxBodyBytes)).ConfigureAwait(false);
            else body = string.Empty;

            return new HttpRequest { Method = parts[0], Path = parts[1], Body = body };
        }

        private static Task WriteAsync(NetworkStream stream, byte[] response)
        {
            return stream.WriteAsync(response, 0, response.Length);
        }

        /// <summary>Buffered reader for the small amount of HTTP framing this needs.</summary>
        private sealed class ByteReader
        {
            private readonly NetworkStream _stream;
            private readonly byte[] _buffer = new byte[8192];
            private int _length;
            private int _position;

            public ByteReader(NetworkStream stream)
            {
                _stream = stream;
            }

            private async Task<int> ReadByteAsync()
            {
                if (_position >= _length)
                {
                    _length = await _stream.ReadAsync(_buffer, 0, _buffer.Length).ConfigureAwait(false);
                    _position = 0;
                    if (_length <= 0) return -1;
                }
                return _buffer[_position++];
            }

            /// <summary>Reads a CRLF-terminated line. Null at end of stream.</summary>
            public async Task<string> ReadLineAsync()
            {
                var line = new List<byte>(128);
                while (true)
                {
                    var next = await ReadByteAsync().ConfigureAwait(false);
                    if (next < 0) return line.Count > 0 ? Encoding.UTF8.GetString(line.ToArray()) : null;
                    if (next == '\n') return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                    line.Add((byte)next);
                }
            }

            public async Task<string> ReadBodyAsync(int length)
            {
                var body = new MemoryStream(length);
                for (var i = 0; i < length; i++)
                {
                    var next = await ReadByteAsync().ConfigureAwait(false);
                    if (next < 0) break;
                    body.WriteByte((byte)next);
                }
                return Encoding.UTF8.GetString(body.ToArray());
            }

            public async Task<string> ReadChunkedBodyAsync(int cap)
            {
                var body = new MemoryStream();
                while (body.Length < cap)
                {
                    var sizeLine = await ReadLineAsync().ConfigureAwait(false);
                    if (sizeLine == null) break;

                    var semicolon = sizeLine.IndexOf(';');
                    if (semicolon >= 0) sizeLine = sizeLine.Substring(0, semicolon);

                    if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber,
                                      System.Globalization.CultureInfo.InvariantCulture, out var size) || size <= 0)
                    {
                        break;
                    }

                    for (var i = 0; i < size; i++)
                    {
                        var next = await ReadByteAsync().ConfigureAwait(false);
                        if (next < 0) break;
                        body.WriteByte((byte)next);
                    }

                    // Trailing CRLF after each chunk.
                    await ReadLineAsync().ConfigureAwait(false);
                }
                return Encoding.UTF8.GetString(body.ToArray());
            }
        }

        private void ApplyEnvironment()
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_ENABLE_TELEMETRY", "1");
            Environment.SetEnvironmentVariable("OTEL_METRICS_EXPORTER", "otlp");
            Environment.SetEnvironmentVariable("OTEL_LOGS_EXPORTER", "otlp");
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", "http/json");
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", Endpoint);
            // Ten seconds rather than the default minute, so the panel fills while the user is
            // still looking at it.
            Environment.SetEnvironmentVariable("OTEL_METRIC_EXPORT_INTERVAL", "10000");
            // Conversation content must not enter telemetry — see ManagedVariables.
            Environment.SetEnvironmentVariable("OTEL_LOG_USER_PROMPTS", "0");
            Environment.SetEnvironmentVariable("OTEL_LOG_ASSISTANT_RESPONSES", "0");
            // Brings the REAL workflow.name into the metrics; without it the CLI reports
            // user-authored workflows as "custom". The tool details this also enables go to
            // /v1/logs, which is discarded entirely.
            Environment.SetEnvironmentVariable("OTEL_LOG_TOOL_DETAILS", "1");
        }

        private static void ClearEnvironment()
        {
            foreach (var name in ManagedVariables) Environment.SetEnvironmentVariable(name, null);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
