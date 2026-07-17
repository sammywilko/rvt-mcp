using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A4 safe-creation group: a closed loop of walls in one transaction —
    // all segments land together or not at all (PRD: failed operations must not
    // leave partial undocumented changes).
    public class CreateWallLoopHandler : IRevitCommand
    {
        public string Name => "create_wall_loop";
        public string Description =>
            "Create a closed loop of walls from corner points (mm), atomically in one transaction. " +
            "Strict type/level resolution (no silent defaults). Supports dryRun.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""points"", ""heightMm"", ""level""],
  ""properties"": {
    ""points"": {
      ""type"": ""array"",
      ""minItems"": 3,
      ""maxItems"": 512,
      ""description"": ""Corner points in order, each {x, y} in mm; the loop closes automatically"",
      ""items"": { ""type"": ""object"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" } } }
    },
    ""heightMm"": { ""type"": ""number"", ""description"": ""Unconnected height (mm)"" },
    ""level"": { ""type"": ""string"", ""description"": ""Base level name (strict — no fallback)"" },
    ""typeId"": { ""type"": ""integer"", ""description"": ""Wall type element id"" },
    ""family"": { ""type"": ""string"", ""description"": ""Wall family name (disambiguates typeName)"" },
    ""typeName"": { ""type"": ""string"", ""description"": ""Wall type name"" },
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
            var heightMm = request.Value<double>("heightMm");
            var structural = request.Value<bool?>("structural") ?? false;
            var dryRun = request.Value<bool?>("dryRun") ?? false;

            if (!SlsWriteSupport.IsFinite(heightMm) || heightMm <= 0 || heightMm > 50000)
                return CommandResult.Fail("heightMm must be a positive height in mm (max 50000).");

            List<XYZ> loop;
            var loopError = SlsWriteSupport.TryParseLoopPoints(request["points"] as JArray, out loop);
            if (loopError != null)
                return CommandResult.Fail(loopError);

            string error;
            var level = SlsWriteSupport.ResolveLevelStrict(doc, request.Value<string>("level"), out error);
            if (level == null) return CommandResult.Fail(error);

            var wallType = SlsWriteSupport.ResolveTypeStrict<WallType>(doc, request, "wall type", null, out error);
            if (wallType == null) return CommandResult.Fail(error);

            return SlsWriteSupport.RunWrite(doc, "create_wall_loop", dryRun, request.Value<string>("operationGroupId"), scope =>
            {
                var walls = new List<object>();
                var ids = new List<long>();
                double perimeterFt = 0;

                for (var i = 0; i < loop.Count; i++)
                {
                    var a = loop[i];
                    var b = loop[(i + 1) % loop.Count];
                    var line = Line.CreateBound(a, b);
                    perimeterFt += line.Length;

                    var wall = Wall.Create(doc, line, wallType.Id, level.Id,
                        SlsWriteSupport.MmToFt(heightMm), 0.0, false, structural);
                    ids.Add(RevitCompat.GetId(wall.Id));
                    walls.Add(new
                    {
                        id = RevitCompat.GetId(wall.Id),
                        length_mm = Math.Round(SlsWriteSupport.FtToMm(line.Length), 1)
                    });
                }
                doc.Regenerate();

                return new
                {
                    element_ids = ids,
                    count = walls.Count,
                    perimeter_mm = Math.Round(SlsWriteSupport.FtToMm(perimeterFt), 1),
                    family = wallType.FamilyName,
                    type = wallType.Name,
                    height_mm = heightMm,
                    level = level.Name,
                    walls
                };
            });
        }
    }
}
