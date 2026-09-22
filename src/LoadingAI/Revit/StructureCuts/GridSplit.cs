using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using LoadingAI.Geometry;

namespace LoadingAI.Revit.StructureCuts;

/// <summary>
/// Splits walls, columns, beams and floors at grids. Curve members use a vertical plane through the
/// grid line; floors reuse the 1.5 region pipeline with those lines as 2D cutters.
/// </summary>
internal static class GridSplit
{
    public static string Run(
        UIApplication uiApp,
        IReadOnlyList<string> elementIds,
        IReadOnlyList<string> gridIds,
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

        var grids = SplitScope.Cutters(doc, uidoc, gridIds, SplitElements.IsGrid)
            .OfType<Grid>()
            .ToList();
        var links = SplitScope.LinkCutters(doc, options, SplitElements.IsGrid);
        if (grids.Count == 0 && links.Cutters.Count == 0)
            return "No grids found." + links.Report();

        var hostStations = new List<LinkCutterMath.GridStation>();
        foreach (var grid in grids)
        {
            if (!TryStation(grid.Curve, out var station))
                continue;
            hostStations.Add(station);
        }

        var linkedStations = links.Cutters
            .Where(cutter => cutter.Grid.HasValue)
            .Select(cutter => cutter.Grid!.Value)
            .ToList();
        var stations = LinkCutterMath.MergeGrids(hostStations, linkedStations);

        var planes = new List<Plane2D>();
        var edges = new List<IReadOnlyList<Point2>>();
        foreach (var station in stations)
        {
            if (!TryPlane(station, out var plane, out var edge))
                continue;
            planes.Add(plane);
            edges.Add(edge);
        }

        if (planes.Count == 0)
            return "No grids found." + links.Report();

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
                PlanFloor(doc, floor, edges, skips, floorPlans);
                continue;
            }

            if (!HostCurve.CanWrite(host))
            {
                skips.NoCurve++;
                continue;
            }

            PlanCurve(host, planes, skips, curvePlans);
        }

        var notes = skips.Note() + SplitReport.Progress(batch) + links.Report();
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

        using (var tx = SplitElements.WriteTx(doc, "LoadingAI for Revit: split by grids"))
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
            + CurveMemberSplit.ParamNote(options, native, recreated, paramSkipped)
            + links.Report();
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
        IReadOnlyList<Plane2D> planes,
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
        foreach (var plane in planes)
            cuts.AddRange(CurveCut.AtPlane(samples, plane, CurveMemberSplit.MinFraction, CurveMemberSplit.MinGapFraction));

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
        IReadOnlyList<IReadOnlyList<Point2>> edges,
        Skips skips,
        List<FloorSplit.Plan> plans)
    {
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

    private static bool TryStation(Curve? curve, out LinkCutterMath.GridStation station)
    {
        station = new LinkCutterMath.GridStation(new Vec3(0, 0, 0), new Vec3(0, 0, 0));
        if (curve == null)
            return false;

        XYZ start;
        XYZ end;
        try
        {
            start = curve.GetEndPoint(0);
            end = curve.GetEndPoint(1);
        }
        catch
        {
            return false;
        }

        station = new LinkCutterMath.GridStation(GeometryRevit.ToVec3(start), GeometryRevit.ToVec3(end));
        return true;
    }

    private static bool TryPlane(LinkCutterMath.GridStation station, out Plane2D plane, out List<Point2> edge)
    {
        plane = new Plane2D(new Vec3(0, 0, 0), new Vec3(0, 0, 1));
        edge = new List<Point2>();

        var start = GeometryRevit.ToXyz(station.Start);
        var end = GeometryRevit.ToXyz(station.End);
        var along = new Vec3(end.X - start.X, end.Y - start.Y, 0);
        if (along.Length <= 1e-9)
            return false;

        var normal = Vec3.Cross(along, new Vec3(0, 0, 1));
        if (normal.Length <= 1e-9)
            return false;

        plane = new Plane2D(station.Start, normal);
        edge = new List<Point2>
        {
            new Point2(start.X, start.Y),
            new Point2(end.X, end.Y)
        };
        return edge[0].DistanceTo(edge[1]) > Polygon2D.VertexTolerance;
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

        /// <summary>Every host this run left alone, whichever reason it was.</summary>
        public int Total => NoCurve + NoHit + Stacked + Curtain + ShapeEdited
            + NoPlanarFace + NoLevel + NoRegions + Unchanged + Failed;

        public string Note()
        {
            var parts = new List<string>();
            SplitElements.AddSkip(parts, NoCurve, "no curve");
            SplitElements.AddSkip(parts, NoHit, "no grid hit");
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
