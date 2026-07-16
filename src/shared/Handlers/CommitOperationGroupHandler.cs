using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A4 operation groups: commit (assimilate) the open group — every staged
    // write becomes a single named undo entry.
    public class CommitOperationGroupHandler : IRevitCommand
    {
        public string Name => "commit_operation_group";
        public string Description =>
            "Commit the open operation group: all writes staged since begin_operation_group become one " +
            "Revit undo entry named after the group.";
        public string ParametersSchema => @"{ ""type"": ""object"", ""properties"": {} }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            return OperationGroupManager.Commit();
        }
    }
}
