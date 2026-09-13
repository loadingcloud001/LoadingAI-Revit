using System;
using System.Collections.Generic;
using System.Linq;

namespace LoadingAI.Geometry;

/// <summary>
/// A planar area with holes - one floor slab piece: an outer loop plus the openings cut out of it.
/// Every loop is stored counter-clockwise; the Revit emitter reverses the holes when it creates them.
/// </summary>
public sealed class Region2D
{
    public Region2D(IReadOnlyList<Point2> outer, IEnumerable<IReadOnlyList<Point2>>? holes = null)
    {
        Outer = Polygon2D.EnsureCcw(outer);

        var list = new List<List<Point2>>();
        if (holes != null)
        {
            foreach (var hole in holes)
            {
                var cleaned = Polygon2D.EnsureCcw(hole);
                if (cleaned.Count >= 3 && Polygon2D.Area(cleaned) > Polygon2D.AreaTolerance)
                    list.Add(cleaned);
            }
        }

        Holes = list;
    }

    public List<Point2> Outer { get; }

    public IReadOnlyList<List<Point2>> Holes { get; }

    /// <summary>Outer area minus every hole.</summary>
    public double Area => Polygon2D.Area(Outer) - Holes.Sum(Polygon2D.Area);

    public bool IsValid => Polygon2D.Area(Outer) > Polygon2D.AreaTolerance;

    /// <summary>Adds another opening, useful when one beam clips several times.</summary>
    public Region2D WithHole(IReadOnlyList<Point2> hole)
    {
        var holes = new List<IReadOnlyList<Point2>>(Holes) { hole };
        return new Region2D(Outer, holes);
    }

    /// <summary>
    /// Splits the region along a cut polyline (a beam edge). Holes follow the half that contains them,
    /// and a hole straddling the cut is split with it. Returns <c>(null, null)</c> when the cut does
    /// not separate the outer loop; a half that collapses to nothing comes back as <c>null</c>.
    /// </summary>
    public (Region2D? Left, Region2D? Right) Split(IReadOnlyList<Point2> cut)
    {
        var (leftOuter, rightOuter) = Polygon2D.SplitByPolyline(Outer, cut);
        if (leftOuter == null || rightOuter == null)
            return (null, null);

        var leftHoles = new List<List<Point2>>();
        var rightHoles = new List<List<Point2>>();
        foreach (var hole in Holes)
            RouteHole(hole, cut, leftOuter, rightOuter, leftHoles, rightHoles);

        var left = new Region2D(leftOuter, leftHoles);
        var right = new Region2D(rightOuter, rightHoles);
        return (left.IsValid ? left : null, right.IsValid ? right : null);
    }

    private static void RouteHole(
        IReadOnlyList<Point2> hole,
        IReadOnlyList<Point2> cut,
        List<Point2> leftOuter,
        List<Point2> rightOuter,
        List<List<Point2>> leftHoles,
        List<List<Point2>> rightHoles)
    {
        var (leftPiece, rightPiece) = Polygon2D.SplitByPolyline(hole, cut);
        if (leftPiece != null && rightPiece != null)
        {
            Place(leftPiece, leftOuter, rightOuter, leftHoles, rightHoles);
            Place(rightPiece, leftOuter, rightOuter, leftHoles, rightHoles);
            return;
        }

        // The cut only grazes the hole: keep the whole opening with whichever half holds it.
        Place(hole, leftOuter, rightOuter, leftHoles, rightHoles);
    }

    private static void Place(
        IReadOnlyList<Point2> piece,
        List<Point2> leftOuter,
        List<Point2> rightOuter,
        List<List<Point2>> leftHoles,
        List<List<Point2>> rightHoles)
    {
        var centroid = Polygon2D.Centroid(piece);
        if (Polygon2D.PointInPolygon(centroid, leftOuter))
        {
            leftHoles.Add(Polygon2D.EnsureCcw(piece));
            return;
        }

        if (Polygon2D.PointInPolygon(centroid, rightOuter))
            rightHoles.Add(Polygon2D.EnsureCcw(piece));
    }
}
