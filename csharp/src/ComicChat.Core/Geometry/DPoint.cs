namespace ComicChat.Core.Geometry;

/// <summary>Double-precision point. Port of DPOINT (vector2d.h).</summary>
public readonly struct DPoint(double x, double y)
{
    public readonly double X = x;
    public readonly double Y = y;

    public static DPoint operator +(DPoint a, DPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static DPoint operator -(DPoint a, DPoint b) => new(a.X - b.X, a.Y - b.Y);
    public static DPoint operator *(DPoint a, double s) => new(a.X * s, a.Y * s);
    public static DPoint operator /(DPoint a, double s) => new(a.X / s, a.Y / s);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public double DistanceTo(DPoint other)
    {
        double dx = X - other.X, dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public DPoint Normalized()
    {
        var len = Length;
        return len < 1e-9 ? new DPoint(0, 0) : new DPoint(X / len, Y / len);
    }

    /// <summary>Perpendicular (rotated 90° CCW). Used to raise the balloon outline's wavies.</summary>
    public DPoint Perpendicular() => new(-Y, X);

    public double Dot(DPoint other) => X * other.X + Y * other.Y;

    public override string ToString() => $"({X:F2}, {Y:F2})";
}

/// <summary>A cubic Bezier segment. Port of BEZIER (vector2d.h).</summary>
public readonly struct Bezier(DPoint p0, DPoint p1, DPoint p2, DPoint p3)
{
    public readonly DPoint P0 = p0;
    public readonly DPoint P1 = p1;
    public readonly DPoint P2 = p2;
    public readonly DPoint P3 = p3;

    /// <summary>de Casteljau evaluation at parameter <paramref name="t"/> in [0,1].</summary>
    public DPoint Evaluate(double t)
    {
        double mt = 1.0 - t;
        double a = mt * mt * mt;
        double b = 3 * mt * mt * t;
        double c = 3 * mt * t * t;
        double d = t * t * t;
        return new DPoint(
            a * P0.X + b * P1.X + c * P2.X + d * P3.X,
            a * P0.Y + b * P1.Y + c * P2.Y + d * P3.Y);
    }
}

public static class AngleUtil
{
    public const double TwoPi = Math.PI * 2.0;

    /// <summary>Normalize an angle into [0, 2*PI).</summary>
    public static double Normalize(double angle)
    {
        angle %= TwoPi;
        if (angle < 0) angle += TwoPi;
        return angle;
    }

    /// <summary>
    /// Shortest angular distance between two angles, in [0, PI].
    /// Port of subtract_angles (vector2d.cpp) — the metric the emotion wheel
    /// matches on, so it must wrap correctly across 0/2*PI.
    /// </summary>
    public static double SubtractAngles(double a, double b)
    {
        double diff = Math.Abs(Normalize(a) - Normalize(b));
        return diff > Math.PI ? TwoPi - diff : diff;
    }
}
