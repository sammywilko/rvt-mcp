using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// A6 batch execution (aspect #3 §A6). Wraps N MCP commands in a single
    /// <see cref="TransactionGroup"/> so the whole batch resolves to one Revit undo
    /// step. Inner handlers each open their own <see cref="Transaction"/>; TransactionGroup
    /// permits that (nested Transactions are forbidden, but inner-tx-inside-tx-group is fine).
    ///
    /// Iteration + rollback-decision logic lives in <see cref="BatchExecutor"/> so it can
    /// be unit-tested without a live document. This handler owns only the TransactionGroup
    /// lifecycle + the final Assimilate/RollBack call based on the outcome.
    /// </summary>
    public class BatchExecuteHandler : IRevitCommand
    {
        private readonly CommandDispatcher _dispatcher;

        public BatchExecuteHandler(CommandDispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public string Name => "batch_execute";
        public string Description => "Run multiple MCP commands in one Revit TransactionGroup (one undo step).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""commands"":{""type"":""array"",""items"":{""type"":""object""}},""continueOnError"":{""type"":""boolean""}},""required"":[""commands""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            // SLS A4 (Codex r3 finding 1): an SLS write inside a batch records into the
            // operation-group ledger when its inner transaction commits — but a later
            // batch failure rolls the MODEL back and not the ledger, splitting the two.
            // Batches and operation groups are therefore mutually exclusive.
            if (OperationGroupManager.IsActive)
                return CommandResult.Fail(
                    "batch_execute is unavailable while an operation group is open — a failed batch " +
                    "would roll the model back but not the group's ledger. Commit or roll back the " +
                    "operation group first.");

            var request = JObject.Parse(paramsJson);
            var commandsArr = request["commands"] as JArray;
            if (commandsArr == null || commandsArr.Count == 0)
                return CommandResult.Fail("'commands' must be a non-empty array.");
            // SLS A4 (Codex r2 finding 4): the batch drains synchronously on Revit's
            // UI thread — cap it here as well as at the server boundary.
            if (commandsArr.Count > BatchExecutor.MaxCommands)
                return CommandResult.Fail(
                    "'commands' contains " + commandsArr.Count + " entries; the maximum is " +
                    BatchExecutor.MaxCommands + ". Split the batch.");

            var continueOnError = request.Value<bool?>("continueOnError") ?? false;

            using (var tg = new TransactionGroup(doc, "MCP: batch_execute"))
            {
                if (tg.Start() != TransactionStatus.Started)
                    return CommandResult.Fail(
                        "batch_execute: could not start a transaction group (is another transaction open?).");

                var outcome = BatchExecutor.Run(commandsArr, continueOnError, (cmdName, subParams) =>
                {
                    var handler = _dispatcher.GetCommand(cmdName);
                    if (handler == null) return BatchExecutor.InvokeResult.Unknown();
                    // SLS A4 (Codex r2 finding 4): batch children previously bypassed the
                    // schema validation direct calls get in McpEventHandler — same
                    // contract on both paths now.
                    var validation = SchemaValidator.Validate(handler.ParametersSchema, subParams);
                    if (!validation.IsValid)
                        return BatchExecutor.InvokeResult.Fail("Validation failed: " + validation.Error);
                    var r = handler.Execute(app, subParams);
                    return r.Success
                        ? BatchExecutor.InvokeResult.Ok(r.Data)
                        : BatchExecutor.InvokeResult.Fail(r.Error);
                }, _dispatcher.IsBakedCommand, enforceBatchSafeList: true);

                bool rolledBack;
                if (outcome.AnyFailed && !continueOnError)
                {
                    // The contract says a failed batch is rolled back — anything else
                    // must surface as failure, not a calm envelope (Codex r3 finding 6).
                    var status = tg.RollBack();
                    if (status != TransactionStatus.RolledBack)
                        return CommandResult.Fail(
                            "batch_execute: a child failed and the rollback returned " + status +
                            " — model state is UNCERTAIN. Results so far: " +
                            Newtonsoft.Json.JsonConvert.SerializeObject(outcome.Results));
                    rolledBack = true;
                }
                else
                {
                    var status = tg.Assimilate();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail(
                            "batch_execute: assimilating the transaction group returned " + status +
                            "; the batch outcome is undefined. Results so far: " +
                            Newtonsoft.Json.JsonConvert.SerializeObject(outcome.Results));
                    rolledBack = false;
                }

                return CommandResult.Ok(new { results = outcome.Results, rolledBack });
            }
        }
    }
}
