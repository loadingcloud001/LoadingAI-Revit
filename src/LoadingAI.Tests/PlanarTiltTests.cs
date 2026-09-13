using System;
using LoadingAI.Geometry;
using Xunit;

namespace LoadingAI.Tests;

/// <summary>
/// Covers the lean of a slab's top face: the uphill direction the slope arrow points along and the angle
/// Revit is asked to build the slab at, plus the self-check the writer runs on what it created.
/// </summary>
public class PlanarTiltTests
{
    private const double Degrees = Math.PI / 180.0;

    [Fact]
    public void TryCreate_LevelFace_HasNoUphillDirectionAndNoSlope()
    {
        Assert.True(PlanarTilt.TryCreate(new Vec3(0, 0, 1), out var tilt));

        Assert.Equal(0.0, tilt.Slope, 9);
        Assert.Equal(0.0, tilt.Uphill.Length, 9);
        Assert.True(tilt.IsHorizontal(0.9999));
        Assert.Equal(1.0, tilt.Normal.Z, 9);
    }

    [Fact]
    public void TryCreate_LeaningFaceUphillIsTheNormalsFlatPartReversed()
    {
        // A face leaning so that it rises towards +X: its normal tips towards -X.
        var normal = new Vec3(-Math.Sin(10 * Degrees), 0, Math.Cos(10 * Degrees));
        Assert.True(PlanarTilt.TryCreate(normal, out var tilt));

        Assert.Equal(10.0, tilt.Slope / Degrees, 9);
        Assert.Equal(1.0, tilt.Uphill.X, 9);
        Assert.Equal(0.0, tilt.Uphill.Y, 9);
        Assert.Equal(0.0, tilt.Uphill.Z, 9);
        Assert.False(tilt.IsHorizontal(0.9999));
    }

    [Fact]
    public void TryCreate_UphillIsTheFlatPartOfTheNormalReversed()
    {
        // Whatever way the face leans, the arrow lies flat, points where the face climbs, and is a unit
        // vector - Revit throws if the slope arrow is anything but a horizontal line.
        var normal = new Vec3(-0.3, 0.4, 0.866).Normalized();
        Assert.True(PlanarTilt.TryCreate(normal, out var tilt));

        Assert.Equal(0.0, tilt.Uphill.Z, 9);
        Assert.Equal(1.0, tilt.Uphill.Length, 9);
        Assert.Equal(0.6, tilt.Uphill.X, 9);
        Assert.Equal(-0.8, tilt.Uphill.Y, 9);
    }

    [Theory]
    [InlineData(0, 0, -1)]
    [InlineData(0.5, 0.5, 0)]
    [InlineData(0, 0, 0)]
    public void TryCreate_AnythingButAnUpwardFacingTop_ReportsFalse(double x, double y, double z)
    {
        Assert.False(PlanarTilt.TryCreate(new Vec3(x, y, z), out _));
    }

    [Fact]
    public void TryCreate_SteeperThanAStair_StillReportsTheAngle()
    {
        // 40 degrees is far past what a slab is built at, but it is still a slope arrow Revit can take.
        var normal = new Vec3(-Math.Sin(40 * Degrees), 0, Math.Cos(40 * Degrees));
        Assert.True(PlanarTilt.TryCreate(normal, out var tilt));

        Assert.Equal(40.0, tilt.Slope / Degrees, 9);
    }

    [Fact]
    public void Matches_AcceptsTheSameLeanAndRejectsAnother()
    {
        Assert.True(PlanarTilt.TryCreate(new Vec3(-Math.Sin(5 * Degrees), 0, Math.Cos(5 * Degrees)), out var wanted));

        Assert.True(PlanarTilt.TryCreate(new Vec3(-Math.Sin(5.02 * Degrees), 0, Math.Cos(5.02 * Degrees)), out var close));
        Assert.True(wanted.Matches(close.Normal, 0.5 * Degrees));

        Assert.True(PlanarTilt.TryCreate(new Vec3(0, -Math.Sin(5 * Degrees), Math.Cos(5 * Degrees)), out var sideways));
        Assert.False(wanted.Matches(sideways.Normal, 0.5 * Degrees));

        Assert.True(PlanarTilt.TryCreate(new Vec3(-Math.Sin(20 * Degrees), 0, Math.Cos(20 * Degrees)), out var steeper));
        Assert.False(wanted.Matches(steeper.Normal, 0.5 * Degrees));
    }

    [Fact]
    public void Matches_TreatsAnUndersideAsADifferentFace()
    {
        var normal = new Vec3(-Math.Sin(5 * Degrees), 0, Math.Cos(5 * Degrees));
        Assert.True(PlanarTilt.TryCreate(normal, out var tilt));

        Assert.False(tilt.Matches(-normal, 0.5 * Degrees));
        Assert.False(tilt.Matches(new Vec3(0, 0, 0), 180 * Degrees));
    }

    [Fact]
    public void IsHorizontal_UsesTheSameNormalHeightTheSlabReaderDoes()
    {
        Assert.True(PlanarTilt.TryCreate(new Vec3(0, 0, 1), out var flat));
        Assert.True(flat.IsHorizontal(0.9999));

        // Just under a degree of lean: still inside what the reader calls horizontal.
        Assert.True(PlanarTilt.TryCreate(new Vec3(-Math.Sin(0.5 * Degrees), 0, Math.Cos(0.5 * Degrees)), out var nearly));
        Assert.True(nearly.IsHorizontal(0.9999));

        Assert.True(PlanarTilt.TryCreate(new Vec3(-Math.Sin(2 * Degrees), 0, Math.Cos(2 * Degrees)), out var leaning));
        Assert.False(leaning.IsHorizontal(0.9999));
    }

    [Fact]
    public void ZAt_ReadsTheFaceHeightOverAPlanPosition()
    {
        // A face climbing towards +X at 10 degrees, passing through (0, 0, 10): one foot along it is a
        // little over two inches higher.
        var normal = new Vec3(-Math.Sin(10 * Degrees), 0, Math.Cos(10 * Degrees));
        Assert.True(PlanarTilt.TryCreate(normal, new Vec3(0, 0, 10), out var tilt));

        Assert.Equal(10.0, tilt.ZAt(0, 0), 9);
        Assert.Equal(10.0 + Math.Tan(10 * Degrees), tilt.ZAt(1, 0), 9);
        Assert.Equal(10.0, tilt.ZAt(0, 5), 9);

        // The reverse direction climbs the other way.
        Assert.Equal(10.0 - Math.Tan(10 * Degrees), tilt.ZAt(-1, 0), 9);
    }

    [Fact]
    public void ZAt_OnALevelFaceIsJustItsHeight()
    {
        Assert.True(PlanarTilt.TryCreate(new Vec3(0, 0, 1), new Vec3(0, 0, 12.5), out var tilt));

        Assert.Equal(12.5, tilt.ZAt(0, 0), 9);
        Assert.Equal(12.5, tilt.ZAt(-40, 17), 9);
    }

    [Fact]
    public void TryCreate_WithoutAFacePointStillReportsTheLean()
    {
        // The lean of a face and where the face sits are separate questions, so the normal on its own is
        // enough when only the angle is wanted.
        Assert.True(PlanarTilt.TryCreate(new Vec3(-Math.Sin(10 * Degrees), 0, Math.Cos(10 * Degrees)), out var tilt));

        Assert.Equal(10.0, tilt.Slope / Degrees, 9);
        Assert.Equal(0.0, tilt.Through.Length, 9);
    }
}
