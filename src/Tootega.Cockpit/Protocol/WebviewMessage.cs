using System.Collections.Generic;
using System.Text.Json;

namespace Tootega.Cockpit.Protocol
{
    /// <summary>
    /// One webview -&gt; host message. Port of the WebviewToHost union in shared/protocol.ts.
    ///
    /// The router branches on <see cref="Kind"/> and then reads either a couple of scalars
    /// or a typed payload. Keeping the raw element around is what makes the contract
    /// tolerant in this direction too: a webview that starts sending an extra field does
    /// not need a host release to be understood.
    /// </summary>
    internal sealed class WebviewMessage
    {
        private readonly JsonElement _root;

        private WebviewMessage(JsonElement root, string kind)
        {
            _root = root;
            Kind = kind;
        }

        public string Kind { get; }

        public JsonElement Raw => _root;

        /// <summary>Returns null when the payload is not an object or carries no `kind`.</summary>
        public static WebviewMessage Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return null;
                    if (!root.TryGetProperty("kind", out var kindProp)) return null;
                    if (kindProp.ValueKind != JsonValueKind.String) return null;
                    // Clone: the element is only valid while the document lives.
                    return new WebviewMessage(root.Clone(), kindProp.GetString());
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public string GetString(string key, string fallback = null)
        {
            return _root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : fallback;
        }

        public bool GetBool(string key, bool fallback = false)
        {
            if (!_root.TryGetProperty(key, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            return fallback;
        }

        public int GetInt(string key, int fallback = 0)
        {
            return _root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
                ? n
                : fallback;
        }

        public double GetDouble(string key, double fallback = 0)
        {
            return _root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)
                ? n
                : fallback;
        }

        /// <summary>The raw sub-element for a key, or null when absent.</summary>
        public JsonElement? GetElement(string key)
        {
            return _root.TryGetProperty(key, out var v) ? v : (JsonElement?)null;
        }

        public List<string> GetStringList(string key)
        {
            var element = GetElement(key);
            if (element == null || element.Value.ValueKind != JsonValueKind.Array) return null;
            var list = new List<string>();
            foreach (var item in element.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString());
            }
            return list;
        }

        /// <summary>Deserializes the whole message into a typed payload.</summary>
        public T As<T>()
        {
            return Json.TryDeserialize<T>(_root);
        }
    }

    /// <summary>Values of <see cref="WebviewMessage.Kind"/>, mirroring shared/protocol.ts.</summary>
    internal static class WebviewMessageKinds
    {
        // Lifecycle
        public const string Init = "init";
        public const string Heartbeat = "heartbeat";

        // Conversation
        public const string SendMessage = "sendMessage";
        public const string Interrupt = "interrupt";
        public const string NewSession = "newSession";
        public const string ClearContext = "clearContext";
        public const string CompactContext = "compactContext";
        public const string Rewind = "rewind";
        public const string ExportMd = "exportMd";
        public const string DraftChanged = "draftChanged";

        // Interactive protocols
        public const string PermissionDecision = "permissionDecision";
        public const string AskResponse = "askResponse";

        // Session configuration
        public const string SetModel = "setModel";
        public const string RemoveModel = "removeModel";
        public const string SetEffort = "setEffort";
        public const string SetPermissionMode = "setPermissionMode";
        public const string SetEngine = "setEngine";
        public const string SetAllowAgents = "setAllowAgents";
        public const string SetKeepCacheAlive = "setKeepCacheAlive";

        // Sessions
        public const string ListSessions = "listSessions";
        public const string ResumeSession = "resumeSession";
        public const string ReloadSession = "reloadSession";
        public const string RenameSession = "renameSession";
        public const string DeleteSession = "deleteSession";
        public const string DeleteAllSessions = "deleteAllSessions";
        public const string RemoteControl = "remoteControl";

        // Tabs
        public const string NewTab = "newTab";
        public const string CloseTab = "closeTab";
        public const string SwitchTab = "switchTab";

        /// <summary>
        /// Moves a tab to another folder. With no path the host asks for one, since the
        /// webview cannot show a folder picker.
        /// </summary>
        public const string SetTabCwd = "setTabCwd";

        // CLI and account
        public const string InstallCli = "installCli";
        public const string UpdateCli = "updateCli";
        public const string RecheckCli = "recheckCli";
        public const string LoginCli = "loginCli";
        public const string LogoutCli = "logoutCli";
        public const string FetchUsage = "fetchUsage";
        public const string EnableUsageTracking = "enableUsageTracking";

        // Editor integration
        public const string OpenLink = "openLink";
        public const string OpenSettings = "openSettings";
        public const string OpenEditor = "openEditor";
        public const string OpenFolder = "openFolder";
        public const string OpenDiff = "openDiff";
        public const string ResolvePaths = "resolvePaths";
        public const string ReadClipboardFiles = "readClipboardFiles";
        public const string MentionSearch = "mentionSearch";
        public const string SaveImage = "saveImage";
        public const string TaskDuration = "taskDuration";

        // Voice
        public const string VoiceStart = "voiceStart";
        public const string VoiceStop = "voiceStop";
        public const string VoiceCorrect = "voiceCorrect";
        public const string VoiceDictGet = "voiceDictGet";
        public const string VoiceDictSave = "voiceDictSave";

        // Spell checker
        public const string SpellCheck = "spellCheck";
        public const string SpellSuggest = "spellSuggest";
        public const string SpellAdd = "spellAdd";

        // Extensibility
        public const string PluginsRefresh = "pluginsRefresh";
        public const string PluginAction = "pluginAction";
        public const string McpRefresh = "mcpRefresh";
        public const string SkillsRefresh = "skillsRefresh";
        public const string SkillOverrideSet = "skillOverrideSet";

        // Credentials vault
        public const string CredsLoad = "credsLoad";
        public const string CredsEnrollBegin = "credsEnrollBegin";
        public const string CredsEnrollConfirm = "credsEnrollConfirm";
        public const string CredsAdd = "credsAdd";
        public const string CredsEdit = "credsEdit";
        public const string CredsUse = "credsUse";
        public const string CredsDelete = "credsDelete";
    }

    // --- Typed payloads for the messages carrying more than a scalar or two ---

    internal sealed class SendMessagePayload
    {
        public string Text { get; set; }
        public List<ImageAttachment> Images { get; set; }
        /// <summary>Set when the user confirmed past the minimum-effort gate.</summary>
        public bool? Force { get; set; }
        /// <summary>The @file#a-b reference the composer chip is sharing.</summary>
        public string Selection { get; set; }
    }

    internal sealed class PermissionDecisionPayload
    {
        public string RequestId { get; set; }
        /// <summary>allow | deny | allow_always</summary>
        public string Decision { get; set; }
        /// <summary>Feedback; in editable plan mode it carries the user's notes.</summary>
        public string Message { get; set; }
    }

    internal sealed class AskResponsePayload
    {
        public string RequestId { get; set; }
        public Dictionary<string, string> Answers { get; set; }
    }

    internal sealed class ExportMdPayload
    {
        public string Markdown { get; set; }
        public string FileName { get; set; }
        /// <summary>direct = already-built markdown; ai = rewritten via the CLI (spends tokens).</summary>
        public string Mode { get; set; }
    }

    internal sealed class OpenDiffPayload
    {
        public string Tool { get; set; }
        public JsonElement? Input { get; set; }
    }

    internal sealed class ResolvePathsPayload
    {
        public string RequestId { get; set; }
        public List<string> AbsPaths { get; set; }
    }

    internal sealed class SaveImagePayload
    {
        public string MediaType { get; set; }
        public string Data { get; set; }
    }

    internal sealed class PluginActionPayload
    {
        /// <summary>install | uninstall | enable | disable | update | marketAdd | marketRemove</summary>
        public string Action { get; set; }
        public string Arg { get; set; }
        public string Scope { get; set; }
    }

    internal sealed class SkillOverrideSetPayload
    {
        public string Name { get; set; }
        /// <summary>One of <see cref="SkillOverrides"/>.</summary>
        public string Value { get; set; }
    }

    internal sealed class VoiceDictSavePayload
    {
        public VoiceDictData Data { get; set; }
    }

    internal sealed class TaskDurationPayload
    {
        public string Type { get; set; }
        public double Ms { get; set; }
    }

    internal sealed class CredsAddPayload
    {
        /// <summary>The TOTP code authorizing the write.</summary>
        public string Code { get; set; }
        public string Name { get; set; }
        public string Username { get; set; }
        public string Value { get; set; }
        public string Note { get; set; }
    }

    internal sealed class CredsEditPayload
    {
        public string Code { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string Username { get; set; }
        /// <summary>Absent keeps the current secret; present replaces it.</summary>
        public string Value { get; set; }
        public string Note { get; set; }
    }
}
