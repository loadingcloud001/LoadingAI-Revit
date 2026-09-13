using System.Globalization;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

using LoadingAI.Geometry;

namespace LoadingAI.Plugins.StructureCuts;

/// <summary>
/// Splits structural framing members where they pass through structural columns. Cutting where the
/// centreline crosses the column's real plan outline leaves a member whole when a column only clips its
/// end, and a curved member is cut along its own curve instead of across a single chord.
/// <para>
/// Members Revit will split itself - line and arc beams - are split in place, so the original element,
/// its host, its joins and its parameters all survive. A member Revit will not split, or one whose split
/// does not land where it was asked to, is re-created piece by piece as before. Each member runs in its
/// own sub-transaction, so a member that fails either way is put back exactly as it was instead of being
/// left silently shortened.
/// </para>
/// </summary>
internal static class BeamSplit
{
    /// <summary>A cut this close to an end is not worth a separate piece.</summary>
    private const double MinFraction = 0.02;

    /// <summary>Two cuts closer together than this make a sliver, so the earlier one wins.</summary>
    private const double MinGapFraction = 0.03;

    public static string Run(UIApplication uiApp, IReadOnlyList<string> beamIds, IReadOnlyList<string> columnIds, SplitOptions options)
    {
        var uidoc = uiApp.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (uidoc == null || doc == null)
            return "Please open a model first.";

        var selection = SplitElements.GetSelection(uidoc);
        var beams = SplitElements.Resolve(doc, beamIds, SplitElements.IsBeam);
        if (beams.Count == 0)
            beams = selection.Where(SplitElements.IsBeam).ToList();
        if (beams.Count == 0)
            return "Please select elements first.";

        var columns = SplitElements.Resolve(doc, columnIds, SplitElements.IsColumn);
        if (columns.Count == 0)
            columns = selection.Where(SplitElements.IsColumn).ToList();
        if (columns.Count == 0)
            return "No columns found.";

        var stopped = beams.Count > options.Limit;
        if (stopped)
            beams = beams.Take(options.Limit).ToList();

        var footprints = new Dictionary<string, List<List<Point2>>>(StringComparer.Ordinal);
        var plans = new List<Plan>();
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

            plans.Add(new Plan(beam, curve, cuts));
        }

        if (options.DryRun)
        {
            var pieces = plans.Sum(plan => plan.Cuts.Count + 1);
            return "Dry-run. " + $"Would split {plans.Count} beams into {pieces} pieces. No changes made."
                + SkipNote(skippedNoCurve, skippedNoCrossing, 0, stopped, options);
        }

        if (plans.Count == 0)
            return "Nothing to split." + SkipNote(skippedNoCurve, skippedNoCrossing, 0, stopped, options);

        var created = new List<ElementId>();
        var split = 0;
        var piecesMade = 0;
        var skippedUnchanged = 0;
        var skippedFailed = 0;
        var paramSkipped = 0;
        var native = 0;
        var recreated = 0;
        var dependents = 0;

        using (var tx = new Transaction(doc, "LoadingAI for Revit: split beams by columns"))
        {
            tx.Start();
            foreach (var plan in plans)
            {
                var (ids, skipped, rollback, wasNative, lost) = SplitAtCuts(doc, plan, options);
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
            + SkipNote(skippedNoCurve, skippedNoCrossing, skippedFailed, stopped, options)
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

        cuts.Sort();
        var filtered = new List<double>();
        foreach (var cut in cuts)
            if (filtered.Count == 0 || cut - filtered[filtered.Count - 1] > MinGapFraction)
                filtered.Add(cut);

        return filtered;
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

    /// <summary>
    /// Splits one member into the pieces its cuts ask for. The native split runs first because it keeps
    /// the member itself - its host, its joins and its parameters all survive - and re-creation is the
    /// fallback for a member Revit will not split or one whose split did not land where it was asked to.
    /// Each path uses its own sub-transaction, so a member that fails is rolled back whole and the outer
    /// transaction only ever commits members that came out right.
    /// </summary>
    private static (List<ElementId> Ids, int ParamSkipped, bool Rollback, bool Native, int Dependent) SplitAtCuts(
        Document doc,
        Plan plan,
        SplitOptions options)
    {
        if (TryNativeSplit(doc, plan, options, out var ids))
            return (ids, 0, false, true, 0);

        var (made, paramSkipped, dependent, ok) = Recreate(doc, plan, options);
        return ok
            ? (made, paramSkipped, false, false, dependent)
            : (new List<ElementId>(), paramSkipped, true, false, 0);
    }

    /// <summary>
    /// Splits a member with Revit's own split, which keeps the element itself and everything the model
    /// already hangs off it. Revit takes one cut per call, so the cuts go in highest first and each
    /// remaining cut is converted onto the curve of whichever piece still owns the low end. Every cut is
    /// read back after it is made, so a split that does not land where it was asked to is rolled back
    /// rather than trusted.
    /// </summary>
    private static bool TryNativeSplit(Document doc, Plan plan, SplitOptions options, out List<ElementId> ids)
    {
        ids = new List<ElementId>();
        if (plan.Beam is not FamilyInstance instance || !instance.CanSplit)
            return false;

        var steps = SplitSequence.Build(plan.Cuts, MinGapFraction);
        if (steps.Count == 0)
            return false;

        var mark = options.UniqueMark ? ParamCopy.Mark(plan.Beam) : null;
        var high = new List<ElementId>();
        var low = plan.Beam.Id;

        using (var sub = new SubTransaction(doc))
        {
            sub.Start();
            try
            {
                foreach (var step in steps)
                {
                    if (doc.GetElement(low) is not FamilyInstance current
                        || !current.CanSplit
                        || current.Location is not LocationCurve location
                        || location.Curve == null)
                    {
                        sub.RollBack();
                        return false;
                    }

                    var live = location.Curve;
                    var span = GeometryRevit.Span(live, out var start);
                    if (Math.Abs(span) <= 1e-12)
                    {
                        sub.RollBack();
                        return false;
                    }

                    var target = GeometryRevit.At(plan.Curve, step.Cut);
                    var local = (live.Project(target).Parameter - start) / span;
                    if (local <= 0.0 || local >= 1.0)
                    {
                        sub.RollBack();
                        return false;
                    }

                    var made = current.Split(local);
                    if (made == null || made == ElementId.InvalidElementId)
                    {
                        sub.RollBack();
                        return false;
                    }

                    // Revit leaves the low end on one of the two pieces and the API does not say which,
                    // so the one that still starts where this piece started is the one the cuts below
                    // this one have to be measured against. Anything else means the member did not come
                    // apart the way it was asked to.
                    if (KeepsStart(current, live, target))
                    {
                        high.Add(made);
                    }
                    else if (doc.GetElement(made) is FamilyInstance other && KeepsStart(other, live, target))
                    {
                        high.Add(low);
                        low = made;
                    }
                    else
                    {
                        sub.RollBack();
                        return false;
                    }
                }
            }
            catch
            {
                try
                {
                    sub.RollBack();
                }
                catch
                {
                    // Nothing left to put back.
                }

                ids = new List<ElementId>();
                return false;
            }

            sub.Commit();
        }

        // The piece holding the low end leads and the pieces gathered along the way run back up the
        // member, because every cut was taken from the top down.
        ids.Add(low);
        for (var i = high.Count - 1; i >= 0; i--)
            ids.Add(high[i]);

        if (ids.Count < 2)
        {
            ids = new List<ElementId>();
            return false;
        }

        for (var i = 1; mark != null && i < ids.Count; i++)
        {
            if (doc.GetElement(ids[i]) is Element piece)
                ParamCopy.SetMark(piece, SplitMarks.For(mark, i));
        }

        return true;
    }

    /// <summary>
    /// Re-creates a member as one piece per cut interval. The first piece reuses the original element so
    /// hosts and connections that reference it stay valid; later pieces are new instances of the same
    /// type on the same level. If a piece cannot be built the member is rolled back rather than left
    /// silently shortened, and the elements the original carried are counted before it is cut about.
    /// </summary>
    private static (List<ElementId> Ids, int ParamSkipped, int Dependent, bool Ok) Recreate(
        Document doc,
        Plan plan,
        SplitOptions options)
    {
        var empty = new List<ElementId>();

        if (plan.Beam is not FamilyInstance instance)
            return (empty, 0, 0, false);

        var symbol = doc.GetElement(plan.Beam.GetTypeId()) as FamilySymbol;
        if (symbol == null)
            return (empty, 0, 0, false);

        var levelId = plan.Beam.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId();
        if (levelId == null || levelId == ElementId.InvalidElementId)
            levelId = plan.Beam.LevelId;
        if (doc.GetElement(levelId) is not Level level)
            return (empty, 0, 0, false);

        var bounds = SplitSequence.Bounds(plan.Cuts, MinGapFraction);
        if (bounds.Count < 2)
            return (empty, 0, 0, false);

        var mark = options.UniqueMark ? ParamCopy.Mark(plan.Beam) : null;
        var snapshot = options.CopyParams ? ParamCopy.Take(plan.Beam) : null;
        var created = new List<ElementId>();
        var paramSkipped = 0;

        using (var sub = new SubTransaction(doc))
        {
            sub.Start();
            try
            {
                if (!symbol.IsActive)
                    symbol.Activate();

                for (var i = 0; i < bounds.Count - 1; i++)
                {
                    var piece = GeometryRevit.SubCurve(plan.Curve, bounds[i], bounds[i + 1]);
                    if (piece == null)
                    {
                        sub.RollBack();
                        return (empty, paramSkipped, 0, false);
                    }

                    if (i == 0)
                    {
                        if (!RewriteLocation(plan.Beam, piece))
                        {
                            sub.RollBack();
                            return (empty, paramSkipped, 0, false);
                        }

                        created.Add(plan.Beam.Id);
                        continue;
                    }

                    var made = CreatePiece(doc, piece, symbol, level, instance.StructuralType);
                    if (made == null)
                    {
                        sub.RollBack();
                        return (empty, paramSkipped, 0, false);
                    }

                    var markFor = options.UniqueMark ? SplitMarks.For(mark, i) : null;
                    if (snapshot != null)
                        paramSkipped += ParamCopy.Apply(snapshot, made, markFor);
                    else
                        ParamCopy.SetMark(made, markFor);

                    created.Add(made.Id);
                }

                // One piece alone means the member was rewritten without gaining a cut, so undo it.
                if (created.Count < 2)
                {
                    sub.RollBack();
                    return (empty, paramSkipped, 0, false);
                }

                var dependent = SplitElements.Dependent(plan.Beam);
                sub.Commit();
                return (created, paramSkipped, dependent, true);
            }
            catch
            {
                // A throw part way through is a member the caller reports as failed, so the pieces made
                // so far go with it and the member is left as it was.
                try
                {
                    sub.RollBack();
                }
                catch
                {
                    // Nothing left to put back.
                }

                return (empty, paramSkipped, 0, false);
            }
        }
    }

    /// <summary>
    /// True while a piece still starts where the piece it came from started and ends at the cut, which is
    /// what the piece that owns the low end does. Only that piece can carry the cuts below it.
    /// </summary>
    private static bool KeepsStart(FamilyInstance piece, Curve before, XYZ target)
    {
        if (piece.Location is not LocationCurve location)
            return false;

        var after = location.Curve;
        if (after == null)
            return false;

        return SamePoint(after.GetEndPoint(0), before.GetEndPoint(0))
            && SamePoint(after.GetEndPoint(1), target);
    }

    private static bool SamePoint(XYZ a, XYZ b)
        => a != null && b != null && a.DistanceTo(b) <= GeometryRevit.Tol;

    private static bool RewriteLocation(Element beam, Curve piece)
    {
        try
        {
            if (beam.Location is LocationCurve location)
            {
                location.Curve = piece;
                return true;
            }
        }
        catch
        {
            // A family that refuses a curve still accepts the straight chord below.
        }

        try
        {
            if (beam.Location is LocationCurve fallback)
            {
                fallback.Curve = Line.CreateBound(piece.GetEndPoint(0), piece.GetEndPoint(1));
                return true;
            }
        }
        catch
        {
            // Nothing else to try; the caller rolls this member back.
        }

        return false;
    }

    private static Element? CreatePiece(Document doc, Curve piece, FamilySymbol symbol, Level level, StructuralType structuralType)
    {
        try
        {
            var fresh = doc.Create.NewFamilyInstance(piece, symbol, level, structuralType);
            if (fresh != null)
                return fresh;
        }
        catch
        {
            // A curve driven family may reject a curved piece; the chord below still cuts the member.
        }

        try
        {
            return doc.Create.NewFamilyInstance(Line.CreateBound(piece.GetEndPoint(0), piece.GetEndPoint(1)), symbol, level, structuralType);
        }
        catch
        {
            return null;
        }
    }

    private static Curve? GetCurve(Element beam)
        => beam.Location is LocationCurve location ? location.Curve : null;

    /// <summary>
    /// How the pieces got their parameters. A native split reuses the members themselves, so nothing had
    /// to be copied across; the re-creation fallback copies the original's parameters onto the pieces it
    /// makes, which is the only path where a parameter can be left behind.
    /// </summary>
    private static string ParamNote(SplitOptions options, int native, int recreated, int paramSkipped)
    {
        if (!options.CopyParams)
            return " Copy params: off.";

        if (recreated == 0)
            return native > 0 ? " Native split preserved parameters." : "";

        return paramSkipped > 0
            ? $" Instance parameters copied to new pieces, {paramSkipped} skipped."
            : " Instance parameters copied to new pieces.";
    }

    private static string SkipNote(int noCurve, int noCrossing, int failed, bool stopped, SplitOptions options)
        => Skips.Note(noCurve, noCrossing, failed, stopped, options.Limit);

    /// <summary>
    /// The reasons members were left alone, each one named rather than folded into a single count, so a
    /// run says what it could not do and why.
    /// </summary>
    private static class Skips
    {
        public static string Note(int noCurve, int noCrossing, int failed, bool stopped, int limit)
        {
            var parts = new List<string>();
            SplitElements.AddSkip(parts, noCurve, "no curve");
            SplitElements.AddSkip(parts, noCrossing, "no column crossing");
            SplitElements.AddSkip(parts, failed, "split failed");

            var note = parts.Count > 0 ? $" Skipped: {string.Join(", ", parts)}." : "";
            if (stopped)
                note += $" Stopped at {limit}.";
            return note;
        }
    }

    private sealed class Plan
    {
        public Plan(Element beam, Curve curve, List<double> cuts)
        {
            Beam = beam;
            Curve = curve;
            Cuts = cuts;
        }

        public Element Beam { get; }

        public Curve Curve { get; }

        public List<double> Cuts { get; }
    }
}
