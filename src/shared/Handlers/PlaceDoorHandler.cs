using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A4 safe-creation group: hosted door placement. create_point_based_element
    // places freestanding instances only — doors need the host overload. Type
    // addressing is (family, type) because 7 of 11 door type names in the stock
    // metric template are ambiguous across families (A2 finding).
    public class PlaceDoorHandler : IRevitCommand
    {
        public string Name => "place_door";
        public string Description =>
            "Place a door hosted in a wall. The point (mm) is projected onto the wall axis (max 500 mm " +
            "off-axis). Strict (family, type) or typeId resolution — ambiguity fails listing candidates. " +
            "Supports dryRun.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""hostWallId"", ""x"", ""y"", ""level""],
  ""properties"": {
    ""hostWallId"": { ""type"": ""integer"", ""description"": ""Element id of the host wall"" },
    ""x"": { ""type"": ""number"", ""description"": ""X (mm) — projected onto the wall axis"" },
    ""y"": { ""type"": ""number"", ""description"": ""Y (mm) — projected onto the wall axis"" },
    ""level"": { ""type"": ""string"", ""description"": ""Level name (strict — no fallback)"" },
    ""typeId"": { ""type"": ""integer"", ""description"": ""Door type (FamilySymbol) element id"" },
    ""family"": { ""type"": ""string"", ""description"": ""Door family name, e.g. 'M_Door-Passage-Single-Flush' (disambiguates typeName)"" },
    ""typeName"": { ""type"": ""string"", ""description"": ""Door type name, e.g. '0915 x 2134mm'"" },
    ""operationGroupId"": { ""type"": ""string"", ""description"": ""Optional: the open operation group id — must match or the write is refused"" },
    ""dryRun"": { ""type"": ""boolean"", ""description"": ""Place + capture warnings, then roll back (default false)"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            return PlaceHosted(app, paramsJson, "place_door", "door type", BuiltInCategory.OST_Doors, null);
        }

        /// <summary>Shared hosted-placement implementation for place_door / place_window.</summary>
        internal static CommandResult PlaceHosted(
            UIApplication app, string paramsJson, string opName, string what,
            BuiltInCategory category, double? sillHeightMm)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var hostWallId = request.Value<long>("hostWallId");
            var x = request.Value<double>("x");
            var y = request.Value<double>("y");
            var dryRun = request.Value<bool?>("dryRun") ?? false;

            if (!SlsWriteSupport.IsFinite(x) || !SlsWriteSupport.IsFinite(y))
                return CommandResult.Fail("x and y must be finite numbers (mm).");

            var host = doc.GetElement(RevitCompat.ToElementId(hostWallId)) as Wall;
            if (host == null)
                return CommandResult.Fail("hostWallId " + hostWallId + " is not a wall in this model. " +
                                          "Pass the element id returned by create_wall / create_wall_loop.");

            var locationCurve = host.Location as LocationCurve;
            if (locationCurve == null || locationCurve.Curve == null)
                return CommandResult.Fail("Wall " + hostWallId + " has no location curve " +
                                          "(curtain/profile walls cannot host this " + what + ").");

            string error;
            var level = SlsWriteSupport.ResolveLevelStrict(doc, request.Value<string>("level"), out error);
            if (level == null) return CommandResult.Fail(error);

            var symbol = SlsWriteSupport.ResolveTypeStrict<FamilySymbol>(doc, request, what, category, out error);
            if (symbol == null) return CommandResult.Fail(error);

            // Project the requested point onto the wall axis (in the axis' own z-plane,
            // so the distance measured is the plan offset).
            var curve = locationCurve.Curve;
            var requested = new XYZ(SlsWriteSupport.MmToFt(x), SlsWriteSupport.MmToFt(y),
                curve.GetEndPoint(0).Z);
            var projection = curve.Project(requested);
            if (projection == null)
                return CommandResult.Fail("Could not project the point onto wall " + hostWallId + "'s axis.");

            var offAxisMm = SlsWriteSupport.FtToMm(projection.Distance);
            if (offAxisMm > 500)
                return CommandResult.Fail("Point is " + Math.Round(offAxisMm, 1) + " mm from wall " +
                                          hostWallId + "'s axis (max 500). Check the coordinates, or the host wall.");

            var placement = projection.XYZPoint;

            return SlsWriteSupport.RunWrite(doc, opName, dryRun, request.Value<string>("operationGroupId"), scope =>
            {
                if (!symbol.IsActive)
                    symbol.Activate();

                var instance = doc.Create.NewFamilyInstance(
                    placement, symbol, host, level, StructuralType.NonStructural);
                doc.Regenerate();

                // A requested sill height must land exactly or the call fails — silently
                // keeping the type default while reporting success is the invisible-default
                // class this slice exists to remove (Codex review finding 5). Throwing here
                // rolls the placement back atomically via RunWrite.
                double? actualSillMm = null;
                var sillParam = instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                if (sillHeightMm.HasValue)
                {
                    if (sillParam == null)
                        throw new InvalidOperationException(
                            "sillHeightMm was requested but this family has no sill-height parameter.");
                    if (sillParam.IsReadOnly)
                        throw new InvalidOperationException(
                            "sillHeightMm was requested but this family's sill-height parameter is read-only.");
                    if (!sillParam.Set(SlsWriteSupport.MmToFt(sillHeightMm.Value)))
                        throw new InvalidOperationException(
                            "Revit refused to set the requested sill height.");
                    doc.Regenerate();
                }
                if (sillParam != null)
                    actualSillMm = Math.Round(SlsWriteSupport.FtToMm(sillParam.AsDouble()), 1);
                if (sillHeightMm.HasValue &&
                    (!actualSillMm.HasValue || Math.Abs(actualSillMm.Value - sillHeightMm.Value) > 0.5))
                    throw new InvalidOperationException(
                        "Requested sill height " + sillHeightMm.Value + " mm, but Revit reports " +
                        (actualSillMm.HasValue ? actualSillMm.Value + " mm" : "no value") + " after setting it.");

                return new
                {
                    element_ids = new[] { RevitCompat.GetId(instance.Id) },
                    instance = new
                    {
                        id = RevitCompat.GetId(instance.Id),
                        family = symbol.FamilyName,
                        type = symbol.Name,
                        host_wall_id = hostWallId,
                        level = level.Name,
                        position = new
                        {
                            x_mm = Math.Round(SlsWriteSupport.FtToMm(placement.X), 1),
                            y_mm = Math.Round(SlsWriteSupport.FtToMm(placement.Y), 1)
                        },
                        // Reported so the agent always knows the value in force — even when
                        // it came from the type/template rather than this call (A2 rule:
                        // no invisible defaults).
                        sill_height_mm = actualSillMm
                    }
                };
            });
        }
    }
}
