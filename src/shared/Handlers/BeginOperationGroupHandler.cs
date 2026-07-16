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
            "Open a named operation group (TransactionGroup). Subsequent writes are staged: " +
            "commit_operation_group lands them as ONE undo entry; rollback_operation_group discards " +
            "them. One group at a time; auto-rolls-back after 10 min idle, or if the document changes/closes.";
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
