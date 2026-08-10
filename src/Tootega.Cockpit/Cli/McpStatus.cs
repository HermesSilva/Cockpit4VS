using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Cli
{
    /// <summary>
    /// The MCP servers panel. Port of src/cli/McpStatus.ts.
    ///
    /// It joins the TWO sources the CLI offers, because neither is sufficient alone:
    ///
    ///  1. the session's init event, which brings the servers the session connected AND —
    ///     through the tool list — which tools each exposes. `mcp list` never says that. It is
    ///     free, since it is already in the stream.
    ///  2. `claude mcp list`, which reveals what init cannot see: servers from a .mcp.json that
    ///     is not approved yet, which the CLI refuses to start at all, plus each server's
    ///     command or URL. It costs a spawn and a health check, so it runs only when the user
    ///     opens the panel.
    /// </summary>
    internal static class McpStatus
    {
        /// <summary>A slow server's health check needs the room; eight seconds was not enough.</summary>
        private const int ListTimeoutMs = 15_000;

        /// <summary>Runs `claude mcp list`. A failure or timeout yields an empty list.</summary>
        public static async Task<IReadOnlyList<McpListEntry>> FetchListAsync(string claudePath)
        {
            try
            {
                var info = ProcessLauncher.Build(claudePath, new[] { "mcp", "list" }, null);
                info.RedirectStandardInput = false;

                using (var process = Process.Start(info))
                {
                    if (process == null) return Array.Empty<McpListEntry>();

                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();

                    var reading = Task.WhenAll(stdout, stderr);
                    var finished = await Task.WhenAny(reading, Task.Delay(ListTimeoutMs)).ConfigureAwait(false);
                    if (finished != reading)
                    {
                        ProcessLauncher.KillTree(process);
                        Log.Debug("mcp list: timed out");
                        return Array.Empty<McpListEntry>();
                    }

                    return McpInventory.ParseList(await stdout.ConfigureAwait(false));
                }
            }
            catch (Exception ex)
            {
                // The panel still has whatever init knew, which is the common case anyway.
                Log.Debug("mcp list failed: " + ex.Message);
                return Array.Empty<McpListEntry>();
            }
        }

        /// <summary>
        /// Merges init, the config errors and `mcp list` into one inventory. Pure logic.
        ///
        /// Servers are matched by exact name: init and `mcp list` use the same key. The
        /// `mcp list` status WINS where present — it is what distinguishes "pending approval"
        /// from "connected", and it was measured just now, whereas init's status is from
        /// whenever the session started.
        /// </summary>
        public static IReadOnlyList<McpServerInfo> Merge(IReadOnlyList<string> tools,
                                                         IReadOnlyList<McpServerRef> initServers,
                                                         IReadOnlyList<McpListEntry> list,
                                                         IReadOnlyList<McpConfigError> errors = null)
        {
            var inventory = McpInventory.ParseInventory(tools, initServers);
            var byName = new Dictionary<string, McpServerInfo>(StringComparer.Ordinal);
            var order = new List<string>();

            void Remember(string key, McpServerInfo server)
            {
                if (!byName.ContainsKey(key)) order.Add(key);
                byName[key] = server;
            }

            foreach (var group in inventory.Servers)
            {
                Remember(group.Name, new McpServerInfo
                {
                    Name = group.Name,
                    Status = NormalizeStatus(group.Status),
                    Tools = group.Tools,
                    Connected = group.Status == McpListStatuses.Connected,
                });
            }

            // Config errors from init: servers the CLI skipped at validation. A named error
            // attaches to its server and forces 'failed'; a nameless one becomes its own row,
            // because the user still needs to see that something was refused.
            var anonymous = 0;
            foreach (var error in errors ?? Array.Empty<McpConfigError>())
            {
                if (!string.IsNullOrEmpty(error.Name))
                {
                    if (byName.TryGetValue(error.Name, out var existing))
                    {
                        existing.Error = error.Error;
                        existing.Status = McpListStatuses.Failed;
                        existing.Connected = false;
                    }
                    else
                    {
                        Remember(error.Name, new McpServerInfo
                        {
                            Name = error.Name,
                            Status = McpListStatuses.Failed,
                            Connected = false,
                            Error = error.Error,
                            Tools = new List<string>(),
                        });
                    }
                }
                else
                {
                    Remember(" err" + anonymous++, new McpServerInfo
                    {
                        Name = "mcp",
                        Status = McpListStatuses.Failed,
                        Connected = false,
                        Error = error.Error,
                        Tools = new List<string>(),
                    });
                }
            }

            foreach (var entry in list ?? Array.Empty<McpListEntry>())
            {
                if (byName.TryGetValue(entry.Name, out var existing))
                {
                    existing.Status = entry.Status;
                    existing.Target = entry.Target;
                    existing.Transport = entry.Transport;
                    existing.NotConfigured = entry.NotConfigured;
                    existing.Connected = entry.Status == McpListStatuses.Connected;
                }
                else
                {
                    // Present only in `mcp list`: typically a server pending approval. The
                    // session never started it, so there are no tools to show.
                    Remember(entry.Name, new McpServerInfo
                    {
                        Name = entry.Name,
                        Status = entry.Status,
                        Target = entry.Target,
                        Transport = entry.Transport,
                        NotConfigured = entry.NotConfigured,
                        Tools = new List<string>(),
                        Connected = entry.Status == McpListStatuses.Connected,
                    });
                }
            }

            // Pending and failed first — those are the rows that need the user to do
            // something — then alphabetically.
            return order
                .Select(key => byName[key])
                .OrderBy(Rank)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int Rank(McpServerInfo server)
        {
            if (server.Status == McpListStatuses.Pending) return 0;
            if (server.Status == McpListStatuses.Failed) return 1;
            return 2;
        }

        /// <summary>Puts an init status on the same scale as an `mcp list` one.</summary>
        internal static string NormalizeStatus(string status)
        {
            switch ((status ?? string.Empty).ToLowerInvariant())
            {
                case "connected": return McpListStatuses.Connected;
                case "failed":
                case "error": return McpListStatuses.Failed;
                case "pending":
                case "needs-auth": return McpListStatuses.Pending;
                default: return McpListStatuses.Unknown;
            }
        }
    }
}
