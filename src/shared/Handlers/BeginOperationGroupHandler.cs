using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A4 operation groups (PRD §12.7 utility group): stage several writes,
    // land them as one undo entry, or discard them all. State + safety rules live
    // in OperationGroupManager.
    public class BeginOperationGroupHandler : IRevitCommand
    {
        public string Name => "begin_operation_group";
        public string Description =>
            "Open a named operation group: elements created by subsequent SLS writes are staged in a " +
            "ledger. commit_operation_group keeps them; rollback_operation_group deletes them all " +
            "(manual edits untouched). Returns a group_id required by commit/rollback. One group at a " +
            "time; after 10 min without writes it auto-closes KEEPING its elements.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""name"": { ""type"": ""string"", ""description"": ""Label for the group (becomes the Revit undo entry name on commit)"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var request = JObject.Parse(paramsJson);
            return OperationGroupManager.Begin(app, request.Value<string>("name"));
        }
    }
}
