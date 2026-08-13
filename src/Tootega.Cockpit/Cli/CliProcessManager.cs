using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    internal sealed class CliEventArgs : EventArgs
    {
        public CliEventArgs(ClaudeEvent value) { Event = value; }
        public ClaudeEvent Event { get; }
    }

    internal sealed class CliExitEventArgs : EventArgs
    {
        public CliExitEventArgs(int? code) { ExitCode = code; }
        public int? ExitCode { get; }
    }

    internal sealed class CliStderrEventArgs : EventArgs
    {
        public CliStderrEventArgs(string text) { Text = text; }
        public string Text { get; }
    }

    /// <summary>
    /// Runs the engine in headless/streaming mode and emits parsed events. Port of
    /// src/cli/CliProcessManager.ts.
    ///
    /// Threading: <see cref="Event"/>, <see cref="Stderr"/> and <see cref="Exit"/> are
    /// raised on a background reader thread. Handlers that touch VS or the WebView2 must
    /// marshal to the UI thread themselves — doing it here would serialize the token stream
    /// through the message pump.
    /// </summary>
    internal sealed class CliProcessManager : IDisposable
    {
        private readonly CliOptions _options;
        private readonly StreamParser _parser = new StreamParser();
        private readonly object _writeLock = new object();

        /// <summary>Our own control_requests awaiting a correlated response.</summary>
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _pendingControl =
            new ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>>();

        /// <summary>Temp files belonging to the current process; removed on stop.</summary>
        private readonly List<string> _tempFiles = new List<string>();

        /// <summary>Last stderr lines, kept for the post-mortem.</summary>
        private readonly Queue<string> _lastStderr = new Queue<string>();

        private Process _process;

        /// <summary>
        /// The prompt channel, in UTF-8 without a BOM.
        ///
        /// Not <c>Process.StandardInput</c>: on .NET Framework that writer is created with the
        /// console's ANSI code page, so "não" left as CP-1252 bytes and the CLI, which reads
        /// UTF-8, wrote replacement characters into the transcript. There is no
        /// StandardInputEncoding to set on this framework, so the stream is wrapped by hand.
        ///
        /// UTF-8 without a BOM is not a preference: it is what the VS Code extension writes,
        /// and a transcript has to be byte-for-byte usable from either editor.
        /// </summary>
        private StreamWriter _stdin;

        /// <summary>
        /// The stdout/stderr reader loops, held so they are observed rather than orphaned.
        /// They are never awaited: they run for the lifetime of the process and end when the
        /// pipes close.
        /// </summary>
        private Task _stdoutPump;
        private Task _stderrPump;

        private int _requestSequence;
        private long _startedAtTicks;
        private int _eventsSeen;
        private bool _stopping;
        private bool _disposed;

        public CliProcessManager(CliOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public event EventHandler<CliEventArgs> Event;
        public event EventHandler<CliExitEventArgs> Exit;
        public event EventHandler<CliStderrEventArgs> Stderr;

        public bool IsRunning => _process != null;

        /// <summary>
        /// Updates the session id to resume if the process has to respawn.
        ///
        /// The CLI only reveals the session_id in the init event. Without recording it, a
        /// silent restart (a write after the process died) would start WITHOUT --resume and
        /// create a second context on disk instead of continuing this one.
        /// </summary>
        public void SetResumeId(string sessionId)
        {
            if (!string.IsNullOrEmpty(sessionId)) _options.ResumeSessionId = sessionId;
        }

        /// <summary>Starts the process. Idempotent: does nothing when already running.</summary>
        public void Start()
        {
            if (_process != null) return;

            string promptFile = null;
            string settingsFile = null;

            if (_options.Engine != EngineIds.Tootega)
            {
                var appended = CliArguments.AppendedSystemPrompt(_options);
                // Only free text typed by the user needs the file route; the language rule
                // alone is a single safe line.
                var userText = !string.IsNullOrWhiteSpace(_options.ExtraSystemPrompt) ||
                               !string.IsNullOrWhiteSpace(_options.QuietPrompt);
                if (!string.IsNullOrEmpty(appended) && userText)
                    promptFile = WriteTempFile("prompt", appended);

                // Thinking stays OFF always, so this file is now written on every spawn (it
                // used to exist only when a skill was overridden). Two locks because the CLI
                // has two ways in: this setting and the MAX_THINKING_TOKENS budget (env, set
                // in SpawnWith). Closing one leaves the other in force.
                var settings = new Dictionary<string, object> { ["alwaysThinkingEnabled"] = false };
                if (_options.SkillOverrides != null && _options.SkillOverrides.Count > 0)
                    settings["skillOverrides"] = _options.SkillOverrides;
                settingsFile = WriteTempFile("settings", Json.Serialize(settings));
            }

            SpawnWith(CliArguments.For(_options, promptFile, settingsFile));
        }

        private void SpawnWith(IReadOnlyList<string> args)
        {
            // The exact command line, always — not behind the debug flag. "CLI exited (1)"
            // on its own says nothing: not which binary, which arguments, which directory,
            // nor what it printed before dying. Reproducing a failure by hand needs all four.
            Log.Info("spawn [" + (_options.Engine ?? EngineIds.Claude) + "] " +
                     _options.ExecutablePath + " " + string.Join(" ", args));
            Log.Info("  cwd=" + _options.Cwd);

            lock (_lastStderr) _lastStderr.Clear();

            try
            {
                var info = ProcessLauncher.Build(_options.ExecutablePath, args, _options.Cwd);

                // Auto mode (the CLI classifier decides allow/deny) is opt-in on
                // Bedrock/Vertex/Foundry via env (2.1.158/159). Setting it when the mode is
                // 'auto' makes behaviour uniform across providers. It does NOT bypass
                // permissions — it enables the CLI's native mode, which still routes what it
                // must through control_request.
                if (_options.PermissionMode == "auto")
                    info.EnvironmentVariables["CLAUDE_CODE_ENABLE_AUTO_MODE"] = "1";

                // Thinking stays off. This is the pair of alwaysThinkingEnabled:false in the
                // settings file — a budget inherited from the Visual Studio process
                // environment would switch reasoning back on despite the setting.
                info.EnvironmentVariables["MAX_THINKING_TOKENS"] = "0";

                var process = new Process { StartInfo = info, EnableRaisingEvents = true };
                process.Exited += OnProcessExited;

                if (!process.Start())
                {
                    Log.Error("spawn failed: the process did not start");
                    RaiseStderr("the engine process did not start");
                    return;
                }

                _process = process;
                _stdin = Utf8StdinFor(process);
                _startedAtTicks = Stopwatch.GetTimestamp();
                _eventsSeen = 0;
                _stopping = false;

                _stdoutPump = RunReaderLoopAsync(process.StandardOutput, PumpStdoutAsync);
                _stderrPump = RunReaderLoopAsync(process.StandardError, PumpStderrAsync);

                // Control-protocol handshake. Enables interactive routing (can_use_tool /
                // AskUserQuestion) and returns the slash-command list.
                WriteLine(new Dictionary<string, object>
                {
                    ["type"] = "control_request",
                    ["request_id"] = "init",
                    ["request"] = new Dictionary<string, object> { ["subtype"] = "initialize" },
                });
            }
            catch (Exception ex)
            {
                Log.Error("spawn failed", ex);
                RaiseStderr(ex.Message);
                _process = null;
                _stdin = null;
            }
        }

        private static Task RunReaderLoopAsync(StreamReader reader, Func<StreamReader, Task> pump)
        {
            return Task.Run(async () =>
            {
                try
                {
                    await pump(reader).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Debug("reader ended: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// Reads stdout in chunks rather than by line. The parser owns line splitting so it
        /// can enforce its own buffer cap; letting the framework split would give up that
        /// protection against a stream with no newline.
        /// </summary>
        private async Task PumpStdoutAsync(StreamReader reader)
        {
            var buffer = new char[16 * 1024];
            while (true)
            {
                var read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read <= 0) break;

                var chunk = new string(buffer, 0, read);
                foreach (var parsed in _parser.Push(chunk)) Dispatch(parsed);
            }

            foreach (var parsed in _parser.Flush()) Dispatch(parsed);
        }

        private async Task PumpStderrAsync(StreamReader reader)
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                RaiseStderr(line);
            }
        }

        private void Dispatch(ClaudeEvent value)
        {
            _eventsSeen++;
            // Every event type except the token-by-token flood, which would bury the log in
            // a single turn.
            if (value.Type != EventTypes.StreamEvent) Log.Debug("<- " + Describe(value));

            SettleControl(value);

            try
            {
                Event?.Invoke(this, new CliEventArgs(value));
            }
            catch (Exception ex)
            {
                Log.Error("event handler threw", ex);
            }
        }

        private void RaiseStderr(string text)
        {
            // An engine that dies at startup usually says why on stderr, and that text is
            // gone by the time anyone looks.
            lock (_lastStderr)
            {
                _lastStderr.Enqueue(text);
                while (_lastStderr.Count > 20) _lastStderr.Dequeue();
            }

            try
            {
                Stderr?.Invoke(this, new CliStderrEventArgs(text));
            }
            catch (Exception ex)
            {
                Log.Error("stderr handler threw", ex);
            }
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            var process = sender as Process;
            int? code = null;
            try
            {
                code = process?.ExitCode;
            }
            catch
            {
                // Already reaped; the code is simply unknown.
            }

            _process = null;

            _stdin = null;

            // A process WE killed reports a non-zero code on Windows (taskkill /F). Logging
            // that as a failure sends whoever reads the log hunting a crash that never
            // happened — which is exactly what switching engines used to look like.
            if (_stopping)
            {
                Log.Info("engine stopped on request (exit " + code + ")");
            }
            else if (code.GetValueOrDefault() != 0)
            {
                var elapsedMs = (Stopwatch.GetTimestamp() - _startedAtTicks) * 1000 / Stopwatch.Frequency;
                Log.Info("exit " + code + " after " + elapsedMs + " ms (" + _eventsSeen + " events)");
                string tail;
                lock (_lastStderr) tail = string.Join(Environment.NewLine, _lastStderr).Trim();
                Log.Info(string.IsNullOrEmpty(tail) ? "  stderr: (nothing)" : "  stderr: " + tail);
            }

            _stopping = false;
            ReleasePendingControl();

            try
            {
                Exit?.Invoke(this, new CliExitEventArgs(code));
            }
            catch (Exception ex)
            {
                Log.Error("exit handler threw", ex);
            }
        }

        /// <summary>Sends a user message through stdin (text plus optional images).</summary>
        public void SendUserMessage(string text, IReadOnlyList<ImageAttachment> images = null)
        {
            var content = new List<object>();
            if (!string.IsNullOrEmpty(text))
                content.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = text });

            if (images != null)
            {
                foreach (var image in images)
                {
                    content.Add(new Dictionary<string, object>
                    {
                        ["type"] = "image",
                        ["source"] = new Dictionary<string, object>
                        {
                            ["type"] = "base64",
                            ["media_type"] = image.MediaType,
                            ["data"] = image.Data,
                        },
                    });
                }
            }

            // The CLI rejects an empty content array, so a bare send still carries a block.
            if (content.Count == 0)
                content.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = string.Empty });

            WriteLine(new Dictionary<string, object>
            {
                ["type"] = "user",
                ["message"] = new Dictionary<string, object> { ["role"] = "user", ["content"] = content },
            });
        }

        /// <summary>
        /// Answers a can_use_tool control_request. <paramref name="response"/> is the
        /// decision payload: { behavior: "allow", updatedInput, … } or
        /// { behavior: "deny", message }.
        ///
        /// An allow REQUIRES updatedInput — the CLI validates with Zod, and returning only
        /// { behavior: "allow" } fails as a union error.
        /// </summary>
        public void SendControlResponse(string requestId, IDictionary<string, object> response)
        {
            WriteLine(new Dictionary<string, object>
            {
                ["type"] = "control_response",
                ["response"] = new Dictionary<string, object>
                {
                    ["subtype"] = "success",
                    ["request_id"] = requestId,
                    ["response"] = response,
                },
            });
        }

        /// <summary>
        /// Sends one of our own control_requests and awaits the response correlated by
        /// request_id. Used by get_context_usage, which is a local computation in the engine:
        /// no turn, no tokens, no transcript line.
        ///
        /// Resolves null on timeout or on an error reply — never throws — so a CLI version
        /// without the subtype degrades to "no data" instead of breaking the panel.
        /// </summary>
        public async Task<JsonElement?> RequestControlAsync(string subtype, int timeoutMs = 5000)
        {
            var id = subtype + "_" + Interlocked.Increment(ref _requestSequence);
            var completion = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingControl[id] = completion;

            WriteLine(new Dictionary<string, object>
            {
                ["type"] = "control_request",
                ["request_id"] = id,
                ["request"] = new Dictionary<string, object> { ["subtype"] = subtype },
            });

            var timeout = Task.Delay(timeoutMs);
            var finished = await Task.WhenAny(completion.Task, timeout).ConfigureAwait(false);
            if (finished != completion.Task)
            {
                _pendingControl.TryRemove(id, out _);
                Log.Debug("control_request " + id + " timed out");
                return null;
            }

            return await completion.Task.ConfigureAwait(false);
        }

        /// <summary>Resolves whichever of our control_requests was waiting on this id.</summary>
        private void SettleControl(ClaudeEvent value)
        {
            if (value.Type != EventTypes.ControlResponse) return;
            if (value.Extra == null || !value.Extra.TryGetValue("response", out var response)) return;
            if (response.ValueKind != JsonValueKind.Object) return;

            if (!response.TryGetProperty("request_id", out var idProp) || idProp.ValueKind != JsonValueKind.String) return;
            var id = idProp.GetString();
            if (id == null || !_pendingControl.TryRemove(id, out var completion)) return;

            var succeeded = response.TryGetProperty("subtype", out var subtype)
                            && subtype.ValueKind == JsonValueKind.String
                            && subtype.GetString() == "success";

            JsonElement? payload = null;
            if (succeeded && response.TryGetProperty("response", out var inner)) payload = inner.Clone();

            completion.TrySetResult(payload);
        }

        private void ReleasePendingControl()
        {
            // Nobody is going to answer now: release the waiters instead of leaving them
            // hanging until their timeout.
            foreach (var key in _pendingControl.Keys)
            {
                if (_pendingControl.TryRemove(key, out var completion)) completion.TrySetResult(null);
            }
        }

        /// <summary>Wraps a process's stdin in a UTF-8 writer, leaving its own alone.</summary>
        private static StreamWriter Utf8StdinFor(Process process)
        {
            try
            {
                return new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
            }
            catch (Exception ex)
            {
                // Falling back to the framework's writer keeps the conversation working, with
                // the accents it used to mangle. Better than no channel at all.
                Log.Error("stdin: could not open a UTF-8 channel; accented text may be mangled", ex);
                return process.StandardInput;
            }
        }

        private void WriteLine(object payload)
        {
            if (_process == null) Start();

            var line = Json.Serialize(payload);
            Log.Debug("-> " + (line.Length > 400 ? line.Substring(0, 400) + "…" : line));

            lock (_writeLock)
            {
                try
                {
                    var stdin = _stdin;
                    if (stdin == null) return;
                    stdin.Write(line);
                    stdin.Write('\n');
                    stdin.Flush();
                }
                catch (Exception ex)
                {
                    // A dead process is normal here: the next send restarts it.
                    Log.Debug("stdin write failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Interrupts the current turn through the control protocol, keeping the process AND
        /// the session alive. That way the next send continues the SAME session instead of
        /// respawning one, which would duplicate the context on disk. With no process there
        /// is nothing to interrupt.
        /// </summary>
        public void Interrupt()
        {
            if (_process == null) return;
            WriteLine(new Dictionary<string, object>
            {
                ["type"] = "control_request",
                ["request_id"] = "interrupt_" + Interlocked.Increment(ref _requestSequence),
                ["request"] = new Dictionary<string, object> { ["subtype"] = "interrupt" },
            });
        }

        public void Stop()
        {
            var process = _process;
            if (process == null) return;

            _process = null;
            _stopping = true;

            ReleasePendingControl();
            CleanTempFiles();

            try
            {
                // Closing our writer closes the underlying stream, which is the signal the CLI
                // waits for; closing the framework's writer as well would only throw.
                var stdin = _stdin;
                _stdin = null;
                if (stdin != null) stdin.Close();
                else process.StandardInput?.Close();
            }
            catch
            {
                // Already gone.
            }

            ProcessLauncher.KillTree(process);

            // The readers end by themselves once the pipes close, and they already handle
            // and log their own exit. Waiting for them here would block whichever thread
            // called Stop — often the UI thread, closing a tab — for no gain.
            _stdoutPump = null;
            _stderrPump = null;
        }

        /// <summary>
        /// Writes a temp file for this process (UTF-8) and returns its path, or null when the
        /// write fails — in which case the CLI starts without the corresponding flag rather
        /// than not starting at all.
        /// </summary>
        private string WriteTempFile(string kind, string content)
        {
            var extension = kind == "settings" ? "json" : "txt";
            var name = "cockpit-" + kind + "-" + Process.GetCurrentProcess().Id + "-" +
                       Interlocked.Increment(ref _requestSequence) + "." + extension;
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);

            try
            {
                // No BOM: the CLI reads these as plain UTF-8 and a BOM would land in the
                // prompt text.
                File.WriteAllText(path, content, new UTF8Encoding(false));
                lock (_tempFiles) _tempFiles.Add(path);
                return path;
            }
            catch (Exception ex)
            {
                Log.Debug("could not write temp file " + path + ": " + ex.Message);
                return null;
            }
        }

        private void CleanTempFiles()
        {
            lock (_tempFiles)
            {
                foreach (var file in _tempFiles)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // A temp file left behind disappears on the next boot.
                    }
                }
                _tempFiles.Clear();
            }
        }

        /// <summary>One readable line per event, for the debug log.</summary>
        private static string Describe(ClaudeEvent value)
        {
            var parts = new List<string> { value.Type };
            if (!string.IsNullOrEmpty(value.Subtype)) parts.Add(value.Subtype);

            var message = value.AsAssistantMessage();
            if (message?.Content != null)
            {
                var blocks = new List<string>();
                foreach (var block in message.Content)
                {
                    blocks.Add(block.Type == "tool_use" ? "tool_use:" + block.Name : block.Type);
                }
                if (blocks.Count > 0) parts.Add(string.Join(",", blocks));
            }

            if (value.Request?.Subtype != null)
            {
                parts.Add(value.Request.Subtype +
                          (value.Request.ToolName != null ? ":" + value.Request.ToolName : string.Empty));
            }

            if (value.IsError == true) parts.Add("IS_ERROR");
            return string.Join(" ", parts);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
