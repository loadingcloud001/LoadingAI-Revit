using System;
using System.Collections.Generic;
using LoadingAI.Geometry;
using Xunit;

namespace LoadingAI.Tests;

/// <summary>
/// Pins the locked BIM rules for split_secondary_beams_by_main_beams: type-depth
/// ranking, occupancy, two outer pieces with no middle stub, and min-gap from the
/// crossing run rather than the target length.
/// </summary>
public class SecondaryByMainTests
{
    [Fact]
    public void DepthOrder_DeeperCutsShallower_IsAntisymmetric()
    {
        var aCutsB = SecondaryByMain.DecideOrder(400, "UB406", 200, "UB203");
        var bCutsA = SecondaryByMain.DecideOrder(200, "UB203", 400, "UB406");

        Assert.Equal(DepthOrderKind.ACutsB, aCutsB.Kind);
        Assert.Equal(DepthOrderKind.BCutsA, bCutsA.Kind);
        Assert.True(aCutsB.AIsMain);
        Assert.False(aCutsB.BIsMain);
        Assert.True(bCutsA.BIsMain);
        Assert.False(bCutsA.AIsMain);

        // A cannot cut B and B cut A in the same ranking: the two answers are
        // opposites, never both true.
        Assert.False(aCutsB.AIsMain && bCutsA.AIsMain);
        Assert.False(aCutsB.BIsMain && bCutsA.BIsMain);
    }

    [Fact]
    public void DepthOrder_EqualDepth_NeitherSplits()
    {
        var order = SecondaryByMain.DecideOrder(300, "UB305", 300, "W12x26");

        Assert.Equal(DepthOrderKind.Neither, order.Kind);
        Assert.True(order.NeitherSplits);
        Assert.False(order.AIsMain);
        Assert.False(order.BIsMain);
        Assert.False(order.Refused);
    }

    [Fact]
    public void DepthOrder_UnreadableOnEither_RefusesAndNamesTheType()
    {
        var missingA = SecondaryByMain.DecideOrder(null, "MysteryA", 400, "UB406");
        Assert.True(missingA.Refused);
        Assert.Equal("MysteryA", missingA.RefusedTypeName);

        var missingB = SecondaryByMain.DecideOrder(400, "UB406", null, "MysteryB");
        Assert.True(missingB.Refused);
        Assert.Equal("MysteryB", missingB.RefusedTypeName);

        var zero = SecondaryByMain.DecideOrder(0, "ZeroA", 400, "UB406");
        Assert.True(zero.Refused);
        Assert.Equal("ZeroA", zero.RefusedTypeName);

        var nan = SecondaryByMain.DecideOrder(double.NaN, "NanType", 400, "UB406");
        Assert.True(nan.Refused);
        Assert.Equal("NanType", nan.RefusedTypeName);

        var bothUnreadable = SecondaryByMain.DecideOrder(null, "FirstBad", null, "SecondBad");
        Assert.True(bothUnreadable.Refused);
        Assert.Equal("FirstBad", bothUnreadable.RefusedTypeName);
    }

    [Fact]
    public void Occupancy_ThroughAtSameHeight_Accepts()
    {
        // Main 400 deep sitting 0..400. Secondary 200 deep, centreline at 200:
        // cutterBot + 0.5*target = 100, cutterTop - 0.5*target = 300. 200 sits strictly inside.
        Assert.True(SecondaryByMain.Occupies(cutterBot: 0, cutterTop: 400, zMid: 200, targetDepth: 200));
    }

    [Fact]
    public void Occupancy_BearingOn_Rejects()
    {
        // Secondary sitting on top of the main: zMid = cutterTop + 0.5*targetDepth.
        // cutterBot=0, cutterTop=400, targetDepth=200 → zMid=500. Fails by construction.
        Assert.False(SecondaryByMain.Occupies(cutterBot: 0, cutterTop: 400, zMid: 500, targetDepth: 200));
    }

    [Fact]
    public void Occupancy_DifferentHeight_Rejects()
    {
        // Plan-crossing pair at different storeys: secondary centreline well above the main.
        Assert.False(SecondaryByMain.Occupies(cutterBot: 0, cutterTop: 400, zMid: 3000, targetDepth: 200));
    }

    [Fact]
    public void Occupancy_FlushWithTopOrBottom_IsStrictAndRejects()
    {
        // Equality is not enough: the half-depth must sit strictly inside the cutter.
        Assert.False(SecondaryByMain.Occupies(cutterBot: 0, cutterTop: 400, zMid: 100, targetDepth: 200));
        Assert.False(SecondaryByMain.Occupies(cutterBot: 0, cutterTop: 400, zMid: 300, targetDepth: 200));
    }

    [Fact]
    public void Crossing_ThroughRun_EmitsTwoOuterBandsAndDropsTheStub()
    {
        var decision = SecondaryByMain.Crossing(new CrossingRun(0.40, 0.55, oneSided: false));

        Assert.True(decision.Keep);
        Assert.Null(decision.SkipReason);
        Assert.Equal(0.40, decision.Enter, 9);
        Assert.Equal(0.55, decision.Leave, 9);

        var bands = decision.OuterBands;
        Assert.Equal(2, bands.Count);
        Assert.Equal(0.0, bands[0].From, 9);
        Assert.Equal(0.40, bands[0].To, 9);
        Assert.Equal(0.55, bands[1].From, 9);
        Assert.Equal(1.0, bands[1].To, 9);

        // The middle stub is not a kept band.
        Assert.DoesNotContain(bands, band => band.From == 0.40 && band.To == 0.55);
    }

    [Fact]
    public void Crossing_MinGapComesFromTheCrossingRun_NotTargetLength()
    {
        // A 2% of-span crossing would collapse under MinGapFraction = 0.03 of target length.
        // Min-gap from the crossing itself (half of 0.02 = 0.01) keeps both cuts.
        var decision = SecondaryByMain.Crossing(new CrossingRun(0.49, 0.51, oneSided: false));

        Assert.True(decision.Keep);
        Assert.Equal(0.01, decision.MinGap, 9);
        Assert.True(decision.MinGap < decision.Leave - decision.Enter);
        Assert.Equal(2, decision.OuterBands.Count);

        // Contrast: SplitSequence with the inherited 0.03-of-span gap would keep only one cut.
        var inherited = SplitSequence.Build(new List<double> { 0.49, 0.51 }, 0.03);
        Assert.Single(inherited);
    }

    [Fact]
    public void Crossing_OneSidedFraming_IsSkippedNotStubbed()
    {
        var flagged = SecondaryByMain.Crossing(new CrossingRun(0.0, 0.12, oneSided: true));
        Assert.False(flagged.Keep);
        Assert.Contains("one-sided", flagged.SkipReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(flagged.OuterBands);

        var endInside = SecondaryByMain.Crossing(new CrossingRun(0.0, 0.12, oneSided: false));
        Assert.False(endInside.Keep);
        Assert.Contains("one-sided", endInside.SkipReason, StringComparison.OrdinalIgnoreCase);
    }
}
