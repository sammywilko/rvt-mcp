using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A4 safe-creation group: hosted window placement (shares the hosted-placement
    // core with place_door; adds sill height).
    public class PlaceWindowHandler : IRevitCommand
    {
        public string Name => "place_window";
        public string Description =>
            "Place a window hosted in a wall. The point (mm) is projected onto the wall axis (max 500 mm " +
            "off-axis). Strict (family, type) or typeId resolution — ambiguity fails listing candidates. " +
            "Optional sillHeightMm; the response always reports the sill height in force. Supports dryRun.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""hostWallId"", ""x"", ""y"", ""level""],
  ""properties"": {
    ""hostWallId"": { ""type"": ""integer"", ""description"": ""Element id of the host wall"" },
    ""x"": { ""type"": ""number"", ""description"": ""X (mm) — projected onto the wall axis"" },
    ""y"": { ""type"": ""number"", ""description"": ""Y (mm) — projected onto the wall axis"" },
    ""level"": { ""type"": ""string"", ""description"": ""Level name (strict — no fallback)"" },
    ""typeId"": { ""type"": ""integer"", ""description"": ""Window type (FamilySymbol) element id"" },
    ""family"": { ""type"": ""string"", ""description"": ""Window family name, e.g. 'M_Fixed' (disambiguates typeName)"" },
    ""typeName"": { ""type"": ""string"", ""description"": ""Window type name, e.g. '1200 x 1500mm'"" },
    ""sillHeightMm"": { ""type"": ""number"", ""description"": ""Sill height above the level (mm). Omitted = the type/template default, which is reported back."" },
    ""dryRun"": { ""type"": ""boolean"", ""description"": ""Place + capture warnings, then roll back (default false)"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var request = JObject.Parse(paramsJson);
            var sillHeightMm = request.Value<double?>("sillHeightMm");
            if (sillHeightMm.HasValue &&
                (!SlsWriteSupport.IsFinite(sillHeightMm.Value) || sillHeightMm.Value < 0 || sillHeightMm.Value > 10000))
                return CommandResult.Fail("sillHeightMm must be between 0 and 10000 (mm).");

            return PlaceDoorHandler.PlaceHosted(
                app, paramsJson, "place_window", "window type", BuiltInCategory.OST_Windows, sillHeightMm);
        }
    }
}
