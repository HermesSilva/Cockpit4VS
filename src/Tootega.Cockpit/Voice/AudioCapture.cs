using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Voice
{
    /// <summary>
    /// Microphone capture in the HOST, through ffmpeg. Port of src/cli/AudioCapture.ts.
    ///
    /// It runs in the host rather than the webview because an embedded browser refuses
    /// getUserMedia — the same reason the VS Code original does it this way. Output is PCM16 at
    /// 16 kHz mono on stdout, which is what the speech WebSocket expects.
    ///
    /// Per platform: dshow on Windows, avfoundation on macOS, pulse on Linux.
    /// </summary>
    internal sealed class AudioCapture : IDisposable
    {
        private const int SampleRate = 16000;

        /// <summary>
        /// 100 ms per frame: 16000 samples/s × 0.1 s × 2 bytes. ffmpeg's stdout arrives in
        /// large bursts, so it is re-sliced — small regular frames are what the server's
        /// endpointing expects, and a burst confuses its silence detection.
        /// </summary>
        private const int FrameBytes = 3200;

        private static readonly Regex QuotedName = new Regex("\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex AudioSection = new Regex("DirectShow audio devices",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly string _ffmpegPath;
        private Process _process;
        private volatile bool _stopped;

        public AudioCapture(string ffmpegPath = null)
        {
            _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();
        }

        /// <summary>
        /// Starts capturing. <paramref name="onData"/> receives PCM16 frames;
        /// <paramref name="onError"/> fires when ffmpeg cannot start or dies.
        ///
        /// The error strings are stable identifiers rather than prose, because the UI has to
        /// tell "ffmpeg is not installed" from "this machine has no microphone" and say
        /// something useful about each.
        /// </summary>
        public async Task StartAsync(Action<byte[]> onData, Action<string> onError, Action onExit)
        {
            string device = null;

            if (ProcessLauncher.IsWindows)
            {
                device = await ListWindowsAudioDeviceAsync().ConfigureAwait(false);
                if (_stopped) return;

                if (device == null)
                {
                    onError("no-audio-device");
                    return;
                }

                Log.Debug("voice: capture device " + device);
            }

            var args = BuildArgs(device);

            Process process;
            try
            {
                process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        Arguments = CliArguments.ToCommandLine(args),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    },
                    EnableRaisingEvents = true,
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) Log.Debug("voice: ffmpeg: " + e.Data.Trim());
                };

                process.Exited += (s, e) =>
                {
                    Log.Debug("voice: ffmpeg exited");
                    onExit?.Invoke();
                };

                if (!process.Start())
                {
                    onError("ffmpeg-spawn");
                    return;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The binary is not on the PATH — by far the most common failure, and the one
                // the user can actually fix.
                onError("ffmpeg-not-found");
                return;
            }
            catch (Exception ex)
            {
                onError("ffmpeg-spawn: " + ex.Message);
                return;
            }

            _process = process;
            process.BeginErrorReadLine();

            // Read on a dedicated thread: this is a continuous byte stream for as long as the
            // user is speaking.
            _ = Task.Run(() => PumpAsync(process, onData));
        }

        private async Task PumpAsync(Process process, Action<byte[]> onData)
        {
            var buffer = new byte[FrameBytes * 4];
            var frame = new byte[FrameBytes];
            var filled = 0;

            try
            {
                var stream = process.StandardOutput.BaseStream;
                while (!_stopped)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read <= 0) break;

                    var offset = 0;
                    while (offset < read)
                    {
                        var take = Math.Min(FrameBytes - filled, read - offset);
                        Buffer.BlockCopy(buffer, offset, frame, filled, take);
                        filled += take;
                        offset += take;

                        if (filled < FrameBytes) continue;

                        var copy = new byte[FrameBytes];
                        Buffer.BlockCopy(frame, 0, copy, 0, FrameBytes);
                        onData?.Invoke(copy);
                        filled = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                // A closed pipe on stop is the normal ending, not a failure.
                Log.Debug("voice: capture ended: " + ex.Message);
            }
        }

        /// <summary>ffmpeg arguments: microphone in, PCM16 16 kHz mono out on stdout.</summary>
        internal string[] BuildArgs(string device)
        {
            var input = InputArgs(device);
            // 3 leading flags + the platform input + 7 output flags.
            var args = new string[input.Length + 10];

            args[0] = "-hide_banner";
            args[1] = "-loglevel";
            args[2] = "error";
            Array.Copy(input, 0, args, 3, input.Length);

            var i = 3 + input.Length;
            args[i++] = "-ar";
            args[i++] = SampleRate.ToString();
            args[i++] = "-ac";
            args[i++] = "1";
            args[i++] = "-f";
            args[i++] = "s16le";
            args[i] = "pipe:1";

            return args;
        }

        private static string[] InputArgs(string device)
        {
            if (ProcessLauncher.IsWindows)
                return new[] { "-f", "dshow", "-i", "audio=" + (device ?? "default") };

            if (Environment.OSVersion.Platform == PlatformID.MacOSX)
                return new[] { "-f", "avfoundation", "-i", ":default" };

            return new[] { "-f", "pulse", "-i", "default" };
        }

        /// <summary>
        /// Enumerates dshow devices and returns the first microphone.
        ///
        /// ffmpeg prints the device list on STDERR and exits non-zero, which is normal for
        /// this invocation — the listing is the point, not the exit code.
        /// </summary>
        private async Task<string> ListWindowsAudioDeviceAsync()
        {
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = CliArguments.ToCommandLine(new[]
                    {
                        "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy",
                    }),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using (var process = Process.Start(info))
                {
                    if (process == null) return null;

                    var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    process.WaitForExit(5000);

                    return FirstAudioDevice(stderr);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Reads the first quoted name after the audio-devices header.</summary>
        internal static string FirstAudioDevice(string ffmpegStderr)
        {
            if (string.IsNullOrEmpty(ffmpegStderr)) return null;

            var inAudioSection = false;
            foreach (var line in ffmpegStderr.Split('\n'))
            {
                if (AudioSection.IsMatch(line))
                {
                    inAudioSection = true;
                    continue;
                }

                // Video devices are listed first; only names after the audio header count.
                if (!inAudioSection) continue;

                var match = QuotedName.Match(line);
                if (match.Success) return match.Groups[1].Value;
            }

            return null;
        }

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;

            var process = _process;
            _process = null;
            if (process == null) return;

            try
            {
                if (process.HasExited) return;
            }
            catch
            {
                return;
            }

            // ffmpeg has no console here, so there is no clean signal to send: the tree is
            // ended and the stream simply stops.
            ProcessLauncher.KillTree(process);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
