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

        /// <summary>Record a successful (non-dry-run) SLS write's created elements.</summary>
        public static void RecordWrite(Document doc, IEnumerable<long> createdElementIds)
        {
            lock (Gate)
            {
                ExpireIfStaleLocked();
                if (_groupId == null || !ReferenceEquals(doc, _doc) || createdElementIds == null)
                    return;
                _createdIds.AddRange(createdElementIds);
                _lastTouchedUtc = DateTime.UtcNow;
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
                    note = "Created elements from subsequent SLS writes are staged in this group. " +
                           "commit_operation_group keeps them and closes the group; " +
                           "rollback_operation_group deletes them. Pass group_id to both. " +
                           "After " + (int)StaleAfter.TotalMinutes + " min without writes the group " +
                           "auto-closes KEEPING its elements. Manual edits are never captured."
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
