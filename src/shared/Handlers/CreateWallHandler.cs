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
    ""disallowJoins"": { ""type"": ""boolean"", ""description"": ""Disallow automatic join at BOTH ends (default false). Revit's auto-join trims/extends wall ends to clean up corners, which silently moves endpoints you supplied — use this when the coordinates are surveyed/derived and must be reproduced exactly."" },
    ""operationGroupId"": { ""type"": ""string"", ""description"": ""Optional: the open operation group id — must match or the write is refused"" },
    ""dryRun"": { ""type"": ""boolean"", ""description"": ""Build + capture warnings, then roll back (default false)"" }
  }
}";

        // Sub-millimetre: the pipeline's coordinates come from a scaled drawing, so a
        // tolerance looser than this would let a real join displacement pass as noise.
        private const double EndpointToleranceMm = 0.5;

        private static double Distance(double x1, double y1, double x2, double y2)
        {
            var dx = x1 - x2;
            var dy = y1 - y2;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

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
            var disallowJoins = request.Value<bool?>("disallowJoins") ?? false;
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

                // Disallow BEFORE the regen that would otherwise perform the join: once
                // Revit has joined and moved the ends, unjoining afterwards does not put
                // the supplied coordinates back.
                if (disallowJoins)
                {
                    WallUtils.DisallowWallJoinAtEnd(wall, 0);
                    WallUtils.DisallowWallJoinAtEnd(wall, 1);
                }
                doc.Regenerate();

                // Endpoint fidelity is the POINT of disallowJoins, so it is measured, not
                // asserted: DisallowWallJoinAtEnd promises no join, it does not promise to
                // restore a location curve something else already moved. Report the wall's
                // actual endpoints either way, and when exact reproduction was REQUESTED,
                // refuse (rolling back) if Revit moved them — a silently relocated wall
                // reported as success is precisely the failure this tool exists to prevent.
                double? deviationMm = null;
                var located = wall.Location as LocationCurve;
                double[] actual = null;
                if (located != null && located.Curve != null)
                {
                    var a = located.Curve.GetEndPoint(0);
                    var b = located.Curve.GetEndPoint(1);
                    actual = new[]
                    {
                        SlsWriteSupport.FtToMm(a.X), SlsWriteSupport.FtToMm(a.Y),
                        SlsWriteSupport.FtToMm(b.X), SlsWriteSupport.FtToMm(b.Y)
                    };
                    // Revit may return the curve in either direction; compare both pairings.
                    var forward = Math.Max(
                        Distance(actual[0], actual[1], startX, startY),
                        Distance(actual[2], actual[3], endX, endY));
                    var reversed = Math.Max(
                        Distance(actual[0], actual[1], endX, endY),
                        Distance(actual[2], actual[3], startX, startY));
                    deviationMm = Math.Round(Math.Min(forward, reversed), 4);
                }

                if (disallowJoins && deviationMm.HasValue && deviationMm.Value > EndpointToleranceMm)
                {
                    throw new InvalidOperationException(
                        "Wall endpoints moved " + deviationMm.Value + " mm after creation despite " +
                        "disallowJoins=true (tolerance " + EndpointToleranceMm + " mm). Requested (" +
                        startX + "," + startY + ")-(" + endX + "," + endY + "), actual (" +
                        actual[0] + "," + actual[1] + ")-(" + actual[2] + "," + actual[3] + "). " +
                        "The wall was rolled back rather than reported as an exact build.");
                }

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
                        structural,
                        joins_disallowed = disallowJoins,
                        // Evidence, always present: what Revit actually built.
                        actual_start_mm = actual == null ? null : new { x = Math.Round(actual[0], 3), y = Math.Round(actual[1], 3) },
                        actual_end_mm = actual == null ? null : new { x = Math.Round(actual[2], 3), y = Math.Round(actual[3], 3) },
                        endpoint_deviation_mm = deviationMm
                    }
                };
            });
        }
    }
}
