using System;

namespace LoadingAI.Geometry;

/// <summary>Revit-free 2D point used by the structure-cuts region math.</summary>
public readonly struct Point2 : IEquatable<Point2>
{
    public Point2(double x, double y)
    {
        X = x;
        Y = y;
    }

    public double X { get; }
    public double Y { get; }

    public double DistanceTo(Point2 other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static Point2 Lerp(Point2 a, Point2 b, double t) => new Point2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    public static Point2 operator +(Point2 a, Point2 b) => new Point2(a.X + b.X, a.Y + b.Y);

    public static Point2 operator -(Point2 a, Point2 b) => new Point2(a.X - b.X, a.Y - b.Y);

    public static Point2 operator *(Point2 a, double scale) => new Point2(a.X * scale, a.Y * scale);

    public bool Equals(Point2 other) => X == other.X && Y == other.Y;

    public override bool Equals(object? obj) => obj is Point2 other && Equals(other);

    public override int GetHashCode() => (X, Y).GetHashCode();

    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}
