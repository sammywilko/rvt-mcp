using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A3 read connector: enumerate views. Upstream lists view templates and view filters
    // but not the views themselves. Feeds camera planning and capture_view_image.
    public class ListViewsHandler : IRevitCommand
    {
        public string Name => "list_views";
        public string Description =>
            "List views. Returns id, name, view type, scale, whether it is a view template, and " +
            "the associated level name where applicable. By default excludes view templates.";
        public string ParametersSchema => @"{
            ""type"": ""object"",
            ""properties"": {
                ""include_templates"": { ""type"": ""boolean"", ""description"": ""Include view templates (default false)."" }
            },
            ""additionalProperties"": false
        }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            bool includeTemplates = false;
            if (!string.IsNullOrEmpty(paramsJson))
            {
                try
                {
                    var req = Newtonsoft.Json.Linq.JObject.Parse(paramsJson);
                    if (req.TryGetValue("include_templates", out var v))
                        includeTemplates = v.Value<bool>();
                }
                catch (Newtonsoft.Json.JsonException ex)
                {
                    return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
                }
            }

            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => includeTemplates || !v.IsTemplate)
                .OrderBy(v => v.ViewType.ToString())
                .ThenBy(v => v.Name)
                .Select(v => (object)new
                {
                    id = RevitCompat.GetId(v.Id),
                    name = v.Name,
                    view_type = v.ViewType.ToString(),
                    scale = SafeScale(v),
                    is_template = v.IsTemplate,
                    level = LevelName(v)
                })
                .ToList();

            return CommandResult.Ok(new { count = views.Count, views });
        }

        // Scale is meaningless / can throw on schedules, sheets and some view types.
        private static int? SafeScale(View v)
        {
            try { return v.Scale; }
            catch { return null; }
        }

        private static string LevelName(View v)
        {
            try
            {
                var level = v.GenLevel;
                return level != null ? level.Name : null;
            }
            catch { return null; }
        }
    }
}
