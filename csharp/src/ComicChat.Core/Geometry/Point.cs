namespace ComicChat.Core.Geometry;

/// <summary>Integer point. Port of Win32 POINT as used throughout the spline/traj code.</summary>
public readonly struct Point(int x, int y) : IEquatable<Point>
{
    public readonly int X = x;
    public readonly int Y = y;

    public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);
    public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);

    /// <summary>Port of point_scalmult (vector2d.cpp) — rounds, as the original does.</summary>
    public static Point operator *(Point a, double s) =>
        new((int)Math.Round(a.X * s, MidpointRounding.AwayFromZero),
            (int)Math.Round(a.Y * s, MidpointRounding.AwayFromZero));

    /// <summary>Port of point_magn (vector2d.cpp).</summary>
    public double Magnitude => Math.Sqrt((double)X * X + (double)Y * Y);

    public DPoint ToDPoint() => new(X, Y);

    public bool Equals(Point other) => X == other.X && Y == other.Y;
    public override bool Equals(object? obj) => obj is Point p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public static bool operator ==(Point a, Point b) => a.Equals(b);
    public static bool operator !=(Point a, Point b) => !a.Equals(b);
    public override string ToString() => $"({X}, {Y})";
}

/// <summary>
/// Where geometry gets emitted. Stands in for the MFC CDC path calls
/// (BeginPath/MoveTo/LineTo/PolyBezierTo/CloseFigure/EndPath) that the original drew into,
/// so the same trajectory code can drive an Avalonia StreamGeometry or a test harness.
/// </summary>
public interface IPathSink
{
    void MoveTo(Point p);
    void LineTo(Point p);
    void CubicTo(Point c1, Point c2, Point end);
    void Close();
}

/// <summary>An IPathSink that flattens everything to a polyline. Used for tests and hit-testing.</summary>
public sealed class FlattenSink(int stepsPerCurve = 24) : IPathSink
{
    private readonly List<List<DPoint>> _figures = [];
    private List<DPoint>? _current;
    private DPoint _cursor;

    public IReadOnlyList<IReadOnlyList<DPoint>> Figures => _figures;

    public void MoveTo(Point p)
    {
        _current = [p.ToDPoint()];
        _figures.Add(_current);
        _cursor = p.ToDPoint();
    }

    public void LineTo(Point p)
    {
        EnsureFigure();
        _current!.Add(p.ToDPoint());
        _cursor = p.ToDPoint();
    }

    public void CubicTo(Point c1, Point c2, Point end)
    {
        EnsureFigure();
        var b = new Bezier(_cursor, c1.ToDPoint(), c2.ToDPoint(), end.ToDPoint());
        for (int i = 1; i <= stepsPerCurve; i++)
            _current!.Add(b.Evaluate((double)i / stepsPerCurve));
        _cursor = end.ToDPoint();
    }

    public void Close()
    {
        if (_current is { Count: > 0 })
            _current.Add(_current[0]);
    }

    private void EnsureFigure()
    {
        if (_current is null) MoveTo(new Point(0, 0));
    }
}
