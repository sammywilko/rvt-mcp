using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// SLS A4 (Codex review finding 3): batch_execute child commands must be
    /// authorized against the resolved tool surface + deny list exactly like a
    /// direct MCP call — presence in the plugin's CommandDispatcher is not
    /// authorization.
    /// </summary>
    [Collection("ServerStateBatch")]
    public class ServerStateBatchTests : IDisposable
    {
        private readonly RvtMcpConfig _savedConfig;
        private readonly HashSet<string> _savedNames;

        public ServerStateBatchTests()
        {
            _savedConfig = ServerState.Config;
            _savedNames = ServerState.EnabledToolNames;
        }

        public void Dispose()
        {
            ServerState.Config = _savedConfig;
            ServerState.EnabledToolNames = _savedNames;
        }

        private static JArray Batch(params string[] commands)
        {
            var arr = new JArray();
            foreach (var c in commands)
                arr.Add(new JObject { ["command"] = c, ["params"] = new JObject() });
            return arr;
        }

        [Fact]
        public void DeniedChild_IsBlocked_EvenWithBareWireName()
        {
            ServerState.Config = new RvtMcpConfig { DenyTools = new List<string> { "revit_delete_element" } };
            ServerState.EnabledToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "revit_delete_element", "revit_create_level" };

            var error = ServerState.ValidateBatchChildren(Batch("delete_element"));

            Assert.NotNull(error);
            Assert.Contains("denied_batch_command", error);
            Assert.Contains("revit_delete_element", error);
        }

        [Fact]
        public void ChildOutsideEnabledSurface_IsBlocked()
        {
            ServerState.Config = new RvtMcpConfig();
            ServerState.EnabledToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "revit_create_level" };

            var error = ServerState.ValidateBatchChildren(Batch("create_level", "delete_element"));

            Assert.NotNull(error);
            Assert.Contains("unauthorized_batch_command", error);
            Assert.Contains("delete_element", error);
        }

        [Fact]
        public void EnabledUndeniedChildren_Pass()
        {
            ServerState.Config = new RvtMcpConfig();
            ServerState.EnabledToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "revit_create_level", "revit_create_grid" };

            Assert.Null(ServerState.ValidateBatchChildren(Batch("create_level", "revit_create_grid")));
        }

        [Fact]
        public void MalformedEntries_AreLeftToThePluginExecutor()
        {
            ServerState.Config = new RvtMcpConfig();
            ServerState.EnabledToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "revit_create_level" };

            var arr = new JArray { new JObject { ["params"] = new JObject() } };
            Assert.Null(ServerState.ValidateBatchChildren(arr));
        }

        [Theory]
        [InlineData("begin_operation_group")]
        [InlineData("commit_operation_group")]
        [InlineData("rollback_operation_group")]
        [InlineData("revit_rollback_operation_group")]
        public void OperationGroupLifecycle_IsRefusedInBatch(string command)
        {
            ServerState.Config = new RvtMcpConfig();
            ServerState.EnabledToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "revit_begin_operation_group", "revit_commit_operation_group", "revit_rollback_operation_group" };

            var error = ServerState.ValidateBatchChildren(Batch(command));

            Assert.NotNull(error);
            Assert.Contains("operation_group_in_batch", error);
        }

        [Fact]
        public void PrefixedChild_IsNormalizedToBareWireName()
        {
            ServerState.Config = new RvtMcpConfig();
            ServerState.EnabledToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "revit_create_level" };

            var arr = Batch("revit_create_level");
            Assert.Null(ServerState.ValidateBatchChildren(arr));
            Assert.Equal("create_level", ((JObject)arr[0]).Value<string>("command"));
        }

        [Fact]
        public void EnabledButBatchUnsafeChild_IsRefused()
        {
            ServerState.Config = new RvtMcpConfig();
            ServerState.EnabledToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "revit_export_pdf" };

            var error = ServerState.ValidateBatchChildren(Batch("export_pdf"));

            Assert.NotNull(error);
            Assert.Contains("batch_unsafe_command", error);
        }

        [Fact]
        public void OversizedBatch_IsRefused()
        {
            ServerState.Config = new RvtMcpConfig();
            ServerState.EnabledToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "revit_create_level" };

            var names = new string[ServerState.MaxBatchCommands + 1];
            for (var i = 0; i < names.Length; i++) names[i] = "create_level";
            var error = ServerState.ValidateBatchChildren(Batch(names));

            Assert.NotNull(error);
            Assert.Contains("batch_too_large", error);
        }
    }
}
