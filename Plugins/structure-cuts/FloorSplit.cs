using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.StructureCuts;

internal static class FloorSplit
{
    private const double Tol = 0.001;
    private static readonly double ZTolFt = Metric.MmToFt(30);
    private static readonly double MinAreaSqFt = 0.09 / Metric.SqMPerSqFt;

    public static string Run(
        UIApplication uiApp,
        IReadOnlyList<string> floorIds,
        IReadOnlyList<string> beamIds,
        IReadOnlyList<string> columnIds,
        int limit,
        bool dryRun = false)
    {
        var uidoc = uiApp.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (uidoc == null || doc == null) return "Please open a model first.";

        var selection = GetSelection(uidoc);
        var floors = Resolve(doc, floorIds, IsFloorLike);
        if (floors.Count == 0)
            floors = selection.Where(IsFloorLike).ToList();
        if (floors.Count == 0) return "Please select elements first.";

        var beams = Resolve(doc, beamIds, IsBeam);
        if (beams.Count == 0)
            beams = selection.Where(IsBeam).ToList();
        if (beams.Count == 0)
            beams = HitElements(doc, BuiltInCategory.OST_StructuralFraming, floors, IsBeam);

        var columns = Resolve(doc, columnIds, IsColumn);
        if (columns.Count == 0)
            columns = selection.Where(IsColumn).ToList();
        if (columns.Count == 0)
            columns = HitElements(doc, BuiltInCategory.OST_StructuralColumns, floors, IsColumn);

        var stopped = floors.Count > limit;
        if (stopped) floors = floors.Take(limit).ToList();

        var splitFloors = 0;
        var newCount = 0;
        var openings = 0;
        var deleted = 0;
        var skipNoBoundary = 0;
        var skipSloped = 0;
        var skipFailed = 0;
        var skipUnchanged = 0;
        var paramSkipped = 0;

        Transaction? tx = dryRun ? null : new Transaction(doc, "LoadingAI for Revit: split floors by beams");
        tx?.Start();
        foreach (var el in floors)
        {
            if (el is not Floor floor)
            {
                skipNoBoundary++;
                continue;
            }

            var (pts, z, sloped) = GetFloorBoundary(floor);
            if (sloped)
            {
                skipSloped++;
                continue;
            }
            if (pts == null || pts.Count < 3 || z == null)
            {
                skipNoBoundary++;
                continue;
            }

            var (created, regions, openCount, skipped, error) = ProcessFloor(doc, floor, pts, z.Value, beams, columns, dryRun);
            if (error == "unchanged")
            {
                skipUnchanged++;
                continue;
            }
            if (dryRun && error == "dryrun")
            {
                splitFloors++;
                newCount += regions;
                openings += openCount;
                continue;
            }
            if (created.Count == 0)
            {
                skipFailed++;
                continue;
            }
            splitFloors++;
            newCount += created.Count;
            openings += openCount;
            paramSkipped += skipped;
            deleted++;
        }
        tx?.Commit();

        var msg = dryRun
            ? $"Dry-run. Would split {splitFloors} floors into {newCount}. Openings: {openings}. No changes made."
            : $"Split {splitFloors} floors into {newCount}. Openings: {openings}. Originals deleted: {deleted}.";
        var skips = new List<string>();
        if (skipNoBoundary > 0) skips.Add($"{skipNoBoundary} no boundary");
        if (skipSloped > 0) skips.Add($"{skipSloped} sloped/curved");
        if (skipUnchanged > 0) skips.Add($"{skipUnchanged} unchanged");
        if (skipFailed > 0) skips.Add($"{skipFailed} failed");
        if (skips.Count > 0) msg += $" Skipped: {string.Join(", ", skips)}.";
        if (!dryRun && splitFloors > 0) msg += " Instance parameters copied to new pieces.";
        if (!dryRun && paramSkipped > 0) msg += $" Param copy skipped: {paramSkipped}.";
        if (stopped) msg += $" Stopped at {limit}.";
        return msg;
    }

    private static (List<Floor> created, int regions, int openings, int paramSkipped, string? error) ProcessFloor(
        Document doc,
        Floor floor,
        List<(double X, double Y)> boundary,
        double zElev,
        IReadOnlyList<Element> beams,
        IReadOnlyList<Element> columns,
        bool dryRun)
    {
        var typeId = floor.GetTypeId();
        var level = floor.get_Parameter(BuiltInParameter.LEVEL_PARAM)?.AsElementId();
        if (level == null || level == ElementId.InvalidElementId || doc.GetElement(level) is not Level)
            return (new List<Floor>(), 0, 0, 0, "no level");

        var heightOffset = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0;
        var isStructural = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1;

        boundary = EnsureCcw(boundary);

        var columnOpenings = new List<List<(double X, double Y)>>();
        foreach (var column in columns)
        {
            if (!BboxIntersectXy(floor, column) || !BboxOverlapZ(floor, column)) continue;
            var footprint = ColumnFootprint(column);
            if (footprint == null) continue;
            var center = Mid(footprint[0], footprint[2]);
            if (PointInPolygon(center, boundary))
                columnOpenings.Add(footprint);
        }

        var polygons = new List<List<(double X, double Y)>> { boundary };
        foreach (var beam in beams)
        {
            if (!BboxIntersectXy(floor, beam) || !BboxOverlapZ(floor, beam)) continue;
            foreach (var (start, end) in BeamEdges(doc, beam, zElev))
            {
                var next = new List<List<(double X, double Y)>>();
                foreach (var poly in polygons)
                    next.AddRange(SplitPolygonByLine(poly, start, end));
                polygons = next;
            }
        }

        polygons = polygons.Where(p => Math.Abs(PolygonArea(p)) > MinAreaSqFt).ToList();
        if (polygons.Count == 0)
            return (new List<Floor>(), 0, 0, 0, "no regions");
        if (polygons.Count == 1 && columnOpenings.Count == 0)
            return (new List<Floor>(), 0, 0, 0, "unchanged");

        if (dryRun)
        {
            var wouldOpen = 0;
            foreach (var poly in polygons)
            {
                foreach (var opening in columnOpenings)
                {
                    if (PointInPolygon(Mid(opening[0], opening[2]), poly))
                        wouldOpen++;
                }
            }
            return (new List<Floor>(), polygons.Count, wouldOpen, 0, "dryrun");
        }

        var snap = ParamCopy.Take(floor);
        var created = new List<Floor>();
        var openingCount = 0;
        var paramSkipped = 0;
        foreach (var poly in polygons)
        {
            var polyOpenings = new List<List<(double X, double Y)>>();
            foreach (var opening in columnOpenings)
            {
                var center = Mid(opening[0], opening[2]);
                if (PointInPolygon(center, poly))
                    polyOpenings.Add(opening);
            }
            var neu = CreateFloor(doc, poly, typeId, level, zElev, polyOpenings, isStructural, heightOffset);
            if (neu == null)
            {
                foreach (var f in created)
                {
                    try { doc.Delete(f.Id); } catch { }
                }
                return (new List<Floor>(), 0, 0, 0, "partial");
            }
            paramSkipped += ParamCopy.Apply(snap, neu);
            created.Add(neu);
            openingCount += polyOpenings.Count;
        }

        try { doc.Delete(floor.Id); }
        catch
        {
            foreach (var f in created)
            {
                try { doc.Delete(f.Id); } catch { }
            }
            return (new List<Floor>(), 0, 0, 0, "delete failed");
        }
        return (created, created.Count, openingCount, paramSkipped, null);
    }

    private static (List<(double X, double Y)>? points, double? z, bool sloped) GetFloorBoundary(Floor floor)
    {
        try
        {
            var options = new Options { DetailLevel = ViewDetailLevel.Fine };
            var geo = floor.get_Geometry(options);
            if (geo == null) return (null, null, false);

            var sawSolid = false;
            foreach (var obj in geo)
            {
                if (obj is not Solid solid || solid.Volume <= 0) continue;
                sawSolid = true;
                foreach (Face face in solid.Faces)
                {
                    if (face is not PlanarFace planar || planar.FaceNormal.Z <= 0.9) continue;
                    foreach (EdgeArray loop in face.EdgeLoops)
                    {
                        var points = new List<(double X, double Y)>();
                        double? z = null;
                        foreach (Edge edge in loop)
                        {
                            var pt = edge.AsCurve().GetEndPoint(0);
                            z ??= pt.Z;
                            points.Add((pt.X, pt.Y));
                        }
                        if (points.Count >= 3)
                            return (points, z, false);
                    }
                }
            }
            return (null, null, sawSolid);
        }
        catch
        {
            return (null, null, false);
        }
    }

    private static List<(XYZ Start, XYZ End)> BeamEdges(Document doc, Element beam, double zElev)
    {
        var edges = new List<(XYZ, XYZ)>();
        if (beam.Location is not LocationCurve lc || lc.Curve is not Line line)
            return edges;
        var bbox = beam.get_BoundingBox(null);
        if (bbox == null) return edges;

        var start = line.GetEndPoint(0);
        var end = line.GetEndPoint(1);
        var dirXy = new XYZ(end.X - start.X, end.Y - start.Y, 0);
        if (dirXy.GetLength() < Tol) return edges;
        dirXy = dirXy.Normalize();
        var perp = new XYZ(-dirXy.Y, dirXy.X, 0);

        var halfWidth = BeamHalfWidth(doc, beam, bbox, start, end, perp);
        var start2d = new XYZ(start.X, start.Y, zElev);
        var end2d = new XYZ(end.X, end.Y, zElev);
        const double extend = 1.0;
        var e1s = start2d + perp * halfWidth - dirXy * extend;
        var e1e = end2d + perp * halfWidth + dirXy * extend;
        var e2s = start2d - perp * halfWidth - dirXy * extend;
        var e2e = end2d - perp * halfWidth + dirXy * extend;
        edges.Add((e1s, e1e));
        edges.Add((e2s, e2e));
        return edges;
    }

    private static double BeamHalfWidth(Document doc, Element beam, BoundingBoxXYZ bbox, XYZ start, XYZ end, XYZ perp)
    {
        try
        {
            var beamType = doc.GetElement(beam.GetTypeId());
            Parameter? widthParam = beamType?.get_Parameter(BuiltInParameter.STRUCTURAL_SECTION_COMMON_WIDTH);
            if (widthParam is not { HasValue: true })
                widthParam = beamType?.LookupParameter("Width");
            if (widthParam is not { HasValue: true })
                widthParam = beamType?.LookupParameter("b");
            if (widthParam is { HasValue: true })
            {
                var w = widthParam.AsDouble() / 2.0;
                if (w > 0) return w;
            }
        }
        catch { }

        var mid = new XYZ((start.X + end.X) / 2.0, (start.Y + end.Y) / 2.0, 0);
        var corners = new[]
        {
            new XYZ(bbox.Min.X - mid.X, bbox.Min.Y - mid.Y, 0),
            new XYZ(bbox.Max.X - mid.X, bbox.Min.Y - mid.Y, 0),
            new XYZ(bbox.Min.X - mid.X, bbox.Max.Y - mid.Y, 0),
            new XYZ(bbox.Max.X - mid.X, bbox.Max.Y - mid.Y, 0)
        };
        var projections = corners.Select(c => c.X * perp.X + c.Y * perp.Y).ToList();
        return (projections.Max() - projections.Min()) / 2.0;
    }

    private static List<(double X, double Y)>? ColumnFootprint(Element column)
    {
        var bbox = column.get_BoundingBox(null);
        if (bbox == null) return null;
        return new List<(double, double)>
        {
            (bbox.Min.X, bbox.Min.Y),
            (bbox.Max.X, bbox.Min.Y),
            (bbox.Max.X, bbox.Max.Y),
            (bbox.Min.X, bbox.Max.Y)
        };
    }

    private static Floor? CreateFloor(
        Document doc,
        List<(double X, double Y)> points,
        ElementId typeId,
        ElementId levelId,
        double zElev,
        List<List<(double X, double Y)>> openings,
        bool structural,
        double heightOffset)
    {
        if (points.Count < 3) return null;
        try
        {
            var outer = LoopFromPoints(points, zElev, clockwise: false);
            if (outer == null) return null;
            var loops = new List<CurveLoop> { outer };
            foreach (var opening in openings)
            {
                if (opening.Count < 3) continue;
                var inner = LoopFromPoints(opening, zElev, clockwise: true);
                if (inner != null) loops.Add(inner);
            }

            var neu = Floor.Create(doc, loops, typeId, levelId);
            if (neu == null) return null;
            if (Math.Abs(heightOffset) > 1e-9)
                neu.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(heightOffset);
            if (structural)
                neu.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.Set(1);
            return neu;
        }
        catch
        {
            return null;
        }
    }

    private static CurveLoop? LoopFromPoints(List<(double X, double Y)> points, double zElev, bool clockwise)
    {
        var ordered = clockwise ? EnsureCw(points) : EnsureCcw(points);
        var curves = new List<Curve>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var p1 = ordered[i];
            var p2 = ordered[(i + 1) % ordered.Count];
            var start = new XYZ(p1.X, p1.Y, zElev);
            var end = new XYZ(p2.X, p2.Y, zElev);
            if (start.DistanceTo(end) <= Tol) continue;
            try { curves.Add(Line.CreateBound(start, end)); }
            catch { return null; }
        }
        if (curves.Count < 3) return null;
        try { return CurveLoop.Create(curves); }
        catch { return null; }
    }

    private static List<List<(double X, double Y)>> SplitPolygonByLine(
        List<(double X, double Y)> polygon,
        XYZ lineStart,
        XYZ lineEnd)
    {
        if (polygon.Count < 3) return new List<List<(double X, double Y)>> { polygon };

        var intersections = new List<(int Index, double T, (double X, double Y) Pt)>();
        var n = polygon.Count;
        for (var i = 0; i < n; i++)
        {
            var p1 = polygon[i];
            var p2 = polygon[(i + 1) % n];
            var hit = LineIntersection(p1, p2, (lineStart.X, lineStart.Y), (lineEnd.X, lineEnd.Y));
            if (hit == null) continue;
            var dx = p2.X - p1.X;
            var dy = p2.Y - p1.Y;
            double t;
            if (Math.Abs(dx) > Math.Abs(dy))
                t = Math.Abs(dx) > Tol ? (hit.Value.X - p1.X) / dx : 0;
            else
                t = Math.Abs(dy) > Tol ? (hit.Value.Y - p1.Y) / dy : 0;
            intersections.Add((i, t, hit.Value));
        }

        if (intersections.Count != 2)
            return new List<List<(double X, double Y)>> { polygon };

        intersections.Sort((a, b) =>
        {
            var c = a.Index.CompareTo(b.Index);
            return c != 0 ? c : a.T.CompareTo(b.T);
        });

        var (idx1, _, pt1) = intersections[0];
        var (idx2, _, pt2) = intersections[1];

        var poly1 = new List<(double X, double Y)> { pt1 };
        var iWalk = (idx1 + 1) % n;
        while (iWalk != (idx2 + 1) % n)
        {
            poly1.Add(polygon[iWalk]);
            iWalk = (iWalk + 1) % n;
        }
        poly1.Add(pt2);

        var poly2 = new List<(double X, double Y)> { pt2 };
        iWalk = (idx2 + 1) % n;
        while (iWalk != (idx1 + 1) % n)
        {
            poly2.Add(polygon[iWalk]);
            iWalk = (iWalk + 1) % n;
        }
        poly2.Add(pt1);

        var result = new List<List<(double X, double Y)>>();
        foreach (var poly in new[] { poly1, poly2 })
        {
            if (poly.Count >= 3 && Math.Abs(PolygonArea(poly)) > Tol)
                result.Add(poly);
        }
        return result.Count > 0 ? result : new List<List<(double X, double Y)>> { polygon };
    }

    private static (double X, double Y)? LineIntersection(
        (double X, double Y) p1, (double X, double Y) p2,
        (double X, double Y) p3, (double X, double Y) p4)
    {
        var denom = (p1.X - p2.X) * (p3.Y - p4.Y) - (p1.Y - p2.Y) * (p3.X - p4.X);
        if (Math.Abs(denom) < Tol) return null;
        var t = ((p1.X - p3.X) * (p3.Y - p4.Y) - (p1.Y - p3.Y) * (p3.X - p4.X)) / denom;
        var u = -((p1.X - p2.X) * (p1.Y - p3.Y) - (p1.Y - p2.Y) * (p1.X - p3.X)) / denom;
        if (t is < 0 or > 1 || u is < 0 or > 1) return null;
        return (p1.X + t * (p2.X - p1.X), p1.Y + t * (p2.Y - p1.Y));
    }

    private static bool PointInPolygon((double X, double Y) point, List<(double X, double Y)> polygon)
    {
        var inside = false;
        var n = polygon.Count;
        var j = n - 1;
        for (var i = 0; i < n; i++)
        {
            var (xi, yi) = polygon[i];
            var (xj, yj) = polygon[j];
            if ((yi > point.Y) != (yj > point.Y) &&
                point.X < (xj - xi) * (point.Y - yi) / (yj - yi) + xi)
                inside = !inside;
            j = i;
        }
        return inside;
    }

    private static double PolygonArea(List<(double X, double Y)> points)
    {
        double area = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var j = (i + 1) % points.Count;
            area += points[i].X * points[j].Y;
            area -= points[j].X * points[i].Y;
        }
        return area / 2;
    }

    private static List<(double X, double Y)> EnsureCcw(List<(double X, double Y)> points)
        => PolygonArea(points) < 0 ? points.AsEnumerable().Reverse().ToList() : points;

    private static List<(double X, double Y)> EnsureCw(List<(double X, double Y)> points)
        => PolygonArea(points) > 0 ? points.AsEnumerable().Reverse().ToList() : points;

    private static (double X, double Y) Mid((double X, double Y) a, (double X, double Y) b)
        => ((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private static bool BboxIntersectXy(Element a, Element b)
    {
        var bb1 = a.get_BoundingBox(null);
        var bb2 = b.get_BoundingBox(null);
        if (bb1 == null || bb2 == null) return false;
        return !(bb1.Max.X < bb2.Min.X || bb1.Min.X > bb2.Max.X ||
                 bb1.Max.Y < bb2.Min.Y || bb1.Min.Y > bb2.Max.Y);
    }

    private static bool BboxOverlapZ(Element a, Element b)
    {
        var bb1 = a.get_BoundingBox(null);
        var bb2 = b.get_BoundingBox(null);
        if (bb1 == null || bb2 == null) return true;
        return !(bb1.Max.Z < bb2.Min.Z - ZTolFt || bb2.Max.Z < bb1.Min.Z - ZTolFt);
    }

    private static List<Element> HitElements(Document doc, BuiltInCategory cat, IReadOnlyList<Element> floors, Func<Element, bool> pred)
        => new FilteredElementCollector(doc)
            .OfCategory(cat)
            .WhereElementIsNotElementType()
            .ToElements()
            .Where(el => pred(el) && floors.Any(f => BboxIntersectXy(f, el) && BboxOverlapZ(f, el)))
            .ToList();

    private static List<Element> Resolve(Document doc, IReadOnlyList<string> ids, Func<Element, bool> pred)
    {
        var list = new List<Element>();
        foreach (var s in ids)
        {
            try
            {
                var el = doc.GetElement(RevitParse.ParseEid(s));
                if (el != null && pred(el)) list.Add(el);
            }
            catch { }
        }
        return list;
    }

    private static List<Element> GetSelection(UIDocument uidoc)
        => uidoc.Selection.GetElementIds()
            .Select(id => uidoc.Document.GetElement(id))
            .Where(e => e != null)
            .Cast<Element>()
            .ToList();

    private static bool IsFloorLike(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_Floors
           || el.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFoundation;

    private static bool IsBeam(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFraming;

    private static bool IsColumn(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns;
}
