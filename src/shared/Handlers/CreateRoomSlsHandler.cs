using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    // SLS A5/W2b: guarded room creation. The upstream create_room has no
    // IFailuresPreprocessor, so a duplicate room number raises Revit's
    // "duplicate number values" warning AS A BLOCKING MODAL and wedges the
    // bridge until a human dismisses the dialog stack — live-caught during the
    // A5.0 join spike (docs/recon/2026-07-17-a5-spike-join-gaps.md, headline).
    // This wrapper is the same failure class fix as the rest of the A4 group:
    // refuse the collision BEFORE writing, and run the write under FailureScope
    // so nothing that slips past the pre-check can ever go modal. Rooms created
    // here also enter the operation-group ledger (upstream rooms did not), so a
    // stage rollback deletes them like any other staged element.
    public class CreateRoomSlsHandler : IRevitCommand
    {
        public string Name => "create_room_sls";
        public string Description =>
            "Create and place a room at a seed point (mm) with non-modal failure capture. " +
            "Refuses duplicate room numbers up front. Reports enclosure state + area. Supports dryRun.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""x"", ""y"", ""level""],
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""Seed point X (mm) — must be inside the intended room boundary"" },
    ""y"": { ""type"": ""number"", ""description"": ""Seed point Y (mm)"" },
    ""level"": { ""type"": ""string"", ""description"": ""Level name (strict — no fallback)"" },
    ""name"": { ""type"": ""string"", ""description"": ""Optional room name"" },
    ""number"": { ""type"": ""string"", ""description"": ""Optional room number — refused up front if any existing room already holds it"" },
    ""requireEnclosed"": { ""type"": ""boolean"", ""description"": ""Fail (and roll back) unless the room encloses with a positive area (default false)"" },
    ""operationGroupId"": { ""type"": ""string"", ""description"": ""Optional: the open operation group id — must match or the write is refused"" },
    ""dryRun"": { ""type"": ""boolean"", ""description"": ""Create + capture warnings, then roll back (default false)"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var x = request.Value<double?>("x");
            var y = request.Value<double?>("y");
            if (x == null || y == null || !SlsWriteSupport.IsFinite(x.Value) || !SlsWriteSupport.IsFinite(y.Value))
                return CommandResult.Fail("x and y must be finite numbers (mm).");

            string error;
            var level = SlsWriteSupport.ResolveLevelStrict(doc, request.Value<string>("level"), out error);
            if (level == null) return CommandResult.Fail(error);

            var roomName = request.Value<string>("name");
            var requestedNumber = request.Value<string>("number");
            requestedNumber = string.IsNullOrWhiteSpace(requestedNumber) ? null : requestedNumber.Trim();
            var requireEnclosed = request.Value<bool?>("requireEnclosed") ?? false;
            var dryRun = request.Value<bool?>("dryRun") ?? false;

            // Pre-check: a requested number colliding with ANY existing room (placed,
            // unplaced or unenclosed — they all hold their number) is refused with the
            // collision identified, instead of surfacing as a commit-time warning. This
            // is the exact collision that went modal through the upstream tool.
            if (requestedNumber != null)
            {
                var collision = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<SpatialElement>()
                    .FirstOrDefault(r => string.Equals(r.Number, requestedNumber, StringComparison.Ordinal));
                if (collision != null)
                {
                    var collisionLevel = collision.Level != null ? collision.Level.Name : "(no level)";
                    return CommandResult.Fail(
                        "Room number '" + requestedNumber + "' is already used by room id " +
                        RevitCompat.GetId(collision.Id) + " ('" + collision.Name + "', level " + collisionLevel +
                        "). Pass a unique number, or omit number to let Revit assign the next free one.");
                }
            }

            return SlsWriteSupport.RunWrite(doc, "create_room_sls", dryRun,
                request.Value<string>("operationGroupId"), scope =>
            {
                var point = new UV(SlsWriteSupport.MmToFt(x.Value), SlsWriteSupport.MmToFt(y.Value));
                var room = doc.Create.NewRoom(level, point);
                if (room == null)
                    throw new InvalidOperationException("Revit returned no room for the seed point.");

                if (!string.IsNullOrWhiteSpace(roomName))
                    room.Name = roomName;
                if (requestedNumber != null)
                    room.Number = requestedNumber;
                doc.Regenerate();

                // Read-back verification (A4 rule: no invisible defaults — the value
                // reported must be the value in force, and a requested value that did
                // not land is a failure, not a success with a footnote).
                if (requestedNumber != null && !string.Equals(room.Number, requestedNumber, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Requested room number '" + requestedNumber + "' but Revit reports '" +
                        room.Number + "' after setting it.");
                if (!string.IsNullOrWhiteSpace(roomName) && !string.Equals(room.Name, roomName, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Requested room name '" + roomName + "' but Revit reports '" +
                        room.Name + "' after setting it.");

                var areaSqFt = room.Area;
                var enclosed = areaSqFt > 0;
                if (requireEnclosed && !enclosed)
                    throw new InvalidOperationException(
                        "requireEnclosed: the room did not enclose at (" + x.Value + ", " + y.Value +
                        ") mm on level '" + level.Name + "' — no bounding loop surrounds the seed point. " +
                        "The placement was rolled back.");

                return new
                {
                    element_ids = new[] { RevitCompat.GetId(room.Id) },
                    room = new
                    {
                        id = RevitCompat.GetId(room.Id),
                        name = room.Name,
                        number = room.Number,
                        level = level.Name,
                        enclosure_state = enclosed ? "enclosed" : "not_enclosed",
                        // The C1/W8 read-back consumes this — null, never 0, when unenclosed.
                        area_m2 = enclosed ? (double?)Math.Round(SlsWriteSupport.SqFtToM2(areaSqFt), 3) : null,
                        seed = new { x_mm = x.Value, y_mm = y.Value }
                    }
                };
            });
        }
    }
}
