using System;
using System.Collections.Generic;
using System.Text;
using Tootega.Cockpit.Protocol;

namespace Tootega.Cockpit.Cli
{
    /// <summary>
    /// Turns the CLI's NDJSON stdout into typed events. Port of src/cli/StreamParser.ts.
    ///
    /// Tolerant by design: an unparseable line is discarded rather than raised. The CLI
    /// also writes plain log noise to stdout on occasion, and a stray line must never take
    /// down a conversation in progress.
    /// </summary>
    internal sealed class StreamParser
    {
        /// <summary>
        /// Cap for a buffer with no line break. A legitimate NDJSON event fits well below
        /// this; above it means a corrupted stream (binary noise, a stuck process), and the
        /// accumulation is dropped so it cannot leak memory or freeze the UI. Events after
        /// the next newline are processed normally again.
        /// </summary>
        private const int MaxBuffer = 64 * 1024 * 1024;

        private readonly StringBuilder _buffer = new StringBuilder();

        /// <summary>
        /// How much of the buffer has already been searched for a newline. Without this the
        /// scan would restart from index 0 on every chunk, which is quadratic over a long
        /// streamed turn.
        /// </summary>
        private int _scanned;

        /// <summary>Feeds a stdout chunk and returns the complete events it completed.</summary>
        public IReadOnlyList<ClaudeEvent> Push(string chunk)
        {
            var events = new List<ClaudeEvent>();
            if (string.IsNullOrEmpty(chunk)) return events;

            _buffer.Append(chunk);

            int newline;
            while ((newline = IndexOfNewline()) >= 0)
            {
                var line = _buffer.ToString(0, newline);
                _buffer.Remove(0, newline + 1);
                _scanned = 0;

                var parsed = ParseLine(line);
                if (parsed != null) events.Add(parsed);
            }

            if (_buffer.Length > MaxBuffer)
            {
                _buffer.Clear();
                _scanned = 0;
            }

            return events;
        }

        /// <summary>Flushes whatever is left in the buffer, at end of process.</summary>
        public IReadOnlyList<ClaudeEvent> Flush()
        {
            var rest = _buffer.ToString();
            _buffer.Clear();
            _scanned = 0;

            var parsed = ParseLine(rest);
            return parsed != null
                ? new List<ClaudeEvent> { parsed }
                : (IReadOnlyList<ClaudeEvent>)Array.Empty<ClaudeEvent>();
        }

        private int IndexOfNewline()
        {
            for (var i = _scanned; i < _buffer.Length; i++)
            {
                if (_buffer[i] == '\n') return i;
            }
            _scanned = _buffer.Length;
            return -1;
        }

        /// <summary>
        /// A line is an event only when it parses and carries a string `type`. Anything
        /// else is noise and is dropped silently.
        /// </summary>
        private static ClaudeEvent ParseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            var trimmed = line.Trim();
            var parsed = Json.TryDeserialize<ClaudeEvent>(trimmed);
            return string.IsNullOrEmpty(parsed?.Type) ? null : parsed;
        }
    }
}
