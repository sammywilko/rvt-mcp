using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A4 operation groups: roll back the open group — the model returns to its
    // pre-group state (the S4 "stage rollback" mechanism).
    public class RollbackOperationGroupHandler : IRevitCommand
    {
        public string Name => "rollback_operation_group";
        public string Description =>
            "Roll back the open operation group: every write staged since begin_operation_group is " +
            "discarded and the model returns to its pre-group state.";
        public string ParametersSchema => @"{ ""type"": ""object"", ""properties"": {} }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            return OperationGroupManager.Rollback();
        }
    }
}
