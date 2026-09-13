using System.Globalization;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

using LoadingAI.Geometry;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.StructureCuts;

/// <summary>
/// Splits structural framing members where they pass through structural columns. Cutting where the
/// centreline crosses the column's real plan outline leaves a member whole when a column only clips its
/// end, and a curved member is cut along its own curve instead of across a single chord.
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

        var selection = GetSelection(uidoc);
        var beams = Resolve(doc, beamIds, IsBeam);
        if (beams.Count == 0)
            beams = selection.Where(IsBeam).ToList();
        if (beams.Count == 0)
            return "Please select elements first.";

        var columns = Resolve(doc, columnIds, IsColumn);
        if (columns.Count == 0)
            columns = selection.Where(IsColumn).ToList();
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

        using (var tx = new Transaction(doc, "LoadingAI for Revit: split beams by columns"))
        {
            tx.Start();
            foreach (var plan in plans)
            {
                var (ids, skipped, rollback) = SplitAtCuts(doc, plan, options);
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
                }
                else
                {
                    skippedUnchanged++;
                }
            }

            tx.Commit();
        }

        var message = $"Split {split} beams into {piecesMade} pieces."
            + SkipNote(skippedNoCurve, skippedNoCrossing, skippedFailed, stopped, options);
        if (options.CopyParams)
            message += paramSkipped > 0
                ? $" Param copy skipped: {paramSkipped}."
                : " Instance parameters copied to new pieces.";
        else
            message += " Copy params: off.";
        if (skippedUnchanged > 0)
            message += $" Left alone: {skippedUnchanged}.";
        if (created.Count > 0)
            message += " Created: " + SplitReport.Ids(created.Select(Fmt)) + ".";

        Select(uidoc, created);
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
    /// Rebuilds a member as one piece per cut interval. The first piece reuses the original element so
    /// hosts and connections that reference it stay valid; later pieces are new instances of the same
    /// type on the same level. If a piece cannot be built the original's curve is put back and the member
    /// is reported as failed rather than left silently shortened.
    /// </summary>
    private static (List<ElementId> Ids, int ParamSkipped, bool Rollback) SplitAtCuts(
        Document doc,
        Plan plan,
        SplitOptions options)
    {
        var created = new List<ElementId>();
        var paramSkipped = 0;

        if (plan.Beam is not FamilyInstance instance)
            return (created, paramSkipped, true);

        var symbol = doc.GetElement(plan.Beam.GetTypeId()) as FamilySymbol;
        if (symbol == null)
            return (created, paramSkipped, true);

        var levelId = plan.Beam.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId();
        if (levelId == null || levelId == ElementId.InvalidElementId)
            levelId = plan.Beam.LevelId;
        if (doc.GetElement(levelId) is not Level level)
            return (created, paramSkipped, true);

        var bounds = new List<double> { 0.0 };
        bounds.AddRange(plan.Cuts);
        bounds.Add(1.0);

        if (!symbol.IsActive)
            symbol.Activate();

        var mark = options.UniqueMark ? ParamCopy.Mark(plan.Beam) : null;
        var snapshot = options.CopyParams ? ParamCopy.Take(plan.Beam) : null;
        var fresh = new List<ElementId>();

        for (var i = 0; i < bounds.Count - 1; i++)
        {
            var piece = GeometryRevit.SubCurve(plan.Curve, bounds[i], bounds[i + 1]);
            if (piece == null)
                break;

            if (i == 0)
            {
                if (!RewriteLocation(plan.Beam, piece))
                {
                    Rollback(doc, plan, fresh);
                    return (new List<ElementId>(), paramSkipped, true);
                }

                created.Add(plan.Beam.Id);
                continue;
            }

            var made = CreatePiece(doc, piece, symbol, level, instance.StructuralType);
            if (made == null)
            {
                Rollback(doc, plan, fresh);
                return (new List<ElementId>(), paramSkipped, true);
            }

            fresh.Add(made.Id);

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
            Rollback(doc, plan, fresh);
            return (new List<ElementId>(), paramSkipped, true);
        }

        return (created, paramSkipped, false);
    }

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

    /// <summary>
    /// Undoes a member that could not be split cleanly: the pieces it produced are removed and its own
    /// curve goes back, so a failed member is left exactly as it was rather than quietly shortened.
    /// </summary>
    private static void Rollback(Document doc, Plan plan, IReadOnlyList<ElementId> pieces)
    {
        foreach (var id in pieces)
        {
            try
            {
                doc.Delete(id);
            }
            catch
            {
                // A piece that will not delete is still reported as part of the failed member.
            }
        }

        if (plan.Beam.Location is LocationCurve location)
        {
            try
            {
                location.Curve = plan.Curve;
            }
            catch
            {
                // Leave it as it is; the run reports the member as failed either way.
            }
        }
    }

    private static Curve? GetCurve(Element beam)
        => beam.Location is LocationCurve location ? location.Curve : null;

    private static string SkipNote(int noCurve, int noCrossing, int failed, bool stopped, SplitOptions options)
    {
        var note = $" Skipped: {noCurve} no curve, {noCrossing} no column crossing";
        if (failed > 0)
            note += $", {failed} failed";
        note += ".";
        if (stopped)
            note += $" Stopped at {options.Limit}.";
        return note;
    }

    private static string Fmt(ElementId id)
        => id.Value.ToString(CultureInfo.InvariantCulture);

    private static void Select(UIDocument uidoc, IReadOnlyList<ElementId> created)
    {
        if (created.Count == 0)
            return;

        try
        {
            uidoc.Selection.SetElementIds(created.ToList());
            uidoc.ShowElements(created[0]);
        }
        catch
        {
            // Selecting is a convenience; it must never fail the run.
        }
    }

    private static List<Element> Resolve(Document doc, IReadOnlyList<string> ids, Func<Element, bool> wanted)
    {
        var list = new List<Element>();
        foreach (var s in ids)
        {
            Element? el;
            try
            {
                el = doc.GetElement(RevitParse.ParseEid(s));
            }
            catch
            {
                continue;
            }

            if (el != null && wanted(el))
                list.Add(el);
        }

        return list;
    }

    private static List<Element> GetSelection(UIDocument uidoc)
        => uidoc.Selection.GetElementIds()
            .Select(id => uidoc.Document.GetElement(id))
            .Where(el => el != null)
            .Cast<Element>()
            .ToList();

    private static bool IsBeam(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFraming;

    private static bool IsColumn(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns;

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
