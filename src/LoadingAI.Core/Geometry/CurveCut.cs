using System;
using System.Collections.Generic;

namespace LoadingAI.Geometry;

/// <summary>
/// Fractions along a sampled polyline where it crosses a plane. The structure-cuts plugin tessellates
/// Revit curves into these samples so the same maths covers a straight beam, a curved beam and a
/// slanted column without taking a Revit type.
/// </summary>
public static class CurveCut
{
    public const double DefaultMinFraction = 0.02;

    /// <summary>
    /// Where a run of samples crosses the horizontal plane <paramref name="elevation"/>.
    /// </summary>
    public static List<double> AtElevation(
        IReadOnlyList<Vec3> samples,
        double elevation,
        double minFraction = DefaultMinFraction,
        double minGap = 0.0)
        => Hits(samples, p => p.Z - elevation, minFraction, minGap);

    /// <summary>
    /// Where a run of samples crosses <paramref name="plane"/>. A grid is a vertical plane; a storey
    /// cut on a leaning member is still this, with a horizontal plane.
    /// </summary>
    public static List<double> AtPlane(
        IReadOnlyList<Vec3> samples,
        Plane2D plane,
        double minFraction = DefaultMinFraction,
        double minGap = 0.0)
        => Hits(samples, plane.SignedDistance, minFraction, minGap);

    private static List<double> Hits(
        IReadOnlyList<Vec3> samples,
        Func<Vec3, double> signed,
        double minFraction,
        double minGap)
    {
        var cuts = new List<double>();
        if (samples == null || samples.Count < 2)
            return cuts;

        var lengths = new double[samples.Count];
        for (var i = 1; i < samples.Count; i++)
            lengths[i] = lengths[i - 1] + samples[i - 1].DistanceTo(samples[i]);

        var total = lengths[samples.Count - 1];
        if (total <= 1e-12)
            return cuts;

        for (var i = 0; i < samples.Count - 1; i++)
        {
            var d0 = signed(samples[i]);
            var d1 = signed(samples[i + 1]);
            if (!TryCrossing(d0, d1, out var t))
                continue;

            var along = lengths[i] + (t * (lengths[i + 1] - lengths[i]));
            var fraction = along / total;
            if (fraction <= minFraction || fraction >= 1.0 - minFraction)
                continue;

            cuts.Add(fraction);
        }

        cuts.Sort();
        var filtered = new List<double>();
        foreach (var cut in cuts)
        {
            if (filtered.Count == 0 || cut - filtered[filtered.Count - 1] > minGap)
                filtered.Add(cut);
        }

        return filtered;
    }

    /// <summary>
    /// A crossing is a sign change along the segment. A segment that lies in the plane is not a cut,
    /// and a sample that starts on the plane is ignored so an end sitting on a storey does not become
    /// a zero-length piece.
    /// </summary>
    public static bool TryCrossing(double d0, double d1, out double t)
    {
        t = 0;
        if (double.IsNaN(d0) || double.IsNaN(d1))
            return false;

        const double on = 1e-9;
        if (Math.Abs(d0) <= on && Math.Abs(d1) <= on)
            return false;

        if (Math.Abs(d0) <= on)
            return false;

        if (d0 * d1 > 0)
            return false;

        var span = d0 - d1;
        if (Math.Abs(span) <= on)
            return false;

        t = d0 / span;
        return t > 0.0 && t < 1.0;
    }
}
