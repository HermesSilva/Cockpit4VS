using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    /// <summary>
    /// The UTF-8 fix for Windows shell tool output. Port of src/cli/Utf8HookInstaller.ts.
    ///
    /// The problem: the Cockpit starts `claude` headless, over pipes, with no console attached.
    /// Without a console, .NET resolves the output encoding from the system OEM code page
    /// instead of UTF-8, so PowerShell writes its output in a legacy page and the CLI — which
    /// reads UTF-8 — shows mojibake. Worse, characters outside that code page are LOST at write
    /// time, so no amount of decoding on our side can recover them. In a terminal this never
    /// happens, because the console is already at 65001.
    ///
    /// The fix is a PreToolUse hook that prefixes each PowerShell command with the encoding
    /// setup. It depends on no machine code page, needs no reboot, and changes no system
    /// setting.
    ///
    /// Safety is the design constraint: the hook NEVER blocks or denies a tool. Every failure
    /// path is a silent no-op, it is idempotent through a marker, and it is reversible.
    /// </summary>
    internal static class Utf8HookInstaller
    {
        private const string HookFileName = "utf8-hook.ps1";

        /// <summary>Marker used both for prefix idempotency and to identify our entry.</summary>
        public const string Mark = "tootega-utf8";

        private static string HookPath => Path.Combine(ClaudeHome.Root, ".tootega", HookFileName);

        private static string HookCommand =>
            "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + HookPath + "\"";

        /// <summary>Whether one of the PreToolUse entries is ours, identified by script path.</summary>
        public static bool IsEnabled()
        {
            var settings = ClaudeSettingsFile.Load();
            var list = PreToolUseList(settings);
            return list != null && list.Any(IsOurEntry);
        }

        private static JsonArray PreToolUseList(JsonObject settings)
        {
            var hooks = settings?["hooks"] as JsonObject;
            return hooks?["PreToolUse"] as JsonArray;
        }

        private static bool IsOurEntry(JsonNode entry)
        {
            var hooks = (entry as JsonObject)?["hooks"] as JsonArray;
            if (hooks == null) return false;

            return hooks.Any(hook =>
            {
                var command = (hook as JsonObject)?["command"]?.GetValue<string>();
                return command != null && command.IndexOf(HookFileName, StringComparison.OrdinalIgnoreCase) >= 0;
            });
        }

        public static SettingsEditResult Enable()
        {
            // The problem, and the PowerShell tool itself, only exist on Windows. Elsewhere the
            // shells are already UTF-8 and there is nothing to install.
            if (!ProcessLauncher.IsWindows) return SettingsEditResult.Unsupported;

            var settings = ClaudeSettingsFile.Load();
            if (settings == null) return SettingsEditResult.ParseError;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HookPath));
                File.WriteAllText(HookPath, HookScript, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Error("could not write the UTF-8 hook", ex);
                return SettingsEditResult.WriteError;
            }

            if (!(settings["hooks"] is JsonObject hooks))
            {
                hooks = new JsonObject();
                settings["hooks"] = hooks;
            }

            // Rebuild the list keeping everyone else's entries: this is shared configuration,
            // and a previous version of ours is replaced rather than duplicated.
            var kept = new JsonArray();
            if (hooks["PreToolUse"] is JsonArray existing)
            {
                foreach (var entry in existing.ToList())
                {
                    if (IsOurEntry(entry)) continue;
                    existing.Remove(entry);
                    kept.Add(entry);
                }
            }

            kept.Add(new JsonObject
            {
                ["matcher"] = "PowerShell",
                ["hooks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = HookCommand,
                        ["timeout"] = 10,
                    },
                },
            });

            hooks["PreToolUse"] = kept;
            return ClaudeSettingsFile.Save(settings);
        }

        /// <summary>Removes the hook, preserving every other PreToolUse entry.</summary>
        public static SettingsEditResult Disable()
        {
            var settings = ClaudeSettingsFile.Load();
            if (settings == null) return SettingsEditResult.ParseError;

            if (settings["hooks"] is JsonObject hooks && hooks["PreToolUse"] is JsonArray list)
            {
                var kept = new JsonArray();
                foreach (var entry in list.ToList())
                {
                    if (IsOurEntry(entry)) continue;
                    list.Remove(entry);
                    kept.Add(entry);
                }

                // Leaving empty containers behind would be litter in someone else's file.
                if (kept.Count > 0) hooks["PreToolUse"] = kept;
                else hooks.Remove("PreToolUse");

                if (hooks.Count == 0) settings.Remove("hooks");
            }

            var result = ClaudeSettingsFile.Save(settings);

            try
            {
                if (File.Exists(HookPath)) File.Delete(HookPath);
            }
            catch
            {
                // Already gone, or locked; the settings entry is what matters.
            }

            return result;
        }

        /// <summary>
        /// The hook script.
        ///
        /// Two details are load-bearing. The prefix is joined with a bare LF rather than a
        /// semicolon, so comments, here-strings and multi-line commands still parse — and it
        /// must be LF, never CRLF, because the CLI validates the rewritten input and rejects
        /// control characters other than tab and newline. And the JSON reply is written as raw
        /// UTF-8 BYTES, because writing through the console would use the very OEM encoding
        /// this hook exists to work around, corrupting the reply.
        /// </summary>
        private const string HookScript = @"# Tootega Cockpit — PreToolUse: forces UTF-8 on the PowerShell tool output.
# Reads the hook event from stdin and returns the same tool_input with the command
# prefixed. Any failure means a silent no-op; it never blocks the tool.
$ErrorActionPreference = 'Stop'

$MARK = '# tootega-utf8'
$PREAMBLE = 'try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new(); $OutputEncoding = [System.Text.UTF8Encoding]::new() } catch {} ' + $MARK

try {
  # Reads stdin as explicit UTF-8, independent of the machine's console code page.
  $stdin = [System.IO.StreamReader]::new([Console]::OpenStandardInput(), [System.Text.UTF8Encoding]::new($false))
  $raw = $stdin.ReadToEnd()
  if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

  $ev = $raw | ConvertFrom-Json
  if ($ev.tool_name -ne 'PowerShell') { exit 0 }

  $ti = $ev.tool_input
  if ($null -eq $ti) { exit 0 }
  $cmd = [string]$ti.command
  if ([string]::IsNullOrEmpty($cmd)) { exit 0 }
  if ($cmd.Contains($MARK)) { exit 0 }   # already prefixed

  # LF rather than ';' so comments, here-strings and multi-line commands survive.
  # Pure LF, never CRLF: the CLI validates updatedInput and rejects control
  # characters other than TAB and LF, so a CR would invalidate the rewrite.
  $ti.command = $PREAMBLE + [string][char]10 + $cmd

  $out = [ordered]@{
    hookSpecificOutput = [ordered]@{
      hookEventName = 'PreToolUse'
      updatedInput  = $ti
    }
  }
  $json = $out | ConvertTo-Json -Depth 20 -Compress
  # Raw UTF-8 bytes straight to stdout: [Console]::Out would use the process output
  # encoding — the OEM page this hook exists to fix — and corrupt the JSON.
  $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)
  $stdout = [Console]::OpenStandardOutput()
  $stdout.Write($bytes, 0, $bytes.Length)
  $stdout.Flush()
} catch {
  exit 0
}
exit 0
";
    }
}
