using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A3 read connector: overall model extents (mm) for camera framing and sanity checks.
    // Unions the model-coordinate bounding boxes of elements that actually carry solid geometry
    // — the definitive test for "physical building geometry". This excludes datums, base/survey
    // points, scope boxes, rooms/spaces, analytical members and view markers, all of which have
    // no solid and would otherwise balloon the box far beyond the building.
    public class GetModelBoundsHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;

        public string Name => "get_model_bounds";
        public string Description =>
            "Overall model bounds unioned over every element that carries solid geometry. Returns " +
            "min, max and size in mm, plus the element count considered. Empty models return null bounds.";
        public string ParametersSchema => @"{ ""type"": ""object"", ""properties"": {}, ""additionalProperties"": false }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var opt = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false };

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            int counted = 0;

            var elements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent();

            foreach (var e in elements)
            {
                GeometryElement geo;
                try { geo = e.get_Geometry(opt); }
                catch { continue; }
                if (geo == null || !HasSolid(geo)) continue;

                var bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                counted++;
                if (bb.Min.X < minX) minX = bb.Min.X;
                if (bb.Min.Y < minY) minY = bb.Min.Y;
                if (bb.Min.Z < minZ) minZ = bb.Min.Z;
                if (bb.Max.X > maxX) maxX = bb.Max.X;
                if (bb.Max.Y > maxY) maxY = bb.Max.Y;
                if (bb.Max.Z > maxZ) maxZ = bb.Max.Z;
            }

            if (counted == 0)
                return CommandResult.Ok(new { element_count = 0, bounds = (object)null });

            return CommandResult.Ok(new
            {
                element_count = counted,
                bounds = new
                {
                    min = Pt(minX, minY, minZ),
                    max = Pt(maxX, maxY, maxZ),
                    size = new
                    {
                        x_mm = Round((maxX - minX) * FeetToMm),
                        y_mm = Round((maxY - minY) * FeetToMm),
                        z_mm = Round((maxZ - minZ) * FeetToMm)
                    }
                }
            });
        }

        // True if the geometry (recursing through instances) contains a non-degenerate solid.
        private static bool HasSolid(GeometryElement geo)
        {
            foreach (GeometryObject go in geo)
            {
                if (go is Solid s && s.Volume > 1e-9) return true;
                if (go is GeometryInstance gi)
                {
                    var inst = gi.GetInstanceGeometry();
                    if (inst != null && HasSolid(inst)) return true;
                }
            }
            return false;
        }

        private static object Pt(double x, double y, double z) => new
        {
            x_mm = Round(x * FeetToMm),
            y_mm = Round(y * FeetToMm),
            z_mm = Round(z * FeetToMm)
        };

        private static double Round(double v) => Math.Round(v, 3);
    }
}
