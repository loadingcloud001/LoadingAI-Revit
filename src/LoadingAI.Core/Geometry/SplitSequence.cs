using System;
using System.Collections.Generic;

namespace LoadingAI.Geometry;

/// <summary>
/// Turns the fractions along one member where it should be cut into the order Revit can actually carry
/// the cuts out. Revit splits an element at a single point per call, so a member with several cuts is
/// split repeatedly: the highest cut is taken first, the element that keeps the low end has shrunk by
/// the time the next cut is applied, and every remaining fraction has to be restated as a fraction of
/// what is left. Splitting from the top down means the element being split always starts at the
/// original start, which keeps that restatement a single division.
/// </summary>
public static class SplitSequence
{
    /// <summary>
    /// The steps to split a member at the given fractions, highest cut first. Fractions outside
    /// <c>(0, 1)</c> and those closer together than <paramref name="minGap"/> are dropped, so the
    /// steps are the cuts Revit will accept in the order it should receive them.
    /// </summary>
    public static List<SplitStep> Build(IReadOnlyList<double> cuts, double minGap = 0.0)
    {
        var steps = new List<SplitStep>();
        if (cuts == null || cuts.Count == 0)
            return steps;

        var wanted = new List<double>();
        foreach (var cut in cuts)
        {
            if (double.IsNaN(cut) || cut <= 0.0 || cut >= 1.0)
                continue;

            wanted.Add(cut);
        }

        // Sorting first means two cuts a hair apart keep the lower of the pair however the caller
        // ordered them, so a caller passing unsorted cuts gets the same answer as one passing them in
        // order.
        wanted.Sort();

        var kept = new List<double>();
        foreach (var cut in wanted)
        {
            if (kept.Count > 0 && cut < kept[kept.Count - 1] + minGap)
                continue;

            kept.Add(cut);
        }

        var top = 1.0;
        for (var i = kept.Count - 1; i >= 0; i--)
        {
            var local = kept[i] / top;
            if (local <= 0.0 || local >= 1.0)
                continue;

            // The step splits the piece that still owns [0, top]: its low half becomes piece i + 1 in
            // member order and its high half is finished off as piece i + 2.
            steps.Add(new SplitStep(kept[i], local, top, i + 1, i + 2));
            top = kept[i];
        }

        return steps;
    }

    /// <summary>
    /// The turns each piece occupies, in member order, as original fractions: for <c>n</c> cuts there
    /// are <c>n + 1</c> pieces and each one is <c>[bounds[i], bounds[i + 1]]</c>.
    /// </summary>
    public static List<double> Bounds(IReadOnlyList<double> cuts, double minGap = 0.0)
    {
        var bounds = new List<double> { 0.0 };
        foreach (var step in Build(cuts, minGap))
            bounds.Add(step.Cut);

        bounds.Sort();
        bounds.Add(1.0);
        return bounds;
    }
}

/// <summary>
/// One split of a member: where it lands as a fraction of the original, and what that is as a fraction
/// of the element Revit is asked to split that turn.
/// </summary>
public readonly struct SplitStep
{
    internal SplitStep(double cut, double local, double top, int lowPiece, int highPiece)
    {
        Cut = cut;
        Local = local;
        Top = top;
        LowPiece = lowPiece;
        HighPiece = highPiece;
    }

    /// <summary>Where the cut sits on the original member, from its start.</summary>
    public double Cut { get; }

    /// <summary>The same cut as a fraction of the element being split, which is what Revit takes.</summary>
    public double Local { get; }

    /// <summary>How much of the original member the element being split still owns, from its start.</summary>
    public double Top { get; }

    /// <summary>The piece the low half becomes, numbered from the member's start.</summary>
    public int LowPiece { get; }

    /// <summary>The piece the high half becomes, numbered from the member's start.</summary>
    public int HighPiece { get; }
}
