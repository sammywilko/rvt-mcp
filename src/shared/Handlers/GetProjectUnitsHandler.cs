using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A3 read connector: report the project's display units so callers can honour the
    // mm-integer discipline (and detect an imperial project) before any read/write.
    public class GetProjectUnitsHandler : IRevitCommand
    {
        public string Name => "get_project_units";
        public string Description =>
            "Report the project's display units for length, area, volume and angle, plus an " +
            "isMetric flag derived from the length unit.";
        public string ParametersSchema => @"{ ""type"": ""object"", ""properties"": {}, ""additionalProperties"": false }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var units = doc.GetUnits();
            var lengthUnit = units.GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();

            bool isMetric =
                lengthUnit == UnitTypeId.Millimeters ||
                lengthUnit == UnitTypeId.Centimeters ||
                lengthUnit == UnitTypeId.Decimeters ||
                lengthUnit == UnitTypeId.Meters ||
                lengthUnit == UnitTypeId.MetersCentimeters ||
                lengthUnit == UnitTypeId.Kilometers;

            return CommandResult.Ok(new
            {
                length = Label(lengthUnit),
                area = Label(units.GetFormatOptions(SpecTypeId.Area).GetUnitTypeId()),
                volume = Label(units.GetFormatOptions(SpecTypeId.Volume).GetUnitTypeId()),
                angle = Label(units.GetFormatOptions(SpecTypeId.Angle).GetUnitTypeId()),
                is_metric = isMetric
            });
        }

        // Stable catalog string (e.g. "millimeters"); falls back to the type id on any API quirk.
        private static string Label(ForgeTypeId unit)
        {
            try { return UnitUtils.GetTypeCatalogStringForUnit(unit); }
            catch { return unit?.TypeId ?? "unknown"; }
        }
    }
}
