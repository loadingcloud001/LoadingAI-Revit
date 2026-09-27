using System;

namespace LoadingAI.Geometry;

/// <summary>
/// How far the top face of a slab leans away from the horizontal, expressed the way Revit's slope
/// arrow wants it: the horizontal direction the slab rises in, and the angle it rises at. The face
/// normal is not enough on its own - a slope arrow is a horizontal line, so the uphill direction is the
/// normal's horizontal part reversed, and the rise is the angle off vertical, not off level.
/// </summary>
public readonly struct PlanarTilt
{
    private PlanarTilt(Vec3 normal, Vec3 through, Vec3 uphill, double slope)
    {
        Normal = normal;
        Through = through;
        Uphill = uphill;
        Slope = slope;
    }

    /// <summary>The unit normal of the face, always pointing upwards.</summary>
    public Vec3 Normal { get; }

    /// <summary>A point the face passes through, or the origin when the lean was read on its own.</summary>
    public Vec3 Through { get; }

    /// <summary>The unit horizontal direction the slab rises in, or zero when the face is level.</summary>
    public Vec3 Uphill { get; }

    /// <summary>The rise off level in radians, <c>0</c> for a level slab, from <c>0</c> to <c>pi/2</c>.</summary>
    public double Slope { get; }

    /// <summary>
    /// Reads the lean of a top face off its outward normal. A normal with no upward part - an underside,
    /// or a face that is edge on - is not a slab's top and reports false.
    /// </summary>
    public static bool TryCreate(Vec3 normal, out PlanarTilt tilt)
        => TryCreate(normal, new Vec3(0, 0, 0), out tilt);

    /// <summary>
    /// Reads the lean of a top face off its outward normal, together with a point on the face so that the
    /// face's height over a plan position can be found later.
    /// </summary>
    public static bool TryCreate(Vec3 normal, Vec3 through, out PlanarTilt tilt)
    {
        tilt = default;

        var unit = normal.Normalized();
        if (unit.Length <= 1e-12 || unit.Z <= 0)
            return false;

        var run = Math.Sqrt((unit.X * unit.X) + (unit.Y * unit.Y));
        var uphill = run <= 1e-12 ? new Vec3(0, 0, 0) : new Vec3(-unit.X / run, -unit.Y / run, 0);
        tilt = new PlanarTilt(unit, through, uphill, Math.Atan2(run, unit.Z));
        return true;
    }

    /// <summary>
    /// The height the face passes over a plan position. A slab that is cut into pieces is rebuilt in the
    /// horizontal, so each piece has to be set back up onto the face it came off.
    /// </summary>
    public double ZAt(double x, double y)
    {
        if (Math.Abs(Normal.Z) <= 1e-9)
            return Through.Z;

        return Through.Z - (((Normal.X * (x - Through.X)) + (Normal.Y * (y - Through.Y))) / Normal.Z);
    }

    /// <summary>
    /// Whether the face is level enough to build without a slope arrow at all, judged on the normal's
    /// height the same way the slab reader decides a face is horizontal.
    /// </summary>
    public bool IsHorizontal(double upTolerance) => Normal.Z >= upTolerance;

    /// <summary>Whether another face leans the same way as this one, within an angular tolerance.</summary>
    public bool Matches(Vec3 other, double tolerance)
    {
        var unit = other.Normalized();
        if (unit.Length <= 1e-12)
            return false;

        var dot = Vec3.Dot(Normal, unit);
        if (dot > 1.0)
            dot = 1.0;
        else if (dot < -1.0)
            dot = -1.0;

        return Math.Acos(dot) <= tolerance;
    }
}
