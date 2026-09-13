using System;
using System.Collections.Generic;
using System.Linq;

namespace LoadingAI.Geometry;

/// <summary>
/// Revit-free polygon helpers used by the structure-cuts plugin: orientation, centroid, containment
/// and splitting a simple polygon with a cut polyline (one straight beam edge, or the tessellated
/// edge of a curved beam).
/// </summary>
public static class Polygon2D
{
    /// <summary>Coordinate tolerance used to drop duplicate vertices.</summary>
    public const double VertexTolerance = 1e-9;

    /// <summary>Absolute area below which a polygon is treated as empty.</summary>
    public const double AreaTolerance = 1e-9;

    /// <summary>Twice-signed area, halved (positive when counter-clockwise).</summary>
    public static double SignedArea(IReadOnlyList<Point2> polygon)
    {
        if (polygon == null || polygon.Count < 3)
            return 0;

        double sum = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var current = polygon[i];
            var next = polygon[(i + 1) % polygon.Count];
            sum += current.X * next.Y - next.X * current.Y;
        }

        return sum / 2.0;
    }

    public static double Area(IReadOnlyList<Point2> polygon) => Math.Abs(SignedArea(polygon));

    public static bool IsCcw(IReadOnlyList<Point2> polygon) => SignedArea(polygon) > 0;

    public static List<Point2> EnsureCcw(IReadOnlyList<Point2> polygon)
    {
        var points = Clean(polygon);
        return SignedArea(points) < 0 ? Reverse(points) : points;
    }

    public static List<Point2> EnsureCw(IReadOnlyList<Point2> polygon)
    {
        var points = Clean(polygon);
        return SignedArea(points) > 0 ? Reverse(points) : points;
    }

    /// <summary>Drops consecutive duplicates and a repeated closing vertex.</summary>
    public static List<Point2> Clean(IReadOnlyList<Point2>? polygon, double tolerance = VertexTolerance)
    {
        var result = new List<Point2>();
        if (polygon == null)
            return result;

        foreach (var point in polygon)
        {
            if (result.Count == 0 || result[result.Count - 1].DistanceTo(point) > tolerance)
                result.Add(point);
        }

        while (result.Count > 1 && result[0].DistanceTo(result[result.Count - 1]) <= tolerance)
            result.RemoveAt(result.Count - 1);

        return result;
    }

    public static List<Point2> Reverse(IReadOnlyList<Point2> polygon)
    {
        var result = new List<Point2>(polygon.Count);
        for (var i = polygon.Count - 1; i >= 0; i--)
            result.Add(polygon[i]);
        return result;
    }

    /// <summary>Area-weighted centroid; falls back to the vertex average for degenerate polygons.</summary>
    public static Point2 Centroid(IReadOnlyList<Point2> polygon)
    {
        var points = Clean(polygon);
        if (points.Count == 0)
            return new Point2(0, 0);
        if (points.Count < 3)
            return Average(points);

        double area = 0, cx = 0, cy = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var current = points[i];
            var next = points[(i + 1) % points.Count];
            var cross = current.X * next.Y - next.X * current.Y;
            area += cross;
            cx += (current.X + next.X) * cross;
            cy += (current.Y + next.Y) * cross;
        }

        if (Math.Abs(area) <= AreaTolerance)
            return Average(points);

        return new Point2(cx / (3.0 * area), cy / (3.0 * area));
    }

    private static Point2 Average(IReadOnlyList<Point2> points)
    {
        double x = 0, y = 0;
        foreach (var point in points)
        {
            x += point.X;
            y += point.Y;
        }

        return new Point2(x / points.Count, y / points.Count);
    }

    /// <summary>Ray-casting containment test; points exactly on the boundary are unspecified.</summary>
    public static bool PointInPolygon(Point2 point, IReadOnlyList<Point2> polygon)
    {
        var points = polygon;
        if (points == null || points.Count < 3)
            return false;

        var inside = false;
        var previous = points.Count - 1;
        for (var i = 0; i < points.Count; i++)
        {
            var current = points[i];
            var prior = points[previous];
            if (current.Y > point.Y != prior.Y > point.Y &&
                point.X < (prior.X - current.X) * (point.Y - current.Y) / (prior.Y - current.Y) + current.X)
                inside = !inside;
            previous = i;
        }

        return inside;
    }

    /// <summary>Largest polygon of a set, or <c>null</c> when the set has no usable polygon.</summary>
    public static List<Point2>? Largest(IEnumerable<IReadOnlyList<Point2>>? polygons)
    {
        if (polygons == null)
            return null;

        List<Point2>? best = null;
        var bestArea = AreaTolerance;
        foreach (var candidate in polygons)
        {
            if (candidate == null)
                continue;
            var area = Area(candidate);
            if (area <= bestArea || candidate.Count < 3)
                continue;
            bestArea = area;
            best = candidate.ToList();
        }

        return best;
    }

    /// <summary>
    /// Miter-offsets an open polyline sideways, the shape a beam centreline grows into when it is
    /// thickened into the two edges that cut a slab. A positive <paramref name="distance"/> moves the
    /// result to the left of the travel direction as seen from above, so the two beam edges are this
    /// polyline offset by plus and minus half the beam width. Sharp corners are capped so a hairpin
    /// bend cannot shoot the offset vertex off to infinity.
    /// </summary>
    public static List<Point2> OffsetPolyline(IReadOnlyList<Point2> polyline, double distance)
    {
        var points = Clean(polyline);
        if (points.Count < 2)
            return points;

        // Unit left-hand normal of every segment.
        var normals = new List<Point2>(points.Count - 1);
        for (var i = 0; i < points.Count - 1; i++)
        {
            var dx = points[i + 1].X - points[i].X;
            var dy = points[i + 1].Y - points[i].Y;
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            normals.Add(length <= VertexTolerance
                ? new Point2(0, 0)
                : new Point2(-dy / length, dx / length));
        }

        var offset = new List<Point2>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var before = i == 0 ? normals[0] : normals[i - 1];
            var after = i == points.Count - 1 ? normals[normals.Count - 1] : normals[i];
            var sum = new Point2(before.X + after.X, before.Y + after.Y);
            var length = Math.Sqrt((sum.X * sum.X) + (sum.Y * sum.Y));
            if (length <= VertexTolerance)
            {
                // Reversing direction: a plain step sideways is the sane choice.
                offset.Add(new Point2(points[i].X + (after.X * distance), points[i].Y + (after.Y * distance)));
                continue;
            }

            var miter = new Point2(sum.X / length, sum.Y / length);
            var cos = (miter.X * after.X) + (miter.Y * after.Y);
            var scale = 1.0 / Math.Max(0.2, cos);
            offset.Add(new Point2(points[i].X + (miter.X * distance * scale), points[i].Y + (miter.Y * distance * scale)));
        }

        return offset;
    }

    /// <summary>
    /// Clips an open polyline to a simple polygon, returning each piece that lies inside it with its
    /// ends sitting on the boundary. This is what keeps a beam edge from cutting a slab piece it never
    /// actually reaches: the cut handed to the splitter only ever spans the piece it is applied to.
    /// Inside-ness is sampled between crossings rather than toggled, so a cut that grazes a vertex,
    /// hugs an edge, or starts on the boundary still yields the right stretches.
    /// </summary>
    public static List<List<Point2>> ClipPolylineToPolygon(
        IReadOnlyList<Point2> polygon,
        IReadOnlyList<Point2> polyline)
    {
        var pieces = new List<List<Point2>>();
        var ring = Clean(polygon);
        var cut = Clean(polyline);
        if (ring.Count < 3 || cut.Count < 2)
            return pieces;

        var segments = cut.Count - 1;

        // Position of every boundary crossing as a single parameter running along the whole cut, so
        // crossings on different segments stay in travel order.
        var events = new List<KeyValuePair<double, Point2>>
        {
            new KeyValuePair<double, Point2>(0.0, cut[0]),
            new KeyValuePair<double, Point2>(1.0, cut[cut.Count - 1]),
        };
        for (var i = 0; i < segments; i++)
        {
            for (var j = 0; j < ring.Count; j++)
            {
                var c = ring[j];
                var d = ring[(j + 1) % ring.Count];
                if (TryIntersect(c, d, cut[i], cut[i + 1], out var point, out var s))
                    events.Add(new KeyValuePair<double, Point2>((i + s) / segments, point));
            }
        }

        events.Sort((x, y) => x.Key.CompareTo(y.Key));

        var ordered = new List<KeyValuePair<double, Point2>>();
        foreach (var item in events)
        {
            if (ordered.Count > 0 && Distance(ordered[ordered.Count - 1].Value, item.Value) <= VertexTolerance)
                continue;
            ordered.Add(item);
        }

        // Every gap between neighbouring events is wholly inside or wholly outside, so sampling its
        // midpoint is enough to classify it - no crossing toggling needed.
        var gaps = ordered.Count - 1;
        var runStart = -1;
        for (var k = 0; k < gaps; k++)
        {
            var middle = PointAt(cut, (ordered[k].Key + ordered[k + 1].Key) / 2);
            var inside = PointInPolygon(middle, ring) && !NearBoundary(middle, ring);

            if (inside && runStart < 0)
                runStart = k;
            else if (!inside && runStart >= 0)
            {
                Emit(pieces, SubPolyline(cut, ordered[runStart].Key, ordered[k].Key));
                runStart = -1;
            }
        }

        if (runStart >= 0)
            Emit(pieces, SubPolyline(cut, ordered[runStart].Key, ordered[gaps].Key));

        return pieces;
    }

    /// <summary>
    /// Pushes both ends of an open polyline outward along its end directions. Clipping a cut to a
    /// boundary leaves its ends sitting exactly on that boundary, which makes the crossing test that
    /// follows numerically fragile; a nudge of a millimetre puts the ends safely across it, and the
    /// splitter ignores the overhang because only vertices between the crossings are used.
    /// </summary>
    public static List<Point2> ExtendEnds(IReadOnlyList<Point2> polyline, double by)
    {
        var result = new List<Point2>();
        if (polyline == null)
            return result;

        result.AddRange(polyline);
        if (result.Count < 2 || by <= 0)
            return result;

        var first = result[0];
        var second = result[1];
        var head = Distance(first, second);
        if (head > VertexTolerance)
            result[0] = new Point2(
                first.X + ((first.X - second.X) / head) * by,
                first.Y + ((first.Y - second.Y) / head) * by);

        var last = result[result.Count - 1];
        var before = result[result.Count - 2];
        var tail = Distance(before, last);
        if (tail > VertexTolerance)
            result[result.Count - 1] = new Point2(
                last.X + ((last.X - before.X) / tail) * by,
                last.Y + ((last.Y - before.Y) / tail) * by);

        return result;
    }

    /// <summary>
    /// Where two line segments cross, if they do. Parallel and collinear segments report no crossing -
    /// an edge that runs flush along another is not a crossing point.
    /// </summary>
    public static bool TryCross(Point2 a, Point2 b, Point2 c, Point2 d, out Point2 point)
    {
        point = default;

        var rx = b.X - a.X;
        var ry = b.Y - a.Y;
        var sx = d.X - c.X;
        var sy = d.Y - c.Y;
        var denominator = (rx * sy) - (ry * sx);
        if (Math.Abs(denominator) <= VertexTolerance)
            return false;

        var cx = c.X - a.X;
        var cy = c.Y - a.Y;
        var t = ((cx * sy) - (cy * sx)) / denominator;
        var u = ((cx * ry) - (cy * rx)) / denominator;
        if (t < -VertexTolerance || t > 1 + VertexTolerance)
            return false;
        if (u < -VertexTolerance || u > 1 + VertexTolerance)
            return false;

        point = new Point2(a.X + (rx * t), a.Y + (ry * t));
        return true;
    }

    /// <summary>
    /// Where along an open polyline a point lands, as a fraction of the whole run - the inverse of
    /// <see cref="PointAt"/>. Only the perpendicular foot matters, so a point off to the side still
    /// reports the stretch it belongs to, which is how a column footprint edge maps back onto a beam
    /// centreline.
    /// </summary>
    public static double ParameterAlong(IReadOnlyList<Point2> polyline, Point2 point)
    {
        if (polyline == null || polyline.Count < 2)
            return 0.0;

        var segments = polyline.Count - 1;
        var best = 0.0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < segments; i++)
        {
            var a = polyline[i];
            var b = polyline[i + 1];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lengthSquared = (dx * dx) + (dy * dy);
            var t = lengthSquared <= VertexTolerance * VertexTolerance
                ? 0.0
                : Math.Max(0.0, Math.Min(1.0, (((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / lengthSquared));
            var distance = Distance(point, new Point2(a.X + (dx * t), a.Y + (dy * t)));
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = (i + t) / segments;
        }

        return best;
    }

    private static Point2 PointAt(IReadOnlyList<Point2> polyline, double u)
    {
        var segments = polyline.Count - 1;
        var scaled = Math.Max(0, Math.Min(segments, u * segments));
        var index = (int)Math.Floor(scaled);
        if (index >= segments)
            index = segments - 1;
        return Point2.Lerp(polyline[index], polyline[index + 1], scaled - index);
    }

    private static List<Point2> SubPolyline(IReadOnlyList<Point2> polyline, double from, double to)
    {
        var result = new List<Point2> { PointAt(polyline, from) };
        for (var i = 1; i < polyline.Count - 1; i++)
        {
            var u = (double)i / (polyline.Count - 1);
            if (u > from + VertexTolerance && u < to - VertexTolerance)
                result.Add(polyline[i]);
        }

        result.Add(PointAt(polyline, to));
        return result;
    }

    private static void Emit(List<List<Point2>> pieces, List<Point2> candidate)
    {
        var cleaned = Clean(candidate);
        if (cleaned.Count >= 2 && Distance(cleaned[0], cleaned[cleaned.Count - 1]) > VertexTolerance)
            pieces.Add(cleaned);
    }

    private static double Distance(Point2 a, Point2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static bool NearBoundary(Point2 point, IReadOnlyList<Point2> ring)
    {
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lengthSquared = (dx * dx) + (dy * dy);
            double t;
            if (lengthSquared <= VertexTolerance)
                t = 0;
            else
                t = Math.Max(0, Math.Min(1, (((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / lengthSquared));
            var closest = new Point2(a.X + (dx * t), a.Y + (dy * t));
            if (Distance(closest, point) <= VertexTolerance)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Splits a simple polygon along a cut polyline that crosses its boundary exactly twice - the shape
    /// of one beam edge passing over a slab. Each half is built by walking the boundary from one
    /// crossing to the other and returning along the cut, so a single-segment cut reproduces a plain
    /// straight-line split exactly while a bent or tessellated cut still leaves both halves complementary.
    /// Returns <c>(null, null)</c> when the cut does not separate the polygon (it misses, grazes or
    /// crosses the boundary more than twice); the caller then leaves the original untouched.
    /// </summary>
    public static (List<Point2>? Left, List<Point2>? Right) SplitByPolyline(
        IReadOnlyList<Point2> polygon,
        IReadOnlyList<Point2> cut)
    {
        var points = EnsureCcw(polygon);
        if (points.Count < 3)
            return (null, null);

        var cutPoints = Clean(cut);
        if (cutPoints.Count < 2)
            return (null, null);

        var crossings = new List<Crossing>();
        for (var edge = 0; edge < points.Count; edge++)
        {
            var p1 = points[edge];
            var p2 = points[(edge + 1) % points.Count];
            for (var segment = 0; segment + 1 < cutPoints.Count; segment++)
            {
                if (!TryIntersect(p1, p2, cutPoints[segment], cutPoints[segment + 1], out var point, out var s))
                    continue;
                if (crossings.Any(c => c.Point.DistanceTo(point) <= VertexTolerance))
                    continue;
                crossings.Add(new Crossing(edge, point, segment, s));
            }
        }

        if (crossings.Count != 2)
            return (null, null);

        crossings.Sort((a, b) => a.Segment != b.Segment ? a.Segment.CompareTo(b.Segment) : a.S.CompareTo(b.S));
        var entry = crossings[0];
        var exit = crossings[1];
        if (entry.Point.DistanceTo(exit.Point) <= VertexTolerance)
            return (null, null);

        var between = CutVerticesBetween(cutPoints, entry, exit);

        // Right of the cut: follow the boundary entry -> exit, then run the cut back to the entry.
        var right = BoundaryWalk(points, entry, exit);
        for (var i = between.Count - 1; i >= 0; i--)
            right.Add(between[i]);

        // Left of the cut: the boundary the other way round (exit -> entry), then the cut forward.
        var left = BoundaryWalk(points, exit, entry);
        left.AddRange(between);

        var leftPolygon = Clean(left);
        var rightPolygon = Clean(right);
        if (Area(leftPolygon) <= AreaTolerance || Area(rightPolygon) <= AreaTolerance)
            return (null, null);

        return (leftPolygon, rightPolygon);
    }

    /// <summary>Cut vertices strictly between two crossings, in cut order.</summary>
    private static List<Point2> CutVerticesBetween(List<Point2> cutPoints, Crossing entry, Crossing exit)
    {
        var result = new List<Point2>();
        var last = exit.S > VertexTolerance ? exit.Segment : exit.Segment - 1;
        for (var i = entry.Segment + 1; i <= last && i < cutPoints.Count; i++)
            result.Add(cutPoints[i]);
        return result;
    }

    /// <summary>Boundary vertices from one crossing to another, following the polygon's own order.</summary>
    private static List<Point2> BoundaryWalk(List<Point2> points, Crossing from, Crossing to)
    {
        var count = points.Count;
        var result = new List<Point2> { from.Point };
        var index = (from.Edge + 1) % count;
        var guard = 0;
        while (index != to.Edge && guard++ <= count)
        {
            result.Add(points[index]);
            index = (index + 1) % count;
        }

        result.Add(points[to.Edge]);
        result.Add(to.Point);
        return result;
    }

    private static bool TryIntersect(
        Point2 p1, Point2 p2, Point2 q1, Point2 q2, out Point2 point, out double s)
    {
        point = default;
        s = 0;

        var rx = p2.X - p1.X;
        var ry = p2.Y - p1.Y;
        var dx = q2.X - q1.X;
        var dy = q2.Y - q1.Y;
        var denominator = rx * dy - ry * dx;
        if (Math.Abs(denominator) <= VertexTolerance)
            return false;

        var wx = q1.X - p1.X;
        var wy = q1.Y - p1.Y;
        var t = (wx * dy - wy * dx) / denominator;
        var u = (ry * wx - rx * wy) / denominator;
        if (t < 0 || t > 1 || u < 0 || u > 1)
            return false;

        point = Point2.Lerp(p1, p2, t);
        s = u;
        return true;
    }

    private readonly struct Crossing
    {
        public Crossing(int edge, Point2 point, int segment, double s)
        {
            Edge = edge;
            Point = point;
            Segment = segment;
            S = s;
        }

        /// <summary>Index of the polygon edge the cut crosses.</summary>
        public int Edge { get; }

        public Point2 Point { get; }

        /// <summary>Index of the cut segment the crossing belongs to.</summary>
        public int Segment { get; }

        /// <summary>Position of the crossing within its cut segment (0..1).</summary>
        public double S { get; }
    }
}
