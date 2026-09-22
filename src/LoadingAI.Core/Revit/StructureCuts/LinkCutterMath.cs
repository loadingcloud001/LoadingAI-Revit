using LoadingAI.Geometry;

namespace LoadingAI.Revit.StructureCuts;

/// <summary>
/// Revit-free cutter stations already transformed into host space. A linked element is never
/// used at its link-local coordinates: the host applies <c>RevitLinkInstance.GetTotalTransform</c>
/// once, then hands the result here. Coincident host stations are dropped before planning so a
/// linked level on a host elevation, or a linked grid on a host line, cannot make a zero-height
/// piece or a double split.
/// </summary>
public static class LinkCutterMath
{
    /// <summary>Same vertical tolerance the host uses before two elevations count as one storey (~30 mm, feet).</summary>
    public const double ElevationTolFt = 30.0 / 304.8;

    /// <summary>Plan tolerance before two grid lines count as the same cut (~0.3 mm, feet).</summary>
    public const double LineTolFt = 0.001;

    /// <summary>
    /// A level or grid whose direction leaves the horizontal/vertical plane the host can cut
    /// with. Named, never guessed into a horizontal elevation or a vertical grid plane.
    /// </summary>
    public const string TiltedSkip = "tilted link datum skipped";

    public const string UnloadedNote = "link not loaded";

    /// <summary>The link opened, but its total transform could not be read, so its cutters were not used.</summary>
    public const string TransformFailedNote = "link transform failed";

    public readonly struct GridStation
    {
        public GridStation(Vec3 start, Vec3 end)
        {
            Start = start;
            End = end;
        }

        public Vec3 Start { get; }

        public Vec3 End { get; }
    }

    /// <summary>Elevations the planner should use: host first, then linked stations that are not already a host storey.</summary>
    public static List<double> MergeElevations(IReadOnlyList<double> host, IReadOnlyList<double> linked)
    {
        var merged = new List<double>();
        foreach (var z in host)
            AddElevation(merged, z);
        foreach (var z in linked)
            AddElevation(merged, z);
        merged.Sort();
        return merged;
    }

    public static bool IsCoincidentElevation(double candidate, IReadOnlyList<double> existing)
    {
        foreach (var z in existing)
        {
            if (Math.Abs(z - candidate) <= ElevationTolFt)
                return true;
        }

        return false;
    }

    /// <summary>Level planes must be horizontal. A linked level with any plan component in its normal is skipped.</summary>
    public static bool IsTiltedLevel(Vec3 normal)
    {
        var length = normal.Length;
        if (length <= 1e-9)
            return true;
        return Math.Abs(Math.Abs(normal.Z) / length - 1.0) > 1e-4;
    }

    /// <summary>Grid cuts are vertical planes. A linked grid whose curve rises is skipped.</summary>
    public static bool IsTiltedGrid(Vec3 start, Vec3 end)
        => Math.Abs(end.Z - start.Z) > ElevationTolFt;

    public static List<GridStation> MergeGrids(IReadOnlyList<GridStation> host, IReadOnlyList<GridStation> linked)
    {
        var merged = new List<GridStation>();
        foreach (var station in host)
        {
            if (!IsCoincidentGrid(station, merged))
                merged.Add(station);
        }

        foreach (var station in linked)
        {
            if (!IsCoincidentGrid(station, merged))
                merged.Add(station);
        }

        return merged;
    }

    public static bool IsCoincidentGrid(GridStation candidate, IReadOnlyList<GridStation> existing)
    {
        foreach (var station in existing)
        {
            if (SameLine(candidate.Start, candidate.End, station.Start, station.End))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Applies a rigid transform (origin + rows of the rotation) to a point. This is the
    /// host-space station, never the link-local point.
    /// </summary>
    public static Vec3 TransformPoint(Vec3 origin, Vec3 basisX, Vec3 basisY, Vec3 basisZ, Vec3 point)
        => origin + basisX * point.X + basisY * point.Y + basisZ * point.Z;

    public static bool SameLine(Vec3 a0, Vec3 a1, Vec3 b0, Vec3 b1)
    {
        var dir = Horizontal(a1 - a0);
        if (dir.Length <= LineTolFt)
            return false;

        var other = Horizontal(b1 - b0);
        if (other.Length <= LineTolFt)
            return false;

        var unit = dir.Normalized();
        var cross = Math.Abs(unit.X * other.Y - unit.Y * other.X);
        if (cross > LineTolFt * Math.Max(other.Length, 1.0))
            return false;

        return DistanceToInfiniteLine(b0, a0, unit) <= LineTolFt
            && DistanceToInfiniteLine(b1, a0, unit) <= LineTolFt;
    }

    private static void AddElevation(List<double> merged, double z)
    {
        if (!IsCoincidentElevation(z, merged))
            merged.Add(z);
    }

    private static Vec3 Horizontal(Vec3 vector) => new Vec3(vector.X, vector.Y, 0);

    private static double DistanceToInfiniteLine(Vec3 point, Vec3 origin, Vec3 unit)
    {
        var flat = Horizontal(point);
        var delta = flat - Horizontal(origin);
        var along = Vec3.Dot(delta, unit);
        var dropped = Horizontal(origin) + unit * along;
        return flat.DistanceTo(dropped);
    }
}
