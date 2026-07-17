using ComicChat.Core.Geometry;

namespace ComicChat.Core.Comic;

/// <summary>
/// The standard Comic Chat cloud, in Jim Woodring's style.
/// Port of CBWoodringNormal (balloon.h:158).
/// </summary>
public class WoodringNormal : Balloon
{
    /// <summary>Nonzero draws the nimbus dashed — how a whisper reads as a whisper.</summary>
    public byte Dashed { get; protected set; }

    /// <summary>Outline pen width. Port of CBWoodringNormal::m_pen (balloon.cpp:96).</summary>
    public const int PenWidth = 28;

    /// <summary>White halo drawn behind the outline so it stands off the art. balloon.cpp:97.</summary>
    public const int NimbusPenWidth = 100;

    public WoodringNormal(string text, FontInfo fontInfo, byte dashed = 0)
        : base(text, fontInfo) => Dashed = dashed;

    protected WoodringNormal(WoodringNormal other) : base(other) => Dashed = other.Dashed;

    public override Balloon Clone() => new WoodringNormal(this);

    /// <summary>Port of CBWoodringNormal::ComputeInternals (balloon.cpp).</summary>
    public override bool ComputeInternals(LayoutContext ctx)
    {
        FInfo ??= new FormatInfo();
        if (BreakIntoLines(FInfo, ctx) == 0)
            return false;
        ShiftLines(FInfo, ctx);
        BalloonSpline = CreateBalloonSpline(FInfo, ctx);
        ComputeCloudBBox();
        return true;
    }

    /// <summary>
    /// Build the cloud outline. Port of CBWoodringNormal::CreateBalloonSpline (balloon.cpp:1700).
    /// </summary>
    /// <remarks>
    /// The outline is not an ellipse: it hugs the ragged text block. GetFilters coalesces the
    /// lines into a few runs of similar edge, PermuteFilters turns those into corners, AddWavies
    /// adds the hand-drawn wobble, and the whole point set becomes one closed beta spline.
    /// </remarks>
    public virtual Spline? CreateBalloonSpline(FormatInfo fInfo, LayoutContext ctx)
    {
        var lFilters = new Range[20];
        var rFilters = new Range[20];
        var pts = new List<Point>(BalloonConst.MaxPts);

        GetFilters(fInfo, lFilters, rFilters, out int nL, out int nR);
        int finalY = PermuteFilters(FontI, lFilters, rFilters, nL, nR);
        int lastY = finalY;

        // Down the left edge.
        for (int i = 0; i < nL; i++)
        {
            var thisPoint = new Point(lFilters[i].X, lFilters[i].Y);
            if (i > 0) AddWavies(pts[^1], thisPoint, pts, BalloonConst.HWaveHeight, BalloonConst.HWaveInterval);
            pts.Add(thisPoint);

            int nextY = i == nL - 1 ? finalY : lFilters[i + 1].Y;
            var nextPoint = new Point(lFilters[i].X, nextY);
            AddWavies(pts[^1], nextPoint, pts, BalloonConst.VWaveHeight, BalloonConst.VWaveInterval);
            pts.Add(nextPoint);
        }

        // Back up the right edge.
        for (int i = nR - 1; i >= 0; i--)
        {
            var thisPoint = new Point(rFilters[i].X, lastY);
            AddWavies(pts[^1], thisPoint, pts, BalloonConst.HWaveHeight, BalloonConst.HWaveInterval);
            pts.Add(thisPoint);

            lastY = rFilters[i].Y;
            var nextPoint = new Point(rFilters[i].X, lastY);
            AddWavies(pts[^1], nextPoint, pts, BalloonConst.VWaveHeight, BalloonConst.VWaveInterval);
            pts.Add(nextPoint);
        }

        AddWavies(pts[^1], pts[0], pts, BalloonConst.HWaveHeight, BalloonConst.HWaveInterval);

        if (pts.Count < 4) return null;
        if (pts.Count > BalloonConst.MaxPts) pts.RemoveRange(BalloonConst.MaxPts, pts.Count - BalloonConst.MaxPts);

        return new Beta([.. pts], isClosed: true);
    }

    /// <summary>
    /// Coalesce lines into runs of similar edge. Port of GetFilters (balloon.cpp:466).
    /// </summary>
    /// <remarks>
    /// Without this the outline would step once per line and look mechanical. A line that
    /// indents dramatically only starts a new run if the <i>following</i> line agrees — a
    /// one-off short line gets absorbed rather than carving a notch in the cloud.
    /// </remarks>
    internal static void GetFilters(FormatInfo fInfo, Range[] l, Range[] r, out int nL, out int nR)
    {
        nL = 0;
        nR = 0;
        l[nL].X = fInfo.LeftX[0];
        r[nR].X = fInfo.LeftX[0] + fInfo.Widths[0];
        l[nL].Start = r[nR].Start = 0;

        for (int i = 1; i < fInfo.NLines; i++)
        {
            int thisLeft = fInfo.LeftX[i];
            int thisRight = fInfo.LeftX[i] + fInfo.Widths[i];
            int leftDelta = thisLeft - l[nL].X;
            int rightDelta = thisRight - r[nR].X;

            if (leftDelta <= BalloonConst.Thresh1)
            {
                // Extends dramatically to the left — always a new run.
                l[nL].End = i - 1;
                l[++nL].Start = i;
                l[nL].X = thisLeft;
            }
            else if (leftDelta <= 0)
            {
                l[nL].X = thisLeft;   // extends marginally; just widen the run
            }
            else if (leftDelta >= BalloonConst.Thresh2)
            {
                // Indents dramatically to the right — only honour it if the next line agrees.
                int nextLeft = i + 1 < fInfo.NLines ? fInfo.LeftX[i + 1] : thisLeft;
                if (nextLeft - l[nL].X >= BalloonConst.Thresh2)
                {
                    l[nL].End = i - 1;
                    l[++nL].Start = i;
                    l[nL].X = Math.Min(thisLeft, nextLeft);
                }
            }

            if (rightDelta >= -BalloonConst.Thresh1)
            {
                r[nR].End = i - 1;
                r[++nR].Start = i;
                r[nR].X = thisRight;
            }
            else if (rightDelta >= 0)
            {
                r[nR].X = thisRight;
            }
            else if (rightDelta <= -BalloonConst.Thresh2)
            {
                int nextRight = i + 1 < fInfo.NLines
                    ? fInfo.LeftX[i + 1] + fInfo.Widths[i + 1]
                    : thisRight;
                if (nextRight - r[nR].X <= -BalloonConst.Thresh2)
                {
                    r[nR].End = i - 1;
                    r[++nR].Start = i;
                    r[nR].X = Math.Max(thisRight, nextRight);
                }
            }
        }

        l[nL++].End = r[nR++].End = fInfo.NLines - 1;
    }

    /// <summary>
    /// Turn edge runs into outline corners. Port of PermuteFilters (balloon.cpp:515).
    /// Returns the final (bottom) y of the outline.
    /// </summary>
    internal static int PermuteFilters(FontInfo fontI, Range[] lFilters, Range[] rFilters, int nL, int nR)
    {
        int baseY = 0;
        int lastX = BalloonConst.LargeInteger;

        for (int i = 0; i < nL; i++)
        {
            lFilters[i].X -= BalloonConst.XBorder;
            if (i == 0)
                lFilters[i].Y = baseY + BalloonConst.TopBorder + BalloonConst.YBorder + fontI.TopOffset;
            else if (lFilters[i].X < lastX)
                lFilters[i].Y = baseY + BalloonConst.YBorder;
            else
                lFilters[i].Y = baseY - BalloonConst.YBorder - fontI.BaseAdd;  // font sits offset vertically

            baseY -= (lFilters[i].End - lFilters[i].Start + 1) * fontI.LineHeight;
            lastX = lFilters[i].X;
        }

        baseY = 0;
        lastX = -BalloonConst.LargeInteger;

        for (int i = 0; i < nR; i++)
        {
            rFilters[i].X += BalloonConst.XBorder;
            if (i == 0)
                rFilters[i].Y = baseY + BalloonConst.TopBorder + BalloonConst.YBorder + fontI.TopOffset;
            else if (rFilters[i].X > lastX)
                rFilters[i].Y = baseY + BalloonConst.YBorder;
            else
                rFilters[i].Y = baseY - BalloonConst.YBorder - fontI.BaseAdd;

            baseY -= (rFilters[i].End - rFilters[i].Start + 1) * fontI.LineHeight;
            lastX = rFilters[i].X;
        }

        return baseY - BalloonConst.TopBorder - BalloonConst.YBorder - fontI.BaseAdd;
    }

    /// <summary>
    /// Insert alternating perpendicular bumps along an edge. Port of AddWavies (balloon.cpp:546).
    /// This is what makes the cloud look hand-drawn rather than CAD-drawn.
    /// </summary>
    internal static void AddWavies(Point pt1, Point pt2, List<Point> pts, int waveDiam, int interval)
    {
        double dist = pt1.ToDPoint().DistanceTo(pt2.ToDPoint());
        if (dist < 1e-9) return;

        double nWaves = dist / interval;
        if (nWaves < 2) return;   // too short to bother

        int iWaves = (int)nWaves;
        double waveLen = dist / iWaves;

        var unitVec = (pt2.ToDPoint() - pt1.ToDPoint()) / dist;
        var incVec = new Point(
            (int)Math.Round(waveLen * unitVec.X, MidpointRounding.AwayFromZero),
            (int)Math.Round(waveLen * unitVec.Y, MidpointRounding.AwayFromZero));

        var normalVec = new DPoint(unitVec.Y, -unitVec.X);
        var extraVec = new Point(
            (int)Math.Round(waveDiam * normalVec.X, MidpointRounding.AwayFromZero),
            (int)Math.Round(waveDiam * normalVec.Y, MidpointRounding.AwayFromZero));

        var thisBase = pt1;
        for (int i = 0; i < iWaves - 1; i++)
        {
            thisBase += incVec;
            pts.Add((i & 1) == 0 ? thisBase + extraVec : thisBase);
        }
    }

    /// <summary>Port of CBWoodringNormal::SetBalloonTraj (balloon.cpp).</summary>
    public override void SetBalloonTraj()
    {
        if (BalloonSpline is null || FInfo is null) return;

        BalloonTraj = new Traj();
        var newSpline = BalloonSpline.Clone();
        BalloonTraj.AddSeg(newSpline);
        AddArrow(this, newSpline, FInfo);
        BalloonTraj.Closed = true;
    }

    /// <summary>Clone the outline and attach a tail, without disturbing the stored spline.</summary>
    public Spline? GetBalloonSpline()
    {
        if (BalloonSpline is null || FInfo is null) return null;
        var spline = BalloonSpline.Clone();
        AddArrow(this, spline, FInfo);
        return spline;
    }

    /// <summary>
    /// Route the tail from the cloud to the speaker. Port of CBWoodringNormal::AddArrow (balloon.cpp:1466).
    /// </summary>
    /// <remarks>
    /// The tail enters at the middle of the reserved route region, nudged to meet the last line
    /// of text sensibly, with its angle clamped to 45° from vertical so it never shears across
    /// the panel. It then cuts a gap out of the closed cloud and bridges the two lips to the
    /// speaker with a pair of opposite-bowing arcs.
    /// </remarks>
    public virtual void AddArrow(Balloon balloon, Spline spline, FormatInfo fInfo)
    {
        if (balloon.Speaker is null || BalloonTraj is null) return;

        var bottom2 = new Point(balloon.Speaker.ArrowX, balloon.Speaker.BBox.Top + 200);
        var bottom = new Point(bottom2.X - balloon.BBox.Left, bottom2.Y - balloon.BBox.Top);

        var cbbox = balloon.GetCloudBBox();
        int xbreak = (balloon.RouteRgn.Left + balloon.RouteRgn.Right) / 2 - balloon.BBox.Left;

        int bottomStart = fInfo.LeftX[fInfo.NLines - 1];
        int bottomEnd = bottomStart + fInfo.Widths[fInfo.NLines - 1];

        // Prefer to meet the last line of text rather than dangle off empty cloud.
        if (xbreak < bottomStart && bottomStart + balloon.BBox.Left < balloon.RouteRgn.Right - BalloonConst.LargeDelta)
            xbreak = bottomStart + BalloonConst.SmallDelta;
        else if (xbreak > bottomEnd && bottomEnd + balloon.BBox.Left > balloon.RouteRgn.Left + BalloonConst.LargeDelta)
            xbreak = bottomEnd - BalloonConst.SmallDelta;

        var top2 = new Point(xbreak + balloon.BBox.Left, cbbox.Bottom);

        if (top2.Y - bottom2.Y < BalloonConst.MinTailHeight)
        {
            bottom2 = new Point(bottom2.X, top2.Y - BalloonConst.MinTailHeight);
            bottom = new Point(bottom.X, bottom2.Y - balloon.BBox.Top);
        }

        var delta = top2 - bottom2;
        double ang = Math.Atan2(delta.Y, delta.X);

        // Clamp to 45° from vertical: pull xbreak back toward the character instead of shearing.
        if (Math.Abs(ang) - Math.PI / 2.0 > Math.PI / 4.0)
        {
            ang = ang > 3 * Math.PI / 4.0 ? 3 * Math.PI / 4.0 : Math.PI / 4.0;
            int heightDelta = top2.Y - bottom2.Y;
            xbreak = (int)(Math.Cos(ang) * heightDelta + bottom2.X - balloon.BBox.Left);
        }

        const double oFactor = 1.0;
        BreakSpline(spline, xbreak, fInfo.BBox.Bottom, oFactor);

        var left = spline.ControlPoints[^1];
        var right = spline.ControlPoints[0];
        top2 = new Point(
            (left.X + right.X) / 2 + balloon.BBox.Left,
            (left.Y + right.Y) / 2 + balloon.BBox.Top);

        int tailLen = (int)top2.ToDPoint().DistanceTo(bottom2.ToDPoint());
        int alt = (int)(0.05 * tailLen);
        int sign = bottom.X > left.X ? 1 : -1;

        // Two arcs bowing opposite ways give the tail its pinched silhouette.
        BalloonTraj.AddSeg(new Arc(left, bottom, sign * alt));
        BalloonTraj.AddSeg(new Arc(bottom, right, -sign * alt));
    }

    /// <summary>
    /// Cut an opening out of the closed cloud for the tail. Port of BreakSpline (balloon.cpp:435).
    /// </summary>
    /// <remarks>
    /// Finds the outline point nearest the gap's left edge, walks the outline rightwards a gap's
    /// width, then rebuilds the control points excluding that arc and reopens the spline. The
    /// rebuilt array starts at the right lip and wraps, so the tail's two arcs can join
    /// <c>cps[^1]</c> (left lip) and <c>cps[0]</c> (right lip).
    /// </remarks>
    internal static void BreakSpline(Spline spline, int x, int y, double oFactor)
    {
        int nCps = spline.ControlPointCount;
        var cps = spline.ControlPoints;

        int gapwidth = (int)(80 * oFactor);
        var left = new Point(x - gapwidth, y);

        var leftNearest = spline.ClosestPoint(left, out int leftKnotIndex);
        var rightNearest = spline.WalkHorizontalDistance(
            leftNearest, leftKnotIndex, leftNearest.X + 2 * gapwidth, out int rightKnotIndex);

        var newCps = new Point[nCps + 2];
        newCps[0] = rightNearest;
        for (int i = 1; i <= nCps; i++)
            newCps[i] = cps[((rightKnotIndex + i - 2) % nCps + nCps) % nCps];

        int nCpsNew = nCps + 2 - ((rightKnotIndex - leftKnotIndex + nCps) % nCps);
        if (nCpsNew < 4) nCpsNew = Math.Min(4, nCps + 2);
        newCps[nCpsNew - 1] = leftNearest;

        spline.SetControlPoints(newCps[..nCpsNew], closed: false);
    }

    /// <summary>
    /// Drop any lines that will not fit in <paramref name="height"/> and return the remainder.
    /// Port of CBWoodringNormal::SplitHeight (balloon.cpp:1534). The caller spills the
    /// remainder into the next panel.
    /// </summary>
    public override string? SplitHeight(int height, LayoutContext ctx)
    {
        if (FInfo is null) return null;

        int maxLines = (height - BalloonConst.BorderFudge) / FontI.LineHeight;
        if (maxLines >= FInfo.NLines) return null;
        if (maxLines < 1) maxLines = 1;

        var kept = new System.Text.StringBuilder();
        for (int i = 0; i < maxLines; i++)
        {
            if (i > 0) kept.Append(' ');
            kept.Append(FInfo.Starts[i]);
        }

        var rest = new System.Text.StringBuilder();
        for (int i = maxLines; i < FInfo.NLines; i++)
        {
            if (rest.Length > 0) rest.Append(' ');
            rest.Append(FInfo.Starts[i]);
        }

        FInfo.NLines = maxLines;
        Str = kept.ToString();
        ComputeInternals(ctx);

        return rest.Length > 0 ? rest.ToString() : null;
    }
}

/// <summary>A whisper: the same cloud, dashed. Port of CBWoodringWhisper (balloon.h:176).</summary>
public sealed class WoodringWhisper : WoodringNormal
{
    public WoodringWhisper(string text, FontInfo fontInfo) : base(text, fontInfo, dashed: 1) { }
    private WoodringWhisper(WoodringWhisper other) : base(other) { }
    public override Balloon Clone() => new WoodringWhisper(this);
}

/// <summary>
/// A thought balloon. Port of CBWoodringThink (balloon.h:184).
/// AddArrow is overridden to do nothing — thoughts trail bubbles instead of a tail.
/// </summary>
public sealed class WoodringThink : WoodringNormal
{
    public WoodringThink(string text, FontInfo fontInfo) : base(text, fontInfo) { }
    private WoodringThink(WoodringThink other) : base(other) { }
    public override Balloon Clone() => new WoodringThink(this);

    public override void AddArrow(Balloon balloon, Spline spline, FormatInfo fInfo) { }
}

/// <summary>
/// A rectangular action box. Port of CBWoodringBox (balloon.h:193).
/// </summary>
/// <remarks>
/// Has no spline and no tail, and its route region is unbounded, so action boxes never
/// constrain anyone else's tail (QueryRouteRgn, balloon.cpp:1911).
/// </remarks>
public sealed class WoodringBox : WoodringNormal
{
    public WoodringBox(string text, FontInfo fontInfo, byte dashed = 0) : base(text, fontInfo, dashed) { }
    private WoodringBox(WoodringBox other) : base(other) { }

    public override Balloon Clone() => new WoodringBox(this);
    public override PeType GetElementType() => PeType.Balloon | PeType.Box;

    public override Spline? CreateBalloonSpline(FormatInfo fInfo, LayoutContext ctx) => null;
    public override void AddArrow(Balloon balloon, Spline spline, FormatInfo fInfo) { }

    /// <summary>A box's extent comes straight from the text box, since there is no outline.</summary>
    public override void ComputeCloudBBox()
    {
        if (FInfo is null) return;
        TrueBox = new SRect(
            FInfo.BBox.Left - BalloonConst.XBoxDelta,
            FInfo.BBox.Top + BalloonConst.YBoxDelta,
            FInfo.BBox.Right + BalloonConst.XBoxDelta,
            FInfo.BBox.Bottom - BalloonConst.YBoxDelta);
    }

    /// <summary>Port of CBWoodringBox::SetBalloonTraj (balloon.cpp:1878) — four straight lines.</summary>
    public override void SetBalloonTraj()
    {
        BalloonTraj = new Traj();
        var b = TrueBox;
        var tl = new Point(b.Left, b.Top);
        var tr = new Point(b.Right, b.Top);
        var br = new Point(b.Right, b.Bottom);
        var bl = new Point(b.Left, b.Bottom);

        BalloonTraj.AddSeg(new Line(tl, tr));
        BalloonTraj.AddSeg(new Line(tr, br));
        BalloonTraj.AddSeg(new Line(br, bl));
        BalloonTraj.AddSeg(new Line(bl, tl));
        BalloonTraj.Closed = true;
    }

    /// <summary>Unbounded: an action box never blocks a tail. Port of balloon.cpp:1911.</summary>
    public override void QueryRouteRgn(int otherToX, out int leftAllowance, out int rightAllowance)
    {
        leftAllowance = -BalloonConst.LargeInteger;
        rightAllowance = BalloonConst.LargeInteger;
    }

    public override void SetRouteRgn(int otherToX, int left, int right) { }
}
