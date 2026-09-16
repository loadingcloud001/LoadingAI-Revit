using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using LoadingAI.Geometry;

namespace LoadingAI.Plugins.StructureCuts;

/// <summary>
/// Splits walls, columns, beams and floors at storey elevations. Curve members use
/// <see cref="CurveCut.AtElevation"/> then native split or location-curve recreate. Horizontal
/// floors skip with no-level-hit; a leaning slab is cut along the plane intersection.
/// </summary>
internal static class LevelSplit
{
    public static string Run(
        UIApplication uiApp,
        IReadOnlyList<string> elementIds,
        IReadOnlyList<string> levelIds,
        IReadOnlyList<string> categories,
        SplitOptions options)
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

            PlanCurve(host, elevations, skips, curvePlans);
        }

        var notes = skips.Note() + SplitReport.Progress(batch);
        if (options.DryRun)
        {
            var curvePieces = curvePlans.Sum(plan => plan.Cuts.Count + 1);
            var floorPieces = floorPlans.Sum(plan => plan.Regions.Count);
            return "Dry-run. "
                + $"Would split {curvePlans.Count} members into {curvePieces} pieces and {floorPlans.Count} floors into {floorPieces} pieces. No changes made."
                + notes;
        }

        if (curvePlans.Count == 0 && floorPlans.Count == 0)
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

        SplitElements.Select(uidoc, created);
        return message;
    }

    private static void PlanCurve(
        Element host,
        IReadOnlyList<double> elevations,
        Skips skips,
        List<CurveMemberSplit.Plan> plans)
    {
        var curve = HostCurve.Of(host);
        var samples = HostCurve.Samples(host);
        if (curve == null || samples.Count < 2)
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
        public int Stacked { get; set; }
        public int Curtain { get; set; }
        public int ShapeEdited { get; set; }
        public int NoPlanarFace { get; set; }
        public int NoLevel { get; set; }
        public int NoRegions { get; set; }
        public int Unchanged { get; set; }
        public int Failed { get; set; }

        public string Note()
        {
            var parts = new List<string>();
            SplitElements.AddSkip(parts, NoCurve, "no curve");
            SplitElements.AddSkip(parts, NoHit, "no level hit");
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
