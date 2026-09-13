using System.Collections.Generic;
using System.Linq;
using LoadingAI.Geometry;
using Xunit;

namespace LoadingAI.Tests;

/// <summary>
/// Covers the Revit-free geometry the structure-cuts plugin relies on to split slabs and beams. These
/// are the only checks that can run without a Revit model, so they pin the split behaviour precisely.
/// </summary>
public class Polygon2DTests
{
    private static List<Point2> Square() => new()
    {
        new Point2(0, 0), new Point2(10, 0), new Point2(10, 10), new Point2(0, 10),
    };

    /// <summary>L-shaped slab: the notch is the top-right corner, cut out of a 10x10 square.</summary>
    private static List<Point2> ConcaveL() => new()
    {
        new Point2(0, 0), new Point2(10, 0), new Point2(10, 4),
        new Point2(4, 4), new Point2(4, 10), new Point2(0, 10),
    };

    private static List<Point2> Hole() => new()
    {
        new Point2(4, 4), new Point2(6, 4), new Point2(6, 6), new Point2(4, 6),
    };

    private static List<Point2> SmallHole() => new()
    {
        new Point2(4, 4), new Point2(5, 4), new Point2(5, 5), new Point2(4, 5),
    };

    [Fact]
    public void Vec3_LengthNormalizeAndCrossFollowTheRightHandRule()
    {
        var x = new Vec3(1, 0, 0);
        var y = new Vec3(0, 1, 0);

        Assert.Equal(1.0, x.Length, 9);
        Assert.Equal(1.0, Vec3.Cross(x, y).Z, 9);
        Assert.Equal(0.0, Vec3.Dot(x, y), 9);
        Assert.Equal(5.0, new Vec3(0, 3, 4).Length, 9);
        Assert.Equal(1.0, new Vec3(0, 3, 4).Normalized().Length, 9);

        var sum = new Vec3(1, 1, 1) + new Vec3(2, 0, -1);
        Assert.Equal(new Vec3(3, 1, 0), sum);
        Assert.Equal(new Vec3(5, 0, 0), new Vec3(1, 0, 0) * 5);
        Assert.Equal(1.0, new Vec3(1, 0, 0).DistanceTo(new Vec3(2, 0, 0)), 9);
    }

    [Fact]
    public void Point2_LerpWalksTheSegment()
    {
        var a = new Point2(0, 0);
        var b = new Point2(10, 20);

        Assert.Equal(5.0, Point2.Lerp(a, b, 0.5).X, 9);
        Assert.Equal(10.0, Point2.Lerp(a, b, 0.5).Y, 9);
        Assert.Equal(0.0, Point2.Lerp(a, b, 0).X, 9);
        Assert.Equal(10.0, Point2.Lerp(a, b, 1).X, 9);
        Assert.Equal(5.0, a.DistanceTo(new Point2(3, 4)), 9);
    }

    [Fact]
    public void SignedArea_IsPositiveForCounterClockwise()
    {
        Assert.Equal(100.0, Polygon2D.SignedArea(Square()), 6);
        Assert.Equal(-100.0, Polygon2D.SignedArea(Polygon2D.Reverse(Square())), 6);
        Assert.True(Polygon2D.IsCcw(Square()));
        Assert.False(Polygon2D.IsCcw(Polygon2D.Reverse(Square())));
        Assert.Equal(100.0, Polygon2D.Area(Polygon2D.Reverse(Square())), 6);
    }

    [Fact]
    public void EnsureCcwAndCw_NormaliseOrientation()
    {
        var clockwise = Polygon2D.Reverse(Square());

        Assert.True(Polygon2D.IsCcw(Polygon2D.EnsureCcw(clockwise)));
        Assert.False(Polygon2D.IsCcw(Polygon2D.EnsureCw(Square())));
        Assert.True(Polygon2D.IsCcw(Polygon2D.EnsureCcw(Square())));

        // Already-correct input is returned unchanged, not rebuilt in place.
        Assert.Equal(Square(), Polygon2D.EnsureCcw(Square()));
    }

    [Fact]
    public void Clean_DropsDuplicateAndClosingVertices()
    {
        var messy = new List<Point2>
        {
            new Point2(0, 0), new Point2(0, 0), new Point2(10, 0),
            new Point2(10, 10), new Point2(0, 10), new Point2(0, 0),
        };

        var cleaned = Polygon2D.Clean(messy);

        Assert.Equal(4, cleaned.Count);
        Assert.Equal(100.0, Polygon2D.Area(cleaned), 6);
        Assert.Empty(Polygon2D.Clean(null));

        // Repeated identical points collapse to a single vertex.
        Assert.Single(Polygon2D.Clean(new List<Point2> { new Point2(2, 2), new Point2(2, 2) }));
    }

    [Fact]
    public void Centroid_OfRectangle_IsItsCentre()
    {
        var centroid = Polygon2D.Centroid(Square());

        Assert.Equal(5.0, centroid.X, 6);
        Assert.Equal(5.0, centroid.Y, 6);
    }

    [Fact]
    public void Centroid_OfConcavePolygon_IsAreaWeighted()
    {
        // 36 area of the arm plus 28 of the leg, so the centroid sits off the plain box centre.
        var centroid = Polygon2D.Centroid(ConcaveL());

        Assert.Equal(3.875, centroid.X, 6);
        Assert.Equal(3.875, centroid.Y, 6);
        Assert.NotEqual(5.0, centroid.X, 3);
    }

    [Fact]
    public void Centroid_OfDegeneratePolygon_FallsBackToVertexAverage()
    {
        var collinear = new List<Point2>
        {
            new Point2(0, 0), new Point2(4, 0), new Point2(8, 0),
        };

        Assert.Equal(0.0, Polygon2D.SignedArea(collinear), 9);
        Assert.Equal(4.0, Polygon2D.Centroid(collinear).X, 6);
        Assert.Equal(0.0, Polygon2D.Centroid(new List<Point2>()).X, 6);
    }

    [Fact]
    public void PointInPolygon_RespectsConcaveNotch()
    {
        var l = ConcaveL();

        Assert.True(Polygon2D.PointInPolygon(new Point2(8, 2), l));
        Assert.True(Polygon2D.PointInPolygon(new Point2(1, 8), l));

        // The notch is outside even though it is inside the bounding rectangle.
        Assert.False(Polygon2D.PointInPolygon(new Point2(8, 8), l));
        Assert.False(Polygon2D.PointInPolygon(new Point2(5, 5), l));
        Assert.False(Polygon2D.PointInPolygon(new Point2(-1, 5), l));
    }

    [Fact]
    public void Largest_PicksTheBiggestPolygon()
    {
        var small = new List<Point2>
        {
            new Point2(0, 0), new Point2(1, 0), new Point2(1, 1), new Point2(0, 1),
        };

        var largest = Polygon2D.Largest(new List<IReadOnlyList<Point2>> { small, Square(), ConcaveL() });

        Assert.NotNull(largest);
        Assert.Equal(100.0, Polygon2D.Area(largest!), 6);
        Assert.Null(Polygon2D.Largest(null));
        Assert.Null(Polygon2D.Largest(new List<IReadOnlyList<Point2>>()));
    }

    [Fact]
    public void SplitByPolyline_StraightCutSplitsSquareInHalf()
    {
        var cut = new List<Point2> { new Point2(5, -1), new Point2(5, 11) };

        var (left, right) = Polygon2D.SplitByPolyline(Square(), cut);

        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal(50.0, Polygon2D.Area(left!), 6);
        Assert.Equal(50.0, Polygon2D.Area(right!), 6);
        Assert.True(Polygon2D.IsCcw(left!));
        Assert.True(Polygon2D.IsCcw(right!));

        // The cut line itself is the new shared edge of the two halves.
        Assert.Contains(left!, p => p.X == 5 && p.Y == 0);
        Assert.Contains(left!, p => p.X == 5 && p.Y == 10);
        Assert.Contains(right!, p => p.X == 5 && p.Y == 0);
        Assert.Contains(right!, p => p.X == 5 && p.Y == 10);
    }

    [Fact]
    public void SplitByPolyline_ReturnsNothingWhenTheCutDoesNotSeparate()
    {
        var square = Square();

        // Clear miss.
        var (missLeft, missRight) = Polygon2D.SplitByPolyline(square, new List<Point2>
        {
            new Point2(20, -1), new Point2(20, 11),
        });
        Assert.Null(missLeft);
        Assert.Null(missRight);

        // Tangential, one crossing only.
        var (touchLeft, touchRight) = Polygon2D.SplitByPolyline(square, new List<Point2>
        {
            new Point2(5, 10), new Point2(5, 20),
        });
        Assert.Null(touchLeft);
        Assert.Null(touchRight);

        // Degenerate and empty cut lists never split.
        Assert.Null(Polygon2D.SplitByPolyline(square, new List<Point2> { new Point2(5, 5) }).Left);
        Assert.Null(Polygon2D.SplitByPolyline(square, new List<Point2>()).Right);
    }

    [Fact]
    public void SplitByPolyline_ConservesAreaForConcavePolygon()
    {
        var cut = new List<Point2> { new Point2(2, -1), new Point2(2, 11) };

        var (left, right) = Polygon2D.SplitByPolyline(ConcaveL(), cut);

        Assert.NotNull(left);
        Assert.NotNull(right);

        // The cut runs up the left of the notch, so the left half is the narrow 2x10 strip.
        Assert.Equal(20.0, Polygon2D.Area(left!), 6);
        Assert.Equal(44.0, Polygon2D.Area(right!), 6);
        Assert.Equal(64.0, Polygon2D.Area(left!) + Polygon2D.Area(right!), 6);
        Assert.Equal(64.0, Polygon2D.Area(ConcaveL()), 6);
    }

    [Fact]
    public void SplitByPolyline_UsesBothSegmentsOfABentCut()
    {
        // An L-shaped cut: up the middle, then off to the right - the shape of two beam edges meeting.
        var cut = new List<Point2>
        {
            new Point2(2, -1), new Point2(2, 2), new Point2(11, 2),
        };

        var (left, right) = Polygon2D.SplitByPolyline(Square(), cut);

        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal(84.0, Polygon2D.Area(left!), 6);
        Assert.Equal(16.0, Polygon2D.Area(right!), 6);
        Assert.Equal(100.0, Polygon2D.Area(left!) + Polygon2D.Area(right!), 6);

        // The interior cut vertex is part of both halves.
        Assert.Contains(left!, p => p.X == 2 && p.Y == 2);
        Assert.Contains(right!, p => p.X == 2 && p.Y == 2);
    }

    [Fact]
    public void Region2D_AreaSubtractsHoles()
    {
        var region = new Region2D(Square(), new List<IReadOnlyList<Point2>> { Hole() });

        Assert.Equal(96.0, region.Area, 6);
        Assert.Single(region.Holes);
        Assert.True(region.IsValid);

        var withAnother = region.WithHole(SmallHole());
        Assert.Equal(2, withAnother.Holes.Count);

        // A hole with no area at all is dropped rather than carried around.
        var degenerate = new Region2D(Square(), new List<IReadOnlyList<Point2>>
        {
            new List<Point2> { new Point2(3, 3), new Point2(3, 3), new Point2(3, 3) },
        });
        Assert.Empty(degenerate.Holes);
    }

    [Fact]
    public void Region2D_IsInvalidWhenItsOuterLoopHasNoArea()
    {
        var sliver = new Region2D(new List<Point2>
        {
            new Point2(0, 0), new Point2(1e-6, 0), new Point2(0, 1e-6),
        });

        Assert.False(sliver.IsValid);
        Assert.True(sliver.Area < 1e-9);
    }

    [Fact]
    public void Region2D_SplitAssignsHoleToTheContainingHalf()
    {
        var region = new Region2D(Square(), new List<IReadOnlyList<Point2>> { SmallHole() });

        // The cut passes clear of the small hole, so the opening belongs entirely to one half.
        var (left, right) = region.Split(new List<Point2> { new Point2(7, -1), new Point2(7, 11) });

        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal(70.0, Polygon2D.Area(left!.Outer), 6);
        Assert.Equal(30.0, Polygon2D.Area(right!.Outer), 6);
        Assert.Single(left.Holes);
        Assert.Empty(right.Holes);
        Assert.Equal(69.0, left.Area, 6);
        Assert.Equal(30.0, right.Area, 6);
    }

    [Fact]
    public void Region2D_SplitSplitsAHoleStraddlingTheCut()
    {
        var region = new Region2D(Square(), new List<IReadOnlyList<Point2>> { Hole() });

        var (left, right) = region.Split(new List<Point2> { new Point2(5, -1), new Point2(5, 11) });

        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Single(left!.Holes);
        Assert.Single(right!.Holes);
        Assert.Equal(2.0, Polygon2D.Area(left.Holes[0]), 6);
        Assert.Equal(2.0, Polygon2D.Area(right.Holes[0]), 6);
        Assert.Equal(48.0, left.Area, 6);
        Assert.Equal(48.0, right.Area, 6);
        Assert.Equal(96.0, left.Area + right.Area, 6);
    }

    [Fact]
    public void Region2D_SplitReturnsNothingWhenTheCutDoesNotSeparate()
    {
        var region = new Region2D(Square(), new List<IReadOnlyList<Point2>> { Hole() });

        var (left, right) = region.Split(new List<Point2> { new Point2(20, -1), new Point2(20, 11) });

        Assert.Null(left);
        Assert.Null(right);
    }

    [Fact]
    public void Region2D_SplitByPolylineKeepsHolePiecesOnBothSides()
    {
        var region = new Region2D(Square(), new List<IReadOnlyList<Point2>> { Hole() });

        // Rounds a corner: the cut turns inside the opening, so the hole is clipped by both segments.
        var (left, right) = region.Split(new List<Point2>
        {
            new Point2(-1, 5), new Point2(5, 5), new Point2(5, 11),
        });

        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal(25.0, Polygon2D.Area(left!.Outer), 6);
        Assert.Equal(75.0, Polygon2D.Area(right!.Outer), 6);
        Assert.Single(left.Holes);
        Assert.Single(right.Holes);
        Assert.Equal(1.0, Polygon2D.Area(left.Holes[0]), 6);
        Assert.Equal(3.0, Polygon2D.Area(right.Holes[0]), 6);
        Assert.Equal(24.0, left.Area, 6);
        Assert.Equal(72.0, right.Area, 6);
    }

    [Fact]
    public void Plane2D_BasisIsRightHandedAndRoundTrips()
    {
        var plane = new Plane2D(new Vec3(0, 0, 10), new Vec3(0, 0, 1));

        Assert.Equal(0.0, plane.Normal.Z - 1, 9);
        Assert.Equal(1.0, plane.U.Length, 9);
        Assert.Equal(1.0, plane.V.Length, 9);
        Assert.Equal(0.0, Vec3.Dot(plane.U, plane.V), 9);
        Assert.Equal(0.0, Vec3.Dot(plane.U, plane.Normal), 9);

        // Right handed basis: a counter-clockwise polygon stays counter-clockwise in 3D.
        var cross = Vec3.Cross(plane.U, plane.V);
        Assert.Equal(plane.Normal.X, cross.X, 9);
        Assert.Equal(plane.Normal.Y, cross.Y, 9);
        Assert.Equal(plane.Normal.Z, cross.Z, 9);

        var point = new Point2(3, -4);
        var back = plane.Project(plane.ToPlane(point));
        Assert.Equal(point.X, back.X, 9);
        Assert.Equal(point.Y, back.Y, 9);
        Assert.Equal(10.0, plane.ToPlane(point).Z, 9);
        Assert.Equal(0.0, plane.SignedDistance(plane.ToPlane(point)), 9);
        Assert.Equal(5.0, plane.SignedDistance(plane.ToPlane(point) + plane.Normal * 5), 9);
    }

    [Fact]
    public void Plane2D_SlopedPlanePreservesOrientationAndDistance()
    {
        var faceNormal = new Vec3(0, 0.5, 0.8660254037844386);
        var plane = new Plane2D(new Vec3(0, 0, 10), faceNormal);

        Assert.Equal(0.0, plane.Normal.X, 9);
        Assert.Equal(faceNormal.Y, plane.Normal.Y, 9);
        Assert.Equal(faceNormal.Z, plane.Normal.Z, 9);

        // Projecting a square and reading it back must not distort it.
        var corners = new List<Point2>
        {
            new Point2(0, 0), new Point2(4, 0), new Point2(4, 3), new Point2(0, 3),
        };
        var projected = corners.Select(plane.ToPlane).Select(plane.Project).ToList();

        for (var i = 0; i < corners.Count; i++)
        {
            Assert.Equal(corners[i].X, projected[i].X, 9);
            Assert.Equal(corners[i].Y, projected[i].Y, 9);
        }

        var slopedPoint = plane.ToPlane(new Point2(3, 3));
        Assert.Equal(-3.0, slopedPoint.X, 9);
        Assert.Equal(11.5, slopedPoint.Z, 6);
        Assert.Equal(3.0, plane.SignedDistance(slopedPoint + faceNormal * 3), 6);
    }

    [Fact]
    public void OffsetPolyline_ShiftsAStraightRunToTheLeft()
    {
        var line = new List<Point2> { new Point2(0, 0), new Point2(10, 0) };

        var left = Polygon2D.OffsetPolyline(line, 2);
        Assert.Equal(0.0, left[0].X, 9);
        Assert.Equal(2.0, left[0].Y, 9);
        Assert.Equal(10.0, left[1].X, 9);
        Assert.Equal(2.0, left[1].Y, 9);

        var right = Polygon2D.OffsetPolyline(line, -2);
        Assert.Equal(-2.0, right[0].Y, 9);
        Assert.Equal(-2.0, right[1].Y, 9);
    }

    [Fact]
    public void OffsetPolyline_MitresTheCornerOfABentRun()
    {
        // A right angle turning from +X into +Y: the inside vertex stays 2 away from both legs, the
        // outside corner is pushed out to its mitre point.
        var bent = new List<Point2> { new Point2(0, 0), new Point2(10, 0), new Point2(10, 10) };

        var left = Polygon2D.OffsetPolyline(bent, 2);
        Assert.Equal(0.0, left[0].X, 9);
        Assert.Equal(2.0, left[0].Y, 9);
        Assert.Equal(8.0, left[1].X, 9);
        Assert.Equal(2.0, left[1].Y, 9);
        Assert.Equal(8.0, left[2].X, 9);
        Assert.Equal(10.0, left[2].Y, 9);

        var right = Polygon2D.OffsetPolyline(bent, -2);
        Assert.Equal(12.0, right[1].X, 9);
        Assert.Equal(-2.0, right[1].Y, 9);

        // Offsetting back the other way must undo the first offset vertex for vertex.
        var back = Polygon2D.OffsetPolyline(left, -2);
        Assert.Equal(10.0, back[1].X, 6);
        Assert.Equal(0.0, back[1].Y, 6);
    }

    [Fact]
    public void OffsetPolyline_SurvivesDegenerateInput()
    {
        var single = new List<Point2> { new Point2(3, 4) };
        Assert.Single(Polygon2D.OffsetPolyline(single, 1));

        // A fold that doubles straight back: the reversal falls back to a plain sideways step instead of
        // dividing by a vanishing mitre length. (A two-legged there-and-back is not the case to test —
        // Clean treats its repeated endpoint as a closing vertex and folds it back to a straight run.)
        var folded = new List<Point2> { new Point2(0, 0), new Point2(5, 0), new Point2(2, 0) };
        var offset = Polygon2D.OffsetPolyline(folded, 2);
        Assert.Equal(3, offset.Count);
        Assert.Equal(0.0, offset[0].X, 9);
        Assert.Equal(2.0, offset[0].Y, 9);
        Assert.Equal(5.0, offset[1].X, 9);
        Assert.Equal(-2.0, offset[1].Y, 9);
        Assert.Equal(2.0, offset[2].X, 9);
        Assert.Equal(-2.0, offset[2].Y, 9);
    }

    [Fact]
    public void ClipPolylineToPolygon_KeepsTheInsideStretchOnly()
    {
        var square = Square();
        var shorter = new List<Point2> { new Point2(-5, 5), new Point2(15, 5) };

        var pieces = Polygon2D.ClipPolylineToPolygon(square, shorter);
        var piece = Assert.Single(pieces);
        Assert.Equal(2, piece.Count);
        Assert.Equal(0.0, piece[0].X, 9);
        Assert.Equal(10.0, piece[1].X, 9);
        Assert.Equal(5.0, piece[0].Y, 9);
    }

    [Fact]
    public void ClipPolylineToPolygon_ReturnsEachSeparateCrossing()
    {
        // A U shaped polygon: a line across the two legs comes out as two separate cuts.
        var u = new List<Point2>
        {
            new Point2(0, 0), new Point2(10, 0), new Point2(10, 10), new Point2(8, 10),
            new Point2(8, 2), new Point2(2, 2), new Point2(2, 10), new Point2(0, 10),
        };

        var pieces = Polygon2D.ClipPolylineToPolygon(u, new List<Point2> { new Point2(-1, 5), new Point2(11, 5) });
        Assert.Equal(2, pieces.Count);
        Assert.Equal(0.0, pieces[0][0].X, 9);
        Assert.Equal(2.0, pieces[0][1].X, 9);
        Assert.Equal(8.0, pieces[1][0].X, 9);
        Assert.Equal(10.0, pieces[1][1].X, 9);
        Assert.Equal(5.0, pieces[0][0].Y, 9);
    }

    [Fact]
    public void ClipPolylineToPolygon_PassesThroughAFullyInsidePolyline()
    {
        var square = Square();
        var inside = new List<Point2> { new Point2(2, 2), new Point2(8, 8) };

        var piece = Assert.Single(Polygon2D.ClipPolylineToPolygon(square, inside));
        Assert.Equal(2.0, piece[0].X, 9);
        Assert.Equal(8.0, piece[1].Y, 9);
    }

    [Fact]
    public void ClipPolylineToPolygon_KeepsTheInnerVerticesOfABentCut()
    {
        var square = Square();
        var bent = new List<Point2> { new Point2(-5, 5), new Point2(5, 5), new Point2(5, 15), new Point2(-5, 15) };

        var piece = Assert.Single(Polygon2D.ClipPolylineToPolygon(square, bent));
        Assert.Equal(3, piece.Count);
        Assert.Equal(0.0, piece[0].X, 9);
        Assert.Equal(5.0, piece[0].Y, 9);
        Assert.Equal(5.0, piece[1].X, 9);
        Assert.Equal(5.0, piece[1].Y, 9);
        Assert.Equal(5.0, piece[2].X, 9);
        Assert.Equal(10.0, piece[2].Y, 9);
    }

    [Fact]
    public void ClipPolylineToPolygon_DropsACutThatMissesOrGrazes()
    {
        var square = Square();

        Assert.Empty(Polygon2D.ClipPolylineToPolygon(square, new List<Point2> { new Point2(20, 0), new Point2(20, 10) }));
        Assert.Empty(Polygon2D.ClipPolylineToPolygon(square, new List<Point2> { new Point2(0, 0), new Point2(10, 0) }));
        Assert.Empty(Polygon2D.ClipPolylineToPolygon(square, new List<Point2> { new Point2(0, 0), new Point2(0, 0) }));
    }

    [Fact]
    public void ClipPolylineToPolygon_SplitsWithTheClippedCut()
    {
        // The end to end behaviour the floor tool relies on: a beam edge clipped to the piece before
        // splitting leaves two complementary halves.
        var square = Square();
        var clipped = Polygon2D.ClipPolylineToPolygon(square, new List<Point2> { new Point2(4, -3), new Point2(4, 13) });

        var (left, right) = Polygon2D.SplitByPolyline(square, Assert.Single(clipped));
        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal(40.0, Polygon2D.Area(left!), 6);
        Assert.Equal(60.0, Polygon2D.Area(right!), 6);
    }

    [Fact]
    public void ExtendEnds_PushesBothEndsOutwardAndSplitsRobustly()
    {
        var square = Square();
        var cut = new List<Point2> { new Point2(4, 0), new Point2(4, 10) };

        var extended = Polygon2D.ExtendEnds(cut, 1.0);
        Assert.Equal(new Point2(4, -1), extended[0]);
        Assert.Equal(new Point2(4, 11), extended[1]);

        // The ends now genuinely cross the boundary, so the crossing count is stable.
        var (left, right) = Polygon2D.SplitByPolyline(square, extended);
        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal(40.0, Polygon2D.Area(left!), 6);
        Assert.Equal(60.0, Polygon2D.Area(right!), 6);
    }

    [Fact]
    public void ExtendEnds_LeavesShortOrDegenerateInputAlone()
    {
        var single = new List<Point2> { new Point2(1, 1) };
        Assert.Single(Polygon2D.ExtendEnds(single, 1.0));
        Assert.Equal(single[0], Polygon2D.ExtendEnds(single, 1.0)[0]);

        var point = new List<Point2> { new Point2(2, 2), new Point2(2, 2) };
        var unchanged = Polygon2D.ExtendEnds(point, 1.0);
        Assert.Equal(2, unchanged.Count);
        Assert.Equal(new Point2(2, 2), unchanged[0]);
        Assert.Equal(new Point2(2, 2), unchanged[1]);

        Assert.Empty(Polygon2D.ExtendEnds(null!, 1.0));
    }

    [Fact]
    public void ParameterAlong_FindsTheStretchAPointBelongsTo()
    {
        // A three segment run: the parameter is the segment index plus the position within it.
        var run = new List<Point2> { new Point2(0, 0), new Point2(10, 0), new Point2(10, 10), new Point2(0, 10) };

        Assert.Equal(0.0, Polygon2D.ParameterAlong(run, new Point2(0, 0)), 9);
        Assert.Equal(1.0 / 3.0, Polygon2D.ParameterAlong(run, new Point2(10, 0)), 9);
        Assert.Equal(2.0 / 3.0, Polygon2D.ParameterAlong(run, new Point2(10, 10)), 9);
        Assert.Equal(0.5, Polygon2D.ParameterAlong(run, new Point2(10, 5)), 9);
        Assert.Equal(1.0, Polygon2D.ParameterAlong(run, new Point2(0, 10)), 9);

        // A point off to the side reports the perpendicular foot, which is what makes a footprint
        // edge sitting slightly off the centreline still map onto it.
        Assert.Equal(0.5, Polygon2D.ParameterAlong(run, new Point2(15, 5)), 9);
        Assert.Equal(1.0 / 6.0, Polygon2D.ParameterAlong(run, new Point2(5, -2)), 9);
    }

    [Fact]
    public void TryCross_FindsACrossingAndRejectsFlushOrDistantSegments()
    {
        Assert.True(Polygon2D.TryCross(new Point2(0, 0), new Point2(10, 10), new Point2(0, 10), new Point2(10, 0), out var crossing));
        Assert.Equal(new Point2(5, 5), crossing);

        // Touching at a shared end counts, an overlap along the same line (parallel) does not.
        Assert.True(Polygon2D.TryCross(new Point2(0, 0), new Point2(4, 0), new Point2(4, 0), new Point2(4, 5), out var corner));
        Assert.Equal(new Point2(4, 0), corner);
        Assert.False(Polygon2D.TryCross(new Point2(0, 0), new Point2(4, 0), new Point2(2, 0), new Point2(6, 0), out _));

        // Parallel, and lines whose infinite extensions would cross but whose segments do not.
        Assert.False(Polygon2D.TryCross(new Point2(0, 0), new Point2(4, 0), new Point2(0, 1), new Point2(4, 1), out _));
        Assert.False(Polygon2D.TryCross(new Point2(0, 0), new Point2(4, 0), new Point2(6, -1), new Point2(6, 1), out _));
        Assert.False(Polygon2D.TryCross(new Point2(0, 0), new Point2(1, 0), new Point2(2, 2), new Point2(3, 2), out _));
    }
}
