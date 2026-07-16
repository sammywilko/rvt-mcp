using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A4 safe-creation group: footprint roof. Geopogo's create_roof failed 5/5
    // (confirmed defective, every argument shape, A2 run 2) — a working, typed roof
    // tool is a headline SLS differentiator.
    public class CreateBasicRoofHandler : IRevitCommand
    {
        public string Name => "create_basic_roof";
        public string Description =>
            "Create a footprint roof from boundary points (mm) at a level. Strict roof type and level " +
            "resolution (no silent defaults). Optional uniform slopeDegrees on all edges (hip roof); " +
            "omit for a flat roof. Supports dryRun.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""points"", ""level""],
  ""properties"": {
    ""points"": {
      ""type"": ""array"",
      ""minItems"": 3,
      ""maxItems"": 512,
      ""description"": ""Roof footprint corner points in order, each {x, y} in mm; the loop closes automatically"",
      ""items"": { ""type"": ""object"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" } } }
    },
    ""level"": { ""type"": ""string"", ""description"": ""Roof base level name (strict — no fallback)"" },
    ""typeId"": { ""type"": ""integer"", ""description"": ""Roof type element id"" },
    ""family"": { ""type"": ""string"", ""description"": ""Roof family name, e.g. 'Basic Roof' (disambiguates typeName)"" },
    ""typeName"": { ""type"": ""string"", ""description"": ""Roof type name, e.g. 'Generic - 300mm'"" },
    ""slopeDegrees"": { ""type"": ""number"", ""description"": ""Uniform slope on every edge, in degrees (0-75). Omit for flat."" },
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
            var slopeDegrees = request.Value<double?>("slopeDegrees");
            var dryRun = request.Value<bool?>("dryRun") ?? false;

            if (slopeDegrees.HasValue &&
                (!SlsWriteSupport.IsFinite(slopeDegrees.Value) || slopeDegrees.Value <= 0 || slopeDegrees.Value > 75))
                return CommandResult.Fail("slopeDegrees must be between 0 (exclusive) and 75. Omit it for a flat roof.");

            System.Collections.Generic.List<XYZ> loop;
            var loopError = SlsWriteSupport.TryParseLoopPoints(request["points"] as JArray, out loop);
            if (loopError != null)
                return CommandResult.Fail(loopError);

            string error;
            var level = SlsWriteSupport.ResolveLevelStrict(doc, request.Value<string>("level"), out error);
            if (level == null) return CommandResult.Fail(error);

            var roofType = SlsWriteSupport.ResolveTypeStrict<RoofType>(doc, request, "roof type", null, out error);
            if (roofType == null) return CommandResult.Fail(error);

            return SlsWriteSupport.RunWrite(doc, "create_basic_roof", dryRun, request.Value<string>("operationGroupId"), scope =>
            {
                var footprint = new CurveArray();
                for (var i = 0; i < loop.Count; i++)
                    footprint.Append(Line.CreateBound(loop[i], loop[(i + 1) % loop.Count]));

                var mapping = new ModelCurveArray();
                var roof = doc.Create.NewFootPrintRoof(footprint, level, roofType, out mapping);

                if (slopeDegrees.HasValue)
                {
                    // FootPrintRoof.set_SlopeAngle takes the slope as rise/run (dimensionless),
                    // not radians — converted from degrees here. Live-verified in the VM as an
                    // A4 acceptance check (read the roof's slope back after creation).
                    var slope = Math.Tan(slopeDegrees.Value * Math.PI / 180.0);
                    foreach (ModelCurve modelCurve in mapping)
                    {
                        roof.set_DefinesSlope(modelCurve, true);
                        roof.set_SlopeAngle(modelCurve, slope);
                    }
                }
                doc.Regenerate();

                return new
                {
                    element_ids = new[] { RevitCompat.GetId(roof.Id) },
                    roof = new
                    {
                        id = RevitCompat.GetId(roof.Id),
                        family = roofType.FamilyName,
                        type = roofType.Name,
                        level = level.Name,
                        slope_defined = slopeDegrees.HasValue,
                        slope_degrees = slopeDegrees,
                        edge_count = mapping.Size
                    }
                };
            });
        }
    }
}
