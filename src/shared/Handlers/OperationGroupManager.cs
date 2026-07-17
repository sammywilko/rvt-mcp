using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// SLS A4 operation groups — PRD §12.7's begin/commit/rollback_operation_group,
    /// implemented as a created-elements LEDGER with compensating delete, not a live
    /// TransactionGroup.
    ///
    /// Why: each MCP request runs inside an ExternalEvent callback, and Revit requires
    /// every transaction-scope object opened in a callback to be closed before the
    /// callback returns — a TransactionGroup cannot legally stay open across begin →
    /// commit calls (Codex review 2026-07-16, finding 1). Instead, every SLS write
    /// commits its own transaction immediately and reports its created element ids to
    /// this ledger; rollback deletes exactly those elements in one transaction.
    ///
    /// Because the A4 surface is creation-only, deleting the created elements IS a
    /// complete rollback of everything a group can stage. When a modification group
    /// ships (A5+), staged semantics need a different mechanism — do not extend the
    /// ledger to cover mutations.
    ///
    /// Trade-off vs a real TransactionGroup, documented deliberately: each write stays
    /// its own Revit undo entry (commit does NOT collapse them into one), and rollback
    /// is a new forward transaction (itself undoable) rather than an undo.
    ///
    /// Safety properties (Codex review findings 1+2 addressed):
    /// - No Revit state is held between callbacks — an abandoned group can never wedge
    ///   the model or the bridge.
    /// - begin returns a group id; commit/rollback must present it (ownership token).
    /// - Manual user edits and non-SLS tools are never captured by the ledger, so
    ///   rollback can never destroy work the group didn't create.
    /// - A stale group (no SLS write for 10 min) auto-CLOSES keeping its elements —
    ///   the safe direction, since silently deleting aged work would be data loss.
    /// </summary>
    internal static class OperationGroupManager
    {
        public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Max staged (ledgered) element ids per group (Codex r3 finding 5): rollback
        /// probe-deletes and then really deletes the whole set synchronously on the
        /// UI thread, so the ledger must be bounded. Includes the full creation
        /// closure (scaffolding ids), hence roomy.
        /// </summary>
        public const int MaxStagedElements = 2000;

        private static readonly object Gate = new object();
        private static string _groupId;
        private static string _name;
        private static Document _doc;
        private static readonly List<long> _createdIds = new List<long>();
        private static DateTime _startedUtc;
        private static DateTime _lastTouchedUtc;
        private static string _autoCloseNote;

        public static bool IsActive
        {
            get { lock (Gate) { ExpireIfStaleLocked(); return _groupId != null; } }
        }

        /// <summary>
        /// Stable same-document check. Revit hands out different managed wrappers
        /// for the same open document across ExternalEvent callbacks, so
        /// ReferenceEquals false-positives "different document" (caught live in the
        /// A4 verify run). Document.Equals compares the underlying native document.
        /// </summary>
        private static bool SameDocument(Document a, Document b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            try { return a.IsValidObject && b.IsValidObject && a.Equals(b); }
            catch { return false; }
        }

        /// <summary>
        /// Fail-closed pre-check for every SLS write (Codex round 2, finding 2): a
        /// write must never silently commit OUTSIDE an open group the caller thinks
        /// it is inside. Returns an error string (the write must not run) or null.
        /// </summary>
        public static string ValidateForWrite(Document doc, string requestedGroupId)
        {
            lock (Gate)
            {
                ExpireIfStaleLocked();
                if (_groupId == null)
                    return string.IsNullOrWhiteSpace(requestedGroupId)
                        ? null
                        : "operationGroupId was passed but no operation group is open " +
                          "(it may have auto-closed). Call begin_operation_group first, or omit operationGroupId.";

                // The group's own document was closed (Codex round 4): its ledger can
                // no longer be rolled back there, and "switch back to its document"
                // would be advice with no referent. Auto-close exactly like the stale
                // path — keep the elements, free the group — instead of misreporting
                // the state as a document mismatch and blocking writes until timeout.
                if (_doc == null || !_doc.IsValidObject)
                {
                    var closedName = _name;
                    var closedCount = _createdIds.Count;
                    ClearLocked();
                    _autoCloseNote = "group '" + closedName + "' auto-closed because its document was " +
                                     "closed; its " + closedCount + " staged elements were KEPT " +
                                     "(auto-close commits, never deletes).";
                    App.DebugLog("SLS operation group auto-closed (document closed): " + closedName);
                    return string.IsNullOrWhiteSpace(requestedGroupId)
                        ? null
                        : "operationGroupId was passed but that group's document was closed, so the " +
                          "group auto-closed (staged elements kept). Call begin_operation_group again.";
                }

                if (!SameDocument(doc, _doc))
                    return "An operation group ('" + _name + "') is open on a DIFFERENT document — this " +
                           "write would silently land outside it. Commit or roll back the group, or switch " +
                           "back to its document, then retry.";

                // While a group is open the token is REQUIRED (Codex r3 finding 2):
                // otherwise a later/other client's write would be silently captured
                // into a group it knows nothing about — and deleted by its rollback.
                if (string.IsNullOrWhiteSpace(requestedGroupId))
                    return "An operation group ('" + _name + "') is open: pass its operationGroupId to " +
                           "stage this write in it, or commit/rollback the group first. Nothing was written.";

                if (!string.Equals(requestedGroupId.Trim(), _groupId, StringComparison.Ordinal))
                    return "operationGroupId does not match the open operation group ('" + _name + "'). " +
                           "Nothing was written.";

                if (_createdIds.Count >= MaxStagedElements)
                    return "The operation group ('" + _name + "') has reached its staged-element budget (" +
                           MaxStagedElements + " ids incl. creation closure). Commit or roll it back " +
                           "before further writes.";

                return null;
            }
        }

        /// <summary>
        /// Record a successful (non-dry-run) SLS write's created elements. Returns
        /// true when the ids were staged in an open group (callers surface this as
        /// operation_group_recorded — never claim staging that didn't happen).
        /// </summary>
        public static bool RecordWrite(Document doc, IEnumerable<long> createdElementIds)
        {
            lock (Gate)
            {
                ExpireIfStaleLocked();
                if (_groupId == null || !SameDocument(doc, _doc) || createdElementIds == null)
                    return false;
                _createdIds.AddRange(createdElementIds);
                _lastTouchedUtc = DateTime.UtcNow;
                return true;
            }
        }

        public static CommandResult Begin(UIApplication app, string name)
        {
            var doc = app.ActiveUIDocument == null ? null : app.ActiveUIDocument.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            lock (Gate)
            {
                ExpireIfStaleLocked();
                if (_groupId != null)
                    return CommandResult.Fail(
                        "An operation group is already open ('" + _name + "', id " + _groupId +
                        ", opened " + OpenSeconds() + "s ago). Commit or roll it back first — " +
                        "only one operation group can be open at a time.");

                _groupId = Guid.NewGuid().ToString("N").Substring(0, 12);
                _name = string.IsNullOrWhiteSpace(name) ? "SLS operation group" : name.Trim();
                _doc = doc;
                _createdIds.Clear();
                _startedUtc = DateTime.UtcNow;
                _lastTouchedUtc = _startedUtc;

                return CommandResult.Ok(new
                {
                    action = "begun",
                    group_id = _groupId,
                    name = _name,
                    stale_after_seconds = StaleAfter.TotalSeconds,
                    note = "Pass this group_id as operationGroupId on EVERY SLS write you want staged — " +
                           "while the group is open, writes without it are refused (nothing is captured " +
                           "silently). commit_operation_group keeps the staged elements; " +
                           "rollback_operation_group deletes them. After " + (int)StaleAfter.TotalMinutes +
                           " min without writes the group auto-closes KEEPING its elements."
                });
            }
        }

        public static CommandResult Commit(string groupId)
        {
            lock (Gate)
            {
                ExpireIfStaleLocked();
                var notReady = GuardOpenGroupLocked(groupId, "commit");
                if (notReady != null) return notReady;

                var result = new
                {
                    action = "committed",
                    group_id = _groupId,
                    name = _name,
                    element_ids = _createdIds.ToList(),
                    element_count = _createdIds.Count,
                    open_seconds = OpenSeconds(),
                    note = "All " + _createdIds.Count + " staged elements are kept. Each write was " +
                           "its own Revit transaction (and undo entry); commit closes the staging ledger."
                };
                ClearLocked();
                return CommandResult.Ok(result);
            }
        }

        public static CommandResult Rollback(UIApplication app, string groupId)
        {
            lock (Gate)
            {
                ExpireIfStaleLocked();
                var notReady = GuardOpenGroupLocked(groupId, "rollback");
                if (notReady != null) return notReady;

                if (_doc == null || !_doc.IsValidObject)
                {
                    var name = _name;
                    ClearLocked();
                    return CommandResult.Fail(
                        "The operation group's document has been closed; group '" + name +
                        "' was discarded without deleting anything.");
                }

                var doc = _doc;
                // Cascade deletes (a wall taking its hosted doors) may have already
                // removed some staged ids — delete only what still exists.
                var alive = _createdIds
                    .Where(id => doc.GetElement(RevitCompat.ToElementId(id)) != null)
                    .Select(RevitCompat.ToElementId)
                    .ToList();

                var deleted = new List<long>();
                if (alive.Count > 0)
                {
                    // Probe pass (Codex round 2, finding 1): Document.Delete cascades to
                    // dependents — e.g. a door the USER hosted on a staged wall after we
                    // created it. Deleting that would be data loss, not rollback. Run the
                    // delete in a transaction that is ALWAYS rolled back, inspect the
                    // closure, and refuse if it contains any unledgered user-meaningful
                    // element. Internal scaffolding (sketches, planes, curves, openings,
                    // constraints) is expected to cascade and does not block.
                    List<ElementId> closure;
                    using (var probe = new Transaction(doc, "SLS: rollback probe (always rolled back)"))
                    {
                        if (probe.Start() != TransactionStatus.Started)
                            return CommandResult.Fail(
                                "rollback_operation_group: could not start the probe transaction " +
                                "(document read-only, or another transaction open?). The group stays open.");
                        SlsWriteSupport.FailureScope.Attach(probe);
                        try
                        {
                            var removed = doc.Delete(alive);
                            closure = removed == null ? new List<ElementId>() : removed.ToList();
                        }
                        catch (Exception ex)
                        {
                            if (!probe.HasEnded()) probe.RollBack();
                            return CommandResult.Fail(
                                "rollback_operation_group: probing the deletion failed: " + ex.Message +
                                ". The group stays open.");
                        }
                        probe.RollBack();
                    }

                    var stagedSet = new HashSet<long>(_createdIds);
                    var blockers = closure
                        .Where(id => !stagedSet.Contains(RevitCompat.GetId(id)))
                        .Select(doc.GetElement)          // elements exist again after the probe rollback
                        .Where(el => el != null && IsUserMeaningful(el))
                        .Select(el => (object)new
                        {
                            id = RevitCompat.GetId(el.Id),
                            category = el.Category == null ? null : el.Category.Name,
                            name = el.Name
                        })
                        .ToList();

                    if (blockers.Count > 0)
                        return CommandResult.Fail(
                            "rollback_operation_group refused: deleting the staged elements would also " +
                            "delete " + blockers.Count + " element(s) the group did NOT create (work was " +
                            "hosted on or depends on staged elements): " +
                            Newtonsoft.Json.JsonConvert.SerializeObject(blockers) + ". " +
                            "Delete or rehost those explicitly, or commit the group instead. The group stays open.");

                    using (var tx = new Transaction(doc, "SLS: rollback_operation_group '" + _name + "'"))
                    {
                        if (tx.Start() != TransactionStatus.Started)
                            return CommandResult.Fail(
                                "rollback_operation_group: could not start the delete transaction " +
                                "(document read-only, or another transaction open?). The group stays open.");
                        var scope = SlsWriteSupport.FailureScope.Attach(tx);
                        try
                        {
                            var removed = doc.Delete(alive);
                            deleted = removed == null
                                ? new List<long>()
                                : removed.Select(RevitCompat.GetId).ToList();
                        }
                        catch (Exception ex)
                        {
                            if (!tx.HasEnded()) tx.RollBack();
                            return SlsWriteSupport.Failure(
                                "rollback_operation_group failed to delete the staged elements: " +
                                ex.Message + ". The group stays open.", scope);
                        }
                        if (tx.Commit() != TransactionStatus.Committed)
                            return SlsWriteSupport.Failure(
                                "rollback_operation_group: the delete transaction was rolled back by Revit. " +
                                "The group stays open.", scope);
                    }
                }

                var staged = _createdIds.Count;
                var result = new
                {
                    action = "rolled_back",
                    group_id = _groupId,
                    name = _name,
                    staged_element_count = staged,
                    deleted_element_count = deleted.Count,
                    open_seconds = OpenSeconds(),
                    note = "All elements created through SLS writes in this group were deleted " +
                           "(cascade-deleted ids counted as already gone). Manual edits were not touched. " +
                           "The deletion is itself one undo entry in Revit."
                };
                ClearLocked();
                return CommandResult.Ok(result);
            }
        }

        private static CommandResult GuardOpenGroupLocked(string groupId, string verb)
        {
            if (_groupId == null)
            {
                var note = _autoCloseNote;
                _autoCloseNote = null;
                return CommandResult.Fail(
                    "No operation group is open" + (note == null ? "." : " — " + note));
            }

            if (string.IsNullOrWhiteSpace(groupId))
                return CommandResult.Fail(
                    "groupId is required: pass the group_id returned by begin_operation_group " +
                    "to " + verb + " group '" + _name + "'.");

            if (!string.Equals(groupId.Trim(), _groupId, StringComparison.Ordinal))
                return CommandResult.Fail(
                    "groupId does not match the open operation group. Nothing was " + verb + "ed.");

            return null;
        }

        private static void ExpireIfStaleLocked()
        {
            if (_groupId == null)
                return;
            if (DateTime.UtcNow - _lastTouchedUtc <= StaleAfter)
                return;
            var name = _name;
            var count = _createdIds.Count;
            ClearLocked();
            _autoCloseNote = "group '" + name + "' auto-closed after " +
                             (int)StaleAfter.TotalMinutes + " min without writes; its " + count +
                             " staged elements were KEPT (auto-close commits, never deletes).";
            App.DebugLog("SLS operation group auto-closed (stale): " + name);
        }

        /// <summary>
        /// Deletion-closure classifier: elements a user (or another tool) could have
        /// meaningfully created block a rollback; Revit-internal scaffolding that
        /// legitimately cascades with our elements does not. Deliberately
        /// conservative — unknown model/annotation classes count as user-meaningful.
        /// </summary>
        private static bool IsUserMeaningful(Element element)
        {
            // Internal scaffolding that cascades with sketched elements. Opening is
            // NOT exempt — users create openings via NewOpening, so deleting one is
            // data loss (Codex r3 finding 3). With the full creation closure now
            // ledgered by RunWrite, our own transactions' scaffolding is matched by
            // id and this classifier only judges genuinely foreign dependents.
            if (element is Sketch || element is SketchPlane || element is CurveElement ||
                element.Category == null)
                return false;
            return element is FamilyInstance || element is Dimension || element is IndependentTag ||
                   element is TextNote || element is Wall || element is Floor || element is Ceiling ||
                   element is RoofBase || element is Autodesk.Revit.DB.Architecture.Room ||
                   element.Category.CategoryType == CategoryType.Annotation ||
                   element.Category.CategoryType == CategoryType.Model;
        }

        private static void ClearLocked()
        {
            _groupId = null;
            _name = null;
            _doc = null;
            _createdIds.Clear();
        }

        private static double OpenSeconds()
        {
            return Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 1);
        }
    }
}
