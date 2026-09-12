using LoadingAI.Plugins.OpeningTools;

namespace LoadingAI.Tests;

public class OpeningCombineTests
{
    static OpeningCombineMath.HostFace WideHost(double thickness = 200)
        => new(-2000, 2000, -1500, 1500, thickness);

    [Fact]
    public void NearbyRects_Merge()
    {
        // 200×200, centers 250 apart → gap 50 < max(200, 200, 150)
        var rects = new[]
        {
            new OpeningCombineMath.FaceRect(0, 0, 200, 200),
            new OpeningCombineMath.FaceRect(250, 0, 200, 200)
        };
        var groups = OpeningCombineMath.Group(rects, WideHost(), 150, 700);
        Assert.Single(groups);
        Assert.True(groups[0].Merged);
        Assert.Equal(2, groups[0].Indices.Length);
        Assert.Equal(450, groups[0].Envelope.W, 6);
        Assert.False(groups[0].Large);
    }

    [Fact]
    public void FarRects_StaySeparate()
    {
        var rects = new[]
        {
            new OpeningCombineMath.FaceRect(0, 0, 200, 200),
            new OpeningCombineMath.FaceRect(800, 0, 200, 200)
        };
        var groups = OpeningCombineMath.Group(rects, WideHost(), 150, 700);
        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.False(g.Merged));
    }

    [Fact]
    public void ThinEdgeWeb_AbortsMerge()
    {
        var host = new OpeningCombineMath.HostFace(-200, 1800, -1500, 1500, 200);
        // envelope UMin = -100; remaining to host UMin = 100 < max(150, 200)
        var rects = new[]
        {
            new OpeningCombineMath.FaceRect(0, 0, 200, 200),
            new OpeningCombineMath.FaceRect(250, 0, 200, 200)
        };
        var groups = OpeningCombineMath.Group(rects, host, 150, 700);
        Assert.Single(groups);
        Assert.False(groups[0].Merged);
        Assert.Equal(2, groups[0].Indices.Length);
    }

    [Fact]
    public void CombinedWiderThanTwoThirds_Aborts()
    {
        var host = new OpeningCombineMath.HostFace(0, 3000, -1000, 1000, 200);
        // gap 100 < 200 → cluster; envelope 2100 > 2/3 of 3000; remaining edge 450 ≥ 200
        var rects = new[]
        {
            new OpeningCombineMath.FaceRect(950, 0, 1000, 200),
            new OpeningCombineMath.FaceRect(2050, 0, 1000, 200)
        };
        var groups = OpeningCombineMath.Group(rects, host, 150, 700);
        Assert.Single(groups);
        Assert.False(groups[0].Merged);
        Assert.True(groups[0].Envelope.W > OpeningCombineMath.MaxHostFraction * host.LengthU);
    }

    [Fact]
    public void CircleEnvelopeOblong_IsNotNearlySquare()
    {
        Assert.False(OpeningCombineMath.IsNearlySquare(new OpeningCombineMath.FaceRect(0, 0, 400, 200)));
        Assert.True(OpeningCombineMath.IsNearlySquare(new OpeningCombineMath.FaceRect(0, 0, 200, 210)));
    }

    [Fact]
    public void ThreeNearby_MergeOneEnvelope()
    {
        var rects = new[]
        {
            new OpeningCombineMath.FaceRect(0, 0, 200, 200),
            new OpeningCombineMath.FaceRect(250, 0, 200, 200),
            new OpeningCombineMath.FaceRect(500, 0, 200, 200)
        };
        var groups = OpeningCombineMath.Group(rects, WideHost(), 150, 700);
        Assert.Single(groups);
        Assert.True(groups[0].Merged);
        Assert.Equal(3, groups[0].Indices.Length);
        Assert.Equal(700, groups[0].Envelope.W, 6);
        Assert.False(groups[0].Large);
    }

    [Fact]
    public void Overlapping_Merge()
    {
        var rects = new[]
        {
            new OpeningCombineMath.FaceRect(0, 0, 300, 200),
            new OpeningCombineMath.FaceRect(100, 0, 300, 200)
        };
        var groups = OpeningCombineMath.Group(rects, WideHost(), 150, 700);
        Assert.Single(groups);
        Assert.True(groups[0].Merged);
        Assert.Equal(400, groups[0].Envelope.W, 6);
    }

    [Fact]
    public void LargeWarn_WhenWidthOver700()
    {
        var rects = new[]
        {
            new OpeningCombineMath.FaceRect(0, 0, 400, 200),
            new OpeningCombineMath.FaceRect(450, 0, 400, 200)
        };
        var groups = OpeningCombineMath.Group(rects, WideHost(), 150, 700);
        Assert.Single(groups);
        Assert.True(groups[0].Merged);
        Assert.True(groups[0].Large);
        Assert.True(OpeningCombineMath.IsLarge(groups[0].Envelope, 700));
    }
}
