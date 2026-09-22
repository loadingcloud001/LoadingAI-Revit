using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

using LoadingAI.Geometry;

namespace LoadingAI.Revit.StructureCuts;

/// <summary>
/// Splits a strictly shallower structural beam at a same-height pass-through of a
/// strictly deeper main. Not cope/notch. Depth ranking is the type's nominal section
/// height only; occupancy is mid-depth-Z inside the main's solid Z; the two outer
/// bands are recreated and the middle stub inside the main is dropped.
/// <para>
/// Scope is <see cref="SplitScope.ByScope"/> (or explicit ids). Empty scope is
/// refused. Never the all-in-model cutter fallback, never RecreateInstance's
/// every-band emission, never BeamSplit.CutFractions / column HorizontalPlane.
/// </para>
/// </summary>
internal static class BeamCut
{
    public static string Run(
        UIApplication uiApp,
        IReadOnlyList<string> hostIds,
        IReadOnlyList<string> cutterIds,
        SplitOptions options,
        SplitOutcome? outcome = null)
    {
        var uidoc = uiApp.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (uidoc == null || doc == null)
            return "Please open a model first.";

        var batch = SplitScope.Collect(doc, uidoc, SplitElements.IsBeam, options, hostIds);
        var beams = batch.Items;
        if (beams.Count == 0 && batch.Total == 0)
            return EmptyHostMessage(options);

        var mains = cutterIds.Count > 0
            ? SplitElements.Resolve(doc, cutterIds, SplitElements.IsBeam)
            : beams;
        var links = SplitScope.LinkCutters(doc, options, SplitElements.IsBeam);
        if (mains.Count == 0 && links.Cutters.Count == 0)
            return "No main beams found." + links.Report();

        var depths = new Dictionary<ElementId, double?>();
        var typeNames = new Dictionary<ElementId, string>();
        var zCache = new Dictionary<ElementId, (double Bot, double Top)?>();
        var ringCache = new Dictionary<ElementId, List<Point2>?>();
        var planned = new HashSet<ElementId>();
        var plans = new List<Plan>();
        var refusedTypes = new SortedSet<string>(StringComparer.Ordinal);
        var skippedNoCurve = 0;
        var skippedNoPair = 0;

        foreach (var secondary in beams)
        {
            if (planned.Contains(secondary.Id))
                continue;

            var curve = SplitElements.LocationCurveOf(secondary);
            if (curve == null)
            {
                skippedNoCurve++;
                continue;
            }

            if (secondary is not FamilyInstance)
            {
                skippedNoPair++;
                continue;
            }

            var paired = false;
            foreach (var main in mains)
            {
                if (main.Id == secondary.Id)
                    continue;

                var order = SecondaryByMain.DecideOrder(
                    TypeDepth(secondary, depths, typeNames),
                    TypeName(secondary, typeNames),
                    TypeDepth(main, depths, typeNames),
                    TypeName(main, typeNames));

                if (order.Refused)
                {
                    if (!string.IsNullOrEmpty(order.RefusedTypeName))
                        refusedTypes.Add(order.RefusedTypeName);
                    continue;
                }

                if (!order.BIsMain)
                    continue;

                var targetDepth = TypeDepth(secondary, depths, typeNames);
                if (targetDepth is not > 0)
                    continue;

                if (!TryCrossing(secondary, curve, main, targetDepth.Value, zCache, ringCache, out var decision))
                    continue;

                plans.Add(new Plan(secondary, curve, decision));
                planned.Add(secondary.Id);
                paired = true;
                break;
            }

            if (!paired)
            {
                foreach (var main in links.Cutters)
                {
                    var order = SecondaryByMain.DecideOrder(
                        TypeDepth(secondary, depths, typeNames),
                        TypeName(secondary, typeNames),
                        main.TypeDepth,
                        main.TypeName);

                    if (order.Refused)
                    {
                        if (!string.IsNullOrEmpty(order.RefusedTypeName))
                            refusedTypes.Add(order.RefusedTypeName);
                        continue;
                    }

                    if (!order.BIsMain)
                        continue;

                    var targetDepth = TypeDepth(secondary, depths, typeNames);
                    if (targetDepth is not > 0)
                        continue;

                    if (!TryCrossing(secondary, curve, main, targetDepth.Value, out var decision))
                        continue;

                    plans.Add(new Plan(secondary, curve, decision));
                    planned.Add(secondary.Id);
                    paired = true;
                    break;
                }
            }

            if (!paired && !planned.Contains(secondary.Id))
                skippedNoPair++;
        }

        var refuseNote = refusedTypes.Count > 0
            ? " Refused unreadable depth: " + string.Join(", ", refusedTypes) + "."
            : "";

        if (options.DryRun)
        {
            var pieces = plans.Count * 2;
            return "Dry-run. " + $"Would split {plans.Count} secondary beams into {pieces} pieces. No changes made."
                + SkipNote(skippedNoCurve, skippedNoPair, 0)
                + refuseNote
                + SplitReport.Progress(batch)
                + links.Report();
        }

        if (plans.Count == 0)
            return "Nothing to split."
                + SkipNote(skippedNoCurve, skippedNoPair, 0)
                + refuseNote
                + SplitReport.Progress(batch)
                + links.Report();

        var created = new List<ElementId>();
        var split = 0;
        var piecesMade = 0;
        var skippedFailed = 0;
        var paramSkipped = 0;
        var dependents = 0;

        using (var tx = SplitElements.WriteTx(doc, "LoadingAI for Revit: split secondary beams by main beams"))
        {
            tx.Start();
            foreach (var plan in plans)
            {
                var (ids, skipped, lost, failed) = RecreateOuter(doc, plan, options);
                paramSkipped += skipped;
                if (failed)
                {
                    skippedFailed++;
                    continue;
                }

                split++;
                piecesMade += ids.Count;
                created.AddRange(ids);
                dependents += lost;
            }

            tx.Commit();
        }

        var message = $"Split {split} secondary beams into {piecesMade} pieces."
            + SkipNote(skippedNoCurve, skippedNoPair, skippedFailed)
            + refuseNote
            + SplitReport.Progress(batch)
            + CurveMemberSplit.ParamNote(options, 0, split, paramSkipped);
        if (dependents > 0)
            message += $" Dependent elements to re-check: {dependents}.";
        if (created.Count > 0)
            message += " Created: " + SplitReport.Ids(created.Select(SplitElements.Fmt)) + ".";
        message += links.Report();

        outcome?.Record(created, 0, skippedNoCurve + skippedNoPair + skippedFailed);

        SplitElements.Select(uidoc, created);
        return message;
    }

    /// <summary>
    /// Plan-clip the secondary into the main, require mid-depth occupancy, and keep
    /// only the two outer bands. One-sided framing is skipped, not stubbed.
    /// </summary>
    private static bool TryCrossing(
        Element secondary,
        Curve curve,
        Element main,
        double targetDepth,
        Dictionary<ElementId, (double Bot, double Top)?> zCache,
        Dictionary<ElementId, List<Point2>?> ringCache,
        out CrossingDecision decision)
    {
        decision = CrossingDecision.Skip("no crossing");

        if (!TrySolidZ(main, zCache, out var cutterBot, out var cutterTop))
            return false;

        var plane = GeometryRevit.HorizontalPlane(curve.GetEndPoint(0));
        var centreline = GeometryRevit.ProjectAll(plane, GeometryRevit.Tessellate(curve));
        if (centreline.Count < 2)
            return false;

        var ring = Ring(main, plane, ringCache);
        if (ring == null || ring.Count < 3)
            return false;

        var clips = Polygon2D.ClipPolylineToPolygon(ring, centreline);
        foreach (var clip in clips)
        {
            if (clip.Count < 2)
                continue;

            var enter = Polygon2D.ParameterAlong(centreline, clip[0]);
            var leave = Polygon2D.ParameterAlong(centreline, clip[clip.Count - 1]);
            var startInside = Polygon2D.PointInPolygon(centreline[0], ring);
            var endInside = Polygon2D.PointInPolygon(centreline[centreline.Count - 1], ring);
            var run = new CrossingRun(enter, leave, oneSided: startInside || endInside);
            var candidate = SecondaryByMain.Crossing(run);
            if (!candidate.Keep)
                continue;

            var mid = GeometryRevit.At(curve, (candidate.Enter + candidate.Leave) / 2.0);
            if (!SecondaryByMain.Occupies(cutterBot, cutterTop, mid.Z, targetDepth))
                continue;

            decision = candidate;
            return true;
        }

        return false;
    }

    private static bool TryCrossing(
        Element secondary,
        Curve curve,
        SplitElements.LinkedCutter main,
        double targetDepth,
        out CrossingDecision decision)
    {
        decision = CrossingDecision.Skip("no crossing");
        if (main.Curve == null)
            return false;

        var range = main.ZRange();
        if (range == null)
            return false;

        var plane = GeometryRevit.HorizontalPlane(curve.GetEndPoint(0));
        var centreline = GeometryRevit.ProjectAll(plane, GeometryRevit.Tessellate(curve));
        if (centreline.Count < 2)
            return false;

        var ring = Polygon2D.Largest(main.Rings(plane));
        if (ring == null || ring.Count < 3)
            return false;

        var clips = Polygon2D.ClipPolylineToPolygon(ring, centreline);
        foreach (var clip in clips)
        {
            if (clip.Count < 2)
                continue;

            var enter = Polygon2D.ParameterAlong(centreline, clip[0]);
            var leave = Polygon2D.ParameterAlong(centreline, clip[clip.Count - 1]);
            var startInside = Polygon2D.PointInPolygon(centreline[0], ring);
            var endInside = Polygon2D.PointInPolygon(centreline[centreline.Count - 1], ring);
            var run = new CrossingRun(enter, leave, oneSided: startInside || endInside);
            var candidate = SecondaryByMain.Crossing(run);
            if (!candidate.Keep)
                continue;

            var mid = GeometryRevit.At(curve, (candidate.Enter + candidate.Leave) / 2.0);
            if (!SecondaryByMain.Occupies(range.Value.Bot, range.Value.Top, mid.Z, targetDepth))
                continue;

            decision = candidate;
            return true;
        }

        return false;
    }

    private static (List<ElementId> Ids, int ParamSkipped, int Dependent, bool Failed) RecreateOuter(
        Document doc,
        Plan plan,
        SplitOptions options)
    {
        var empty = new List<ElementId>();
        if (plan.Host is not FamilyInstance instance)
            return (empty, 0, 0, true);

        var symbol = doc.GetElement(plan.Host.GetTypeId()) as FamilySymbol;
        if (symbol == null)
            return (empty, 0, 0, true);

        var levelId = plan.Host.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId();
        if (levelId == null || levelId == ElementId.InvalidElementId)
            levelId = plan.Host.LevelId;
        if (doc.GetElement(levelId) is not Level level)
            return (empty, 0, 0, true);

        var bands = plan.Decision.OuterBands;
        if (bands.Count != 2)
            return (empty, 0, 0, true);

        var mark = options.UniqueMark ? ParamCopy.Mark(plan.Host) : null;
        var snapshot = options.CopyParams ? ParamCopy.Take(plan.Host) : null;
        var created = new List<ElementId>();
        var paramSkipped = 0;
        var dependent = SplitElements.Dependent(plan.Host);

        using (var sub = new SubTransaction(doc))
        {
            sub.Start();
            try
            {
                if (!symbol.IsActive)
                    symbol.Activate();

                for (var i = 0; i < bands.Count; i++)
                {
                    var piece = GeometryRevit.SubCurve(plan.Curve, bands[i].From, bands[i].To);
                    if (piece == null)
                    {
                        sub.RollBack();
                        return (empty, paramSkipped, 0, true);
                    }

                    var made = CreatePiece(doc, piece, symbol, level, instance.StructuralType);
                    if (made == null)
                    {
                        sub.RollBack();
                        return (empty, paramSkipped, 0, true);
                    }

                    var markFor = options.UniqueMark ? SplitMarks.For(mark, i) : null;
                    if (snapshot != null)
                        paramSkipped += ParamCopy.Apply(snapshot, made, markFor);
                    else
                        ParamCopy.SetMark(made, markFor);

                    created.Add(made.Id);
                }

                if (created.Count != 2)
                {
                    sub.RollBack();
                    return (empty, paramSkipped, 0, true);
                }

                doc.Delete(plan.Host.Id);
                sub.Commit();
                return (created, paramSkipped, dependent, false);
            }
            catch
            {
                try { sub.RollBack(); } catch { }
                return (empty, paramSkipped, 0, true);
            }
        }
    }

    private static Element? CreatePiece(
        Document doc,
        Curve piece,
        FamilySymbol symbol,
        Level level,
        StructuralType structuralType)
    {
        try
        {
            var fresh = doc.Create.NewFamilyInstance(piece, symbol, level, structuralType);
            if (fresh != null)
                return fresh;
        }
        catch { }

        try
        {
            return doc.Create.NewFamilyInstance(
                Line.CreateBound(piece.GetEndPoint(0), piece.GetEndPoint(1)),
                symbol,
                level,
                structuralType);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Type nominal section height: <c>STRUCTURAL_SECTION_COMMON_HEIGHT</c>, then
    /// <c>"h"</c>. Neither is unreadable — never bbox, never HalfWidth, never 100 mm.
    /// </summary>
    private static double? TypeDepth(
        Element beam,
        Dictionary<ElementId, double?> cache,
        Dictionary<ElementId, string> names)
    {
        if (cache.TryGetValue(beam.Id, out var cached))
            return cached;

        TypeName(beam, names);
        double? depth = null;
        try
        {
            var beamType = beam.Document?.GetElement(beam.GetTypeId());
            if (beamType != null)
            {
                Parameter? height = null;
                try
                {
                    height = beamType.get_Parameter(BuiltInParameter.STRUCTURAL_SECTION_COMMON_HEIGHT);
                }
                catch
                {
                    height = null;
                }

                if (height == null || !height.HasValue)
                    height = beamType.LookupParameter("h");

                if (height != null
                    && height.HasValue
                    && height.StorageType == StorageType.Double)
                {
                    var value = height.AsDouble();
                    if (value > 0 && !double.IsNaN(value) && !double.IsInfinity(value))
                        depth = value;
                }
            }
        }
        catch
        {
            depth = null;
        }

        cache[beam.Id] = depth;
        return depth;
    }

    private static string TypeName(Element beam, Dictionary<ElementId, string> names)
    {
        if (names.TryGetValue(beam.Id, out var cached))
            return cached;

        var name = "";
        try
        {
            var beamType = beam.Document?.GetElement(beam.GetTypeId());
            name = beamType?.Name ?? beam.Name ?? "";
        }
        catch
        {
            name = beam.Name ?? "";
        }

        names[beam.Id] = name;
        return name;
    }

    private static bool TrySolidZ(
        Element element,
        Dictionary<ElementId, (double Bot, double Top)?> cache,
        out double bot,
        out double top)
    {
        if (cache.TryGetValue(element.Id, out var cached))
        {
            if (cached is not { } pair)
            {
                bot = 0;
                top = 0;
                return false;
            }

            bot = pair.Bot;
            top = pair.Top;
            return true;
        }

        bot = double.MaxValue;
        top = double.MinValue;
        foreach (var solid in GeometryRevit.Solids(element))
        {
            try
            {
                foreach (Edge edge in solid.Edges)
                {
                    var curve = edge.AsCurve();
                    if (curve == null)
                        continue;

                    foreach (var point in curve.Tessellate())
                    {
                        bot = Math.Min(bot, point.Z);
                        top = Math.Max(top, point.Z);
                    }
                }
            }
            catch
            {
                // A solid without tessellable edges is ignored; another solid may still qualify.
            }
        }

        if (bot >= top || double.IsInfinity(bot) || double.IsInfinity(top))
        {
            cache[element.Id] = null;
            bot = 0;
            top = 0;
            return false;
        }

        cache[element.Id] = (bot, top);
        return true;
    }

    private static List<Point2>? Ring(
        Element main,
        Plane2D plane,
        Dictionary<ElementId, List<Point2>?> cache)
    {
        if (cache.TryGetValue(main.Id, out var cached))
            return cached;

        var ring = Polygon2D.Largest(GeometryRevit.HorizontalRings(main, plane));
        cache[main.Id] = ring;
        return ring;
    }

    private static string EmptyHostMessage(SplitOptions options)
        => string.Equals(options.Scope, "selection", StringComparison.OrdinalIgnoreCase)
            ? "Please select elements first."
            : "No beams found.";

    private static string SkipNote(int noCurve, int noPair, int failed)
    {
        var parts = new List<string>();
        SplitElements.AddSkip(parts, noCurve, "no curve");
        SplitElements.AddSkip(parts, noPair, "no through-main");
        SplitElements.AddSkip(parts, failed, "split failed");
        return parts.Count > 0 ? $" Skipped: {string.Join(", ", parts)}." : "";
    }

    private sealed class Plan
    {
        public Plan(Element host, Curve curve, CrossingDecision decision)
        {
            Host = host;
            Curve = curve;
            Decision = decision;
        }

        public Element Host { get; }

        public Curve Curve { get; }

        public CrossingDecision Decision { get; }
    }
}
