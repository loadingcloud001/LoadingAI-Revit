using System;

namespace LoadingAI.Geometry;

/// <summary>
/// Revit-free 3D vector. The structure-cuts plugin converts Revit <c>XYZ</c> values into this
/// type so the plane math can be unit tested without a Revit host.
/// </summary>
public readonly struct Vec3 : IEquatable<Vec3>
{
    public Vec3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    /// <summary>Returns a unit vector, or the zero vector when this vector is degenerate.</summary>
    public Vec3 Normalized()
    {
        var length = Length;
        return length <= 1e-12 ? new Vec3(0, 0, 0) : new Vec3(X / length, Y / length, Z / length);
    }

    public double DistanceTo(Vec3 other) => (this - other).Length;

    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);

    public static Vec3 operator *(Vec3 a, double scale) => new Vec3(a.X * scale, a.Y * scale, a.Z * scale);

    public static Vec3 operator *(double scale, Vec3 a) => a * scale;

    public static Vec3 operator /(Vec3 a, double divisor) => new Vec3(a.X / divisor, a.Y / divisor, a.Z / divisor);

    public bool Equals(Vec3 other) => X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object? obj) => obj is Vec3 other && Equals(other);

    public override int GetHashCode() => (X, Y, Z).GetHashCode();

    public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
}
