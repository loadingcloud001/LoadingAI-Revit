using System;

namespace LoadingAI.Geometry;

/// <summary>
/// A plane with an orthonormal in-plane basis, used to run 2D polygon math on sloped (but planar)
/// Revit faces. The basis is right handed (<c>U x V == Normal</c>), so a counter-clockwise polygon
/// in this plane stays counter-clockwise when mapped back to 3D.
/// </summary>
public sealed class Plane2D
{
    public Plane2D(Vec3 origin, Vec3 normal)
    {
        Origin = origin;
        var unit = normal.Normalized();
        if (unit.Length <= 1e-9)
            unit = new Vec3(0, 0, 1);
        Normal = unit;

        var seed = Math.Abs(unit.Z) < 0.9 ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0);
        U = Vec3.Cross(seed, unit).Normalized();
        V = Vec3.Cross(unit, U).Normalized();
    }

    public Vec3 Origin { get; }

    public Vec3 Normal { get; }

    /// <summary>First in-plane axis.</summary>
    public Vec3 U { get; }

    /// <summary>Second in-plane axis, chosen so that <c>(U, V, Normal)</c> is right handed.</summary>
    public Vec3 V { get; }

    public Point2 Project(Vec3 point)
    {
        var delta = point - Origin;
        return new Point2(Vec3.Dot(delta, U), Vec3.Dot(delta, V));
    }

    public Vec3 ToPlane(Point2 point) => Origin + U * point.X + V * point.Y;

    /// <summary>Signed distance along the plane normal; zero for points on the plane.</summary>
    public double SignedDistance(Vec3 point) => Vec3.Dot(point - Origin, Normal);
}
