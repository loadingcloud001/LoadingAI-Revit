using System;
using System.Collections.Generic;
using LoadingAI.Geometry;
using Xunit;

namespace LoadingAI.Tests;

/// <summary>
/// The beam splitter hands Revit one cut at a time, from the top of the member down. These pin the
/// order and the restatement of each remaining cut as a fraction of what is left, which is the part the
/// plugin cannot get wrong without producing lop-sided pieces.
/// </summary>
public class SplitSequenceTests
{
    [Fact]
    public void Build_NoCuts_IsEmpty()
    {
        Assert.Empty(SplitSequence.Build(new List<double>()));
        Assert.Empty(SplitSequence.Build(null!));
    }

    [Fact]
    public void Build_SingleCut_KeepsItsFractionOfTheWholeMember()
    {
        var steps = SplitSequence.Build(new List<double> { 0.25 });

        var step = Assert.Single(steps);
        Assert.Equal(0.25, step.Cut, 9);
        Assert.Equal(0.25, step.Local, 9);
        Assert.Equal(1.0, step.Top, 9);
        Assert.Equal(1, step.LowPiece);
        Assert.Equal(2, step.HighPiece);
    }

    [Fact]
    public void Build_ManyCuts_TakesTheHighestFirstAndRestatesTheRestAgainstWhatIsLeft()
    {
        var steps = SplitSequence.Build(new List<double> { 0.3, 0.8 });

        Assert.Equal(2, steps.Count);
        Assert.Equal(0.8, steps[0].Cut, 9);
        Assert.Equal(0.8, steps[0].Local, 9);
        Assert.Equal(1.0, steps[0].Top, 9);

        // The second cut is 0.3 of the original, but only 0.375 of the piece the first split left
        // behind, which still runs from the member's start to 0.8.
        Assert.Equal(0.3, steps[1].Cut, 9);
        Assert.Equal(0.375, steps[1].Local, 9);
        Assert.Equal(0.8, steps[1].Top, 9);
        Assert.Equal(1, steps[1].LowPiece);
        Assert.Equal(2, steps[1].HighPiece);
    }

    [Fact]
    public void Build_StepsAreDescending_SoTheLowEndElementIsTheOnlyOneSplitAgain()
    {
        var cuts = new List<double> { 0.15, 0.4, 0.55, 0.92 };
        var steps = SplitSequence.Build(cuts);

        Assert.Equal(4, steps.Count);
        for (var i = 1; i < steps.Count; i++)
        {
            Assert.True(steps[i].Cut < steps[i - 1].Cut, "every cut after the first must sit below the last one taken");
            Assert.Equal(steps[i - 1].Cut, steps[i].Top, 9);
        }

        Assert.Equal(1.0, steps[0].Top, 9);
        Assert.Equal(0.15, steps[steps.Count - 1].Cut, 9);

        // Each step's local fraction lands back on the original cut once the piece it was measured
        // against is taken into account.
        foreach (var step in steps)
            Assert.Equal(step.Cut, step.Local * step.Top, 9);
    }

    [Fact]
    public void Build_UnsortedCuts_GetTheSameAnswerAsSortedOnes()
    {
        var shuffled = SplitSequence.Build(new List<double> { 0.55, 0.15, 0.92, 0.4 });
        var sorted = SplitSequence.Build(new List<double> { 0.15, 0.4, 0.55, 0.92 });

        Assert.Equal(sorted.Count, shuffled.Count);
        for (var i = 0; i < sorted.Count; i++)
        {
            Assert.Equal(sorted[i].Cut, shuffled[i].Cut, 9);
            Assert.Equal(sorted[i].Local, shuffled[i].Local, 9);
            Assert.Equal(sorted[i].LowPiece, shuffled[i].LowPiece);
            Assert.Equal(sorted[i].HighPiece, shuffled[i].HighPiece);
        }
    }

    [Fact]
    public void Build_DropsCutsRevitWouldReject()
    {
        // Outside the open span, not a number, or so close to the cut below that the piece between them
        // is a sliver.
        var steps = SplitSequence.Build(new List<double> { -0.2, 0.0, 1.0, 1.4, double.NaN, 0.5, 0.505, 0.9 }, 0.03);

        Assert.Equal(2, steps.Count);
        Assert.Equal(0.9, steps[0].Cut, 9);
        Assert.Equal(0.5, steps[1].Cut, 9);
    }

    [Fact]
    public void Build_MinGapKeepsTheLowerOfAClosePair()
    {
        var steps = SplitSequence.Build(new List<double> { 0.5, 0.52 }, 0.03);

        var step = Assert.Single(steps);
        Assert.Equal(0.5, step.Cut, 9);
    }

    [Fact]
    public void Build_ExactDuplicates_AreOneCut()
    {
        var step = Assert.Single(SplitSequence.Build(new List<double> { 0.4, 0.4 }));

        Assert.Equal(0.4, step.Cut, 9);
        Assert.Equal(0.4, step.Local, 9);
    }

    [Fact]
    public void Bounds_BracketTheMemberWithItsCutsInOrder()
    {
        var bounds = SplitSequence.Bounds(new List<double> { 0.7, 0.2 });

        Assert.Equal(new[] { 0.0, 0.2, 0.7, 1.0 }, bounds);
    }

    [Fact]
    public void Bounds_NoCuts_IsTheWholeMember()
    {
        Assert.Equal(new[] { 0.0, 1.0 }, SplitSequence.Bounds(new List<double>()));
    }
}
