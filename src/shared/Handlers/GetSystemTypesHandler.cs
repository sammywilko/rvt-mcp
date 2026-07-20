using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    // SLS W5: system-type discovery. get_available_family_types collects FamilySymbol,
    // so it can only ever see LOADABLE families — walls, floors, roofs and ceilings are
    // SYSTEM types (HostObjAttributes), which means the strict create tools' refusal
    // guidance ("use get_available_family_types to list valid types") dead-ended for
    // exactly the categories those tools most often refuse on. This handler closes that
    // loop: it is the tool a refused caller can actually act on.
    public class GetSystemTypesHandler : IRevitCommand
    {
        // Canonical, language-independent keys. Category.Name is LOCALIZED, so filtering
        // on the English display name returns an empty (but successful) result on a
        // French or German Revit — a silent wrong answer. Callers name a category by key;
        // the localized display name is returned alongside for humans.
        private static readonly Dictionary<string, BuiltInCategory> CanonicalCategories =
            new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
            {
                { "walls", BuiltInCategory.OST_Walls },
                { "floors", BuiltInCategory.OST_Floors },
                { "roofs", BuiltInCategory.OST_Roofs },
                { "ceilings", BuiltInCategory.OST_Ceilings }
            };

        public string Name => "get_system_types";
        public string Description =>
            "List system (host) element types — walls, floors, roofs, ceilings. These are " +
            "NOT loadable families and never appear in get_available_family_types. Returns " +
            "typeId, typeName, familyName and thickness per type. Filter by canonical key: " +
            "walls, floors, roofs, ceilings.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""category"": { ""type"": ""string"", ""description"": ""Optional canonical category key (case-insensitive): walls, floors, roofs, ceilings. Omit for all. NOT a localized display name."" }
  },
  ""additionalProperties"": false
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = string.IsNullOrWhiteSpace(paramsJson)
                ? new Newtonsoft.Json.Linq.JObject()
                : Newtonsoft.Json.Linq.JObject.Parse(paramsJson);
            var categoryFilter = request.Value<string>("category");
            var wantedKey = string.IsNullOrWhiteSpace(categoryFilter) ? null : categoryFilter.Trim();

            BuiltInCategory wantedCategory = default(BuiltInCategory);
            if (wantedKey != null && !CanonicalCategories.TryGetValue(wantedKey, out wantedCategory))
            {
                // Refuse an unrecognised key instead of returning an empty success — an
                // empty OK reads as "this document has no wall types" and sends the caller
                // down the wrong path (the dead-end this whole tool exists to remove).
                return CommandResult.Fail(
                    "Unknown category '" + wantedKey + "'. Valid keys: " +
                    string.Join(", ", CanonicalCategories.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) +
                    ". Category keys are canonical and language-independent — do not pass a localized display name.");
            }

            // HostObjAttributes is the common base of WallType, FloorType, RoofType and
            // CeilingType, so one collector covers every system-type category. OfClass
            // accepts a base class and returns derived types.
            var collector = new FilteredElementCollector(doc)
                .OfClass(typeof(HostObjAttributes))
                .WhereElementIsElementType();
            if (wantedKey != null)
                collector = collector.OfCategory(wantedCategory);

            var selected = collector
                .Cast<HostObjAttributes>()
                .Where(t => t.Category != null)
                .ToList();

            var categories = selected
                .GroupBy(t => t.Category.Name)
                .Select(g => new
                {
                    category = g.Key,
                    category_key = CanonicalKeyFor(g.First().Category),
                    count = g.Count(),
                    types = g
                        .Select(BuildTypeInfo)
                        .OrderBy(t => t.typeName, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                })
                .OrderBy(g => g.category, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return CommandResult.Ok(new
            {
                totalCategories = categories.Length,
                totalTypes = categories.Sum(g => g.count),
                categories,
                valid_category_keys = CanonicalCategories.Keys
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                category_filter = wantedKey
            });
        }

        private static string CanonicalKeyFor(Category category)
        {
            var id = RevitCompat.GetId(category.Id);
            foreach (var pair in CanonicalCategories)
            {
                if (id == (long)(int)pair.Value)
                    return pair.Key;
            }
            return null;
        }

        private static TypeInfo BuildTypeInfo(HostObjAttributes type)
        {
            var thickness = MeasureThickness(type);
            return new TypeInfo
            {
                typeId = RevitCompat.GetId(type.Id),
                familyName = type.FamilyName ?? string.Empty,
                typeName = type.Name,
                // thickness_mm is the number a downstream type mapper is allowed to match
                // on, so it is populated ONLY when it is a single reliable thickness.
                // A variable or vertically-compound type reports null here and carries its
                // nominal value in nominal_thickness_mm — the mapper fails closed rather
                // than matching a wall whose real thickness varies along its length.
                thickness_mm = thickness.IsVariable ? null : thickness.NominalMm,
                nominal_thickness_mm = thickness.NominalMm,
                thickness_basis = thickness.Basis,
                thickness_is_variable = thickness.IsVariable,
                // The evidence behind thickness_is_variable, so a null thickness_mm is
                // self-diagnosing rather than an unexplained refusal.
                vertically_compound = thickness.VerticallyCompound,
                variable_layer_index = thickness.VariableLayerIndex,
                region_count = thickness.RegionCount
            };
        }

        private class TypeInfo
        {
            public long typeId { get; set; }
            public string familyName { get; set; }
            public string typeName { get; set; }
            public double? thickness_mm { get; set; }
            public double? nominal_thickness_mm { get; set; }
            public string thickness_basis { get; set; }
            public bool thickness_is_variable { get; set; }
            public bool? vertically_compound { get; set; }
            public int? variable_layer_index { get; set; }
            public int? region_count { get; set; }
        }

        private struct Thickness
        {
            public double? NominalMm;
            public string Basis;
            public bool IsVariable;
            public bool? VerticallyCompound;
            public int? VariableLayerIndex;
            public int? RegionCount;
        }

        // CompoundStructure.GetWidth() is NOMINAL: for a vertically compound structure it
        // reports the rectangular grid width, and a variable-thickness layer (tapered
        // floor/roof, shape-edited slab) is reported at its specified width. Publishing
        // that as "thickness" would hand a nearest-thickness mapper an authoritative
        // -looking number for a type whose real thickness varies — so variability is
        // detected and reported rather than flattened away.
        private static Thickness MeasureThickness(HostObjAttributes type)
        {
            var result = new Thickness { NominalMm = null, Basis = "none", IsVariable = false };

            CompoundStructure structure = null;
            try
            {
                structure = type.GetCompoundStructure();
                if (structure != null)
                {
                    result.VerticallyCompound = structure.IsVerticallyCompound;
                    result.VariableLayerIndex = structure.VariableLayerIndex;
                    result.RegionCount = structure.GetRegionIds().Count;
                    result.IsVariable = IsThicknessVariable(structure);
                    result.NominalMm = ToMm(structure.GetWidth());
                    result.Basis = "compound-structure-nominal";
                }

                // A Basic wall's Width is the authoritative type width; prefer it over the
                // compound-structure figure, but keep any variability the structure
                // reported — a basic wall can still be vertically compound.
                var wallType = type as WallType;
                if (wallType != null && wallType.Kind == WallKind.Basic)
                {
                    var width = ToMm(wallType.Width);
                    if (width != null)
                    {
                        result.NominalMm = width;
                        result.Basis = "wall-type-width";
                    }
                }
            }
            finally
            {
                // CompoundStructure is a native-backed APIObject; long-lived Revit sessions
                // running repeated discovery calls should not accumulate them.
                if (structure != null)
                    structure.Dispose();
            }

            if (result.NominalMm == null)
                result.Basis = "none";

            return result;
        }

        // Which signal means "this type's thickness is not one number" depends on the
        // KIND of structure, established live against Revit 2027 (2026-07-20) rather than
        // assumed: EVERY wall type reports IsVerticallyCompound=true — including a plain
        // single-layer 'Concrete 200mm' — while every floor type reports false. The flag
        // therefore identifies a wall-kind (vertical) section, NOT variable thickness.
        // Reading it as variability flagged all 44 wall types and would have nulled every
        // thickness the type mapper needs.
        //   vertical (walls):   thickness varies when the section has more than one
        //                       region, i.e. genuinely different widths up the wall.
        //   horizontal (floors/roofs/ceilings): thickness varies when a layer is marked
        //                       variable (tapered slab). VariableLayerIndex is not
        //                       meaningful for vertical sections — some stock wall types
        //                       report index 0 with a single uniform region.
        private static bool IsThicknessVariable(CompoundStructure structure)
        {
            if (structure.IsVerticallyCompound)
                return structure.GetRegionIds().Count > 1;
            return structure.VariableLayerIndex >= 0;
        }

        private static double? ToMm(double feet)
        {
            if (feet <= 0)
                return null;
            return Math.Round(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters), 3);
        }
    }
}
