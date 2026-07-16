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
        /// Max child commands per batch — mirrors BatchExecutor.MaxCommands in the
        /// plugin (not referencable from the server assembly). Keep the two in sync.
        /// </summary>
        public const int MaxBatchCommands = 100;

        /// <summary>
        /// Authorize every batch child command as if it were a direct MCP call: its
        /// revit_-prefixed name must be part of the enabled tool surface and must not
        /// be denied; operation-group lifecycle commands are refused (their ledger
        /// state lives outside the batch TransactionGroup — Codex r2 finding 3).
        /// An optional revit_ prefix on a child is normalized IN PLACE to the bare
        /// wire name so authorization and dispatch agree (Codex r2 finding 4).
        /// Returns an error JSON string on the first violation, else null.
        /// Presence in the plugin's CommandDispatcher is NOT authorization.
        /// </summary>
        public static string ValidateBatchChildren(JArray commands)
        {
            if (commands == null) return null;

            if (commands.Count > MaxBatchCommands)
                return JsonConvert.SerializeObject(new
                {
                    error = "batch_too_large",
                    count = commands.Count,
                    message = $"'commands' contains {commands.Count} entries; the maximum is " +
                              $"{MaxBatchCommands}. Split the batch."
                }, Formatting.Indented);

            var deny = new HashSet<string>(
                Config?.DenyToolsOrDefault ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var token in commands)
            {
                var child = token as JObject;
                var raw = child?.Value<string>("command");
                if (string.IsNullOrWhiteSpace(raw))
                    continue; // the plugin-side executor reports the malformed entry

                var bare = raw.StartsWith("revit_", StringComparison.OrdinalIgnoreCase)
                    ? raw.Substring("revit_".Length)
                    : raw;
                var mcpName = "revit_" + bare;
                if (!string.Equals(raw, bare, StringComparison.Ordinal))
                    child["command"] = bare; // normalize for plugin dispatch

                if (string.Equals(bare, "begin_operation_group", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bare, "commit_operation_group", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bare, "rollback_operation_group", StringComparison.OrdinalIgnoreCase))
                    return JsonConvert.SerializeObject(new
                    {
                        error = "operation_group_in_batch",
                        tool = mcpName,
                        message = $"'{bare}' cannot run inside batch_execute: operation-group state lives " +
                                  "outside the batch's TransactionGroup, so a failed batch could not " +
                                  "restore it. Call it directly."
                    }, Formatting.Indented);

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
