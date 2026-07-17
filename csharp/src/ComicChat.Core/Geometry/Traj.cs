namespace ComicChat.Core.Geometry;

/// <summary>One segment of a trajectory. Port of CSeg (traj.h:16).</summary>
public abstract class Seg
{
    /// <summary>The segment's start point — where the path must be before this segment draws.</summary>
    public abstract Point SegLo();

    /// <summary>Emit this segment into a path, assuming the cursor is already at <see cref="SegLo"/>.</summary>
    public abstract void AppendTo(IPathSink sink);

    /// <summary>Convenience: flatten to a polyline.</summary>
    public virtual void Flatten(IList<DPoint> into, int stepsPerSegment = 24)
    {
        var sink = new FlattenSink(stepsPerSegment);
        sink.MoveTo(SegLo());
        AppendTo(sink);
        foreach (var fig in sink.Figures)
            foreach (var p in fig)
                into.Add(p);
    }
}

/// <summary>Straight segment. Port of CLine (traj.h:23).</summary>
public sealed class Line(Point lo, Point hi) : Seg
{
    public Point Lo { get; } = lo;
    public Point Hi { get; } = hi;

    public override Point SegLo() => Lo;
    public override void AppendTo(IPathSink sink) => sink.LineTo(Hi);
}

/// <summary>
/// Circular arc through two points with a given altitude (bulge). Port of CArc (traj.h:33).
/// The balloon's tail is two of these with opposite-signed altitudes.
/// </summary>
public sealed class Arc(Point lo, Point hi, int altitude) : Seg
{
    public Point Lo { get; } = lo;
    public Point Hi { get; } = hi;

    /// <summary>Signed bulge height. Sign selects which side the arc bows to.</summary>
    public int Altitude { get; } = altitude;

    public override Point SegLo() => Lo;

    /// <summary>Port of CArc::Draw (traj.cpp:98) → DrawArc2 (arc.cpp:97).</summary>
    public override void AppendTo(IPathSink sink) => DrawArc2(sink, Lo, Hi, Altitude);

    /// <summary>
    /// Port of DrawArc2 (arc.cpp:97). Solves for the circle through start/end whose
    /// maximum deviation from the chord is <paramref name="altitude"/>, then scans it.
    /// </summary>
    public static void DrawArc2(IPathSink sink, Point start, Point end, int altitude)
    {
        // Degenerate bulge: the original just draws a straight line.
        if (altitude is < 1 and > -1)
        {
            sink.LineTo(end);
            return;
        }

        Point mid = (start + end) * 0.5;
        Point endToMid = mid - end;
        double endToMidDist = endToMid.Magnitude;

        // radius, altitude and midToCenterDist are all signed here — that is what
        // lets the sign of altitude pick the arc's side.
        double radius = (endToMidDist * endToMidDist + (double)altitude * altitude) / (2.0 * altitude);
        double midToCenterDist = radius - altitude;

        var perp = new Point(endToMid.Y, -endToMid.X);
        double perpMagn = perp.Magnitude;
        if (perpMagn < 1e-9)
        {
            sink.LineTo(end);
            return;
        }

        var midToCenter = perp * (midToCenterDist / perpMagn);
        Point absCenter = end + endToMid + midToCenter;

        ScanArc(sink, absCenter, start, end, altitude > 0);
    }

    private const double ArcStep = Math.PI / 2;

    /// <summary>Port of ScanArc (arc.cpp:31). Steps round the circle in <=90° bites.</summary>
    private static void ScanArc(IPathSink sink, Point absCenter, Point start, Point end, bool ccw)
    {
        DPoint a = (start - absCenter).ToDPoint();
        DPoint finalC = (end - absCenter).ToDPoint();
        double radius = a.Length;

        double trueAngle = AngleBetweenVecs(finalC, a);
        if (ccw) trueAngle = -trueAngle;
        if (trueAngle <= 0) trueAngle += AngleUtil.TwoPi;

        double nextEnd = VectorToAngle(a);
        double step = ccw ? ArcStep : -ArcStep;

        while (true)
        {
            DPoint c;
            bool doExit = false;

            if (trueAngle > ArcStep)
            {
                nextEnd += step;
                c = AngleToVector(nextEnd) * radius;
            }
            else
            {
                doExit = true;
                c = finalC;
                step = trueAngle;
            }

            ScanArcAux(sink, a, c, absCenter, radius, step);
            if (doExit) break;

            a = c;
            trueAngle -= ArcStep;
        }
    }

    /// <summary>
    /// Port of ScanArcAux (arc.cpp:7). Emits one cubic approximating a circular sweep;
    /// tau is the standard 4/3*tan(theta/4) circle-to-Bezier constant in disguise.
    /// </summary>
    private static void ScanArcAux(IPathSink sink, DPoint a, DPoint c, Point center, double radius, double angle)
    {
        double s = Math.Cos(angle / 2);
        double tau = 4 * s / (3 * (s + 1));

        double divisor = (a.X * c.Y - a.Y * c.X) / (radius * radius);
        if (Math.Abs(divisor) < 1e-12)
        {
            sink.LineTo(new Point(
                (int)Math.Round(c.X) + center.X,
                (int)Math.Round(c.Y) + center.Y));
            return;
        }

        var b = new DPoint((c.Y - a.Y) / divisor, (a.X - c.X) / divisor);
        var tauB = b * tau;

        // Bezier control points are computed about (0,0); adding center makes them absolute.
        var c1 = new Point(
            Round((1 - tau) * a.X + tauB.X) + center.X,
            Round((1 - tau) * a.Y + tauB.Y) + center.Y);
        var c2 = new Point(
            Round((1 - tau) * c.X + tauB.X) + center.X,
            Round((1 - tau) * c.Y + tauB.Y) + center.Y);
        var endPt = new Point(Round(c.X) + center.X, Round(c.Y) + center.Y);

        sink.CubicTo(c1, c2, endPt);
    }

    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>Port of vector_to_angle (vector2d.cpp).</summary>
    private static double VectorToAngle(DPoint v) => Math.Atan2(v.Y, v.X);

    /// <summary>Port of angle_to_vector (vector2d.cpp).</summary>
    private static DPoint AngleToVector(double a) => new(Math.Cos(a), Math.Sin(a));

    /// <summary>Port of angle_between_vecs (vector2d.cpp) — signed, via cross and dot.</summary>
    private static double AngleBetweenVecs(DPoint a, DPoint b)
    {
        double cross = a.X * b.Y - a.Y * b.X;
        double dot = a.X * b.X + a.Y * b.Y;
        return Math.Atan2(cross, dot);
    }
}

/// <summary>
/// An ordered chain of segments forming one drawable outline.
/// Port of CTraj (traj.h:44). A word balloon is a Traj of
/// [Beta spline outline] + [Arc] + [Arc] (the two lips of the tail).
/// </summary>
public sealed class Traj
{
    private readonly List<Seg> _segs = [];

    public IReadOnlyList<Seg> Segments => _segs;
    public bool Closed { get; set; }

    public void AddSeg(Seg seg) => _segs.Add(seg);
    public void Clear() => _segs.Clear();

    /// <summary>Port of CTraj::Draw (traj.cpp:45).</summary>
    public void BuildPath(IPathSink sink)
    {
        bool first = true;
        foreach (var seg in _segs)
        {
            if (first)
            {
                sink.MoveTo(seg.SegLo());
                first = false;
            }
            seg.AppendTo(sink);
        }
        if (Closed) sink.Close();
    }

    /// <summary>Flatten the whole trajectory to polylines.</summary>
    public FlattenSink Flatten(int stepsPerCurve = 24)
    {
        var sink = new FlattenSink(stepsPerCurve);
        BuildPath(sink);
        return sink;
    }
}
