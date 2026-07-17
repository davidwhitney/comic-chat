using System.Collections.Concurrent;

namespace ComicChat.Core.Geometry;

/// <summary>A 4x4 spline basis matrix. Port of MATRIX (spline.h:1).</summary>
public sealed class SplineMatrix
{
    private readonly double[,] _m = new double[4, 4];
    public double this[int i, int j]
    {
        get => _m[i, j];
        set => _m[i, j] = value;
    }
}

/// <summary>
/// Base spline. Port of CSpline (spline.h:3, spline.cpp:10).
/// </summary>
/// <remarks>
/// Control points and Bezier points are integers throughout, matching the original's
/// POINT arrays and its ROUND() at every step. This is deliberate: the rounding is
/// observable in the balloon outline, so widening to double would silently change
/// the art.
/// </remarks>
public abstract class Spline : Seg
{
    public bool Closed { get; protected set; }
    protected SplineMatrix Matrix = null!;

    private Point[] _cps;
    private Point[]? _bezpts;

    public int ControlPointCount => _cps.Length;
    public Point[] ControlPoints => _cps;
    public Point[] BezierPoints => _bezpts ?? throw new InvalidOperationException("Bezier points not computed.");

    protected Spline(Point[] cpArray, bool isClosed)
    {
        if (cpArray.Length < 2)
            throw new ArgumentException("A spline needs at least 2 control points.", nameof(cpArray));
        _cps = (Point[])cpArray.Clone();
        Closed = isClosed;
    }

    protected Spline(Spline other)
    {
        Closed = other.Closed;
        Matrix = other.Matrix;
        _cps = (Point[])other._cps.Clone();
        _bezpts = other._bezpts is null ? null : (Point[])other._bezpts.Clone();
    }

    /// <summary>Number of duplicated control points needed to close. Cardinal=2, Beta=3.</summary>
    public abstract int GetDups();

    public abstract int KnotCount();

    /// <summary>Port of BezierCount (spline.h:17).</summary>
    public int BezierCount() => 3 * KnotCount() - 8;

    public abstract Spline Clone();

    /// <summary>Port of CSpline::GetKnot (spline.cpp:232) — phantom knots for closure.</summary>
    public Point GetKnot(int index)
    {
        int n = _cps.Length;
        if (Closed)
        {
            if (index == 0) return _cps[n - 1];
            if (index == n + 1) return _cps[0];
            if (index == n + 2) return _cps[1];
            return _cps[index - 1];
        }

        int dups = GetDups();
        if (index < dups) return _cps[0];
        if (index >= n + dups - 2) return _cps[n - 1];
        return _cps[index - dups + 1];
    }

    /// <summary>Port of CSpline::ComputeBezpts (spline.cpp:169).</summary>
    public void ComputeBezpts()
    {
        int nKnots = KnotCount();
        if (nKnots < 4)
            throw new InvalidOperationException($"Spline needs >= 4 knots, has {nKnots}.");

        _bezpts ??= new Point[BezierCount()];

        int bezIndex = 1;
        Point k0 = GetKnot(0), k1 = GetKnot(1), k2 = GetKnot(2), k3 = GetKnot(3);

        for (int i = 0; ; i++)
        {
            CvertsToCubic(k0, k1, k2, k3, out var c0, out var c1, out var c2, out var c3);
            CubicToBezier(c0, c1, c2, c3, out var b0, out var b1, out var b2, out var b3);

            if (i == 0) _bezpts[0] = b0;
            _bezpts[bezIndex] = b1;
            _bezpts[bezIndex + 1] = b2;
            _bezpts[bezIndex + 2] = b3;

            if (i + 4 == nKnots) return;

            bezIndex += 3;
            k0 = k1;
            k1 = k2;
            k2 = k3;
            k3 = GetKnot(i + 4);
        }
    }

    /// <summary>MFC's ROUND macro: round-half-away-from-zero, not banker's rounding.</summary>
    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>Port of CSpline::CvertsToCubic (spline.cpp:206). Note the row order is reversed: c3 from row 0.</summary>
    private void CvertsToCubic(Point k0, Point k1, Point k2, Point k3,
                               out Point c0, out Point c1, out Point c2, out Point c3)
    {
        c3 = new Point(
            Round(Matrix[0, 0] * k0.X + Matrix[0, 1] * k1.X + Matrix[0, 2] * k2.X + Matrix[0, 3] * k3.X),
            Round(Matrix[0, 0] * k0.Y + Matrix[0, 1] * k1.Y + Matrix[0, 2] * k2.Y + Matrix[0, 3] * k3.Y));
        c2 = new Point(
            Round(Matrix[1, 0] * k0.X + Matrix[1, 1] * k1.X + Matrix[1, 2] * k2.X + Matrix[1, 3] * k3.X),
            Round(Matrix[1, 0] * k0.Y + Matrix[1, 1] * k1.Y + Matrix[1, 2] * k2.Y + Matrix[1, 3] * k3.Y));
        c1 = new Point(
            Round(Matrix[2, 0] * k0.X + Matrix[2, 1] * k1.X + Matrix[2, 2] * k2.X + Matrix[2, 3] * k3.X),
            Round(Matrix[2, 0] * k0.Y + Matrix[2, 1] * k1.Y + Matrix[2, 2] * k2.Y + Matrix[2, 3] * k3.Y));
        c0 = new Point(
            Round(Matrix[3, 0] * k0.X + Matrix[3, 1] * k1.X + Matrix[3, 2] * k2.X + Matrix[3, 3] * k3.X),
            Round(Matrix[3, 0] * k0.Y + Matrix[3, 1] * k1.Y + Matrix[3, 2] * k2.Y + Matrix[3, 3] * k3.Y));
    }

    /// <summary>Port of CSpline::CubicToBezier (spline.cpp:218).</summary>
    private static void CubicToBezier(Point c0, Point c1, Point c2, Point c3,
                                      out Point b0, out Point b1, out Point b2, out Point b3)
    {
        b0 = c0;
        b1 = new Point(
            c0.X + Round(c1.X / 3.0),
            c0.Y + Round(c1.Y / 3.0));
        b2 = new Point(
            b1.X + Round((c1.X + c2.X) / 3.0),
            b1.Y + Round((c1.Y + c2.Y) / 3.0));
        b3 = new Point(
            c0.X + c1.X + c2.X + c3.X,
            c0.Y + c1.Y + c2.Y + c3.Y);
    }

    /// <summary>
    /// Port of CSpline::ClosestPoint (spline.cpp:251). Returns the point on the outline
    /// nearest <paramref name="toPt"/>; <paramref name="knotIndex"/> receives the knot it sits on.
    /// Used to find where to cut the balloon open for the tail.
    /// </summary>
    public Point ClosestPoint(Point toPt, out int knotIndex)
    {
        var bez = BezierPoints;
        int bezCount = BezierCount();
        double minDist = double.MaxValue;
        Point minPos = default;
        knotIndex = 2;

        for (int i = 0; i < bezCount - 1; i += 3)
        {
            var seg = new Bezier(
                bez[i].ToDPoint(), bez[i + 1].ToDPoint(),
                bez[i + 2].ToDPoint(), bez[i + 3].ToDPoint());
            var (pos, dist) = BezierNearestPoint(seg, toPt.ToDPoint());
            if (dist < minDist)
            {
                minDist = dist;
                minPos = new Point((int)Math.Round(pos.X), (int)Math.Round(pos.Y));
                knotIndex = i / 3 + 2;
            }
        }
        return minPos;
    }

    /// <summary>
    /// Port of CSpline::WalkHorizontalDistance (spline.cpp:269). Walks the outline
    /// rightwards from a knot until it reaches <paramref name="goalX"/>, wrapping around
    /// the closed spline. Defines the far lip of the tail's mouth.
    /// </summary>
    public Point WalkHorizontalDistance(Point fromPt, int fromKnotIndex, int goalX, out int foundKnotIndex)
    {
        var bez = BezierPoints;
        int bezCount = BezierCount();
        foundKnotIndex = -1;

        int index = (fromKnotIndex - 2) * 3;
        var lastFurthest = new Point(-100000, -100000);

        for (int i = 0; i < bezCount - 1; i += 3)
        {
            if (index + 3 > bezCount - 1) index = 0;

            var seg = new Bezier(
                bez[index].ToDPoint(), bez[index + 1].ToDPoint(),
                bez[index + 2].ToDPoint(), bez[index + 3].ToDPoint());

            if (WalkHorizontalDist(seg, goalX, out var furthest))
            {
                foundKnotIndex = index / 3 + 2;
                return furthest;
            }

            // No hit on this segment: keep the rightmost point seen so far as a fallback.
            if (furthest.X > lastFurthest.X)
            {
                foundKnotIndex = index / 3 + 2;
                lastFurthest = furthest;
            }
            index += 3;
        }

        return lastFurthest;
    }

    /// <summary>
    /// Flattening search for the nearest point on a cubic. The original called
    /// int_bezier_nearest_point (splinutl.cpp); a dense parametric scan plus a local
    /// refinement is equivalent at the integer precision the outline is stored in.
    /// </summary>
    private static (DPoint pos, double dist) BezierNearestPoint(Bezier b, DPoint to)
    {
        const int coarse = 64;
        double bestT = 0, bestDist = double.MaxValue;

        for (int i = 0; i <= coarse; i++)
        {
            double t = (double)i / coarse;
            double d = b.Evaluate(t).DistanceTo(to);
            if (d < bestDist) { bestDist = d; bestT = t; }
        }

        // Golden-section-ish refinement around the coarse winner.
        double lo = Math.Max(0, bestT - 1.0 / coarse);
        double hi = Math.Min(1, bestT + 1.0 / coarse);
        for (int iter = 0; iter < 40; iter++)
        {
            double m1 = lo + (hi - lo) / 3.0;
            double m2 = hi - (hi - lo) / 3.0;
            if (b.Evaluate(m1).DistanceTo(to) < b.Evaluate(m2).DistanceTo(to)) hi = m2;
            else lo = m1;
        }
        double ft = (lo + hi) / 2.0;
        var fp = b.Evaluate(ft);
        return (fp, fp.DistanceTo(to));
    }

    /// <summary>
    /// Port of walk_horizontal_dist (splinutl.cpp). True if this segment reaches
    /// <paramref name="goalX"/>; <paramref name="furthest"/> always receives the
    /// rightmost point sampled so the caller can fall back to it.
    /// </summary>
    private static bool WalkHorizontalDist(Bezier b, int goalX, out Point furthest)
    {
        const int steps = 32;
        var best = new Point(-100000, -100000);
        bool found = false;
        var result = best;

        for (int i = 0; i <= steps; i++)
        {
            var p = b.Evaluate((double)i / steps);
            var ip = new Point((int)Math.Round(p.X), (int)Math.Round(p.Y));
            if (ip.X > best.X) best = ip;
            if (!found && ip.X >= goalX)
            {
                found = true;
                result = ip;
            }
        }

        furthest = found ? result : best;
        return found;
    }

    public override Point SegLo() => BezierPoints[0];

    /// <summary>
    /// Port of CSpline::Draw (spline.cpp:298), which was a single
    /// <c>PolyBezierTo(bezpts+1, BezierCount()-1)</c> — the cursor is already at bezpts[0].
    /// </summary>
    public override void AppendTo(IPathSink sink)
    {
        var bez = BezierPoints;
        int bezCount = BezierCount();
        for (int i = 1; i + 2 < bezCount; i += 3)
            sink.CubicTo(bez[i], bez[i + 1], bez[i + 2]);
    }

    /// <summary>Replace the control points and recompute. Used when the tail cuts the cloud open.</summary>
    public void SetControlPoints(Point[] cps, bool closed)
    {
        _cps = (Point[])cps.Clone();
        Closed = closed;
        _bezpts = null;
        ComputeBezpts();
    }
}

/// <summary>Cardinal spline, tension 0.4. Port of CCardinal (spline.cpp:44).</summary>
public sealed class Cardinal : Spline
{
    public const double DefaultTension = 0.4;
    private static readonly ConcurrentDictionary<double, SplineMatrix> Cache = new();

    public double Tension { get; }

    public Cardinal(Point[] cpArray, bool isClosed, double? tension = null)
        : base(cpArray, isClosed)
    {
        Tension = tension ?? DefaultTension;
        Matrix = Cache.GetOrAdd(Tension, BuildMatrix);
        ComputeBezpts();
    }

    private Cardinal(Cardinal other) : base(other) => Tension = other.Tension;

    /// <summary>Port of CCardinal::SetMatrix (spline.cpp:93).</summary>
    private static SplineMatrix BuildMatrix(double t)
    {
        var m = new SplineMatrix();
        m[0, 1] = 2.0 - t;
        m[0, 2] = t - 2.0;
        m[1, 0] = 2.0 * t;
        m[1, 1] = t - 3.0;
        m[1, 2] = 3.0 - 2.0 * t;
        m[3, 1] = 1.0;
        m[0, 3] = m[2, 2] = t;
        m[0, 0] = m[1, 3] = m[2, 0] = -t;
        m[2, 1] = m[2, 3] = m[3, 0] = m[3, 2] = m[3, 3] = 0.0;
        return m;
    }

    public override int GetDups() => 2;
    public override int KnotCount() => Closed ? ControlPointCount + 3 : ControlPointCount + 2;
    public override Spline Clone() => new Cardinal(this);
}

/// <summary>
/// Beta spline, tension 5.0, bias 1.0. Port of CBeta (spline.cpp:65).
/// This is the one that draws the word balloons.
/// </summary>
public sealed class Beta : Spline
{
    public const double DefaultTension = 5.0;
    public const double DefaultBias = 1.0;
    private static readonly ConcurrentDictionary<(double, double), SplineMatrix> Cache = new();

    public double Tension { get; }
    public double Bias { get; }

    public Beta(Point[] cpArray, bool isClosed, double? tension = null, double? bias = null)
        : base(cpArray, isClosed)
    {
        Tension = tension ?? DefaultTension;
        Bias = bias ?? DefaultBias;
        Matrix = Cache.GetOrAdd((Tension, Bias), k => BuildMatrix(k.Item1, k.Item2));
        ComputeBezpts();
    }

    private Beta(Beta other) : base(other)
    {
        Tension = other.Tension;
        Bias = other.Bias;
    }

    /// <summary>Port of CBeta::SetMatrix (spline.cpp:112).</summary>
    private static SplineMatrix BuildMatrix(double tension, double bias)
    {
        var m = new SplineMatrix();
        double b2 = bias * bias;
        double b3 = bias * b2;
        double d = 1.0 / (tension + 2.0 * b3 + 4.0 * (b2 + bias) + 2.0);

        m[0, 0] = -2.0 * b3;
        m[0, 1] = 2.0 * (tension + b3 + b2 + bias);
        m[0, 2] = -2.0 * (tension + b2 + bias + 1.0);
        m[1, 0] = 6.0 * b3;
        m[1, 1] = -3.0 * (tension + 2.0 * (b3 + b2));
        m[1, 2] = 3.0 * (tension + 2.0 * b2);
        m[2, 0] = -6.0 * b3;
        m[2, 1] = 6.0 * (b3 - bias);
        m[2, 2] = 6.0 * bias;
        m[3, 0] = 2.0 * b3;
        m[3, 1] = tension + 4.0 * (b2 + bias);
        m[0, 3] = m[3, 2] = 2.0;
        m[1, 3] = m[2, 3] = m[3, 3] = 0.0;

        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                m[i, j] *= d;

        return m;
    }

    public override int GetDups() => 3;
    public override int KnotCount() => Closed ? ControlPointCount + 3 : ControlPointCount + 4;
    public override Spline Clone() => new Beta(this);
}
