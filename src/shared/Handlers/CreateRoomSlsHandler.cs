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
    //  - the duplicate-number check runs inside the write, scoped to the phase
    //    the room ACTUALLY landed in (numbers are per-phase, and predicting the
    //    landing phase from a headless context proved unreliable in W7), before
    //    the number is set — refused with the collision identified;
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
    ""expectedPhase"": { ""type"": ""string"", ""description"": ""Optional phase name ASSERTED, not selected — creation always follows the active view's phase. Refused up front if it conflicts with the active view, rolled back if the created room still lands elsewhere. Default: the active view's phase, falling back to the document's last phase"" },
    ""requireEnclosed"": { ""type"": ""boolean"", ""description"": ""Fail (and roll back) unless the room encloses with a positive area (default false)"" },
    ""operationGroupId"": { ""type"": ""string"", ""description"": ""Optional: the open operation group id — must match or the write is refused"" },
    ""dryRun"": { ""type"": ""boolean"", ""description"": ""Create + capture warnings, then roll back (default false)"" }
  }
}";

        // The ONE phase-resolution rule, applied to the new room and to every
        // collision candidate alike: the room's own Phase parameter
        // (ROOM_PHASE_ID) when populated, else the generic CreatedPhaseId.
        // Mixing the two domains across sides of a comparison is how a legal
        // cross-phase duplicate gets refused or a real one vetted (Codex review
        // 20260718-020206 finding 2).
        private static Phase ResolveRoomPhase(Document doc, SpatialElement room)
        {
            var phaseParam = room.get_Parameter(BuiltInParameter.ROOM_PHASE_ID);
            if (phaseParam != null && phaseParam.HasValue)
            {
                var fromParam = doc.GetElement(phaseParam.AsElementId()) as Phase;
                if (fromParam != null) return fromParam;
            }
            return doc.GetElement(room.CreatedPhaseId) as Phase;
        }

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

            // Room numbers are unique PER PHASE, not per document (the same number
            // in a different phase is legal — Autodesk "About Phase-Specific
            // Rooms"). There is no reliable way to predict which phase
            // NewRoom(Level, UV) will use from a headless ExternalEvent context,
            // so the duplicate-number check runs INSIDE the write, scoped to the
            // phase the room ACTUALLY landed in — never a prediction (Codex
            // review 20260718-014018: a predicted-phase pre-check both wrongly
            // refused free numbers and wrongly vetted taken ones).
            //
            // The EXPECTED phase below is a separate concern: caller INTENT. It
            // only decides whether the landed room is acceptable — it never
            // scopes the uniqueness check, so a wrong default cannot corrupt the
            // duplicate guarantee; it can only refuse (Codex review
            // 20260718-020206 finding 1: an unintended landing phase must not
            // return success).
            var phases = doc.Phases;
            if (phases == null || phases.Size == 0)
                return CommandResult.Fail("The document has no phases; a room cannot be created.");
            var requestedPhaseName = request.Value<string>("expectedPhase");
            requestedPhaseName = string.IsNullOrWhiteSpace(requestedPhaseName) ? null : requestedPhaseName.Trim();
            // Creation is ALWAYS active-view-driven (NewRoom(Level, UV) has no
            // phase selector) — expectedPhase asserts intent, it cannot steer.
            // So an explicit expectedPhase that conflicts with the active view is
            // refused BEFORE any write; the post-create check remains the
            // authoritative net for whatever Revit actually did (Codex review
            // 20260718-021237 finding 1: no selector-shaped argument that
            // silently ignores the request).
            Phase viewPhase = null;
            var activeView = app.ActiveUIDocument.ActiveView;
            var viewPhaseParam = activeView != null ? activeView.get_Parameter(BuiltInParameter.VIEW_PHASE) : null;
            if (viewPhaseParam != null && viewPhaseParam.HasValue)
                viewPhase = doc.GetElement(viewPhaseParam.AsElementId()) as Phase;
            Phase expectedPhase = null;
            if (requestedPhaseName != null)
            {
                foreach (Phase p in phases)
                    if (string.Equals(p.Name, requestedPhaseName, StringComparison.Ordinal)) { expectedPhase = p; break; }
                if (expectedPhase == null)
                {
                    var known = new List<string>();
                    foreach (Phase p in phases) known.Add("'" + p.Name + "'");
                    return CommandResult.Fail(
                        "Unknown expectedPhase '" + requestedPhaseName + "'. Known phases: " + string.Join(", ", known) + ".");
                }
                if (viewPhase != null && RevitCompat.GetId(viewPhase.Id) != RevitCompat.GetId(expectedPhase.Id))
                    return CommandResult.Fail(
                        "expectedPhase '" + expectedPhase.Name + "' conflicts with the active view's phase '" +
                        viewPhase.Name + "' — room creation follows the active view. Activate a view in the " +
                        "intended phase (revit_set_view_phase / revit_activate_view), then retry.");
            }
            else
            {
                expectedPhase = viewPhase != null ? viewPhase : phases.get_Item(phases.Size - 1);
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
                doc.Regenerate();

                // Resolve the phase the room ACTUALLY landed in, evidence-first
                // (ResolveRoomPhase: ROOM_PHASE_ID, else CreatedPhaseId — one rule
                // for the new room AND every collision candidate, never mixed
                // domains). Asserting CreatedPhaseId straight after NewRoom refused
                // every create in the first live run (W7, 2026-07-18); a refusal
                // here carries both raw values so a live failure is
                // self-diagnosing, not another guess at the lifecycle.
                var actualPhase = ResolveRoomPhase(doc, room);
                if (actualPhase == null)
                {
                    var roomPhaseParam = room.get_Parameter(BuiltInParameter.ROOM_PHASE_ID);
                    throw new InvalidOperationException(
                        "The room's phase is unresolved after regeneration (ROOM_PHASE_ID " +
                        (roomPhaseParam != null && roomPhaseParam.HasValue ? RevitCompat.GetId(roomPhaseParam.AsElementId()).ToString() : "(unset)") +
                        ", CreatedPhaseId " + RevitCompat.GetId(room.CreatedPhaseId) +
                        "). Refusing rather than risk a duplicate number.");
                }

                // Caller-intent enforcement: the landed phase must be the expected
                // one (explicit `phase` arg, else the active view's phase). A
                // room's phase is read-only once created — a wrong-phase room is
                // not a reportable footnote, it is a rollback.
                if (RevitCompat.GetId(actualPhase.Id) != RevitCompat.GetId(expectedPhase.Id))
                    throw new InvalidOperationException(
                        "The room landed in phase '" + actualPhase.Name + "' but '" + expectedPhase.Name +
                        "' was required (" + (requestedPhaseName != null ? "explicit expectedPhase argument" : "the active view's phase") +
                        "). Rolled back — activate a view in the intended phase and retry.");

                // Duplicate-number check, scoped to the actual phase and run BEFORE
                // the number is set — refuse with the collision identified instead
                // of surfacing as a commit-time warning (the collision that went
                // modal through the upstream tool). Stored numbers are trimmed like
                // the request so a padded stored value cannot dodge the check. A
                // same-number candidate whose phase cannot be resolved fails the
                // call (fail closed), never silently skips.
                if (requestedNumber != null)
                {
                    var sameNumber = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType()
                        .Cast<SpatialElement>()
                        .Where(r => RevitCompat.GetId(r.Id) != RevitCompat.GetId(room.Id))
                        .Where(r => string.Equals((r.Number ?? string.Empty).Trim(), requestedNumber, StringComparison.Ordinal))
                        .ToList();
                    foreach (var candidate in sameNumber)
                    {
                        var candidatePhase = ResolveRoomPhase(doc, candidate);
                        if (candidatePhase == null)
                            throw new InvalidOperationException(
                                "Room id " + RevitCompat.GetId(candidate.Id) + " also holds number '" + requestedNumber +
                                "' but its phase cannot be resolved — refusing rather than risk a duplicate number.");
                        if (RevitCompat.GetId(candidatePhase.Id) == RevitCompat.GetId(actualPhase.Id))
                        {
                            var collisionLevel = candidate.Level != null ? candidate.Level.Name : "(no level)";
                            throw new InvalidOperationException(
                                "Room number '" + requestedNumber + "' is already used in phase '" + actualPhase.Name +
                                "' by room id " + RevitCompat.GetId(candidate.Id) + " ('" + candidate.Name + "', level " +
                                collisionLevel + "). Pass a unique number, or omit number to let Revit assign the next free one.");
                        }
                    }
                }

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
                        phase = actualPhase.Name,
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
