using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    /// <summary>The outcome of a settings edit, in the terms the UI reports to the user.</summary>
    internal enum SettingsEditResult
    {
        Ok,
        /// <summary>The file exists but could not be parsed — most often it has comments.</summary>
        ParseError,
        /// <summary>Not applicable on this platform.</summary>
        Unsupported,
        WriteError,
    }

    /// <summary>
    /// Reads and edits ~/.claude/settings.json — the CLI's own user settings, shared with the
    /// CLI itself.
    ///
    /// Two rules follow from it being someone else's file. First, edits are surgical: the
    /// document is loaded as a node tree and only the target key is replaced, so unrelated
    /// settings keep their values AND their order. Second, a file we cannot parse is never
    /// overwritten — JSON with comments is valid for the CLI but not for a strict parser, and
    /// silently rewriting it would destroy the user's configuration. In that case the caller
    /// reports the problem and asks them to edit it by hand.
    /// </summary>
    internal static class ClaudeSettingsFile
    {
        public static string Path => ClaudeHome.SettingsFile;

        /// <summary>
        /// Loads the settings as a mutable tree.
        ///
        /// Returns an empty object when the file does not exist, and null when it exists but
        /// cannot be parsed — the two cases must not be confused, because one is safe to write
        /// and the other is not.
        /// </summary>
        public static JsonObject Load()
        {
            string raw;
            try
            {
                if (!File.Exists(Path)) return new JsonObject();
                raw = File.ReadAllText(Path);
            }
            catch (Exception ex)
            {
                Log.Debug("could not read " + Path + ": " + ex.Message);
                return null;
            }

            if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();

            try
            {
                return JsonNode.Parse(raw, null, new JsonDocumentOptions
                {
                    // The CLI tolerates both, so we must at least manage to read them.
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Writes the tree back, indented to match what the CLI produces.
        ///
        /// Note the trade-off this makes explicit: comments in the original are lost, because no
        /// strict JSON writer can preserve them. That is why <see cref="Load"/> refusing to
        /// parse must stop the edit rather than fall back to writing a fresh document.
        /// </summary>
        public static SettingsEditResult Save(JsonObject settings)
        {
            if (settings == null) return SettingsEditResult.ParseError;

            try
            {
                var json = settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(Path, json, new UTF8Encoding(false));
                return SettingsEditResult.Ok;
            }
            catch (Exception ex)
            {
                Log.Error("could not write " + Path, ex);
                return SettingsEditResult.WriteError;
            }
        }

        /// <summary>Reads a nested string value, e.g. statusLine.command. Null when absent.</summary>
        public static string ReadString(params string[] path)
        {
            var settings = Load();
            if (settings == null || path == null || path.Length == 0) return null;

            JsonNode node = settings;
            foreach (var segment in path)
            {
                if (!(node is JsonObject obj) || !obj.TryGetPropertyValue(segment, out node)) return null;
            }

            return node?.GetValue<string>();
        }
    }
}
