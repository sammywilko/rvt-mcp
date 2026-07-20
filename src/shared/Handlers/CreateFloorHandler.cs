using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A4 safe-creation group: floor with a REQUIRED type and a foundation-slab
    // guard. The A2 benchmark's most damaging Geopogo defect was create_floor silently
    // defaulting to "150mm Foundation Slab" (sorts first) and reporting success while
    // the model had no Floors-category floor. This tool makes both halves impossible:
    // the type is never defaulted, and a foundation-slab type is refused unless
    // explicitly requested.
    public class CreateFloorHandler : IRevitCommand
    {
        public string Name => "create_floor";
        public string Description =>
            "Create a floor from corner points (mm). Strict: floor type (typeId, or typeName + optional " +
            "family) and level must resolve or the call fails — no silent defaults. Foundation-slab types " +
            "are refused unless allowFoundationSlab=true. Supports dryRun. Returns the computed area (m²).";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""points"", ""level""],
  ""properties"": {
    ""points"": {
      ""type"": ""array"",
      ""minItems"": 3,
      ""maxItems"": 512,
      ""description"": ""Boundary corner points in order, each {x, y} in mm; the loop closes automatically"",
      ""items"": { ""type"": ""object"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" } } }
    },
    ""level"": { ""type"": ""string"", ""description"": ""Level name (strict — no fallback)"" },
    ""typeId"": { ""type"": ""integer"", ""description"": ""Floor type element id"" },
    ""family"": { ""type"": ""string"", ""description"": ""Floor family name, e.g. 'Floor' (disambiguates typeName)"" },
    ""typeName"": { ""type"": ""string"", ""description"": ""Floor type name, e.g. 'Concrete 150mm'"" },
    ""allowFoundationSlab"": { ""type"": ""boolean"", ""description"": ""Permit a foundation-slab floor type (default false)"" },
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
            var allowFoundationSlab = request.Value<bool?>("allowFoundationSlab") ?? false;
            var dryRun = request.Value<bool?>("dryRun") ?? false;

            List<XYZ> loop;
            var loopError = SlsWriteSupport.TryParseLoopPoints(request["points"] as JArray, out loop);
            if (loopError != null)
                return CommandResult.Fail(loopError);

            string error;
            var level = SlsWriteSupport.ResolveLevelStrict(doc, request.Value<string>("level"), out error);
            if (level == null) return CommandResult.Fail(error);

            var floorType = SlsWriteSupport.ResolveTypeStrict<FloorType>(doc, request, "floor type", null, out error);
            if (floorType == null) return CommandResult.Fail(error);

            if (floorType.IsFoundationSlab && !allowFoundationSlab)
                return CommandResult.Fail(
                    "'" + floorType.FamilyName + ": " + floorType.Name + "' is a foundation slab, not a floor. " +
                    "Pick a Floors-category type (revit_get_system_types category='floors' — floor types are " +
                    "SYSTEM types and never appear in revit_get_available_family_types), or pass " +
                    "allowFoundationSlab=true if a foundation slab is genuinely intended.");

            return SlsWriteSupport.RunWrite(doc, "create_floor", dryRun, request.Value<string>("operationGroupId"), scope =>
            {
                var curveLoop = new CurveLoop();
                for (var i = 0; i < loop.Count; i++)
                    curveLoop.Append(Line.CreateBound(loop[i], loop[(i + 1) % loop.Count]));

                var floor = Floor.Create(doc, new List<CurveLoop> { curveLoop }, floorType.Id, level.Id);
                doc.Regenerate();

                var areaParam = floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                var areaM2 = areaParam == null ? (double?)null
                    : Math.Round(SlsWriteSupport.SqFtToM2(areaParam.AsDouble()), 3);

                return new
                {
                    element_ids = new[] { RevitCompat.GetId(floor.Id) },
                    floor = new
                    {
                        id = RevitCompat.GetId(floor.Id),
                        family = floorType.FamilyName,
                        type = floorType.Name,
                        level = level.Name,
                        area_m2 = areaM2,
                        category = floor.Category == null ? null : floor.Category.Name
                    }
                };
            });
        }
    }
}
