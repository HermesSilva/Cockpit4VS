using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tootega.Cockpit.Protocol;

namespace Tootega.Cockpit.Cli
{
    internal sealed class McpServerGroup
    {
        /// <summary>Display name from mcp_servers[] when it matched, otherwise the sanitized key.</summary>
        public string Name { get; set; }
        /// <summary>The sanitized key used in the tool prefix.</summary>
        public string Key { get; set; }
        /// <summary>Status from init. Absent when the server was not announced there.</summary>
        public string Status { get; set; }
        /// <summary>Short tool names, without the prefix, in a stable order.</summary>
        public List<string> Tools { get; } = new List<string>();
    }

    internal sealed class McpInventoryResult
    {
        /// <summary>Servers with their tools, including ones announced with no tools at all.</summary>
        public List<McpServerGroup> Servers { get; } = new List<McpServerGroup>();
        /// <summary>Native agent tools (Read, Edit, Bash, …).</summary>
        public List<string> NativeTools { get; } = new List<string>();
    }

    internal static class McpListStatuses
    {
        public const string Connected = "connected";
        public const string Failed = "failed";
        public const string Pending = "pending";
        public const string Unknown = "unknown";
    }

    internal sealed class McpListEntry
    {
        public string Name { get; set; }
        /// <summary>Command (stdio) or URL (http/sse), already without the transport suffix.</summary>
        public string Target { get; set; }
        /// <summary>Declared remote transport. Absent means stdio.</summary>
        public string Transport { get; set; }
        /// <summary>A remote declared with no URL — the CLI labels it "not configured".</summary>
        public bool? NotConfigured { get; set; }
        public string Status { get; set; }
    }

    /// <summary>
    /// A server the CLI refused at config validation, normalized. Distinct from the wire type
    /// of the same idea in Protocol, which models the several shapes it can arrive in.
    /// </summary>
    internal sealed class McpConfigError
    {
        /// <summary>Server name when the CLI gave one; absent for a bare-string entry.</summary>
        public string Name { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Parses the CLI's MCP surfaces. Port of src/cli/McpInventory.ts.
    ///
    /// Deliberately pure: no process, no file system. All three inputs are text or JSON the CLI
    /// produced, and keeping the parsing separate is what makes the awkward parts — a tool-name
    /// scheme with an ambiguous separator, a status glyph that changed between versions —
    /// testable against real samples.
    /// </summary>
    internal static class McpInventory
    {
        private const string McpPrefix = "mcp__";

        /// <summary>
        /// A `claude mcp list` line. There is no --json for this command, so the output is
        /// parsed as text.
        /// </summary>
        private static readonly Regex ListLine = new Regex(@"^(.+?):\s(.*?)\s+-\s+(.+)$", RegexOptions.Compiled);

        /// <summary>The transport suffix the CLI appends to remote servers: "… (HTTP)".</summary>
        private static readonly Regex TransportSuffix = new Regex(@"^(.*?)\s*\(([A-Za-z]+)\)$", RegexOptions.Compiled);

        private static readonly Regex Unsafe = new Regex(@"[^A-Za-z0-9_-]", RegexOptions.Compiled);
        private static readonly Regex NamedError = new Regex(@"^([^:]{1,80}):\s*(.+)$", RegexOptions.Compiled);

        /// <summary>
        /// Builds the inventory from the raw lists of the init event.
        ///
        /// The tool naming is mcp__&lt;server&gt;__&lt;tool&gt;, where the server part is
        /// sanitized: anything outside [A-Za-z0-9_-] becomes '_'. Since a sanitized server name
        /// never contains a double underscore, the separator is the FIRST "__" after the
        /// prefix — splitting on the last one would cut a tool name like sql_execute_query in
        /// the wrong place.
        /// </summary>
        public static McpInventoryResult ParseInventory(IReadOnlyList<string> tools,
                                                        IReadOnlyList<McpServerRef> servers)
        {
            var result = new McpInventoryResult();
            // Insertion-ordered, so the panel lists servers as the CLI announced them.
            var groups = new List<McpServerGroup>();
            var byKey = new Dictionary<string, McpServerGroup>(StringComparer.Ordinal);

            // Seed from init first, so a server with zero tools still appears and carries its
            // display name and status.
            if (servers != null)
            {
                foreach (var server in servers)
                {
                    if (string.IsNullOrEmpty(server?.Name)) continue;

                    var key = Sanitize(server.Name);
                    if (byKey.ContainsKey(key)) continue;

                    var group = new McpServerGroup { Name = server.Name, Key = key, Status = server.Status };
                    byKey[key] = group;
                    groups.Add(group);
                }
            }

            if (tools != null)
            {
                foreach (var full in tools)
                {
                    if (string.IsNullOrEmpty(full)) continue;

                    if (!full.StartsWith(McpPrefix, StringComparison.Ordinal))
                    {
                        result.NativeTools.Add(full);
                        continue;
                    }

                    var rest = full.Substring(McpPrefix.Length);
                    var separator = rest.IndexOf("__", StringComparison.Ordinal);

                    if (separator < 0)
                    {
                        // "mcp__something" with no tool separator: a server with no named tool.
                        EnsureGroup(byKey, groups, rest);
                        continue;
                    }

                    var key = rest.Substring(0, separator);
                    var tool = rest.Substring(separator + 2);

                    var group = EnsureGroup(byKey, groups, key);
                    if (!string.IsNullOrEmpty(tool) && !group.Tools.Contains(tool)) group.Tools.Add(tool);
                }
            }

            result.Servers.AddRange(groups);
            return result;
        }

        private static McpServerGroup EnsureGroup(Dictionary<string, McpServerGroup> byKey,
                                                  List<McpServerGroup> groups, string key)
        {
            if (byKey.TryGetValue(key, out var group)) return group;

            // Not announced in init, or announced under a diverging name: derived from the
            // prefix rather than dropped, since its tools are demonstrably present.
            group = new McpServerGroup { Name = key, Key = key };
            byKey[key] = group;
            groups.Add(group);
            return group;
        }

        /// <summary>
        /// Turns the stdout of `claude mcp list` into entries.
        ///
        /// This command is the only way to see a server the session did NOT connect: an
        /// unapproved .mcp.json server never reaches init, because the CLI will not start it.
        /// Lines that do not match are ignored — the output has headers and footers.
        /// </summary>
        public static IReadOnlyList<McpListEntry> ParseList(string stdout)
        {
            var entries = new List<McpListEntry>();
            if (string.IsNullOrEmpty(stdout)) return entries;

            foreach (var raw in stdout.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                var match = ListLine.Match(line);
                if (!match.Success) continue;

                var name = match.Groups[1].Value.Trim();
                if (name.Length == 0) continue;

                var target = match.Groups[2].Value.Trim();
                string transport = null;

                // A remote server carries a "(HTTP)"/"(SSE)" suffix; stdio has none.
                var transportMatch = TransportSuffix.Match(target);
                if (transportMatch.Success)
                {
                    target = transportMatch.Groups[1].Value.Trim();
                    transport = transportMatch.Groups[2].Value.ToUpperInvariant();
                }

                // A remote declared without a URL leaves only the transport behind.
                var notConfigured = transport != null && target.Length == 0;

                entries.Add(new McpListEntry
                {
                    Name = name,
                    Target = target.Length > 0 ? target : null,
                    Transport = transport,
                    NotConfigured = notConfigured ? true : (bool?)null,
                    Status = ListStatus(match.Groups[3].Value),
                });
            }

            return entries;
        }

        /// <summary>
        /// Reads the status from the tail of a list line.
        ///
        /// Matched by WORD, never by glyph: the status symbol has already changed between CLI
        /// versions, and pinning to it would silently break the panel on an upgrade.
        /// </summary>
        internal static string ListStatus(string tail)
        {
            var text = (tail ?? string.Empty).ToLowerInvariant();

            if (text.IndexOf("pending", StringComparison.Ordinal) >= 0) return McpListStatuses.Pending;
            if (text.IndexOf("fail", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("error", StringComparison.Ordinal) >= 0) return McpListStatuses.Failed;
            if (text.IndexOf("connected", StringComparison.Ordinal) >= 0) return McpListStatuses.Connected;

            return McpListStatuses.Unknown;
        }

        /// <summary>
        /// Normalizes the init event's mcp_server_errors — servers the CLI refused to start at
        /// config validation, listed separately from the ones it did start.
        ///
        /// The shape varies, so both forms are accepted: a bare string ("weather: invalid url")
        /// or an object naming the server and the reason under one of several keys.
        /// </summary>
        public static IReadOnlyList<McpConfigError> ParseErrors(JsonElement? raw)
        {
            var errors = new List<McpConfigError>();
            if (raw?.ValueKind != JsonValueKind.Array) return errors;

            foreach (var entry in raw.Value.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    var text = entry.GetString()?.Trim();
                    if (string.IsNullOrEmpty(text)) continue;

                    var match = NamedError.Match(text);
                    if (match.Success)
                    {
                        errors.Add(new McpConfigError {
                            Name = match.Groups[1].Value.Trim(),
                            Error = match.Groups[2].Value.Trim(),
                        });
                    }
                    else
                    {
                        errors.Add(new McpConfigError { Error = text });
                    }
                }
                else if (entry.ValueKind == JsonValueKind.Object)
                {
                    var parsed = Protocol.Json.TryDeserialize<Protocol.McpServerError>(entry);
                    var name = parsed?.ServerName;
                    var reason = parsed?.Reason;
                    if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(reason)) continue;

                    errors.Add(new McpConfigError {
                        Name = string.IsNullOrEmpty(name) ? null : name,
                        // Something went wrong even if the CLI did not say what.
                        Error = string.IsNullOrEmpty(reason) ? "error" : reason,
                    });
                }
            }

            return errors;
        }

        /// <summary>Sanitizes a server name into the form used in the tool prefix.</summary>
        internal static string Sanitize(string name)
        {
            return string.IsNullOrEmpty(name) ? string.Empty : Unsafe.Replace(name, "_");
        }
    }
}
