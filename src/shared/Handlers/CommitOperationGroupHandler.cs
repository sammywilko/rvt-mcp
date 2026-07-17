using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A4 operation groups: commit the open group — its staged elements are kept
    // and the ledger closes. Requires the group_id ownership token from begin.
    public class CommitOperationGroupHandler : IRevitCommand
    {
        public string Name => "commit_operation_group";
        public string Description =>
            "Commit the open operation group: all elements created through SLS writes since " +
            "begin_operation_group are kept and the group closes. Requires the group_id returned by begin.";
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
            return OperationGroupManager.Commit(request.Value<string>("groupId"));
        }
    }
}
