using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A3 read connector: the flagship read gap — a level enumeration for a
    // storey-thinking PDF->shell pipeline. Upstream exposes only revit_create_level.
    public class ListLevelsHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;

        public string Name => "list_levels";
        public string Description =>
            "List all levels ordered by elevation. Returns id, name, elevation (mm and feet), " +
            "and whether the level is a building storey.";
        public string ParametersSchema => @"{ ""type"": ""object"", ""properties"": {}, ""additionalProperties"": false }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => (object)new
                {
                    id = RevitCompat.GetId(l.Id),
                    name = l.Name,
                    elevation_mm = Math.Round(l.Elevation * FeetToMm, 3),
                    elevation_ft = Math.Round(l.Elevation, 4),
                    is_building_storey = IsBuildingStorey(l)
                })
                .ToList();

            return CommandResult.Ok(new { count = levels.Count, levels });
        }

        private static bool IsBuildingStorey(Level level)
        {
            var p = level.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY);
            return p != null && p.AsInteger() == 1;
        }
    }
}
