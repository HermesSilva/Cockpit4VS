using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    /// <summary>
    /// Installs and removes the statusline wrapper that captures the account's real
    /// rate_limits. Port of src/cli/StatuslineInstaller.ts.
    ///
    /// The wrapper does two things: it writes the statusline payload to a cache the Cockpit
    /// reads, and it re-invokes the user's ORIGINAL statusline so nothing they had stops
    /// working. That second half is what makes this acceptable at all — we are editing shared
    /// configuration, so the change has to be additive and reversible.
    ///
    /// The original command is remembered in two places: our own state store, and base64 inside
    /// the wrapper's own argument list. The second is what lets a re-enable or a disable recover
    /// the original even when the state store is empty — a wrapper installed by another machine
    /// or an older version would otherwise be impossible to undo cleanly.
    ///
    /// Windows only for now. Elsewhere the real usage comes from the OAuth /usage API, which is
    /// the primary cross-platform source, so there is nothing to install.
    /// </summary>
    internal sealed class StatuslineInstaller
    {
        private const string StateKey = "statuslineOriginal";
        private const string WrapperFileName = "statusline-wrapper.ps1";

        private static readonly Regex OriginalArgument =
            new Regex(@"-Original\s+""([^""]*)""", RegexOptions.Compiled);

        private readonly StateStore _state;

        public StatuslineInstaller(StateStore state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        private static string WrapperPath => Path.Combine(ClaudeHome.Root, ".tootega", WrapperFileName);

        /// <summary>Whether the installed statusline is ours.</summary>
        public static bool IsEnabled()
        {
            return IsOurWrapper(ClaudeSettingsFile.ReadString("statusLine", "command"));
        }

        private static bool IsOurWrapper(string command)
        {
            return command != null && command.IndexOf(WrapperFileName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Recovers the original statusline from a wrapper command's -Original argument. This is
        /// the fallback that keeps the operation reversible without our own state.
        /// </summary>
        internal static string DecodeOriginal(string command)
        {
            if (command == null) return null;
            var match = OriginalArgument.Match(command);
            if (!match.Success) return null;

            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value));
                return string.IsNullOrEmpty(decoded) ? null : decoded;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public SettingsEditResult Enable()
        {
            if (!ProcessLauncher.IsWindows) return SettingsEditResult.Unsupported;

            var settings = ClaudeSettingsFile.Load();
            // Null means the file exists but could not be parsed. Overwriting it would destroy
            // the user's configuration, so the caller reports it instead.
            if (settings == null) return SettingsEditResult.ParseError;

            var current = (settings["statusLine"] as JsonObject)?["command"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(current))
            {
                if (!IsOurWrapper(current))
                {
                    _state.Set(StateKey, current);
                }
                else if (string.IsNullOrEmpty(_state.GetString(StateKey)))
                {
                    // Re-enabling over a wrapper installed elsewhere: recover the original from
                    // the command itself, or disabling later would silently drop it.
                    var recovered = DecodeOriginal(current);
                    if (recovered != null) _state.Set(StateKey, recovered);
                }
            }

            var original = _state.GetString(StateKey, string.Empty);

            try
            {
                var wrapper = WrapperPath;
                Directory.CreateDirectory(Path.GetDirectoryName(wrapper));
                File.WriteAllText(wrapper, WrapperScript, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Error("could not write the statusline wrapper", ex);
                return SettingsEditResult.WriteError;
            }

            // Base64 so a command containing quotes, pipes or dollars survives being an
            // argument — the same reason the system prompt travels by file.
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(original ?? string.Empty));
            settings["statusLine"] = new JsonObject
            {
                ["type"] = "command",
                ["command"] = "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + WrapperPath +
                              "\" -Original \"" + encoded + "\"",
            };

            return ClaudeSettingsFile.Save(settings);
        }

        /// <summary>Removes the wrapper and restores whatever statusline was there before.</summary>
        public SettingsEditResult Disable()
        {
            var settings = ClaudeSettingsFile.Load();
            if (settings == null) return SettingsEditResult.ParseError;

            var original = _state.GetString(StateKey, string.Empty);
            if (string.IsNullOrEmpty(original))
            {
                var current = (settings["statusLine"] as JsonObject)?["command"]?.GetValue<string>();
                if (IsOurWrapper(current)) original = DecodeOriginal(current);
            }

            if (!string.IsNullOrEmpty(original))
            {
                settings["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = original };
            }
            else
            {
                // There was nothing before ours, so leaving an empty statusLine block behind
                // would be litter.
                settings.Remove("statusLine");
            }

            var result = ClaudeSettingsFile.Save(settings);
            if (result == SettingsEditResult.Ok) _state.Remove(StateKey);
            return result;
        }

        /// <summary>
        /// The wrapper itself.
        ///
        /// It caches the payload and then re-emits the original statusline, falling back to the
        /// caveman badge only when there was no original to run. Every step is wrapped in a
        /// try/catch that swallows: a statusline that throws would break the user's prompt, and
        /// this feature is not worth that.
        /// </summary>
        private const string WrapperScript = @"param([string]$Original = """")
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
$ClaudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $HOME "".claude"" }

$raw = """"
try { if ([Console]::IsInputRedirected) { $raw = [Console]::In.ReadToEnd() } } catch {}

# 1) Cache the payload for the Cockpit (rate limits + context + session flags).
if ($raw.Trim().Length -gt 0) {
  try {
    $j = $raw | ConvertFrom-Json
    $cache = [ordered]@{
      ts             = (Get-Date).ToUniversalTime().ToString(""o"")
      rate_limits    = $j.rate_limits
      context_window = $j.context_window
      model          = $j.model
      fast_mode      = $j.fast_mode
      effort         = $j.effort
      output_style   = $j.output_style
      # Session kind (interactive|attached|unattended), reported since CLI 2.1.221.
      # Tolerant: only stored when the payload carries it.
      session_kind   = if ($j.session_kind) { $j.session_kind } elseif ($j.session -and $j.session.kind) { $j.session.kind } else { $null }
    }
    ($cache | ConvertTo-Json -Depth 12) | Out-File -FilePath (Join-Path $ClaudeDir "".tootega-usage.json"") -Encoding utf8
  } catch {}
}

# 2) Re-emit the original statusline, so nothing the user had stops working.
$printed = $false
if ($Original.Length -gt 0) {
  try {
    $cmd = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Original))
    $res = ($raw | & $env:ComSpec /c $cmd 2>$null | Out-String)
    if ($null -ne $res -and $res.Trim().Length -gt 0) { [Console]::Write($res.TrimEnd()); $printed = $true }
  } catch {}
}
if (-not $printed) {
  $badge = """"
  $flag = Join-Path $ClaudeDir "".caveman-active""
  if (Test-Path $flag) { $badge = ""[CAVEMAN] "" }
  [Console]::Write($badge)
}
";
    }
}
