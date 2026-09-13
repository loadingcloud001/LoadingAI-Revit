using System.Globalization;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using LoadingAI.Geometry;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.StructureCuts;

/// <summary>
/// Splits slabs along the beams that run through them. The slab's real outline is read back as a region,
/// so inner boundaries survive, beams cut along their own width with the slab's openings kept, and
/// columns become openings in the piece they land in. Nothing is written until every floor has been
/// planned, and a floor that cannot be rebuilt is reported instead of guessed at.
/// </summary>
internal static class FloorSplit
{
    /// <summary>Smaller than this and a piece is shaving left by a cut rather than a usable slab.</summary>
    private static readonly double MinAreaSqFt = 0.09 / Metric.SqMPerSqFt;

    public static string Run(
        UIApplication uiApp,
        IReadOnlyList<string> floorIds,
        IReadOnlyList<string> beamIds,
        IReadOnlyList<string> columnIds,
        SplitOptions options)
    {
        var uidoc = uiApp.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (uidoc == null || doc == null)
            return "Please open a model first.";

        var selection = GetSelection(uidoc);
        var floors = Resolve(doc, floorIds, IsFloorLike);
        if (floors.Count == 0)
            floors = selection.Where(IsFloorLike).ToList();
        if (floors.Count == 0)
            return "Please select elements first.";

        var beams = Resolve(doc, beamIds, IsBeam);
        if (beams.Count == 0)
            beams = selection.Where(IsBeam).ToList();
        if (beams.Count == 0)
            beams = HitElements(doc, BuiltInCategory.OST_StructuralFraming, floors, IsBeam);
        if (beams.Count == 0)
            return "No beams found to split along.";

        var columns = Resolve(doc, columnIds, IsColumn);
        if (columns.Count == 0)
            columns = selection.Where(IsColumn).ToList();
        if (columns.Count == 0)
            columns = HitElements(doc, BuiltInCategory.OST_StructuralColumns, floors, IsColumn);

        var stopped = floors.Count > options.Limit;
        if (stopped)
            floors = floors.Take(options.Limit).ToList();

        var skippedUnsupported = 0;
        var skippedNoLevel = 0;
        var skippedUnchanged = 0;
        var skippedEmpty = 0;

        var plans = new List<Plan>();
        foreach (var element in floors)
        {
            if (element is not Floor floor)
            {
                skippedUnsupported++;
                continue;
            }

            var (plan, error) = Prepare(doc, floor, beams, columns);
            switch (error)
            {
                case "unsupported":
                    skippedUnsupported++;
                    continue;
                case "no level":
                    skippedNoLevel++;
                    continue;
                case "no regions":
                    skippedEmpty++;
                    continue;
                case "unchanged":
                    skippedUnchanged++;
                    continue;
                case null:
                    plans.Add(plan!);
                    break;
            }
        }

        var notes = Notes(skippedUnsupported, skippedNoLevel, skippedEmpty, skippedUnchanged, 0, stopped, options);

        if (options.DryRun)
        {
            var pieces = plans.Sum(plan => plan.Regions.Count);
            var openings = plans.Sum(plan => plan.Regions.Sum(region => region.Holes.Count));
            return "Dry-run. " + $"Would split {plans.Count} floors into {pieces} pieces. Openings: {openings}. No changes made."
                + notes;
        }

        if (plans.Count == 0)
            return "Nothing to split." + notes;

        var created = new List<ElementId>();
        var splitFloors = 0;
        var piecesMade = 0;
        var openingsMade = 0;
        var deleted = 0;
        var skippedFailed = 0;
        var paramSkipped = 0;

        using (var tx = new Transaction(doc, "LoadingAI for Revit: split floors by beams"))
        {
            tx.Start();
            foreach (var plan in plans)
            {
                var (ids, openings, skipped, error) = Write(doc, plan, options);
                paramSkipped += skipped;
                if (error != null || ids.Count == 0)
                {
                    skippedFailed++;
                    continue;
                }

                splitFloors++;
                piecesMade += ids.Count;
                openingsMade += openings;
                deleted++;
                created.AddRange(ids);
            }

            tx.Commit();
        }

        var message = $"Split {splitFloors} floors into {piecesMade} pieces. Openings: {openingsMade}."
            + Notes(skippedUnsupported, skippedNoLevel, skippedEmpty, skippedUnchanged, skippedFailed, stopped, options);
        message += options.CopyParams
            ? " Instance parameters copied to new pieces."
            : " Copy params: off.";
        if (options.CopyParams && paramSkipped > 0)
            message += $" Param copy skipped: {paramSkipped}.";
        if (deleted > 0)
            message += $" Originals deleted: {deleted}.";
        if (created.Count > 0)
            message += " Created: " + SplitReport.Ids(created.Select(Fmt)) + ".";

        Select(uidoc, created);
        return message;
    }

    /// <summary>
    /// Works out the pieces a floor would become without touching the document: the slab outline as a
    /// region (inner boundaries included), a column footprint turned into an opening wherever it lands
    /// inside, and one cut per beam edge.
    /// </summary>
    private static (Plan? Plan, string? Error) Prepare(Document doc, Floor floor, IReadOnlyList<Element> beams, IReadOnlyList<Element> columns)
    {
        if (!GeometryRevit.TrySlabRegion(floor, out var plane, out var slab) || slab == null)
            return (null, "unsupported");

        var levelId = floor.get_Parameter(BuiltInParameter.LEVEL_PARAM)?.AsElementId();
        if (levelId == null || levelId == ElementId.InvalidElementId || doc.GetElement(levelId) is not Level)
            return (null, "no level");

        var originalHoles = slab.Holes.Count;
        var region = slab;

        foreach (var column in columns)
        {
            if (!GeometryRevit.Reaches(floor, column))
                continue;

            // Only the outer outline: a hollow column still blocks the slab, so its inner ring is not a
            // hole. A footprint below the smallest usable piece is noise rather than an opening.
            var ring = Polygon2D.Largest(GeometryRevit.HorizontalRings(column, plane));
            if (ring == null || Polygon2D.Area(ring) <= MinAreaSqFt)
                continue;
            if (!Inside(region, ring))
                continue;

            region = region.WithHole(ring);
        }

        var regions = new List<Region2D> { region };
        foreach (var beam in beams)
        {
            if (!GeometryRevit.Reaches(floor, beam))
                continue;

            foreach (var edge in GeometryRevit.BeamEdges(beam, plane, out _))
            {
                var next = new List<Region2D>();
                foreach (var piece in regions)
                    next.AddRange(Cut(piece, edge));
                regions = next;
            }
        }

        regions = regions.Where(piece => piece.Area > MinAreaSqFt).ToList();
        if (regions.Count == 0)
            return (null, "no regions");
        if (regions.Count == 1 && region.Holes.Count == originalHoles)
            return (null, "unchanged");

        var plan = new Plan
        {
            Floor = floor,
            Plane = plane,
            Regions = regions,
            OriginalHoles = originalHoles,
            TypeId = floor.GetTypeId(),
            LevelId = levelId,
            Structural = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1,
            HeightOffset = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0
        };
        return (plan, null);
    }

    /// <summary>
    /// Splits one region with a beam edge. The edge is trimmed to the outline first and then pushed just
    /// past it, because a cut that stops exactly on the boundary does not read as a crossing; an edge
    /// that only clips a corner, or that runs flush along the boundary, leaves the region whole.
    /// </summary>
    private static List<Region2D> Cut(Region2D region, IReadOnlyList<Point2> edge)
    {
        foreach (var run in Polygon2D.ClipPolylineToPolygon(region.Outer, edge))
        {
            var cut = Polygon2D.ExtendEnds(run, GeometryRevit.NudgeFt);
            if (cut.Count < 2)
                continue;

            var (left, right) = region.Split(cut);
            if (left != null && right != null)
                return new List<Region2D> { left, right };
        }

        return new List<Region2D> { region };
    }

    /// <summary>
    /// Replaces the original floor with one floor per region, carrying the openings each piece kept. A
    /// piece that will not build takes the whole floor back to how it was, so a partial split never
    /// survives as a silently shrunk slab.
    /// </summary>
    private static (List<ElementId> Ids, int Openings, int ParamSkipped, string? Error) Write(Document doc, Plan plan, SplitOptions options)
    {
        var created = new List<Floor>();
        var openings = 0;
        var paramSkipped = 0;

        var mark = options.UniqueMark ? ParamCopy.Mark(plan.Floor) : null;
        var snapshot = options.CopyParams ? ParamCopy.Take(plan.Floor) : null;

        for (var i = 0; i < plan.Regions.Count; i++)
        {
            var region = plan.Regions[i];
            var piece = CreateFloor(doc, plan, region);
            if (piece == null)
            {
                Rollback(doc, created);
                return (new List<ElementId>(), openings, paramSkipped, "partial");
            }

            var markFor = options.UniqueMark ? SplitMarks.For(mark, i) : null;
            if (snapshot != null)
                paramSkipped += ParamCopy.Apply(snapshot, piece, markFor);
            else
                ParamCopy.SetMark(piece, markFor);

            created.Add(piece);
            openings += region.Holes.Count;
        }

        try
        {
            doc.Delete(plan.Floor.Id);
        }
        catch
        {
            Rollback(doc, created);
            return (new List<ElementId>(), openings, paramSkipped, "delete failed");
        }

        return (created.Select(piece => piece.Id).ToList(), openings, paramSkipped, null);
    }

    private static Floor? CreateFloor(Document doc, Plan plan, Region2D region)
    {
        var outer = GeometryRevit.Loop(plan.Plane, region.Outer, clockwise: false);
        if (outer == null)
            return null;

        var loops = new List<CurveLoop> { outer };
        foreach (var hole in region.Holes)
        {
            var inner = GeometryRevit.Loop(plan.Plane, hole, clockwise: true);
            if (inner != null)
                loops.Add(inner);
        }

        try
        {
            var piece = Floor.Create(doc, loops, plan.TypeId, plan.LevelId);
            if (piece == null)
                return null;

            if (Math.Abs(plan.HeightOffset) > 1e-9)
                piece.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(plan.HeightOffset);
            if (plan.Structural)
                piece.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.Set(1);
            return piece;
        }
        catch
        {
            return null;
        }
    }

    private static void Rollback(Document doc, IReadOnlyList<Floor> created)
    {
        foreach (var piece in created)
        {
            try
            {
                doc.Delete(piece.Id);
            }
            catch
            {
                // The floor is reported as failed whether or not every piece came back out.
            }
        }
    }

    /// <summary>Whether a column outline sits wholly inside the slab, so it can be an opening at all.</summary>
    private static bool Inside(Region2D region, IReadOnlyList<Point2> ring)
    {
        foreach (var point in ring)
            if (!Polygon2D.PointInPolygon(point, region.Outer))
                return false;

        return true;
    }

    private static string Notes(int unsupported, int noLevel, int empty, int unchanged, int failed, bool stopped, SplitOptions options)
    {
        var skips = new List<string>();
        if (unsupported > 0) skips.Add($"{unsupported} unsupported");
        if (noLevel > 0) skips.Add($"{noLevel} no level");
        if (empty > 0) skips.Add($"{empty} no regions");
        if (unchanged > 0) skips.Add($"{unchanged} unchanged");
        if (failed > 0) skips.Add($"{failed} failed");

        var note = skips.Count > 0 ? $" Skipped: {string.Join(", ", skips)}." : "";
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

    private static List<Element> HitElements(Document doc, BuiltInCategory category, IReadOnlyList<Element> floors, Func<Element, bool> wanted)
        => new FilteredElementCollector(doc)
            .OfCategory(category)
            .WhereElementIsNotElementType()
            .ToElements()
            .Where(el => wanted(el) && floors.Any(floor => GeometryRevit.Reaches(floor, el)))
            .ToList();

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

    private static bool IsFloorLike(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_Floors
           || el.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFoundation;

    private static bool IsBeam(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFraming;

    private static bool IsColumn(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns;

    /// <summary>A floor and the pieces it was planned to become, before anything is written.</summary>
    private sealed class Plan
    {
        public Floor Floor { get; init; } = null!;

        public Plane2D Plane { get; init; } = null!;

        public List<Region2D> Regions { get; init; } = new();

        public int OriginalHoles { get; init; }

        public ElementId TypeId { get; init; } = ElementId.InvalidElementId;

        public ElementId LevelId { get; init; } = ElementId.InvalidElementId;

        public bool Structural { get; init; }

        public double HeightOffset { get; init; }
    }
}
