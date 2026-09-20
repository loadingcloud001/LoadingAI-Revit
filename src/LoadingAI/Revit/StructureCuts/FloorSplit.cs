using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using LoadingAI.Geometry;
using LoadingAI.Revit;

namespace LoadingAI.Revit.StructureCuts;

/// <summary>
/// Splits slabs along the beams that run through them. The slab's real outline is read back as a region,
/// so inner boundaries survive, beams cut along their own width with the slab's openings kept, and
/// columns become openings in the piece they land in. A slab that leans is rebuilt with a slope arrow so
/// the pieces keep their fall. Nothing is written until every floor has been planned, every floor is
/// written inside its own sub-transaction so a bad piece takes that floor back to how it was, and a
/// floor that cannot be rebuilt is named and left alone rather than guessed at.
/// </summary>
internal static class FloorSplit
{
    /// <summary>Smaller than this and a piece is shaving left by a cut rather than a usable slab.</summary>
    private static readonly double MinAreaSqFt = 0.09 / Metric.SqMPerSqFt;

    /// <summary>How far a rebuilt slab may lean away from the slab it replaced before it is taken back (~0.6 degrees).</summary>
    private static readonly double SlopeTolRad = 0.01;

    public static string Run(
        UIApplication uiApp,
        IReadOnlyList<string> floorIds,
        IReadOnlyList<string> beamIds,
        IReadOnlyList<string> columnIds,
        SplitOptions options,
        SplitOutcome? outcome = null)
    {
        var uidoc = uiApp.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (uidoc == null || doc == null)
            return "Please open a model first.";

        var batch = SplitScope.Collect(doc, uidoc, SplitElements.IsFloorLike, options, floorIds);
        var floors = batch.Items;
        if (floors.Count == 0 && batch.Total == 0)
            return EmptyHostMessage(options);

        var selection = SplitElements.GetSelection(uidoc);
        var beams = SplitElements.Resolve(doc, beamIds, SplitElements.IsBeam);
        if (beams.Count == 0)
            beams = selection.Where(SplitElements.IsBeam).ToList();
        if (beams.Count == 0)
            beams = HitElements(doc, BuiltInCategory.OST_StructuralFraming, floors, SplitElements.IsBeam);
        if (beams.Count == 0)
            return "No beams found to split along.";

        var columns = SplitElements.Resolve(doc, columnIds, SplitElements.IsColumn);
        if (columns.Count == 0)
            columns = selection.Where(SplitElements.IsColumn).ToList();
        if (columns.Count == 0)
            columns = HitElements(doc, BuiltInCategory.OST_StructuralColumns, floors, SplitElements.IsColumn);

        var skips = new Skips();
        var plans = new List<Plan>();
        foreach (var element in floors)
        {
            if (element is not Floor floor)
            {
                skips.NotASlab++;
                continue;
            }

            var (plan, error) = Prepare(doc, floor, beams, columns);
            switch (error)
            {
                case "shape-edited":
                    skips.ShapeEdited++;
                    continue;
                case "no-planar-face":
                    skips.NoPlanarFace++;
                    continue;
                case "no-level":
                    skips.NoLevel++;
                    continue;
                case "no-regions":
                    skips.NoRegions++;
                    continue;
                case "unchanged":
                    skips.Unchanged++;
                    continue;
                case null:
                    plans.Add(plan!);
                    break;
            }
        }

        var notes = skips.Note() + SplitReport.Progress(batch);

        if (options.DryRun)
        {
            var pieces = plans.Sum(plan => plan.Regions.Count);
            var openings = plans.Sum(plan => plan.Regions.Sum(region => region.Holes.Count));
            var sloped = plans.Count(plan => plan.Leans);
            return "Dry-run. " + $"Would split {plans.Count} floors into {pieces} pieces. Openings: {openings}. No changes made."
                + notes
                + (sloped > 0 ? $" Replaced with a slope arrow: {sloped}." : "");
        }

        if (plans.Count == 0)
            return "Nothing to split." + notes;

        var created = new List<ElementId>();
        var splitFloors = 0;
        var piecesMade = 0;
        var openingsMade = 0;
        var slopedMade = 0;
        var dependents = 0;
        var paramSkipped = 0;

        using (var tx = SplitElements.WriteTx(doc, "LoadingAI for Revit: split floors by beams"))
        {
            tx.Start();
            foreach (var plan in plans)
            {
                var (ids, openings, skipped, lost, error) = Write(doc, plan, options);
                paramSkipped += skipped;
                if (error != null || ids.Count == 0)
                {
                    switch (error)
                    {
                        case "sloped-mismatch":
                            skips.SlopedMismatch++;
                            break;
                        case "delete-failed":
                            skips.DeleteFailed++;
                            break;
                        default:
                            skips.Partial++;
                            break;
                    }

                    continue;
                }

                splitFloors++;
                piecesMade += ids.Count;
                openingsMade += openings;
                dependents += lost;
                if (plan.Leans)
                    slopedMade++;

                created.AddRange(ids);
            }

            tx.Commit();
        }

        var message = $"Split {splitFloors} floors into {piecesMade} pieces. Openings: {openingsMade}."
            + skips.Note()
            + SplitReport.Progress(batch);
        message += options.CopyParams
            ? (paramSkipped > 0
                ? $" Instance parameters copied; {paramSkipped} parameter(s) skipped."
                : " Instance parameters copied.")
            : " Copy params: off.";
        if (slopedMade > 0)
            message += $" Replaced with a slope arrow: {slopedMade}.";
        if (splitFloors > 0)
            message += $" Replaced {splitFloors} floors ({dependents} dependent elements lost).";
        if (created.Count > 0)
            message += " Created: " + SplitReport.Ids(created.Select(SplitElements.Fmt)) + ".";

        outcome?.Record(created, openingsMade, skips.Total);

        SplitElements.Select(uidoc, created);
        return message;
    }

    /// <summary>
    /// Works out the pieces a floor would become without touching the document: the slab outline as a
    /// region (inner boundaries included), a column footprint turned into an opening wherever it lands
    /// inside, and one cut per beam edge.
    /// </summary>
    internal static (Plan? Plan, string? Error) PrepareAlong(
        Document doc,
        Floor floor,
        IReadOnlyList<IReadOnlyList<Point2>> edges)
    {
        if (GeometryRevit.IsShapeEdited(floor))
            return (null, "shape-edited");

        if (!GeometryRevit.TrySlabRegion(floor, out var plane, out var tilt, out var slab) || slab == null)
            return (null, "no-planar-face");

        var levelId = floor.get_Parameter(BuiltInParameter.LEVEL_PARAM)?.AsElementId();
        if (levelId == null || levelId == ElementId.InvalidElementId || doc.GetElement(levelId) is not Level)
            return (null, "no-level");

        var regions = new List<Region2D> { slab };
        foreach (var edge in edges)
            regions = SplitAlong(regions, edge);

        regions = KeepUsable(regions);
        if (regions.Count == 0)
            return (null, "no-regions");
        if (regions.Count == 1)
            return (null, "unchanged");

        return (new Plan
        {
            Floor = floor,
            Plane = plane,
            Tilt = tilt,
            Regions = regions,
            TypeId = floor.GetTypeId(),
            LevelId = levelId,
            Structural = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1,
            HeightOffset = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0
        }, null);
    }

    private static (Plan? Plan, string? Error) Prepare(Document doc, Floor floor, IReadOnlyList<Element> beams, IReadOnlyList<Element> columns)
    {
        // A slab whose shape has been edited by hand carries creases no slope arrow can express, so it is
        // left alone by name. Ask before reading the geometry: the name is the more useful of the two.
        if (GeometryRevit.IsShapeEdited(floor))
            return (null, "shape-edited");

        if (!GeometryRevit.TrySlabRegion(floor, out var plane, out var tilt, out var slab) || slab == null)
            return (null, "no-planar-face");

        var levelId = floor.get_Parameter(BuiltInParameter.LEVEL_PARAM)?.AsElementId();
        if (levelId == null || levelId == ElementId.InvalidElementId || doc.GetElement(levelId) is not Level)
            return (null, "no-level");

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
                regions = SplitAlong(regions, edge);
        }

        regions = regions.Where(piece => piece.Area > MinAreaSqFt).ToList();
        if (regions.Count == 0)
            return (null, "no-regions");
        if (regions.Count == 1 && region.Holes.Count == originalHoles)
            return (null, "unchanged");

        var plan = new Plan
        {
            Floor = floor,
            Plane = plane,
            Tilt = tilt,
            Regions = regions,
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
    internal static List<Region2D> SplitAlong(IReadOnlyList<Region2D> regions, IReadOnlyList<Point2> edge)
    {
        var next = new List<Region2D>();
        foreach (var piece in regions)
            next.AddRange(Cut(piece, edge));
        return next;
    }

    internal static List<Region2D> KeepUsable(IReadOnlyList<Region2D> regions)
        => regions.Where(piece => piece.Area > MinAreaSqFt).ToList();

    internal static List<Region2D> Cut(Region2D region, IReadOnlyList<Point2> edge)
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
    /// Replaces the original floor with one floor per region, carrying the openings each piece kept, all
    /// inside its own sub-transaction: a piece that will not build, or a piece that comes back leaning
    /// the wrong way, takes the whole floor back to how it was, so a partial split never survives as a
    /// silently shrunk slab.
    /// </summary>
    internal static (List<ElementId> Ids, int Openings, int ParamSkipped, int Dependent, string? Error) Write(Document doc, Plan plan, SplitOptions options)
    {
        var created = new List<Floor>();
        var openings = 0;
        var paramSkipped = 0;
        var dependents = 0;
        string? failed = null;

        var mark = options.UniqueMark ? ParamCopy.Mark(plan.Floor) : null;
        var snapshot = options.CopyParams ? ParamCopy.Take(plan.Floor) : null;

        using (var sub = new SubTransaction(doc))
        {
            sub.Start();
            try
            {
                for (var i = 0; i < plan.Regions.Count; i++)
                {
                    var region = plan.Regions[i];
                    var piece = CreateFloor(doc, plan, region, out var error);
                    if (piece == null)
                    {
                        failed = error ?? "partial";
                        break;
                    }

                    var markFor = options.UniqueMark ? SplitMarks.For(mark, i) : null;
                    if (snapshot != null)
                        paramSkipped += ParamCopy.Apply(snapshot, piece, markFor);
                    else
                        ParamCopy.SetMark(piece, markFor);

                    created.Add(piece);
                    openings += region.Holes.Count;
                }

                if (failed == null && !Leans(plan, created))
                    failed = "sloped-mismatch";

                if (failed == null)
                {
                    dependents = SplitElements.Dependent(plan.Floor);
                    try
                    {
                        doc.Delete(plan.Floor.Id);
                    }
                    catch
                    {
                        failed = "delete-failed";
                    }
                }
            }
            catch
            {
                if (failed == null)
                    failed = "partial";
            }

            if (failed != null)
            {
                sub.RollBack();
                return (new List<ElementId>(), 0, 0, 0, failed);
            }

            sub.Commit();
        }

        return (created.Select(piece => piece.Id).ToList(), openings, paramSkipped, dependents, null);
    }

    /// <summary>
    /// Builds one piece. Its outline is written in the horizontal plane, because Floor.Create only takes
    /// loops parallel to the XY plane, and it is set back up onto the face it was cut from - a piece from
    /// the top of a leaning slab starts higher than one from the bottom. A leaning slab also hands over
    /// the slope arrow that puts the fall back.
    /// </summary>
    private static Floor? CreateFloor(Document doc, Plan plan, Region2D region, out string? error)
    {
        error = null;

        var plane = GeometryRevit.HorizontalPlane(new XYZ(0, 0, GeometryRevit.FaceZ(plan.Plane, plan.Tilt, region.Outer)));
        var outer = GeometryRevit.Loop(plane, region.Outer, clockwise: false);
        if (outer == null)
        {
            error = "partial";
            return null;
        }

        var loops = new List<CurveLoop> { outer };
        foreach (var hole in region.Holes)
        {
            var inner = GeometryRevit.Loop(plane, hole, clockwise: true);
            if (inner != null)
                loops.Add(inner);
        }

        Line? arrow = null;
        var slope = 0.0;
        if (plan.Leans)
        {
            arrow = GeometryRevit.SlopeArrowFor(plane, region.Outer, plan.Tilt);
            if (arrow == null)
            {
                error = "partial";
                return null;
            }

            slope = plan.Tilt.Slope;
        }

        try
        {
            var piece = Floor.Create(doc, loops, plan.TypeId, plan.LevelId, plan.Structural, arrow, slope);
            if (piece == null)
            {
                error = "partial";
                return null;
            }

            if (Math.Abs(plan.HeightOffset) > 1e-9)
                piece.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(plan.HeightOffset);
            return piece;
        }
        catch
        {
            error = "partial";
            return null;
        }
    }

    /// <summary>
    /// Whether what was built leans the way the original did. A piece whose top face tilts somewhere else
    /// is a slab with the wrong geometry - the one outcome this tool must never leave behind - so the
    /// floor goes back and the run names it instead.
    /// </summary>
    private static bool Leans(Plan plan, IReadOnlyList<Floor> pieces)
    {
        if (!plan.Leans)
            return true;

        foreach (var piece in pieces)
            if (!GeometryRevit.TrySlabRegion(piece, out _, out var built, out _) || !plan.Tilt.Matches(built.Normal, SlopeTolRad))
                return false;

        return true;
    }

    /// <summary>Whether a column outline sits wholly inside the slab, so it can be an opening at all.</summary>
    private static bool Inside(Region2D region, IReadOnlyList<Point2> ring)
    {
        foreach (var point in ring)
            if (!Polygon2D.PointInPolygon(point, region.Outer))
                return false;

        return true;
    }

    private static List<Element> HitElements(Document doc, BuiltInCategory category, IReadOnlyList<Element> floors, Func<Element, bool> wanted)
        => new FilteredElementCollector(doc)
            .OfCategory(category)
            .WhereElementIsNotElementType()
            .ToElements()
            .Where(el => wanted(el) && floors.Any(floor => GeometryRevit.Reaches(floor, el)))
            .ToList();

    /// <summary>
    /// Why floors were left alone, counted one name at a time. Nothing is lumped under one blanket
    /// reason: a shape edited slab, a slab with no usable top face and a slab that simply has nothing
    /// crossing it are three different things for the person reading the report.
    /// </summary>
    private sealed class Skips
    {
        public int NotASlab { get; set; }

        public int ShapeEdited { get; set; }

        public int NoPlanarFace { get; set; }

        public int SlopedMismatch { get; set; }

        public int NoLevel { get; set; }

        public int NoRegions { get; set; }

        public int Unchanged { get; set; }

        public int Partial { get; set; }

        public int DeleteFailed { get; set; }

        /// <summary>Every host this run left alone, whichever reason it was.</summary>
        public int Total => NotASlab + ShapeEdited + NoPlanarFace + SlopedMismatch
            + NoLevel + NoRegions + Unchanged + Partial + DeleteFailed;

        public string Note()
        {
            var parts = new List<string>();
            SplitElements.AddSkip(parts, NotASlab, "not a slab");
            SplitElements.AddSkip(parts, ShapeEdited, "shape edited");
            SplitElements.AddSkip(parts, NoPlanarFace, "no planar top face");
            SplitElements.AddSkip(parts, SlopedMismatch, "sloped rebuild did not match");
            SplitElements.AddSkip(parts, NoLevel, "no level");
            SplitElements.AddSkip(parts, NoRegions, "no regions");
            SplitElements.AddSkip(parts, Unchanged, "unchanged");
            SplitElements.AddSkip(parts, Partial, "piece not built");
            SplitElements.AddSkip(parts, DeleteFailed, "original not deleted");
            return parts.Count > 0 ? $" Skipped: {string.Join(", ", parts)}." : "";
        }
    }

    private static string EmptyHostMessage(SplitOptions options)
        => string.Equals(options.Scope, "selection", StringComparison.OrdinalIgnoreCase)
            ? "Please select elements first."
            : "No floors found.";

    /// <summary>A floor and the pieces it was planned to become, before anything is written.</summary>
    internal sealed class Plan
    {
        public Floor Floor { get; init; } = null!;

        public Plane2D Plane { get; init; } = null!;

        public PlanarTilt Tilt { get; init; }

        public List<Region2D> Regions { get; init; } = new();

        public ElementId TypeId { get; init; } = ElementId.InvalidElementId;

        public ElementId LevelId { get; init; } = ElementId.InvalidElementId;

        public bool Structural { get; init; }

        public double HeightOffset { get; init; }

        /// <summary>Whether the slab leans enough that its pieces have to be rebuilt with a slope arrow.</summary>
        public bool Leans => !Tilt.IsHorizontal(GeometryRevit.HorizontalZTol);
    }
}
