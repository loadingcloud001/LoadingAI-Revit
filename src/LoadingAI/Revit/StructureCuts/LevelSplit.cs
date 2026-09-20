using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using LoadingAI.Geometry;

namespace LoadingAI.Revit.StructureCuts;

/// <summary>
/// Splits walls, columns, beams and floors at storey elevations. Curve members use
/// <see cref="CurveCut.AtElevation"/> then native split or location-curve recreate. Horizontal
/// floors skip with no-level-hit; a leaning slab is cut along the plane intersection. A horizontal
/// wall is cut into one piece per storey instead, since a storey plane can never cross its
/// horizontal location curve; a leaning wall still cuts along the plane intersection.
/// </summary>
internal static class LevelSplit
{
    public static string Run(
        UIApplication uiApp,
        IReadOnlyList<string> elementIds,
        IReadOnlyList<string> levelIds,
        IReadOnlyList<string> categories,
        SplitOptions options,
        SplitOutcome? outcome = null)
    {
        var uidoc = uiApp.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (uidoc == null || doc == null)
            return "Please open a model first.";

        var batch = SplitScope.Collect(doc, uidoc, SplitElements.HostsIn(categories), options, elementIds);
        if (batch.Items.Count == 0 && batch.Total == 0)
            return SplitElements.EmptyHostMessage(options);

        var levels = SplitScope.Cutters(doc, uidoc, levelIds, SplitElements.IsLevel)
            .OfType<Level>()
            .ToList();
        if (levels.Count == 0)
            return "No levels found.";

        var elevations = levels.Select(level => level.Elevation).Distinct().OrderBy(z => z).ToList();
        var skips = new Skips();
        var curvePlans = new List<CurveMemberSplit.Plan>();
        var stackPlans = new List<CurveMemberSplit.Stack>();
        var floorPlans = new List<FloorSplit.Plan>();

        foreach (var host in batch.Items)
        {
            var wallSkip = HostCurve.WallSkip(host);
            if (wallSkip == "stacked")
            {
                skips.Stacked++;
                continue;
            }
            if (wallSkip == "curtain")
            {
                skips.Curtain++;
                continue;
            }

            if (host is Floor floor)
            {
                PlanFloor(doc, floor, elevations, skips, floorPlans);
                continue;
            }

            if (!HostCurve.CanWrite(host))
            {
                skips.NoCurve++;
                continue;
            }

            var samples = HostCurve.Samples(host);
            if (samples.Count < 2)
            {
                skips.NoCurve++;
                continue;
            }

            // A wall whose location curve is horizontal is cut into storeys, because a horizontal
            // curve cannot be crossed by a horizontal plane. Everything else keeps the curve path,
            // which already cuts a slanted wall at the storeys.
            if (PlanWallStack(doc, host, samples, levels, skips, stackPlans))
                continue;

            PlanCurve(host, samples, elevations, skips, curvePlans);
        }

        var notes = skips.Note() + SplitReport.Progress(batch);
        if (options.DryRun)
        {
            var curvePieces = curvePlans.Sum(plan => plan.Cuts.Count + 1);
            var stackPieces = stackPlans.Sum(stack => stack.Pieces.Count);
            var floorPieces = floorPlans.Sum(plan => plan.Regions.Count);
            return "Dry-run. "
                + $"Would split {curvePlans.Count + stackPlans.Count} members into {curvePieces + stackPieces} pieces and {floorPlans.Count} floors into {floorPieces} pieces. No changes made."
                + notes;
        }

        if (curvePlans.Count == 0 && stackPlans.Count == 0 && floorPlans.Count == 0)
            return "Nothing to split." + notes;

        var created = new List<ElementId>();
        var split = 0;
        var piecesMade = 0;
        var paramSkipped = 0;
        var native = 0;
        var recreated = 0;
        var dependents = 0;
        var openings = 0;

        using (var tx = SplitElements.WriteTx(doc, "LoadingAI for Revit: split by levels"))
        {
            tx.Start();
            foreach (var plan in curvePlans)
            {
                var (ids, skipped, rollback, wasNative, lost) = CurveMemberSplit.SplitAtCuts(doc, plan, options);
                paramSkipped += skipped;
                if (rollback)
                {
                    skips.Failed++;
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
                    skips.Unchanged++;
                }
            }

            foreach (var stack in stackPlans)
            {
                var (ids, skipped, lost, rollback) = CurveMemberSplit.SplitWallStack(doc, stack, options);
                paramSkipped += skipped;
                if (rollback)
                {
                    skips.Failed++;
                    continue;
                }

                if (ids.Count > 1)
                {
                    split++;
                    piecesMade += ids.Count;
                    created.AddRange(ids);
                    recreated++;
                    // The storey stack deletes the wall it replaces, so anything hanging off that host goes
                    // with it. Like the curve path, the report counts that rather than re-hosting it: the
                    // write stays undoable as one step, but the report may not stay silent about what it
                    // took with the host.
                    dependents += lost;
                }
                else
                {
                    skips.Unchanged++;
                }
            }

            foreach (var plan in floorPlans)
            {
                var (ids, holeCount, skipped, lost, error) = FloorSplit.Write(doc, plan, options);
                paramSkipped += skipped;
                if (error != null || ids.Count == 0)
                {
                    skips.Failed++;
                    continue;
                }

                split++;
                piecesMade += ids.Count;
                openings += holeCount;
                dependents += lost;
                created.AddRange(ids);
            }

            tx.Commit();
        }

        var message = $"Split {split} elements into {piecesMade} pieces."
            + skips.Note()
            + SplitReport.Progress(batch)
            + CurveMemberSplit.ParamNote(options, native, recreated, paramSkipped);
        if (openings > 0)
            message += $" Openings: {openings}.";
        if (recreated > 0)
        {
            message += $" Native split: {native} members, re-created: {recreated}.";
            if (dependents > 0)
                message += $" Dependent elements to re-check: {dependents}.";
        }
        if (created.Count > 0)
            message += " Created: " + SplitReport.Ids(created.Select(SplitElements.Fmt)) + ".";

        outcome?.Record(created, openings, skips.Total);

        SplitElements.Select(uidoc, created);
        return message;
    }

    private static void PlanCurve(
        Element host,
        IReadOnlyList<Vec3> samples,
        IReadOnlyList<double> elevations,
        Skips skips,
        List<CurveMemberSplit.Plan> plans)
    {
        var curve = HostCurve.Of(host);
        if (curve == null)
        {
            skips.NoCurve++;
            return;
        }

        var cuts = new List<double>();
        foreach (var elevation in elevations)
            cuts.AddRange(CurveCut.AtElevation(samples, elevation, CurveMemberSplit.MinFraction, CurveMemberSplit.MinGapFraction));

        cuts = CurveMemberSplit.DistinctCuts(cuts);
        if (cuts.Count == 0)
        {
            skips.NoHit++;
            return;
        }

        plans.Add(new CurveMemberSplit.Plan(host, curve, cuts));
    }

    /// <summary>
    /// A wall is cut into one piece per storey when its location curve is horizontal, because the
    /// planar cut <see cref="PlanCurve"/> uses can only fail there: a horizontal plane crosses a
    /// horizontal curve end-on, never at a point. Claims the host (returns true) when it has handled
    /// it, either by planning the stack or by naming why it would not. Returns false to leave the host
    /// on the curve path, which is what a slanted wall, a wall inside one storey, or a non-wall needs.
    /// </summary>
    private static bool PlanWallStack(
        Document doc,
        Element host,
        IReadOnlyList<Vec3> samples,
        IReadOnlyList<Level> levels,
        Skips skips,
        List<CurveMemberSplit.Stack> stacks)
    {
        if (host is not Wall wall)
            return false;

        var rise = 0.0;
        foreach (var sample in samples)
            rise = Math.Max(rise, Math.Abs(sample.Z - samples[0].Z));
        if (rise > GeometryRevit.ZTolFt)
            return false;

        var curve = HostCurve.Of(host);
        if (curve == null || doc.GetElement(wall.LevelId) is not Level baseLevel)
            return false;

        // A top this path cannot rebuild is named, not folded into "no level hit": the storeys are
        // there, the top is what is missing, and the engineer needs to read that difference.
        if (!WallTop(wall, baseLevel, out var top))
        {
            skips.TopAttached++;
            return true;
        }

        var offset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
        var baseZ = baseLevel.Elevation + offset;
        if (top - baseZ <= GeometryRevit.ZTolFt)
            return false;

        // Only the storeys the engineer selected, and only those strictly inside the wall: a storey
        // level with the base or the top would be a zero-height piece.
        var interior = levels
            .Where(level => level.Elevation > baseZ + GeometryRevit.ZTolFt
                && level.Elevation < top - GeometryRevit.ZTolFt)
            .GroupBy(level => level.Elevation)
            .Select(group => group.First())
            .OrderBy(level => level.Elevation)
            .ToList();
        if (interior.Count == 0)
            return false;

        var pieces = new List<CurveMemberSplit.StackPiece>
        {
            // The wall's own storey keeps the wall's own base offset, so a piece based below the wall
            // base still lands where the wall was. Every storey above starts at its own 0.
            new(baseLevel, offset, interior[0].Elevation - baseZ)
        };

        for (var i = 0; i < interior.Count - 1; i++)
            pieces.Add(new CurveMemberSplit.StackPiece(interior[i], 0, interior[i + 1].Elevation - interior[i].Elevation));

        pieces.Add(new CurveMemberSplit.StackPiece(interior[interior.Count - 1], 0, top - interior[interior.Count - 1].Elevation));

        stacks.Add(new CurveMemberSplit.Stack(wall, curve, pieces));
        return true;
    }

    /// <summary>
    /// The wall's top in model Z, but only when the wall's own unconnected height is what places it.
    /// An attached top, whose height parameter no longer places the top, is refused: rebuilding it as
    /// pieces would silently drop the attachment and write pieces the model never had.
    /// </summary>
    private static bool WallTop(Wall wall, Level baseLevel, out double top)
    {
        top = 0;
        try
        {
            var height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
            if (height <= GeometryRevit.Tol)
                return false;

            var bottom = baseLevel.Elevation + (wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0);
            top = bottom + height;

            var box = wall.get_BoundingBox(null);
            if (box == null)
                return true;

            return Math.Abs(box.Min.Z - bottom) <= GeometryRevit.ZTolFt
                && Math.Abs(box.Max.Z - top) <= GeometryRevit.ZTolFt;
        }
        catch
        {
            return false;
        }
    }

    private static void PlanFloor(
        Document doc,
        Floor floor,
        IReadOnlyList<double> elevations,
        Skips skips,
        List<FloorSplit.Plan> plans)
    {
        if (!GeometryRevit.TrySlabRegion(floor, out var plane, out var tilt, out var slab) || slab == null)
        {
            skips.NoPlanarFace++;
            return;
        }

        if (tilt.IsHorizontal(GeometryRevit.HorizontalZTol))
        {
            skips.NoHit++;
            return;
        }

        var edges = new List<IReadOnlyList<Point2>>();
        foreach (var elevation in elevations)
        {
            var edge = LevelEdge(plane, tilt, slab, elevation);
            if (edge != null)
                edges.Add(edge);
        }

        if (edges.Count == 0)
        {
            skips.NoHit++;
            return;
        }

        var (plan, error) = FloorSplit.PrepareAlong(doc, floor, edges);
        switch (error)
        {
            case "shape-edited":
                skips.ShapeEdited++;
                return;
            case "no-planar-face":
                skips.NoPlanarFace++;
                return;
            case "no-level":
                skips.NoLevel++;
                return;
            case "no-regions":
                skips.NoRegions++;
                return;
            case "unchanged":
                skips.Unchanged++;
                return;
            case null:
                if (plan != null)
                    plans.Add(plan);
                return;
            default:
                skips.Failed++;
                return;
        }
    }

    /// <summary>
    /// Horizontal intersection of the storey plane with a leaning slab, projected into the slab's
    /// 2D region so FloorSplit can cut along it.
    /// </summary>
    private static List<Point2>? LevelEdge(Plane2D plane, PlanarTilt tilt, Region2D slab, double elevation)
    {
        var bounds = Bounds(slab.Outer);
        if (bounds == null)
            return null;

        var (minX, minY, maxX, maxY) = bounds.Value;
        var points = new List<Point2>();
        TryAdd(points, IntersectX(tilt, elevation, minX, minY, maxY), plane);
        TryAdd(points, IntersectX(tilt, elevation, maxX, minY, maxY), plane);
        TryAdd(points, IntersectY(tilt, elevation, minY, minX, maxX), plane);
        TryAdd(points, IntersectY(tilt, elevation, maxY, minX, maxX), plane);

        if (points.Count < 2)
            return null;

        return new List<Point2> { points[0], points[points.Count - 1] };
    }

    private static (double minX, double minY, double maxX, double maxY)? Bounds(IReadOnlyList<Point2> ring)
    {
        if (ring.Count == 0)
            return null;

        var minX = ring[0].X;
        var minY = ring[0].Y;
        var maxX = ring[0].X;
        var maxY = ring[0].Y;
        foreach (var point in ring)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        return (minX, minY, maxX, maxY);
    }

    private static Vec3? IntersectX(PlanarTilt tilt, double elevation, double x, double minY, double maxY)
    {
        if (Math.Abs(tilt.Normal.Y) <= 1e-9)
            return null;

        var y = tilt.Through.Y - ((tilt.Normal.X * (x - tilt.Through.X) + tilt.Normal.Z * (elevation - tilt.Through.Z)) / tilt.Normal.Y);
        if (y < minY - GeometryRevit.Tol || y > maxY + GeometryRevit.Tol)
            return null;

        return new Vec3(x, y, elevation);
    }

    private static Vec3? IntersectY(PlanarTilt tilt, double elevation, double y, double minX, double maxX)
    {
        if (Math.Abs(tilt.Normal.X) <= 1e-9)
            return null;

        var x = tilt.Through.X - ((tilt.Normal.Y * (y - tilt.Through.Y) + tilt.Normal.Z * (elevation - tilt.Through.Z)) / tilt.Normal.X);
        if (x < minX - GeometryRevit.Tol || x > maxX + GeometryRevit.Tol)
            return null;

        return new Vec3(x, y, elevation);
    }

    private static void TryAdd(List<Point2> points, Vec3? point, Plane2D plane)
    {
        if (point == null)
            return;

        var projected = plane.Project(point.Value);
        if (points.Count == 0 || points[points.Count - 1].DistanceTo(projected) > Polygon2D.VertexTolerance)
            points.Add(projected);
    }

    private sealed class Skips
    {
        public int NoCurve { get; set; }
        public int NoHit { get; set; }
        public int TopAttached { get; set; }
        public int Stacked { get; set; }
        public int Curtain { get; set; }
        public int ShapeEdited { get; set; }
        public int NoPlanarFace { get; set; }
        public int NoLevel { get; set; }
        public int NoRegions { get; set; }
        public int Unchanged { get; set; }
        public int Failed { get; set; }

        /// <summary>Every host this run left alone, whichever reason it was.</summary>
        public int Total => NoCurve + NoHit + TopAttached + Stacked + Curtain + ShapeEdited
            + NoPlanarFace + NoLevel + NoRegions + Unchanged + Failed;

        public string Note()
        {
            var parts = new List<string>();
            SplitElements.AddSkip(parts, NoCurve, "no curve");
            SplitElements.AddSkip(parts, NoHit, "no level hit");
            SplitElements.AddSkip(parts, TopAttached, "top not at wall height");
            SplitElements.AddSkip(parts, Stacked, "stacked");
            SplitElements.AddSkip(parts, Curtain, "curtain");
            SplitElements.AddSkip(parts, ShapeEdited, "shape edited");
            SplitElements.AddSkip(parts, NoPlanarFace, "no planar top face");
            SplitElements.AddSkip(parts, NoLevel, "no level");
            SplitElements.AddSkip(parts, NoRegions, "no regions");
            SplitElements.AddSkip(parts, Unchanged, "unchanged");
            SplitElements.AddSkip(parts, Failed, "split failed");
            return parts.Count > 0 ? $" Skipped: {string.Join(", ", parts)}." : "";
        }
    }
}
