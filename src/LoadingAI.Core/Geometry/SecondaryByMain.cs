using System;
using System.Collections.Generic;

namespace LoadingAI.Geometry;

/// <summary>
/// Pure decision math for splitting a strictly shallower secondary beam at a same-height
/// pass-through of a strictly deeper main. Not cope/notch. Unit-testable without Revit.
/// </summary>
public static class SecondaryByMain
{
    /// <summary>
    /// Depth ranking by type nominal section height only. Unreadable depth on either member
    /// is a refuse, not a silent fallback. Equal depth is neither.
    /// </summary>
    public static DepthOrder DecideOrder(
        double? aDepth,
        string aTypeName,
        double? bDepth,
        string bTypeName)
    {
        if (!Readable(aDepth))
            return DepthOrder.Refuse(aTypeName);
        if (!Readable(bDepth))
            return DepthOrder.Refuse(bTypeName);
        if (aDepth!.Value > bDepth!.Value)
            return DepthOrder.ACutsB();
        if (bDepth.Value > aDepth.Value)
            return DepthOrder.BCutsA();
        return DepthOrder.Neither();
    }

    /// <summary>
    /// Mid-depth occupancy: ACCEPT iff
    /// <c>cutterBot + 0.5*targetDepth &lt; zMid</c> and
    /// <c>zMid &lt; cutterTop - 0.5*targetDepth</c> (strict). Bearing-ON and different
    /// heights fail by construction.
    /// </summary>
    public static bool Occupies(
        double cutterBot,
        double cutterTop,
        double zMid,
        double targetDepth)
    {
        if (!Finite(cutterBot) || !Finite(cutterTop) || !Finite(zMid) || !Readable(targetDepth))
            return false;
        if (cutterTop <= cutterBot)
            return false;

        var half = targetDepth / 2.0;
        return cutterBot + half < zMid && zMid < cutterTop - half;
    }

    /// <summary>
    /// Two outer pieces of a through-run, dropping the middle stub inside the main's plan
    /// width. Min-gap is taken from the measured crossing, never from target length, so a
    /// narrow main still produces both enter and leave cuts. One-sided framing (a run that
    /// includes a centreline endpoint) is out of this slice.
    /// </summary>
    public static CrossingDecision Crossing(CrossingRun run)
    {
        if (!run.Valid)
            return CrossingDecision.Skip("invalid crossing");

        if (run.OneSided)
            return CrossingDecision.Skip("one-sided framing (wants a trim, not 2-piece)");

        var enter = run.Enter;
        var leave = run.Leave;
        if (double.IsNaN(enter) || double.IsNaN(leave))
            return CrossingDecision.Skip("invalid crossing");
        if (enter > leave)
        {
            var swap = enter;
            enter = leave;
            leave = swap;
        }

        var crossing = leave - enter;
        if (crossing <= 0.0)
            return CrossingDecision.Skip("zero-length crossing");

        // Own min-gap from the measured crossing so both enter/leave survive. A fraction of
        // target length would collapse a narrow main to one cut and leave a forbidden stub.
        var minGap = crossing * 0.5;
        if (leave < enter + minGap)
            return CrossingDecision.Skip("crossing narrower than min-gap");

        if (enter <= 0.0 || leave >= 1.0)
            return CrossingDecision.Skip("one-sided framing (wants a trim, not 2-piece)");

        return CrossingDecision.TwoPiece(enter, leave, minGap);
    }

    private static bool Readable(double? depth)
        => depth.HasValue && Finite(depth.Value) && depth.Value > 0.0;

    private static bool Finite(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value);
}

/// <summary>Who cuts whom by type depth. Never mixed with bbox or plan-width.</summary>
public readonly struct DepthOrder
{
    private DepthOrder(DepthOrderKind kind, string? refusedTypeName)
    {
        Kind = kind;
        RefusedTypeName = refusedTypeName;
    }

    public DepthOrderKind Kind { get; }

    /// <summary>Type name of the member whose depth could not be read, when Kind is Refuse.</summary>
    public string? RefusedTypeName { get; }

    public bool AIsMain => Kind == DepthOrderKind.ACutsB;

    public bool BIsMain => Kind == DepthOrderKind.BCutsA;

    public bool NeitherSplits => Kind == DepthOrderKind.Neither;

    public bool Refused => Kind == DepthOrderKind.Refuse;

    public static DepthOrder ACutsB() => new DepthOrder(DepthOrderKind.ACutsB, null);

    public static DepthOrder BCutsA() => new DepthOrder(DepthOrderKind.BCutsA, null);

    public static DepthOrder Neither() => new DepthOrder(DepthOrderKind.Neither, null);

    public static DepthOrder Refuse(string typeName)
        => new DepthOrder(DepthOrderKind.Refuse, typeName ?? "");
}

public enum DepthOrderKind
{
    ACutsB,
    BCutsA,
    Neither,
    Refuse,
}

/// <summary>
/// One centreline run of the secondary inside the main's plan footprint, as fractions of the
/// secondary's length. <see cref="OneSided"/> is true when either end of that run sits on a
/// centreline endpoint (the secondary frames into the main rather than passing through).
/// </summary>
public readonly struct CrossingRun
{
    public CrossingRun(double enter, double leave, bool oneSided)
    {
        Enter = enter;
        Leave = leave;
        OneSided = oneSided;
        Valid = !double.IsNaN(enter) && !double.IsNaN(leave);
    }

    public double Enter { get; }

    public double Leave { get; }

    public bool OneSided { get; }

    public bool Valid { get; }

    public double Length => Math.Abs(Leave - Enter);
}

/// <summary>Keep the two outer bands; drop the middle stub inside the main.</summary>
public readonly struct CrossingDecision
{
    private CrossingDecision(bool keep, double enter, double leave, double minGap, string? skipReason)
    {
        Keep = keep;
        Enter = enter;
        Leave = leave;
        MinGap = minGap;
        SkipReason = skipReason;
    }

    public bool Keep { get; }

    public double Enter { get; }

    public double Leave { get; }

    /// <summary>Min-gap derived from the crossing run, not from the secondary's span.</summary>
    public double MinGap { get; }

    public string? SkipReason { get; }

    /// <summary>
    /// The two outer bands as original-member fractions: <c>[0, enter]</c> and
    /// <c>[leave, 1]</c>. The middle <c>[enter, leave]</c> is the stub and is not emitted.
    /// </summary>
    public IReadOnlyList<(double From, double To)> OuterBands
        => Keep
            ? new[] { (0.0, Enter), (Leave, 1.0) }
            : Array.Empty<(double, double)>();

    public static CrossingDecision TwoPiece(double enter, double leave, double minGap)
        => new CrossingDecision(true, enter, leave, minGap, null);

    public static CrossingDecision Skip(string reason)
        => new CrossingDecision(false, 0.0, 0.0, 0.0, reason);
}
