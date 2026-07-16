using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// SLS A4 controlled writes: shared support for the safe-creation tool group.
    /// Encodes the A2 benchmark lessons as hard rules: strict (family, type) resolution
    /// with no silent type defaults, strict level resolution with no fallback, and
    /// non-modal failure capture — the error the agent receives must be the error that
    /// occurred, and a Revit dialog must never block the bridge.
    /// </summary>
    internal static class SlsWriteSupport
    {
        public const double MmPerFoot = 304.8;
        private const double SqFtToSqM = 0.09290304;

        public static double MmToFt(double mm) { return mm / MmPerFoot; }
        public static double FtToMm(double ft) { return ft * MmPerFoot; }
        public static double SqFtToM2(double sqFt) { return sqFt * SqFtToSqM; }

        // ---------------------------------------------------------------- levels

        /// <summary>
        /// Resolve a level by name, strictly: no fallback to "first level" when the
        /// name is missing or unknown (create_line_based_element's silent fallback is
        /// exactly the defect class A4 exists to remove).
        /// </summary>
        public static Level ResolveLevelStrict(Document doc, string levelName, out string error)
        {
            error = null;
            var all = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToList();

            if (string.IsNullOrWhiteSpace(levelName))
            {
                error = "level is required (a level name). No default level is applied. " +
                        "Known levels: " + KnownLevelNames(all);
                return null;
            }

            var wanted = levelName.Trim();
            var matches = all
                .Where(l => l.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 1)
                return matches[0];

            error = matches.Count == 0
                ? "Level '" + wanted + "' not found. Known levels: " + KnownLevelNames(all) +
                  ". No fallback level is applied."
                : "Level name '" + wanted + "' matches " + matches.Count + " levels; rename or use a unique name.";
            return null;
        }

        private static string KnownLevelNames(List<Level> all)
        {
            if (all.Count == 0) return "(none)";
            return string.Join(", ", all.OrderBy(l => l.ProjectElevation).Select(l => "'" + l.Name + "'"));
        }

        // ---------------------------------------------------------------- types

        /// <summary>
        /// Resolve an element type strictly from request fields typeId OR
        /// typeName (+ optional family). Never defaults. Ambiguity fails listing the
        /// candidates — 7 of 11 door type names in the stock metric template are
        /// ambiguous across families (A2 finding), so (family, type) addressing is
        /// first-class here.
        /// </summary>
        public static T ResolveTypeStrict<T>(
            Document doc, JObject request, string what, BuiltInCategory? category, out string error)
            where T : ElementType
        {
            error = null;
            var typeId = request.Value<long?>("typeId");
            var typeName = request.Value<string>("typeName");
            var family = request.Value<string>("family");

            if (typeId.HasValue)
            {
                var byId = doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as T;
                if (byId == null || (category.HasValue && !InCategory(byId, category.Value)))
                {
                    error = "typeId " + typeId.Value + " does not resolve to a " + what + ". " +
                            "Use revit_get_available_family_types to list valid types.";
                    return null;
                }
                return byId;
            }

            if (string.IsNullOrWhiteSpace(typeName))
            {
                error = "A " + what + " is required: pass typeId, or typeName (optionally with family). " +
                        "No default type is applied.";
                return null;
            }

            var collector = new FilteredElementCollector(doc).OfClass(typeof(T));
            if (category.HasValue)
                collector = collector.OfCategory(category.Value);

            var wantedType = typeName.Trim();
            var wantedFamily = string.IsNullOrWhiteSpace(family) ? null : family.Trim();

            var candidates = collector
                .Cast<T>()
                .Where(t => t.Name.Equals(wantedType, StringComparison.OrdinalIgnoreCase))
                .Where(t => wantedFamily == null ||
                            (t.FamilyName ?? string.Empty).Equals(wantedFamily, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 1)
                return candidates[0];

            if (candidates.Count == 0)
            {
                error = "No " + what + " named '" + wantedType + "'" +
                        (wantedFamily == null ? "" : " in family '" + wantedFamily + "'") +
                        " is loaded. Use revit_get_available_family_types to list valid types.";
                return null;
            }

            var listing = string.Join("; ", candidates
                .Take(10)
                .Select(t => "'" + (t.FamilyName ?? "?") + "': '" + t.Name + "' (typeId " + RevitCompat.GetId(t.Id) + ")"));
            error = "'" + wantedType + "' is ambiguous — " + candidates.Count + " " + what + "s match: " +
                    listing + ". Pass family to disambiguate, or use typeId.";
            return null;
        }

        private static bool InCategory(Element element, BuiltInCategory category)
        {
            var cat = element.Category;
            return cat != null && RevitCompat.GetId(cat.Id) == (long)(int)category;
        }

        // ---------------------------------------------------------------- geometry

        /// <summary>
        /// Parse a JSON array of {x, y} points (mm) into a closed, validated loop of
        /// XYZ points (feet, z = 0). Returns an error string, or null when valid.
        /// </summary>
        public static string TryParseLoopPoints(JArray points, out List<XYZ> loopFt)
        {
            loopFt = null;
            if (points == null || points.Count < 3)
                return "points must be an array of at least 3 {x, y} objects (mm).";

            var mm = new List<double[]>();
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i] as JObject;
                var x = p == null ? null : p.Value<double?>("x");
                var y = p == null ? null : p.Value<double?>("y");
                if (x == null || y == null || !IsFinite(x.Value) || !IsFinite(y.Value))
                    return "points[" + i + "] must be an object with finite numeric x and y (mm).";
                mm.Add(new[] { x.Value, y.Value });
            }

            // Tolerate an explicitly closed loop (last point repeats the first).
            if (mm.Count > 3 && Distance(mm[0], mm[mm.Count - 1]) < 1.0)
                mm.RemoveAt(mm.Count - 1);

            if (mm.Count < 3)
                return "points must describe at least 3 distinct corners.";

            for (var i = 0; i < mm.Count; i++)
            {
                var next = mm[(i + 1) % mm.Count];
                if (Distance(mm[i], next) < 1.0)
                    return "points[" + i + "] and the next point are less than 1 mm apart.";
            }

            // Shoelace area — rejects collinear inputs before Revit turns them into a
            // cryptic "curve loop is not valid" failure.
            double area2 = 0;
            for (var i = 0; i < mm.Count; i++)
            {
                var a = mm[i];
                var b = mm[(i + 1) % mm.Count];
                area2 += a[0] * b[1] - b[0] * a[1];
            }
            if (Math.Abs(area2 / 2.0) < 10000.0) // < 0.01 m² in mm²
                return "points are collinear or enclose no meaningful area.";

            loopFt = mm.Select(p => new XYZ(MmToFt(p[0]), MmToFt(p[1]), 0)).ToList();
            return null;
        }

        public static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double Distance(double[] a, double[] b)
        {
            var dx = a[0] - b[0];
            var dy = a[1] - b[1];
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ---------------------------------------------------------------- failures

        /// <summary>
        /// Non-modal failure capture for SLS write transactions. Warnings are collected
        /// into the typed response and suppressed; errors are collected and the
        /// transaction rolls back. Revit never raises a dialog the agent can't see.
        /// </summary>
        public sealed class FailureScope : IFailuresPreprocessor
        {
            public readonly List<object> Warnings = new List<object>();
            public readonly List<string> Errors = new List<string>();

            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                foreach (var failure in accessor.GetFailureMessages())
                {
                    string text;
                    List<long> ids;
                    try
                    {
                        text = failure.GetDescriptionText();
                        ids = failure.GetFailingElementIds().Select(RevitCompat.GetId).ToList();
                    }
                    catch (Exception ex)
                    {
                        text = "(failure message unavailable: " + ex.Message + ")";
                        ids = new List<long>();
                    }
                    if (string.IsNullOrWhiteSpace(text))
                        text = "(unnamed Revit failure)";

                    if (failure.GetSeverity() == FailureSeverity.Warning)
                    {
                        Warnings.Add(new { message = text, element_ids = ids });
                        accessor.DeleteWarning(failure);
                    }
                    else
                    {
                        Errors.Add(text);
                    }
                }

                return Errors.Count > 0
                    ? FailureProcessingResult.ProceedWithRollBack
                    : FailureProcessingResult.Continue;
            }

            public static FailureScope Attach(Transaction tx)
            {
                var scope = new FailureScope();
                var options = tx.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(scope);
                options.SetForcedModalHandling(false);
                options.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(options);
                return scope;
            }
        }

        // ---------------------------------------------------------------- execution

        /// <summary>
        /// Run one SLS write inside a Transaction with failure capture. When dryRun is
        /// true the transaction runs to a REAL commit inside a wrapping TransactionGroup
        /// which is then rolled back — so commit-time warnings are captured for real
        /// (a validate-only stub would have called Geopogo's dead create_roof "fine"),
        /// but the model is left unchanged.
        /// </summary>
        public static CommandResult RunWrite(Document doc, string opName, bool dryRun, Func<FailureScope, object> body)
        {
            TransactionGroup dryRunGroup = null;
            FailureScope scope = null;
            try
            {
                if (dryRun)
                {
                    dryRunGroup = new TransactionGroup(doc, "SLS dry-run: " + opName);
                    if (dryRunGroup.Start() != TransactionStatus.Started)
                        return CommandResult.Fail(opName + " dry-run: could not start a transaction group " +
                                                  "(is another transaction already open?).");
                }

                object payload;
                TransactionStatus commitStatus;
                using (var tx = new Transaction(doc, "SLS: " + opName))
                {
                    if (tx.Start() != TransactionStatus.Started)
                        return CommandResult.Fail(opName + ": could not start a transaction " +
                                                  "(document read-only, or another transaction open?).");
                    scope = FailureScope.Attach(tx);
                    try
                    {
                        payload = body(scope);
                    }
                    catch (Exception ex)
                    {
                        if (!tx.HasEnded()) tx.RollBack();
                        return Failure(opName + " failed: " + ex.Message, scope);
                    }
                    commitStatus = tx.Commit();
                }

                if (commitStatus != TransactionStatus.Committed)
                    return Failure(opName + " was rolled back by Revit.", scope);

                if (dryRunGroup != null)
                {
                    dryRunGroup.RollBack();
                    dryRunGroup.Dispose();
                    dryRunGroup = null;
                }

                OperationGroupManager.TouchFromWrite(doc);
                return Success(payload, scope, dryRun);
            }
            finally
            {
                if (dryRunGroup != null)
                {
                    // Cleanup must never mask the real result being returned.
                    try
                    {
                        if (dryRunGroup.HasStarted() && !dryRunGroup.HasEnded()) dryRunGroup.RollBack();
                        dryRunGroup.Dispose();
                    }
                    catch { }
                }
            }
        }

        public static CommandResult Success(object payload, FailureScope scope, bool dryRun)
        {
            var result = payload == null ? new JObject() : JObject.FromObject(payload);
            result["status"] = scope != null && scope.Warnings.Count > 0 ? "warning" : "success";
            result["warnings"] = scope == null ? new JArray() : JArray.FromObject(scope.Warnings);
            result["dry_run"] = dryRun;
            if (dryRun)
                result["dry_run_note"] = "All changes were rolled back; element_ids were real during the " +
                                         "dry run but no longer exist.";
            result["operation_group_active"] = OperationGroupManager.IsActive;
            return CommandResult.Ok(result);
        }

        public static CommandResult Failure(string message, FailureScope scope)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(message)) parts.Add(message);
            if (scope != null)
                foreach (var error in scope.Errors)
                    if (!parts.Contains(error)) parts.Add(error);
            return CommandResult.Fail(string.Join(" | ", parts));
        }
    }
}
