using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A3 read connector: overall model extents (mm) for camera framing and sanity checks.
    // Bounds are derived from the SOLIDS of each element (not the element's whole bounding box),
    // so non-physical model elements (datums, base/survey points, spaces, analytical members,
    // view markers) and any oversized non-solid sub-geometry cannot balloon the box.
    //
    // Known v0 limitations (acceptable for M1 = architecture-only, small models):
    //  - mesh-only physical geometry is excluded (R22/23 TopographySurface, mesh DirectShapes,
    //    imports, point clouds) — these are not part of the building envelope we frame on;
    //  - extracting geometry for every element can be slow on very large models / links.
    public class GetModelBoundsHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;

        public string Name => "get_model_bounds";
        public string Description =>
            "Overall model bounds derived from the solid geometry of every element that has any. " +
            "Returns min, max and size in mm plus the element count considered; null bounds for a " +
            "model with no solid geometry.";
        public string ParametersSchema => @"{ ""type"": ""object"", ""properties"": {}, ""additionalProperties"": false }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var opt = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false };
            var b = new Bounds();
            int counted = 0;

            var elements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent();

            foreach (var e in elements)
            {
                // One malformed element must never fail the whole read: guard the entire body.
                try
                {
                    var geo = e.get_Geometry(opt);
                    if (geo == null) continue;
                    var before = b.Count;
                    AccumulateSolids(geo, ref b);
                    if (b.Count > before) counted++;
                }
                catch { /* skip elements whose geometry can't be read */ }
            }

            if (!b.Any)
                return CommandResult.Ok(new { element_count = 0, bounds = (object)null });

            return CommandResult.Ok(new
            {
                element_count = counted,
                bounds = new
                {
                    min = Pt(b.MinX, b.MinY, b.MinZ),
                    max = Pt(b.MaxX, b.MaxY, b.MaxZ),
                    size = new
                    {
                        x_mm = Round((b.MaxX - b.MinX) * FeetToMm),
                        y_mm = Round((b.MaxY - b.MinY) * FeetToMm),
                        z_mm = Round((b.MaxZ - b.MinZ) * FeetToMm)
                    }
                }
            });
        }

        // Union the model-space bounding box of every non-degenerate solid, recursing through
        // instance geometry (GetInstanceGeometry returns solids already in model coordinates).
        private static void AccumulateSolids(GeometryElement geo, ref Bounds b)
        {
            foreach (GeometryObject go in geo)
            {
                if (go is Solid s)
                {
                    if (s.Volume <= 1e-9) continue;
                    BoundingBoxXYZ bb;
                    try { bb = s.GetBoundingBox(); } catch { continue; }
                    if (bb == null) continue;
                    var t = bb.Transform;
                    foreach (var corner in Corners(bb.Min, bb.Max))
                        b.Add(t.OfPoint(corner));
                }
                else if (go is GeometryInstance gi)
                {
                    GeometryElement inst;
                    try { inst = gi.GetInstanceGeometry(); } catch { continue; }
                    if (inst != null) AccumulateSolids(inst, ref b);
                }
            }
        }

        private static System.Collections.Generic.IEnumerable<XYZ> Corners(XYZ min, XYZ max)
        {
            foreach (var x in new[] { min.X, max.X })
                foreach (var y in new[] { min.Y, max.Y })
                    foreach (var z in new[] { min.Z, max.Z })
                        yield return new XYZ(x, y, z);
        }

        private static object Pt(double x, double y, double z) => new
        {
            x_mm = Round(x * FeetToMm),
            y_mm = Round(y * FeetToMm),
            z_mm = Round(z * FeetToMm)
        };

        private static double Round(double v) => Math.Round(v, 3);

        private struct Bounds
        {
            public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
            public int Count;
            public bool Any => Count > 0;
            public void Add(XYZ p)
            {
                if (Count == 0)
                {
                    MinX = MaxX = p.X; MinY = MaxY = p.Y; MinZ = MaxZ = p.Z;
                }
                else
                {
                    if (p.X < MinX) MinX = p.X; if (p.X > MaxX) MaxX = p.X;
                    if (p.Y < MinY) MinY = p.Y; if (p.Y > MaxY) MaxY = p.Y;
                    if (p.Z < MinZ) MinZ = p.Z; if (p.Z > MaxZ) MaxZ = p.Z;
                }
                Count++;
            }
        }
    }
}
