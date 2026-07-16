using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A3 read connector: overall model extents (mm) for camera framing and sanity checks.
    // Unions the model-coordinate bounding boxes of physical building elements only.
    public class GetModelBoundsHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;

        // Model-typed categories that carry huge/spurious extents and are not building geometry.
        // (Datums are usually CategoryType.Annotation and already excluded, but blocklisted too.)
        private static readonly System.Collections.Generic.HashSet<long> ExcludedCategories =
            new System.Collections.Generic.HashSet<long>
            {
                (long)(int)BuiltInCategory.OST_ProjectBasePoint,
                (long)(int)BuiltInCategory.OST_SharedBasePoint,
                (long)(int)BuiltInCategory.OST_VolumeOfInterest, // scope boxes
                (long)(int)BuiltInCategory.OST_Levels,
                (long)(int)BuiltInCategory.OST_Grids,
                (long)(int)BuiltInCategory.OST_CLines,           // reference planes
            };

        public string Name => "get_model_bounds";
        public string Description =>
            "Overall model bounds as a union of every model element's bounding box. Returns min, " +
            "max and size in mm, plus the element count considered. Empty models return null bounds.";
        public string ParametersSchema => @"{ ""type"": ""object"", ""properties"": {}, ""additionalProperties"": false }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            int counted = 0;

            var elements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent();

            foreach (var e in elements)
            {
                // Only physical model geometry defines "model bounds". Datums (levels, grids,
                // reference planes) and the base/survey points carry huge or spurious extents
                // that would balloon the box, so restrict to Model categories and blocklist the
                // model-typed non-geometry ones.
                var cat = e.Category;
                if (cat == null || cat.CategoryType != CategoryType.Model) continue;
                if (ExcludedCategories.Contains(RevitCompat.GetId(cat.Id))) continue;
                // null bbox = no model geometry
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

        private static object Pt(double x, double y, double z) => new
        {
            x_mm = Round(x * FeetToMm),
            y_mm = Round(y * FeetToMm),
            z_mm = Round(z * FeetToMm)
        };

        private static double Round(double v) => Math.Round(v, 3);
    }
}
