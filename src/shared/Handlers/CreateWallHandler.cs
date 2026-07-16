using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A4 safe-creation group: architectural wall with strict type/level resolution.
    // Unlike create_line_based_element, an unresolved type or level FAILS — it is never
    // silently defaulted (A2 Finding-5 class).
    public class CreateWallHandler : IRevitCommand
    {
        public string Name => "create_wall";
        public string Description =>
            "Create a straight wall from start/end points (mm). Strict: level and wall type " +
            "(typeId, or typeName + optional family) must resolve or the call fails — no silent defaults. " +
            "Supports dryRun (build, capture real warnings, roll back).";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""startX"", ""startY"", ""endX"", ""endY"", ""heightMm"", ""level""],
  ""properties"": {
    ""startX"": { ""type"": ""number"", ""description"": ""Start X (mm)"" },
    ""startY"": { ""type"": ""number"", ""description"": ""Start Y (mm)"" },
    ""endX"": { ""type"": ""number"", ""description"": ""End X (mm)"" },
    ""endY"": { ""type"": ""number"", ""description"": ""End Y (mm)"" },
    ""heightMm"": { ""type"": ""number"", ""description"": ""Unconnected height (mm)"" },
    ""level"": { ""type"": ""string"", ""description"": ""Base level name (strict — no fallback)"" },
    ""typeId"": { ""type"": ""integer"", ""description"": ""Wall type element id"" },
    ""family"": { ""type"": ""string"", ""description"": ""Wall family name, e.g. 'Basic Wall' (disambiguates typeName)"" },
    ""typeName"": { ""type"": ""string"", ""description"": ""Wall type name, e.g. 'Interior - 114mm Partition'"" },
    ""structural"": { ""type"": ""boolean"", ""description"": ""Structural usage (default false)"" },
    ""operationGroupId"": { ""type"": ""string"", ""description"": ""Optional: the open operation group id — must match or the write is refused"" },
    ""dryRun"": { ""type"": ""boolean"", ""description"": ""Build + capture warnings, then roll back (default false)"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var startX = request.Value<double>("startX");
            var startY = request.Value<double>("startY");
            var endX = request.Value<double>("endX");
            var endY = request.Value<double>("endY");
            var heightMm = request.Value<double>("heightMm");
            var structural = request.Value<bool?>("structural") ?? false;
            var dryRun = request.Value<bool?>("dryRun") ?? false;

            if (!SlsWriteSupport.IsFinite(startX) || !SlsWriteSupport.IsFinite(startY) ||
                !SlsWriteSupport.IsFinite(endX) || !SlsWriteSupport.IsFinite(endY))
                return CommandResult.Fail("startX/startY/endX/endY must be finite numbers (mm).");

            var dx = endX - startX;
            var dy = endY - startY;
            var lengthMm = Math.Sqrt(dx * dx + dy * dy);
            if (lengthMm < 10)
                return CommandResult.Fail("Wall length must be at least 10 mm (got " +
                                          Math.Round(lengthMm, 2) + " mm).");
            if (!SlsWriteSupport.IsFinite(heightMm) || heightMm <= 0 || heightMm > 50000)
                return CommandResult.Fail("heightMm must be a positive height in mm (max 50000).");

            string error;
            var level = SlsWriteSupport.ResolveLevelStrict(doc, request.Value<string>("level"), out error);
            if (level == null) return CommandResult.Fail(error);

            var wallType = SlsWriteSupport.ResolveTypeStrict<WallType>(doc, request, "wall type", null, out error);
            if (wallType == null) return CommandResult.Fail(error);

            return SlsWriteSupport.RunWrite(doc, "create_wall", dryRun, request.Value<string>("operationGroupId"), scope =>
            {
                var line = Line.CreateBound(
                    new XYZ(SlsWriteSupport.MmToFt(startX), SlsWriteSupport.MmToFt(startY), 0),
                    new XYZ(SlsWriteSupport.MmToFt(endX), SlsWriteSupport.MmToFt(endY), 0));

                var wall = Wall.Create(doc, line, wallType.Id, level.Id,
                    SlsWriteSupport.MmToFt(heightMm), 0.0, false, structural);
                doc.Regenerate();

                return new
                {
                    element_ids = new[] { RevitCompat.GetId(wall.Id) },
                    wall = new
                    {
                        id = RevitCompat.GetId(wall.Id),
                        family = wallType.FamilyName,
                        type = wallType.Name,
                        length_mm = Math.Round(lengthMm, 1),
                        height_mm = heightMm,
                        level = level.Name,
                        structural
                    }
                };
            });
        }
    }
}
