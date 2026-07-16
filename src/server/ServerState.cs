using System;
using System.Collections.Generic;
using RvtMcp.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    internal static class ServerState
    {
        public static RvtMcpConfig Config { get; set; }

        /// <summary>
        /// MCP tool names (revit_*) actually registered for this run — the resolved
        /// toolset surface. Populated at startup by Program.RegisterToolsets. Used to
        /// authorize batch_execute child commands against the same surface a direct
        /// MCP call would see (SLS A4, Codex review finding 3).
        /// </summary>
        public static HashSet<string> EnabledToolNames { get; set; }

        public static bool IsReadOnly => Config?.ReadOnlyOrDefault ?? false;

        public static string BlockIfReadOnly(string toolName)
        {
            if (!IsReadOnly) return null;
            return JsonConvert.SerializeObject(new
            {
                error = "read_only_mode",
                tool = toolName,
                message = $"Tool '{toolName}' is disabled because the server is running with --read-only."
            }, Formatting.Indented);
        }

        /// <summary>
        /// Authorize every batch child command as if it were a direct MCP call: its
        /// revit_-prefixed name must be part of the enabled tool surface and must not
        /// be denied. Returns an error JSON string on the first violation, else null.
        /// Presence in the plugin's CommandDispatcher is NOT authorization.
        /// </summary>
        public static string ValidateBatchChildren(JArray commands)
        {
            if (commands == null) return null;

            var deny = new HashSet<string>(
                Config?.DenyToolsOrDefault ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var token in commands)
            {
                var bare = (token as JObject)?.Value<string>("command");
                if (string.IsNullOrWhiteSpace(bare))
                    continue; // the plugin-side executor reports the malformed entry

                var mcpName = bare.StartsWith("revit_", StringComparison.OrdinalIgnoreCase)
                    ? bare
                    : "revit_" + bare;

                if (deny.Contains(mcpName) || deny.Contains(bare))
                    return JsonConvert.SerializeObject(new
                    {
                        error = "denied_batch_command",
                        tool = mcpName,
                        message = $"Tool '{mcpName}' is denied on this server (--deny-tools) and cannot " +
                                  "be invoked through batch_execute."
                    }, Formatting.Indented);

                if (EnabledToolNames != null && !EnabledToolNames.Contains(mcpName))
                    return JsonConvert.SerializeObject(new
                    {
                        error = "unauthorized_batch_command",
                        tool = mcpName,
                        message = $"'{bare}' is not part of this server's enabled tool surface, so it " +
                                  "cannot be invoked through batch_execute."
                    }, Formatting.Indented);
            }

            return null;
        }
    }
}
