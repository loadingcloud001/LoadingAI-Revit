using System.Globalization;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using LoadingAI.Geometry;

namespace LoadingAI.Revit.StructureCuts;

/// <summary>
/// Splits structural framing members where they pass through structural columns. Cutting where the
/// centreline crosses the column's real plan outline leaves a member whole when a column only clips its
/// end, and a curved member is cut along its own curve instead of across a single chord.
/// <para>
/// Unjoined line and arc beams that are not grouped or pinned are split in place, so the original
/// element, its host and its parameters survive. Grouped, pinned, joined or non-line/arc members
/// recreate the location curve — native Split on those is a Revit process abort. Each member runs in
/// its own sub-transaction, so a member that fails either way is put back exactly as it was instead of
/// being left silently shortened.
/// </para>
/// </summary>
internal static class BeamSplit
{
    private const double MinFraction = CurveMemberSplit.MinFraction;

    public static string Run(UIApplication uiApp, IReadOnlyList<string> beamIds, IReadOnlyList<string> columnIds, SplitOptions options)
    {
        var uidoc = uiApp.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (uidoc == null || doc == null)
            return "Please open a model first.";

        var batch = SplitScope.Collect(doc, uidoc, SplitElements.IsBeam, options, beamIds);
        var beams = batch.Items;
        if (beams.Count == 0 && batch.Total == 0)
            return EmptyHostMessage(options);

        var columns = SplitScope.Cutters(doc, uidoc, columnIds, SplitElements.IsColumn);
        if (columns.Count == 0)
            return "No columns found.";

        var footprints = new Dictionary<string, List<List<Point2>>>(StringComparer.Ordinal);
        var plans = new List<CurveMemberSplit.Plan>();
        var skippedNoCurve = 0;
        var skippedNoCrossing = 0;
        foreach (var beam in beams)
        {
            var curve = GetCurve(beam);
            if (curve == null)
            {
                skippedNoCurve++;
                continue;
            }

            var cuts = CutFractions(beam, curve, columns, footprints);
            if (cuts.Count == 0)
            {
                skippedNoCrossing++;
                continue;
            }

            plans.Add(new CurveMemberSplit.Plan(beam, curve, cuts));
        }

        if (options.DryRun)
        {
            var pieces = plans.Sum(plan => plan.Cuts.Count + 1);
            return "Dry-run. " + $"Would split {plans.Count} beams into {pieces} pieces. No changes made."
                + SkipNote(skippedNoCurve, skippedNoCrossing, 0)
                + SplitReport.Progress(batch);
        }

        if (plans.Count == 0)
            return "Nothing to split." + SkipNote(skippedNoCurve, skippedNoCrossing, 0) + SplitReport.Progress(batch);

        var created = new List<ElementId>();
        var split = 0;
        var piecesMade = 0;
        var skippedUnchanged = 0;
        var skippedFailed = 0;
        var paramSkipped = 0;
        var native = 0;
        var recreated = 0;
        var dependents = 0;

        using (var tx = SplitElements.WriteTx(doc, "LoadingAI for Revit: split beams by columns"))
        {
            tx.Start();
            foreach (var plan in plans)
            {
                var (ids, skipped, rollback, wasNative, lost) = CurveMemberSplit.SplitAtCuts(doc, plan, options);
                paramSkipped += skipped;
                if (rollback)
                {
                    skippedFailed++;
                    continue;
                }

                if (ids.Count > 1)
                {
                    split++;
                    piecesMade += ids.Count;
                    created.AddRange(ids);
                    if (wasNative)
                    {
                        native++;
                    }
                    else
                    {
                        recreated++;
                        dependents += lost;
                    }
                }
                else
                {
                    skippedUnchanged++;
                }
            }

            tx.Commit();
        }

        var message = $"Split {split} beams into {piecesMade} pieces."
            + SkipNote(skippedNoCurve, skippedNoCrossing, skippedFailed)
            + SplitReport.Progress(batch)
            + ParamNote(options, native, recreated, paramSkipped);
        if (recreated > 0)
        {
            message += $" Native split: {native} members, re-created: {recreated}.";
            if (dependents > 0)
                message += $" Dependent elements to re-check: {dependents}.";
        }
        if (skippedUnchanged > 0)
            message += $" Left alone: {skippedUnchanged}.";
        if (created.Count > 0)
            message += " Created: " + SplitReport.Ids(created.Select(SplitElements.Fmt)) + ".";

        SplitElements.Select(uidoc, created);
        return message;
    }

    /// <summary>
    /// Fractions along the beam where its centreline enters and leaves a column footprint. A column
    /// counts only when it overlaps the beam in plan and in height, so a beam that merely passes near a
    /// column is left alone; a column on the centreline that the beam only clips never becomes a cut.
    /// </summary>
    private static List<double> CutFractions(
        Element beam,
        Curve curve,
        IReadOnlyList<Element> columns,
        Dictionary<string, List<List<Point2>>> footprints)
    {
        var cuts = new List<double>();
        var plane = GeometryRevit.HorizontalPlane(curve.GetEndPoint(0));
        var centreline = GeometryRevit.ProjectAll(plane, GeometryRevit.Tessellate(curve));
        if (centreline.Count < 2)
            return cuts;

        foreach (var column in columns)
        {
            if (ReferenceEquals(column, beam) || !GeometryRevit.Reaches(beam, column))
                continue;

            // Only the outer outline: a hollow column is solid ground for the beam that lands on it, so
            // its inner ring would otherwise read as two phantom cuts through the middle of the footprint.
            var ring = Polygon2D.Largest(Footprint(column, plane, footprints));
            if (ring == null)
                continue;

            for (var i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                for (var s = 0; s < centreline.Count - 1; s++)
                {
                    if (!Polygon2D.TryCross(centreline[s], centreline[s + 1], a, b, out var hit))
                        continue;

                    var fraction = Polygon2D.ParameterAlong(centreline, hit);
                    if (fraction <= MinFraction || fraction >= 1 - MinFraction)
                        continue;

                    cuts.Add(fraction);
                }
            }
        }

        return CurveMemberSplit.DistinctCuts(cuts);
    }

    /// <summary>
    /// Column outlines are the same shape up and down a prismatic column, so the extraction is cached per
    /// column and per height. Without this a big model re-reads every column's geometry for every member.
    /// </summary>
    private static List<List<Point2>> Footprint(
        Element column,
        Plane2D plane,
        Dictionary<string, List<List<Point2>>> footprints)
    {
        var key = column.Id.Value.ToString(CultureInfo.InvariantCulture) + ":"
            + plane.Origin.Z.ToString("R", CultureInfo.InvariantCulture);
        if (footprints.TryGetValue(key, out var cached))
            return cached;

        var rings = GeometryRevit.HorizontalRings(column, plane);
        footprints[key] = rings;
        return rings;
    }

    private static Curve? GetCurve(Element beam)
        => SplitElements.LocationCurveOf(beam);

    private static string ParamNote(SplitOptions options, int native, int recreated, int paramSkipped)
        => CurveMemberSplit.ParamNote(options, native, recreated, paramSkipped);

    private static string SkipNote(int noCurve, int noCrossing, int failed)
        => Skips.Note(noCurve, noCrossing, failed);

    private static string EmptyHostMessage(SplitOptions options)
        => string.Equals(options.Scope, "selection", StringComparison.OrdinalIgnoreCase)
            ? "Please select elements first."
            : "No beams found.";

    /// <summary>
    /// The reasons members were left alone, each one named rather than folded into a single count, so a
    /// run says what it could not do and why.
    /// </summary>
    private static class Skips
    {
        public static string Note(int noCurve, int noCrossing, int failed)
        {
            var parts = new List<string>();
            SplitElements.AddSkip(parts, noCurve, "no curve");
            SplitElements.AddSkip(parts, noCrossing, "no column crossing");
            SplitElements.AddSkip(parts, failed, "split failed");
            return parts.Count > 0 ? $" Skipped: {string.Join(", ", parts)}." : "";
        }
    }

}
