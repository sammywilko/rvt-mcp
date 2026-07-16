using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A3 read connector: enumerate grids (layout/structural reference). Upstream exposes
    // only revit_create_grid.
    public class ListGridsHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;

        public string Name => "list_grids";
        public string Description =>
            "List all grids. Returns id, name, whether the grid is a straight line or an arc, " +
            "and start/end points (mm) for straight grids.";
        public string ParametersSchema => @"{ ""type"": ""object"", ""properties"": {}, ""additionalProperties"": false }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var grids = new FilteredElementCollector(doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .OrderBy(g => g.Name)
                .Select(g => (object)BuildGrid(g))
                .ToList();

            return CommandResult.Ok(new { count = grids.Count, grids });
        }

        private static object BuildGrid(Grid g)
        {
            var curve = g.Curve;
            bool isLine = curve is Line;
            object start = null, end = null;
            if (isLine)
            {
                start = Point(curve.GetEndPoint(0));
                end = Point(curve.GetEndPoint(1));
            }
            return new
            {
                id = RevitCompat.GetId(g.Id),
                name = g.Name,
                is_curved = !isLine,
                start,
                end
            };
        }

        private static object Point(XYZ p) => new
        {
            x_mm = Math.Round(p.X * FeetToMm, 3),
            y_mm = Math.Round(p.Y * FeetToMm, 3),
            z_mm = Math.Round(p.Z * FeetToMm, 3)
        };
    }
}
