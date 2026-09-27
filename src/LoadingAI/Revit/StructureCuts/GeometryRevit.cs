using System;
using System.Collections.Generic;

using Autodesk.Revit.DB;

using LoadingAI.Geometry;
using LoadingAI.Revit;

namespace LoadingAI.Revit.StructureCuts;

/// <summary>
/// The bridge between the Revit API and the unit tested geometry in LoadingAI.Geometry: projecting
/// Revit points into a plane, flattening curves into polylines, and reading the horizontal outline off
/// a solid. Everything interesting is deliberately a thin translation - the maths lives in Core, where
/// it is covered by tests that do not need Revit.
/// </summary>
internal static class GeometryRevit
{
    /// <summary>Straight-line tolerance in feet (~0.3 mm).</summary>
    public const double Tol = 0.001;

    /// <summary>
    /// A planar face counts as horizontal when its normal is within ~0.8 degrees of up, i.e. the slab is
    /// level enough to build without a slope arrow at all.
    /// </summary>
    public const double HorizontalZTol = 0.9999;

    /// <summary>
    /// The steepest a top face can lean and still be rebuilt: its normal has to keep about 1 degree of
    /// upward part, so a ramp or hardstanding is a slab but an edge-on face is not.
    /// </summary>
    public const double MinUpZ = 0.0175;

    /// <summary>Longest chord when a curve is flattened into a polyline (~75 mm).</summary>
    public static readonly double MaxChordFt = Metric.MmToFt(75);

    /// <summary>Vertical overlap two elements need before they can touch (~30 mm).</summary>
    public static readonly double ZTolFt = Metric.MmToFt(30);

    /// <summary>How far a clipped cut is pushed past the boundary before it is used (1 mm).</summary>
    public static readonly double NudgeFt = Metric.MmToFt(1);

    public static Vec3 ToVec3(XYZ point) => new Vec3(point.X, point.Y, point.Z);

    public static XYZ ToXyz(Vec3 point) => new XYZ(point.X, point.Y, point.Z);

    /// <summary>A plane parallel to the XY plane through the given point.</summary>
    public static Plane2D HorizontalPlane(XYZ through) => new Plane2D(ToVec3(through), new Vec3(0, 0, 1));

    public static Point2 Project(Plane2D plane, XYZ point) => plane.Project(ToVec3(point));

    public static XYZ ToXyz(Plane2D plane, Point2 point) => ToXyz(plane.ToPlane(point));

    /// <summary>Projects a 3D run into the plane, dropping repeated points along the way.</summary>
    public static List<Point2> ProjectAll(Plane2D plane, IEnumerable<XYZ> points)
    {
        var result = new List<Point2>();
        foreach (var point in points)
        {
            var projected = Project(plane, point);
            if (result.Count == 0 || result[result.Count - 1].DistanceTo(projected) > Polygon2D.VertexTolerance)
                result.Add(projected);
        }

        return result;
    }

    /// <summary>
    /// Flattens a curve into a polyline. Lines keep their two ends; everything else is sampled along
    /// its parameter span, well inside the chord budget so a curved member is followed faithfully.
    /// </summary>
    public static List<XYZ> Tessellate(Curve curve)
    {
        var points = new List<XYZ>();
        if (curve == null)
            return points;

        try
        {
            points.Add(curve.GetEndPoint(0));
            points.Add(curve.GetEndPoint(1));
            if (curve is Line)
                return points;
        }
        catch
        {
            return new List<XYZ>();
        }

        var length = 0.0;
        try
        {
            length = curve.Length;
        }
        catch
        {
            // An unbounded or degenerate curve falls back to a modest fixed sampling below.
        }

        // Half the chord budget: sampling follows the parameter, which is uniform in angle on an arc
        // rather than in arc length, so the slack keeps every real chord inside the target.
        var segments = (int)Math.Ceiling(length / (MaxChordFt * 0.5));
        segments = Math.Max(4, Math.Min(256, segments));

        var sampled = new List<XYZ>();
        try
        {
            var start = curve.GetEndParameter(0);
            var span = curve.GetEndParameter(1) - start;
            if (Math.Abs(span) <= 1e-12)
                return points;

            for (var i = 0; i <= segments; i++)
                sampled.Add(curve.Evaluate(start + (span * i / segments), false));
        }
        catch
        {
            return points;
        }

        var result = new List<XYZ>();
        foreach (var point in sampled)
        {
            if (result.Count == 0 || result[result.Count - 1].DistanceTo(point) > Tol)
                result.Add(point);
        }

        return result.Count >= 2 ? result : points;
    }

    /// <summary>The parameter span of a curve, as the pair the split fractions are measured against.</summary>
    public static double Span(Curve curve, out double start)
    {
        start = curve.GetEndParameter(0);
        return curve.GetEndParameter(1) - start;
    }

    /// <summary>The point at the given fraction of a curve's parameter span.</summary>
    public static XYZ At(Curve curve, double fraction)
    {
        var span = Span(curve, out var start);
        return curve.Evaluate(start + (fraction * span), false);
    }

    /// <summary>
    /// The piece of a curve between two fractions of its span. Lines and arcs are rebuilt exactly -
    /// an arc keeps its curvature - and any other curve type falls back to a straight chord.
    /// </summary>
    public static Curve? SubCurve(Curve curve, double from, double to)
    {
        var span = Span(curve, out var start);
        if (Math.Abs(span) <= 1e-12)
            return null;

        XYZ first;
        XYZ last;
        XYZ middle;
        try
        {
            first = curve.Evaluate(start + (from * span), false);
            last = curve.Evaluate(start + (to * span), false);
            middle = curve.Evaluate(start + (((from + to) / 2.0) * span), false);
        }
        catch
        {
            return null;
        }

        if (first.DistanceTo(last) <= Tol)
            return null;

        if (curve is Line)
            return Line.CreateBound(first, last);

        try
        {
            return Arc.Create(first, last, middle);
        }
        catch
        {
            return Line.CreateBound(first, last);
        }
    }

    /// <summary>The solids of an element, following one level of nested instances.</summary>
    public static List<Solid> Solids(Element element)
    {
        var solids = new List<Solid>();
        try
        {
            var options = new Options { DetailLevel = ViewDetailLevel.Fine };
            var geometry = element.get_Geometry(options);
            if (geometry == null)
                return solids;

            foreach (var item in geometry)
            {
                if (item is Solid solid)
                {
                    if (solid.Volume > 0)
                        solids.Add(solid);
                }
                else if (item is GeometryInstance instance)
                {
                    var nested = instance.GetInstanceGeometry();
                    if (nested == null)
                        continue;

                    foreach (var sub in nested)
                        if (sub is Solid inner && inner.Volume > 0)
                            solids.Add(inner);
                }
            }
        }
        catch
        {
            // A element without usable geometry simply reports no solids.
        }

        return solids;
    }

    /// <summary>
    /// The horizontal outline of an element, in the coordinates of the given horizontal plane: the
    /// solid's own shadow along Z where Revit can compute one, which is exact for an L shaped or round
    /// section, and its widest horizontal face or bounding box when it cannot.
    /// </summary>
    public static List<List<Point2>> HorizontalRings(Element element, Plane2D plane)
    {
        var solids = Solids(element);
        var z = plane.Origin.Z;

        var best = new List<List<Point2>>();
        var bestArea = 0.0;
        foreach (var solid in solids)
        {
            var rings = ShadowRings(solid, plane, z);
            if (rings.Count == 0)
                continue;

            var area = OuterArea(rings);
            if (area <= bestArea)
                continue;

            bestArea = area;
            best = rings;
        }

        if (best.Count > 0)
            return best;

        foreach (var solid in solids)
        {
            var rings = FaceRings(solid, plane, z);
            if (rings.Count == 0)
                continue;

            var area = OuterArea(rings);
            if (area <= bestArea)
                continue;

            bestArea = area;
            best = rings;
        }

        return best.Count > 0 ? best : BboxRings(element, plane);
    }

    /// <summary>
    /// The largest upward facing planar face of an element as a region, together with how far that face
    /// leans. Floors with no such face - a doubly curved slab, or an edge-on face - cannot be rebuilt by
    /// Floor.Create, so they report false and the caller counts them instead of guessing.
    /// The plane is horizontal even for a leaning face: Floor.Create only takes outlines parallel to the
    /// XY plane, so the lean is handed over as a slope arrow rather than as the plane of the outline.
    /// </summary>
    public static bool TrySlabRegion(Element element, out Plane2D plane, out PlanarTilt tilt, out Region2D? region)
    {
        plane = HorizontalPlane(new XYZ(0, 0, 0));
        tilt = default;
        region = null;

        PlanarFace? best = null;
        var bestArea = 0.0;
        foreach (var solid in Solids(element))
        {
            try
            {
                foreach (Face face in solid.Faces)
                {
                    if (face is not PlanarFace planar || planar.FaceNormal.Z <= MinUpZ)
                        continue;

                    var area = face.Area;
                    if (area <= bestArea)
                        continue;

                    bestArea = area;
                    best = planar;
                }
            }
            catch
            {
                // Skip solids whose faces cannot be read.
            }
        }

        if (best == null)
            return false;

        if (!PlanarTilt.TryCreate(ToVec3(best.FaceNormal), ToVec3(best.Origin), out tilt))
            return false;

        var rings = Loops(best, plane);
        var outer = Polygon2D.Largest(rings);
        if (outer == null)
            return false;

        // The biggest loop is the slab's outside; every smaller loop - a shaft or a stairwell - is a
        // hole in it.
        var outerArea = Polygon2D.Area(outer);
        var holes = new List<IReadOnlyList<Point2>>();
        foreach (var ring in rings)
            if (Polygon2D.Area(ring) < outerArea - Polygon2D.AreaTolerance)
                holes.Add(ring);

        region = new Region2D(outer, holes);
        if (!region.IsValid)
            return false;

        plane = HorizontalPlane(new XYZ(0, 0, FaceZ(plane, tilt, outer)));
        return true;
    }

    /// <summary>
    /// The lowest height a face passes over a ring of plan points, which is where a piece cut out of it
    /// starts. A leaning slab is rebuilt from a flat outline lifted by a slope arrow, so every piece has
    /// to come back up onto the face it was cut from rather than sharing the slab's lowest point.
    /// </summary>
    public static double FaceZ(Plane2D plane, PlanarTilt tilt, IReadOnlyList<Point2> ring)
    {
        var lowest = double.MaxValue;
        foreach (var point in ring)
        {
            var world = plane.ToPlane(point);
            var height = tilt.ZAt(world.X, world.Y);
            if (height < lowest)
                lowest = height;
        }

        return lowest == double.MaxValue ? tilt.ZAt(plane.Origin.X, plane.Origin.Y) : lowest;
    }

    /// <summary>
    /// The line Revit's sloped Floor.Create wants: horizontal, drawn along the direction the piece
    /// climbs in, from where the piece is lowest to where it is highest. Both ends sit in the plane the
    /// piece's outline was written in, because Revit throws when the arrow is not horizontal.
    /// </summary>
    public static Line? SlopeArrowFor(Plane2D plane, IReadOnlyList<Point2> ring, PlanarTilt tilt)
    {
        var uphill = new Point2(Vec3.Dot(tilt.Uphill, plane.U), Vec3.Dot(tilt.Uphill, plane.V));
        if (uphill.DistanceTo(new Point2(0, 0)) <= 1e-9)
            return null;

        var low = double.MaxValue;
        var high = double.MinValue;
        foreach (var point in ring)
        {
            var along = (point.X * uphill.X) + (point.Y * uphill.Y);
            if (along < low)
                low = along;

            if (along > high)
                high = along;
        }

        if (low == double.MaxValue || high == double.MinValue)
            return null;

        var from = new Point2(uphill.X * low, uphill.Y * low);
        var to = new Point2(uphill.X * high, uphill.Y * high);
        if (from.DistanceTo(to) <= Tol)
            return null;

        try
        {
            return Line.CreateBound(ToXyz(plane, from), ToXyz(plane, to));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a slab's shape has been edited by hand. A slab carrying creases or vertices cannot be
    /// rebuilt from a slope arrow, so it is named and left alone. A floor that will not hand over its
    /// shape editor is read as an ordinary slab: the lean, and the self-check on what was created, still
    /// stand between it and wrong geometry.
    /// </summary>
    public static bool IsShapeEdited(Element element)
    {
        if (element is not Floor floor)
            return false;

        try
        {
            var editor = floor.GetSlabShapeEditor();
            if (editor == null)
                return false;

            // IsEnabled only means the editor is open, not that the slab was edited. Creases, or a
            // vertex that is not an original corner, are the real signal.
            var creases = editor.SlabShapeCreases;
            if (creases != null && !creases.IsEmpty)
                return true;

            var vertices = editor.SlabShapeVertices;
            if (vertices == null || vertices.IsEmpty)
                return false;

            foreach (SlabShapeVertex vertex in vertices)
            {
                if (vertex.VertexType != SlabShapeVertexType.Corner)
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The two lines a beam's width grows into in plan: its centreline offset half a width to either
    /// side. A curved beam is tessellated first, so the edges are bent and the slab pieces follow the
    /// curve instead of cutting a straight chord across it.
    /// </summary>
    public static List<List<Point2>> BeamEdges(Element beam, Plane2D plane, out List<Point2> centreline)
    {
        centreline = new List<Point2>();
        var edges = new List<List<Point2>>();

        if (beam.Location is not LocationCurve location || location.Curve == null)
            return edges;

        centreline = ProjectAll(plane, Tessellate(location.Curve));
        if (centreline.Count < 2)
            return edges;

        var half = HalfWidth(beam, centreline);
        if (half <= Polygon2D.VertexTolerance)
            half = Metric.MmToFt(50);

        edges.Add(Polygon2D.OffsetPolyline(centreline, half));
        edges.Add(Polygon2D.OffsetPolyline(centreline, -half));
        return edges;
    }

    /// <summary>Turns a planar ring back into a Revit curve loop, wound the way Floor.Create wants it.</summary>
    public static CurveLoop? Loop(Plane2D plane, IReadOnlyList<Point2> ring, bool clockwise)
    {
        var ordered = new List<Point2>(ring);
        if (Polygon2D.IsCcw(ordered) == clockwise)
            ordered.Reverse();

        if (ordered.Count < 3)
            return null;

        var points = new List<XYZ>();
        foreach (var point in ordered)
            points.Add(ToXyz(plane, point));

        var loop = new CurveLoop();
        for (var i = 0; i < points.Count; i++)
        {
            var next = points[(i + 1) % points.Count];
            if (points[i].DistanceTo(next) <= Tol)
                return null;

            loop.Append(Line.CreateBound(points[i], next));
        }

        return loop;
    }

    /// <summary>Whether two elements share any of their vertical extent.</summary>
    public static bool OverlapsVertically(Element a, Element b)
    {
        var first = a.get_BoundingBox(null);
        var second = b.get_BoundingBox(null);
        if (first == null || second == null)
            return false;

        return first.Min.Z <= second.Max.Z + ZTolFt && second.Min.Z <= first.Max.Z + ZTolFt;
    }

    /// <summary>
    /// Same reach test against a bounding box already in host space. A linked cutter must not be
    /// asked through <see cref="Reaches(Element, Element)"/>: that reads the link-local box.
    /// </summary>
    public static bool Reaches(Element host, BoundingBoxXYZ cutter)
    {
        var first = host.get_BoundingBox(null);
        if (first == null || cutter == null)
            return false;

        if (first.Min.X > cutter.Max.X + Tol || cutter.Min.X > first.Max.X + Tol)
            return false;

        if (first.Min.Y > cutter.Max.Y + Tol || cutter.Min.Y > first.Max.Y + Tol)
            return false;

        return first.Min.Z <= cutter.Max.Z + ZTolFt && cutter.Min.Z <= first.Max.Z + ZTolFt;
    }

    /// <summary>Whether two elements' outlines overlap in plan and in height, i.e. whether they can interact.</summary>
    public static bool Reaches(Element a, Element b)
    {
        var first = a.get_BoundingBox(null);
        var second = b.get_BoundingBox(null);
        if (first == null || second == null)
            return false;

        if (first.Min.X > second.Max.X + Tol || second.Min.X > first.Max.X + Tol)
            return false;

        if (first.Min.Y > second.Max.Y + Tol || second.Min.Y > first.Max.Y + Tol)
            return false;

        return OverlapsVertically(a, b);
    }

    private static List<List<Point2>> ShadowRings(Solid solid, Plane2D plane, double z)
    {
        var rings = new List<List<Point2>>();
        try
        {
            var basePlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z));
            var analyzer = ExtrusionAnalyzer.Create(solid, basePlane, XYZ.BasisZ);
            var baseFace = analyzer?.GetExtrusionBase();
            if (baseFace != null)
                rings.AddRange(Loops(baseFace, plane));
        }
        catch
        {
            // Revit refuses solids it cannot treat as an extrusion; the face fallback takes over.
        }

        return rings;
    }

    private static List<List<Point2>> FaceRings(Solid solid, Plane2D plane, double z)
    {
        PlanarFace? best = null;
        var bestArea = 0.0;
        try
        {
            foreach (Face face in solid.Faces)
            {
                if (face is not PlanarFace planar || Math.Abs(planar.FaceNormal.Z) <= HorizontalZTol)
                    continue;

                var area = face.Area;
                if (area <= bestArea)
                    continue;

                bestArea = area;
                best = planar;
            }
        }
        catch
        {
            return new List<List<Point2>>();
        }

        var rings = new List<List<Point2>>();
        if (best == null)
            return rings;

        // The face may sit at another height; its plan outline is what a footprint needs, so it is
        // reported in the target plane's coordinates either way.
        rings.AddRange(Loops(best, plane));
        return rings;
    }

    private static List<List<Point2>> BboxRings(Element element, Plane2D plane)
    {
        var rings = new List<List<Point2>>();
        var box = element.get_BoundingBox(null);
        if (box == null)
            return rings;

        var corners = new List<XYZ>
        {
            new XYZ(box.Min.X, box.Min.Y, box.Min.Z),
            new XYZ(box.Max.X, box.Min.Y, box.Min.Z),
            new XYZ(box.Max.X, box.Max.Y, box.Min.Z),
            new XYZ(box.Min.X, box.Max.Y, box.Min.Z),
        };

        var ring = ProjectAll(plane, corners);
        if (ring.Count >= 3 && Polygon2D.Area(ring) > Polygon2D.AreaTolerance)
            rings.Add(ring);

        return rings;
    }

    /// <summary>Every edge loop of a face, projected into the plane and cleaned into rings.</summary>
    private static List<List<Point2>> Loops(Face face, Plane2D plane)
    {
        var rings = new List<List<Point2>>();
        try
        {
            foreach (EdgeArray edges in face.EdgeLoops)
            {
                var ring = new List<Point2>();
                foreach (Edge edge in edges)
                {
                    List<XYZ> points;
                    try
                    {
                        points = new List<XYZ>(edge.Tessellate());
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var point in ProjectAll(plane, points))
                    {
                        if (ring.Count == 0 || ring[ring.Count - 1].DistanceTo(point) > Polygon2D.VertexTolerance)
                            ring.Add(point);
                    }
                }

                ring = Polygon2D.Clean(ring);
                if (ring.Count >= 3 && Polygon2D.Area(ring) > Polygon2D.AreaTolerance)
                    rings.Add(ring);
            }
        }
        catch
        {
            return new List<List<Point2>>();
        }

        return rings;
    }

    private static double OuterArea(List<List<Point2>> rings)
    {
        var area = 0.0;
        foreach (var ring in rings)
            area = Math.Max(area, Polygon2D.Area(ring));

        return area;
    }

    /// <summary>
    /// The half width of a beam in plan. The section's own width is exact for an upright steel or
    /// precast member; failing that the bounding box is measured across the centreline's chord, which
    /// is conservative for a curved beam.
    /// </summary>
    private static double HalfWidth(Element beam, IReadOnlyList<Point2> centreline)
    {
        try
        {
            var beamType = beam.Document?.GetElement(beam.GetTypeId());
            if (beamType != null)
            {
                var width = beamType.get_Parameter(BuiltInParameter.STRUCTURAL_SECTION_COMMON_WIDTH);
                if (width == null || !width.HasValue)
                    width = beamType.LookupParameter("Width");

                if (width == null || !width.HasValue)
                    width = beamType.LookupParameter("b");

                if (width != null && width.HasValue && width.StorageType == StorageType.Double)
                {
                    var half = width.AsDouble() / 2.0;
                    if (half > 0)
                        return half;
                }
            }
        }
        catch
        {
            // Fall through to the bounding box.
        }

        var box = beam.get_BoundingBox(null);
        if (box == null)
            return 0;

        var start = centreline[0];
        var end = centreline[centreline.Count - 1];
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= Polygon2D.VertexTolerance)
            return 0;

        var nx = -dy / length;
        var ny = dx / length;
        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var corner in new[]
        {
            new Point2(box.Min.X, box.Min.Y),
            new Point2(box.Max.X, box.Min.Y),
            new Point2(box.Max.X, box.Max.Y),
            new Point2(box.Min.X, box.Max.Y),
        })
        {
            var offset = ((corner.X - start.X) * nx) + ((corner.Y - start.Y) * ny);
            min = Math.Min(min, offset);
            max = Math.Max(max, offset);
        }

        return (max - min) / 2.0;
    }

    /// <summary>
    /// Solids already in host space. Instance geometry is link-local; the link transform is applied
    /// once. Do not also call <c>GetTransformed</c>.
    /// </summary>
    public static List<Solid> SolidsInHost(Element element, Transform toHost)
    {
        var solids = Solids(element);
        if (toHost == null || toHost.IsIdentity)
            return solids;

        var moved = new List<Solid>(solids.Count);
        foreach (var solid in solids)
        {
            try
            {
                var transformed = SolidUtils.CreateTransformed(solid, toHost);
                if (transformed != null && transformed.Volume > 0)
                    moved.Add(transformed);
            }
            catch
            {
                // A solid the transform refuses is omitted, not used at link-local coordinates.
            }
        }

        return moved;
    }

    /// <summary>Location curve in host space. A null transform is identity, not a reason to keep link-local.</summary>
    public static Curve? CurveInHost(Curve? curve, Transform toHost)
    {
        if (curve == null)
            return null;
        if (toHost == null || toHost.IsIdentity)
            return curve;

        try
        {
            return curve.CreateTransformed(toHost);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Horizontal rings of solids that are already in host space.</summary>
    public static List<List<Point2>> RingsOf(IReadOnlyList<Solid> solids, Plane2D plane, BoundingBoxXYZ? fallback)
    {
        var z = plane.Origin.Z;
        var best = new List<List<Point2>>();
        var bestArea = 0.0;
        foreach (var solid in solids)
        {
            var rings = ShadowRings(solid, plane, z);
            if (rings.Count == 0)
                rings = FaceRings(solid, plane, z);
            if (rings.Count == 0)
                continue;

            var area = OuterArea(rings);
            if (area <= bestArea)
                continue;

            bestArea = area;
            best = rings;
        }

        if (best.Count > 0)
            return best;

        return fallback == null ? best : BoxRing(fallback, plane);
    }

    public static BoundingBoxXYZ? BoxInHost(BoundingBoxXYZ? box, Transform toHost)
    {
        if (box == null)
            return null;
        if (toHost == null || toHost.IsIdentity)
            return box;

        try
        {
            var moved = new BoundingBoxXYZ();
            var corners = new[]
            {
                new XYZ(box.Min.X, box.Min.Y, box.Min.Z),
                new XYZ(box.Min.X, box.Min.Y, box.Max.Z),
                new XYZ(box.Min.X, box.Max.Y, box.Min.Z),
                new XYZ(box.Min.X, box.Max.Y, box.Max.Z),
                new XYZ(box.Max.X, box.Min.Y, box.Min.Z),
                new XYZ(box.Max.X, box.Min.Y, box.Max.Z),
                new XYZ(box.Max.X, box.Max.Y, box.Min.Z),
                new XYZ(box.Max.X, box.Max.Y, box.Max.Z)
            };
            var min = toHost.OfPoint(corners[0]);
            var max = min;
            for (var i = 1; i < corners.Length; i++)
            {
                var point = toHost.OfPoint(corners[i]);
                min = new XYZ(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y), Math.Min(min.Z, point.Z));
                max = new XYZ(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y), Math.Max(max.Z, point.Z));
            }

            moved.Min = min;
            moved.Max = max;
            return moved;
        }
        catch
        {
            return null;
        }
    }

    public static List<List<Point2>> BeamEdgesOf(Curve curve, Plane2D plane, double halfWidth, out List<Point2> centreline)
    {
        centreline = ProjectAll(plane, Tessellate(curve));
        var edges = new List<List<Point2>>();
        if (centreline.Count < 2)
            return edges;

        var half = halfWidth > Polygon2D.VertexTolerance ? halfWidth : Metric.MmToFt(50);
        edges.Add(Polygon2D.OffsetPolyline(centreline, half));
        edges.Add(Polygon2D.OffsetPolyline(centreline, -half));
        return edges;
    }

    public static double HalfWidthOf(Element beam, IReadOnlyList<Point2> centreline, BoundingBoxXYZ? box)
    {
        try
        {
            var beamType = beam.Document?.GetElement(beam.GetTypeId());
            if (beamType != null)
            {
                var width = beamType.get_Parameter(BuiltInParameter.STRUCTURAL_SECTION_COMMON_WIDTH);
                if (width == null || !width.HasValue)
                    width = beamType.LookupParameter("Width");
                if (width == null || !width.HasValue)
                    width = beamType.LookupParameter("b");
                if (width != null && width.HasValue && width.StorageType == StorageType.Double)
                {
                    var half = width.AsDouble() / 2.0;
                    if (half > 0)
                        return half;
                }
            }
        }
        catch
        {
            // Fall through to the host-space box.
        }

        if (box == null || centreline.Count < 2)
            return 0;

        var start = centreline[0];
        var end = centreline[centreline.Count - 1];
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= Polygon2D.VertexTolerance)
            return 0;

        var nx = -dy / length;
        var ny = dx / length;
        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var corner in new[]
        {
            new Point2(box.Min.X, box.Min.Y),
            new Point2(box.Max.X, box.Min.Y),
            new Point2(box.Max.X, box.Max.Y),
            new Point2(box.Min.X, box.Max.Y),
        })
        {
            var offset = ((corner.X - start.X) * nx) + ((corner.Y - start.Y) * ny);
            min = Math.Min(min, offset);
            max = Math.Max(max, offset);
        }

        return (max - min) / 2.0;
    }

    public static (double Bot, double Top)? SolidZ(IReadOnlyList<Solid> solids)
    {
        if (solids.Count == 0)
            return null;

        var bot = double.MaxValue;
        var top = double.MinValue;
        foreach (var solid in solids)
        {
            var box = solid.GetBoundingBox();
            if (box == null)
                continue;
            var corners = new[]
            {
                box.Transform.OfPoint(box.Min),
                box.Transform.OfPoint(new XYZ(box.Min.X, box.Min.Y, box.Max.Z)),
                box.Transform.OfPoint(new XYZ(box.Min.X, box.Max.Y, box.Min.Z)),
                box.Transform.OfPoint(box.Max)
            };
            foreach (var corner in corners)
            {
                bot = Math.Min(bot, corner.Z);
                top = Math.Max(top, corner.Z);
            }
        }

        return bot <= top ? (bot, top) : null;
    }

    private static List<List<Point2>> BoxRing(BoundingBoxXYZ box, Plane2D plane)
    {
        var ring = new List<Point2>
        {
            plane.Project(new Vec3(box.Min.X, box.Min.Y, plane.Origin.Z)),
            plane.Project(new Vec3(box.Max.X, box.Min.Y, plane.Origin.Z)),
            plane.Project(new Vec3(box.Max.X, box.Max.Y, plane.Origin.Z)),
            plane.Project(new Vec3(box.Min.X, box.Max.Y, plane.Origin.Z))
        };
        return Polygon2D.Area(ring) <= Polygon2D.AreaTolerance
            ? new List<List<Point2>>()
            : new List<List<Point2>> { ring };
    }
}
