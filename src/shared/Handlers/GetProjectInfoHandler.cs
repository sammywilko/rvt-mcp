using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A3 read connector: read counterpart to the fork's write-only revit_set_project_info.
    // Also folds get_revit_version (version number/name/build).
    public class GetProjectInfoHandler : IRevitCommand
    {
        public string Name => "get_project_info";
        public string Description =>
            "Project information (name, number, client, building, organization, address, author) " +
            "plus the running Revit version number, name and build.";
        public string ParametersSchema => @"{ ""type"": ""object"", ""properties"": {}, ""additionalProperties"": false }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var info = doc.ProjectInformation;
            var appl = app.Application;

            return CommandResult.Ok(new
            {
                project_name = info?.Name,
                project_number = info?.Number,
                client_name = info?.ClientName,
                building_name = info?.BuildingName,
                organization_name = info?.OrganizationName,
                organization_description = info?.OrganizationDescription,
                address = info?.Address,
                author = info?.Author,
                issue_date = info?.IssueDate,
                status = info?.Status,
                file_path = doc.PathName,
                is_workshared = doc.IsWorkshared,
                revit_version_number = appl.VersionNumber,
                revit_version_name = appl.VersionName,
                revit_version_build = appl.VersionBuild
            });
        }
    }
}
