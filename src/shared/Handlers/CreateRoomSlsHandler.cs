using System;
using System.Collections.Generic;
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
    //
    // Defence in depth (Codex adversarial review, 2026-07-18):
    //  - pre-check refuses number collisions with the collision identified,
    //    scoped to the phase NewRoom will actually use (numbers are per-phase);
    //  - the write runs under FailureScope with duplicate-number and
    //    room-in-same-region warnings declared FATAL, so anything the
    //    pre-check misses rolls back instead of committing suppressed;
    //  - name/number go through ROOM_NAME/ROOM_NUMBER parameters — Room.Name
    //    returns the COMPOSED "name number" string and would fail an equality
    //    read-back on every valid named room.
    // Rooms created here enter the operation-group ledger (upstream rooms did
    // not), so a stage rollback deletes them like any other staged element.
    public class CreateRoomSlsHandler : IRevitCommand
    {
        public string Name => "create_room_sls";
        public string Description =>
            "Create and place a room at a seed point (mm) with non-modal failure capture. " +
            "Refuses duplicate room numbers (checked in the creation phase) and occupied " +
            "regions up front or by rollback — never a suppressed warning. Reports enclosure " +
            "state + area + phase. Supports dryRun.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""x"", ""y"", ""level""],
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""Seed point X (mm) — must be inside the intended room boundary"" },
    ""y"": { ""type"": ""number"", ""description"": ""Seed point Y (mm)"" },
    ""level"": { ""type"": ""string"", ""description"": ""Level name (strict — no fallback)"" },
    ""name"": { ""type"": ""string"", ""description"": ""Optional room name"" },
    ""number"": { ""type"": ""string"", ""description"": ""Optional room number — refused if any room in the creation phase already holds it"" },
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
            roomName = string.IsNullOrWhiteSpace(roomName) ? null : roomName.Trim();
            var requestedNumber = request.Value<string>("number");
            requestedNumber = string.IsNullOrWhiteSpace(requestedNumber) ? null : requestedNumber.Trim();
            var requireEnclosed = request.Value<bool?>("requireEnclosed") ?? false;
            var dryRun = request.Value<bool?>("dryRun") ?? false;

            // NewRoom(Level, UV) places the room in the document's LAST phase (Revit
            // API contract). Room numbers are unique per phase, not per document, so
            // the pre-check must be scoped to that phase or it refuses valid input
            // (an Existing-phase "101" does not block a New Construction "101").
            var phases = doc.Phases;
            if (phases == null || phases.Size == 0)
                return CommandResult.Fail("The document has no phases; a room cannot be created.");
            var creationPhase = phases.get_Item(phases.Size - 1);

            // Pre-check: a requested number colliding with a room in the creation
            // phase (placed, unplaced or unenclosed — they all hold their number) is
            // refused with the collision identified, instead of surfacing as a
            // commit-time warning. This is the exact collision that went modal
            // through the upstream tool. Stored numbers are trimmed like the request
            // so a padded stored value cannot dodge the check.
            if (requestedNumber != null)
            {
                var collision = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<SpatialElement>()
                    .Where(r => RevitCompat.GetId(r.CreatedPhaseId) == RevitCompat.GetId(creationPhase.Id))
                    .FirstOrDefault(r => string.Equals((r.Number ?? string.Empty).Trim(), requestedNumber, StringComparison.Ordinal));
                if (collision != null)
                {
                    var collisionLevel = collision.Level != null ? collision.Level.Name : "(no level)";
                    return CommandResult.Fail(
                        "Room number '" + requestedNumber + "' is already used in phase '" + creationPhase.Name +
                        "' by room id " + RevitCompat.GetId(collision.Id) + " ('" + collision.Name + "', level " +
                        collisionLevel + "). Pass a unique number, or omit number to let Revit assign the next free one.");
                }
            }

            // Belt to the pre-check's braces: if a duplicate number or an
            // already-occupied region still reaches commit, the warning is FATAL —
            // rolled back with the real message — never suppressed-and-committed.
            var fatalWarnings = new List<FailureDefinitionId>
            {
                BuiltInFailures.GeneralFailures.DuplicateValue,
                BuiltInFailures.RoomFailures.RoomsInSameRegion,
            };

            return SlsWriteSupport.RunWrite(doc, "create_room_sls", dryRun,
                request.Value<string>("operationGroupId"), fatalWarnings, scope =>
            {
                var point = new UV(SlsWriteSupport.MmToFt(x.Value), SlsWriteSupport.MmToFt(y.Value));
                var room = doc.Create.NewRoom(level, point);
                if (room == null)
                    throw new InvalidOperationException("Revit returned no room for the seed point.");

                // The pre-check was scoped to the phase NewRoom is documented to use;
                // if Revit ever places the room elsewhere the uniqueness guarantee is
                // void — fail closed rather than report a wrongly-vetted room.
                if (RevitCompat.GetId(room.CreatedPhaseId) != RevitCompat.GetId(creationPhase.Id))
                    throw new InvalidOperationException(
                        "The room was created in a different phase than the uniqueness pre-check covered " +
                        "(expected '" + creationPhase.Name + "'). Refusing rather than risk a duplicate number.");

                // Room.Name is the COMPOSED "name number" string; ROOM_NAME is the
                // actual name. Set and read through the parameters so read-back
                // verification compares like with like (Codex review finding 1).
                var nameParam = room.get_Parameter(BuiltInParameter.ROOM_NAME);
                var numberParam = room.get_Parameter(BuiltInParameter.ROOM_NUMBER);
                if (roomName != null)
                {
                    if (nameParam == null || nameParam.IsReadOnly)
                        throw new InvalidOperationException("The room's name parameter is missing or read-only.");
                    if (!nameParam.Set(roomName))
                        throw new InvalidOperationException("Revit refused to set the room name.");
                }
                if (requestedNumber != null)
                {
                    if (numberParam == null || numberParam.IsReadOnly)
                        throw new InvalidOperationException("The room's number parameter is missing or read-only.");
                    if (!numberParam.Set(requestedNumber))
                        throw new InvalidOperationException("Revit refused to set the room number.");
                }
                doc.Regenerate();

                // Read-back verification (A4 rule: no invisible defaults — the value
                // reported must be the value in force, and a requested value that did
                // not land is a failure, not a success with a footnote).
                var actualName = nameParam != null ? nameParam.AsString() : null;
                var actualNumber = numberParam != null ? numberParam.AsString() : null;
                if (roomName != null && !string.Equals(actualName, roomName, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Requested room name '" + roomName + "' but Revit reports '" +
                        (actualName ?? "(null)") + "' after setting it.");
                if (requestedNumber != null && !string.Equals(actualNumber, requestedNumber, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Requested room number '" + requestedNumber + "' but Revit reports '" +
                        (actualNumber ?? "(null)") + "' after setting it.");

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
                        name = actualName,
                        number = actualNumber,
                        level = level.Name,
                        phase = creationPhase.Name,
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
