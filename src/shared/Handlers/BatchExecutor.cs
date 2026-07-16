using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// A6 batch iteration logic, factored out of <see cref="BatchExecuteHandler"/> so it
    /// can be unit-tested without a live Revit document or TransactionGroup. The handler
    /// still owns the TransactionGroup Start/Assimilate/RollBack calls and decides what
    /// to do based on <see cref="Outcome.AnyFailed"/>.
    /// </summary>
    public static class BatchExecutor
    {
        /// <summary>
        /// Hard cap on commands per batch (SLS A4, Codex r2 finding 4): the batch
        /// drains synchronously on Revit's UI thread, so an unbounded list is a local
        /// denial of service. The server enforces the same cap at its boundary.
        /// </summary>
        public const int MaxCommands = 100;

        public class Outcome
        {
            public List<object> Results { get; set; } = new List<object>();
            public bool AnyFailed { get; set; }
        }

        /// <param name="commandsArr">The <c>commands</c> JArray from the request params.</param>
        /// <param name="continueOnError">If false, stops at first failure.</param>
        /// <param name="invoke">
        /// Called for each valid sub-command. Receives command name + params JSON, returns
        /// (success, dataOrError). Implementations wrap the real dispatcher in the handler;
        /// tests wrap a dictionary-backed stub.
        /// </param>
        /// <param name="isBakedCommand">
        /// Optional predicate used by the Revit handler to keep baked tools out of
        /// batch_execute; baked tools must run through run_baked_tool.
        /// </param>
        public static Outcome Run(
            JArray commandsArr,
            bool continueOnError,
            Func<string, string, InvokeResult> invoke,
            Func<string, bool> isBakedCommand = null)
        {
            if (commandsArr == null) throw new ArgumentNullException(nameof(commandsArr));
            if (invoke == null) throw new ArgumentNullException(nameof(invoke));

            var outcome = new Outcome();

            for (var i = 0; i < commandsArr.Count; i++)
            {
                var cmd = commandsArr[i] as JObject;
                var cmdName = cmd?.Value<string>("command");

                if (string.IsNullOrEmpty(cmdName))
                {
                    outcome.Results.Add(new { index = i, ok = false, error = "Missing 'command' field." });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                    continue;
                }

                // Nested batch_execute would double-wrap the TransactionGroup — block explicitly.
                if (string.Equals(cmdName, "batch_execute", StringComparison.Ordinal))
                {
                    outcome.Results.Add(new { index = i, ok = false, error = "Nested batch_execute is not supported." });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                    continue;
                }

                if (string.Equals(cmdName, "run_baked_tool", StringComparison.Ordinal))
                {
                    outcome.Results.Add(new { index = i, ok = false, error = RunBakedToolNotSupportedMessage() });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                    continue;
                }

                // SLS A4: eval commands are blocked here because batch_execute dispatches
                // wire-level command names directly to the plugin — without this, a batch
                // could smuggle send_code_to_revit past a server that has the toolbaker
                // toolset disabled (PRD §6.3/§9.3: no arbitrary code execution by default).
                // Both bare and revit_-prefixed spellings are normalized before checking.
                var bareName = cmdName.StartsWith("revit_", StringComparison.OrdinalIgnoreCase)
                    ? cmdName.Substring("revit_".Length)
                    : cmdName;
                if (string.Equals(bareName, "send_code_to_revit", StringComparison.Ordinal) ||
                    string.Equals(bareName, "apply_bake_suggestion", StringComparison.Ordinal))
                {
                    outcome.Results.Add(new { index = i, ok = false, error = EvalCommandNotSupportedMessage(bareName) });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                    continue;
                }

                // SLS A4 (Codex r2 finding 3): operation-group lifecycle commands mutate
                // ledger state OUTSIDE the batch's TransactionGroup — a later batch
                // failure would roll the model back but not the ledger, splitting the two
                // states. They must run as direct calls only.
                if (string.Equals(bareName, "begin_operation_group", StringComparison.Ordinal) ||
                    string.Equals(bareName, "commit_operation_group", StringComparison.Ordinal) ||
                    string.Equals(bareName, "rollback_operation_group", StringComparison.Ordinal))
                {
                    outcome.Results.Add(new { index = i, ok = false, error = OperationGroupCommandNotSupportedMessage(bareName) });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                    continue;
                }

                if (isBakedCommand != null && isBakedCommand(cmdName))
                {
                    outcome.Results.Add(new { index = i, ok = false, error = BakedCommandNotSupportedMessage(cmdName) });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                    continue;
                }

                var subParams = cmd["params"]?.ToString() ?? "{}";
                InvokeResult r;
                try
                {
                    r = invoke(cmdName, subParams);
                }
                catch (Exception ex)
                {
                    outcome.Results.Add(new { index = i, ok = false, error = ex.Message });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                    continue;
                }

                if (r.UnknownCommand)
                {
                    outcome.Results.Add(new { index = i, ok = false, error = $"Unknown command: {cmdName}" });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                    continue;
                }

                if (r.Success)
                {
                    if (TryGetPartialFailureCount(r.Data, out var failedCount))
                    {
                        outcome.Results.Add(new
                        {
                            index = i,
                            ok = false,
                            error = $"Command completed with {failedCount} partial failure(s).",
                            data = r.Data
                        });
                        outcome.AnyFailed = true;
                        if (!continueOnError) return outcome;
                        continue;
                    }

                    outcome.Results.Add(new { index = i, ok = true, data = r.Data });
                }
                else
                {
                    outcome.Results.Add(new { index = i, ok = false, error = r.Error });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                }
            }

            return outcome;
        }

        public static string BakedCommandNotSupportedMessage(string name) =>
            $"Baked tool '{name}' cannot be run through batch_execute. Use run_baked_tool instead.";

        public static string RunBakedToolNotSupportedMessage() =>
            "run_baked_tool cannot be invoked through batch_execute; call run_baked_tool directly.";

        public static string EvalCommandNotSupportedMessage(string name) =>
            $"'{name}' cannot run inside batch_execute; call it directly (if it is enabled on this server).";

        public static string OperationGroupCommandNotSupportedMessage(string name) =>
            $"'{name}' cannot run inside batch_execute: operation-group state lives outside the batch's " +
            "TransactionGroup, so a later batch failure could not restore it. Call it directly.";

        private static bool TryGetPartialFailureCount(object data, out int failedCount)
        {
            failedCount = 0;
            if (data == null)
                return false;

            try
            {
                var obj = data as JObject ?? JObject.FromObject(data);
                var token = obj["failedCount"];
                if (token == null || token.Type != JTokenType.Integer)
                    return false;

                failedCount = token.Value<int>();
                return failedCount > 0;
            }
            catch
            {
                failedCount = 0;
                return false;
            }
        }

        public class InvokeResult
        {
            public bool Success { get; set; }
            public object Data { get; set; }
            public string Error { get; set; }
            public bool UnknownCommand { get; set; }

            public static InvokeResult Ok(object data) =>
                new InvokeResult { Success = true, Data = data };

            public static InvokeResult Fail(string error) =>
                new InvokeResult { Success = false, Error = error };

            public static InvokeResult Unknown() =>
                new InvokeResult { UnknownCommand = true };
        }
    }
}
