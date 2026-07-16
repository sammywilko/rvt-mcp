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
                }, _dispatcher.IsBakedCommand);

                bool rolledBack;
                if (outcome.AnyFailed && !continueOnError)
                {
                    rolledBack = tg.RollBack() == TransactionStatus.RolledBack;
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
