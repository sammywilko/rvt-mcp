using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A4 operation groups: roll back the open group — every element created
    // through SLS writes in the group is deleted (compensating delete, one
    // transaction). Manual edits are never touched. Requires the group_id token.
    public class RollbackOperationGroupHandler : IRevitCommand
    {
        public string Name => "rollback_operation_group";
        public string Description =>
            "Roll back the open operation group: every element created through SLS writes since " +
            "begin_operation_group is deleted in one transaction (manual edits are untouched). " +
            "Requires the group_id returned by begin.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""groupId""],
  ""properties"": {
    ""groupId"": { ""type"": ""string"", ""description"": ""The group_id returned by begin_operation_group"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var request = JObject.Parse(paramsJson);
            return OperationGroupManager.Rollback(app, request.Value<string>("groupId"));
        }
    }
}
