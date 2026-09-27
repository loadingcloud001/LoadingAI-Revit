using System.Collections.Generic;
using LoadingAI.Geometry;
using Xunit;

namespace LoadingAI.Tests;

/// <summary>
/// Level and grid splits ask where a member's centreline crosses a storey or a grid plane. These pin
/// that answer in Core so a cut cannot drift when the plugin tessellates a Revit curve.
/// </summary>
public class CurveCutTests
{
    [Fact]
    public void AtElevation_MidHeightColumn_IsHalf()
    {
        var samples = new[] { new Vec3(0, 0, 0), new Vec3(0, 0, 10) };
        var cuts = CurveCut.AtElevation(samples, 5);

        var cut = Assert.Single(cuts);
        Assert.Equal(0.5, cut, 9);
    }

    [Fact]
    public void AtElevation_LevelAboveMember_IsEmpty()
    {
        var samples = new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 0) };
        Assert.Empty(CurveCut.AtElevation(samples, 5));
    }

    [Fact]
    public void AtElevation_LevelOnAnEnd_IsDropped()
    {
        var samples = new[] { new Vec3(0, 0, 0), new Vec3(0, 0, 10) };
        Assert.Empty(CurveCut.AtElevation(samples, 0));
        Assert.Empty(CurveCut.AtElevation(samples, 10));
    }

    [Fact]
    public void AtElevation_NearEnd_IsDroppedByMinFraction()
    {
        var samples = new[] { new Vec3(0, 0, 0), new Vec3(0, 0, 100) };
        Assert.Empty(CurveCut.AtElevation(samples, 1, minFraction: 0.02));
        Assert.Empty(CurveCut.AtElevation(samples, 99, minFraction: 0.02));
    }

    [Fact]
    public void AtPlane_VerticalGridAcrossABeam_IsTheCrossing()
    {
        var samples = new[] { new Vec3(0, 0, 1), new Vec3(10, 0, 1) };
        var plane = new Plane2D(new Vec3(4, 0, 0), new Vec3(1, 0, 0));
        var cuts = CurveCut.AtPlane(samples, plane);

        var cut = Assert.Single(cuts);
        Assert.Equal(0.4, cut, 9);
    }

    [Fact]
    public void AtPlane_MemberParallelToGrid_IsEmpty()
    {
        var samples = new[] { new Vec3(0, 0, 0), new Vec3(0, 10, 0) };
        var plane = new Plane2D(new Vec3(4, 0, 0), new Vec3(1, 0, 0));
        Assert.Empty(CurveCut.AtPlane(samples, plane));
    }

    [Fact]
    public void AtPlane_TwoGrids_KeepGap()
    {
        var samples = new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 0) };
        var first = CurveCut.AtPlane(samples, new Plane2D(new Vec3(3, 0, 0), new Vec3(1, 0, 0)));
        var second = CurveCut.AtPlane(samples, new Plane2D(new Vec3(7, 0, 0), new Vec3(1, 0, 0)));

        Assert.Equal(0.3, Assert.Single(first), 9);
        Assert.Equal(0.7, Assert.Single(second), 9);
    }

    [Fact]
    public void TryCrossing_SameSign_IsNotAHit()
    {
        Assert.False(CurveCut.TryCrossing(1, 2, out _));
        Assert.False(CurveCut.TryCrossing(-1, -0.1, out _));
    }

    [Fact]
    public void TryCrossing_SegmentInPlane_IsNotAHit()
    {
        Assert.False(CurveCut.TryCrossing(0, 0, out _));
    }

    [Fact]
    public void TryCrossing_StartOnPlane_IsNotAHit()
    {
        Assert.False(CurveCut.TryCrossing(0, 4, out _));
    }

    [Fact]
    public void TryCrossing_SignChange_Interpolates()
    {
        Assert.True(CurveCut.TryCrossing(-2, 2, out var t));
        Assert.Equal(0.5, t, 9);
    }
}
