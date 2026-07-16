using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// SLS A4 operation groups: a single named TransactionGroup that lets an agent
    /// stage several writes and land them as ONE undo entry (commit) or discard them
    /// all (rollback) — PRD §12.7's begin/commit/rollback_operation_group.
    ///
    /// Safety posture (the A2 "wedged bridge" lesson inverted): a dead client must
    /// never leave the model wedged. An Idling-event babysitter auto-rolls the group
    /// back when it goes stale (no SLS write or group call for <see cref="StaleAfter"/>),
    /// when its document closes, or when the active document changes. Only one group
    /// can be open at a time; groups are short-lived agent macro-steps, not sessions.
    ///
    /// Known caveat (documented, accepted for v0): manual edits made in Revit while a
    /// group is open assimilate or roll back with it.
    /// </summary>
    internal static class OperationGroupManager
    {
        public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

        private static readonly object Gate = new object();
        private static TransactionGroup _group;
        private static Document _doc;
        private static string _name;
        private static DateTime _startedUtc;
        private static DateTime _lastTouchedUtc;
        private static UIApplication _idlingApp;
        private static string _autoCloseNote;

        public static bool IsActive
        {
            get { lock (Gate) { return _group != null; } }
        }

        /// <summary>Called by SLS write tools after a successful write on the group's doc.</summary>
        public static void TouchFromWrite(Document doc)
        {
            lock (Gate)
            {
                if (_group != null && ReferenceEquals(doc, _doc))
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
                if (_group != null)
                    return CommandResult.Fail(
                        "An operation group is already open ('" + _name + "', opened " +
                        OpenSeconds() + "s ago). Commit or roll it back first — only one " +
                        "operation group can be open at a time.");

                var label = string.IsNullOrWhiteSpace(name) ? "SLS operation group" : name.Trim();
                var group = new TransactionGroup(doc, label);
                if (group.Start() != TransactionStatus.Started)
                {
                    group.Dispose();
                    return CommandResult.Fail(
                        "Could not start an operation group (is another transaction or group open?).");
                }

                _group = group;
                _doc = doc;
                _name = label;
                _startedUtc = DateTime.UtcNow;
                _lastTouchedUtc = _startedUtc;

                // Idling babysitter: staleness / doc-close / doc-switch auto-rollback.
                // Idling handlers run on the Revit UI thread, where TransactionGroup
                // operations are legal.
                if (_idlingApp == null)
                {
                    _idlingApp = app;
                    app.Idling += OnIdling;
                }

                return CommandResult.Ok(StatusLocked("begun"));
            }
        }

        public static CommandResult Commit()
        {
            lock (Gate)
            {
                var notReady = GuardOpenGroupLocked("commit");
                if (notReady != null) return notReady;

                var name = _name;
                var openSeconds = OpenSeconds();
                var status = _group.Assimilate();
                var committed = status == TransactionStatus.Committed;
                if (committed)
                {
                    ClearLocked();
                    return CommandResult.Ok(new
                    {
                        action = "committed",
                        undo_entry = name,
                        open_seconds = openSeconds,
                        note = "All writes in the group are now a single undo entry named '" + name + "'."
                    });
                }

                // Assimilate failing leaves the group in an undefined state — force it shut
                // so the model is never left wedged behind a half-closed group.
                try { if (!_group.HasEnded()) _group.RollBack(); }
                catch { }
                ClearLocked();
                return CommandResult.Fail(
                    "commit_operation_group: Assimilate returned " + status +
                    "; the group was rolled back to avoid leaving the model in a wedged state.");
            }
        }

        public static CommandResult Rollback()
        {
            lock (Gate)
            {
                var notReady = GuardOpenGroupLocked("rollback");
                if (notReady != null) return notReady;

                var name = _name;
                var openSeconds = OpenSeconds();
                var status = _group.RollBack();
                ClearLocked();
                if (status == TransactionStatus.RolledBack)
                    return CommandResult.Ok(new
                    {
                        action = "rolled_back",
                        name,
                        open_seconds = openSeconds,
                        note = "All writes in the group were discarded; the model is back to its pre-group state."
                    });
                return CommandResult.Fail("rollback_operation_group: RollBack returned " + status + ".");
            }
        }

        /// <summary>
        /// Shared open-group guard: reports a missing group (surfacing any auto-close
        /// note exactly once, so the agent learns WHY its group vanished), and clears
        /// a group whose document has been closed.
        /// </summary>
        private static CommandResult GuardOpenGroupLocked(string verb)
        {
            if (_group == null)
            {
                var note = _autoCloseNote;
                _autoCloseNote = null;
                return CommandResult.Fail(
                    "No operation group is open" +
                    (note == null ? "." : " — " + note));
            }

            if (_doc == null || !_doc.IsValidObject)
            {
                try { _group.Dispose(); }
                catch { }
                ClearLocked();
                return CommandResult.Fail(
                    "The operation group's document has been closed; the group is gone. " +
                    "Nothing to " + verb + ".");
            }

            return null;
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            lock (Gate)
            {
                if (_group == null)
                {
                    // Group already closed — retire the babysitter (safe on the UI thread).
                    if (_idlingApp != null)
                    {
                        _idlingApp.Idling -= OnIdling;
                        _idlingApp = null;
                    }
                    return;
                }

                if (_doc == null || !_doc.IsValidObject)
                {
                    try { _group.Dispose(); }
                    catch { }
                    ClearLocked();
                    _autoCloseNote = "the group's document was closed, so the group was discarded.";
                    App.DebugLog("SLS operation group discarded: document closed.");
                    return;
                }

                var app = sender as UIApplication;
                var activeDoc = app == null || app.ActiveUIDocument == null ? null : app.ActiveUIDocument.Document;
                if (activeDoc != null && !ReferenceEquals(activeDoc, _doc))
                {
                    AutoRollbackLocked("the active document changed while the group was open");
                    return;
                }

                if (DateTime.UtcNow - _lastTouchedUtc > StaleAfter)
                    AutoRollbackLocked("the group went stale (no writes for " +
                                       (int)StaleAfter.TotalMinutes + " min — client assumed dead)");
            }
        }

        private static void AutoRollbackLocked(string reason)
        {
            try
            {
                if (!_group.HasEnded()) _group.RollBack();
            }
            catch (Exception ex)
            {
                App.DebugLog("SLS operation group auto-rollback threw: " + ex.Message);
            }
            ClearLocked();
            _autoCloseNote = "it was auto-rolled-back because " + reason + ".";
            App.DebugLog("SLS operation group auto-rolled-back: " + reason);
        }

        private static void ClearLocked()
        {
            _group = null;
            _doc = null;
            _name = null;
        }

        private static double OpenSeconds()
        {
            return Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 1);
        }

        private static object StatusLocked(string action)
        {
            return new
            {
                action,
                name = _name,
                stale_after_seconds = StaleAfter.TotalSeconds,
                note = "Subsequent SLS writes are staged in this group. Commit lands them as one undo " +
                       "entry; rollback discards them. The group auto-rolls-back if idle for " +
                       (int)StaleAfter.TotalMinutes + " min, or if the document changes or closes."
            };
        }
    }
}
