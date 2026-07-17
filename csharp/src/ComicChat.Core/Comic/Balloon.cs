using ComicChat.Core.Geometry;

namespace ComicChat.Core.Comic;

/// <summary>Balloon tuning constants. Port of the #defines at balloon.cpp:51-74.</summary>
internal static class BalloonConst
{
    public const int XBoxDelta = 90;
    public const int YBoxDelta = 50;

    /// <summary>Minimum horizontal corridor a tail needs to reach its speaker.</summary>
    public const int MinRouteWidth = 300;

    public const int BubbleHeight = 150;
    public const int InterBubble = 100;
    public const int EndBubbleWidth = 400;

    public const int VWaveHeight = 70;
    public const int VWaveInterval = 300;
    public const int HWaveHeight = 70;
    public const int HWaveInterval = 300;

    public const int MaxPts = 150;

    /// <summary>Line-edge deltas beyond which the outline starts a new run. balloon.cpp:62-63.</summary>
    public const int Thresh1 = -70;
    public const int Thresh2 = 70;

    public const int XBorder = 100;
    public const int YBorder = 40;
    public const int TopBorder = -20;

    public const int LargeDelta = 350;
    public const int SmallDelta = 150;
    public const int MinTailHeight = 100;

    /// <summary>"Yum fudge!" (balloon.cpp:74) — slack subtracted before computing how many lines fit.</summary>
    public const int BorderFudge = 400;

    public const int LargeInteger = int.MaxValue / 4;
}

/// <summary>A run of consecutive lines sharing an outline edge. Port of RANGE (balloon.cpp:84).</summary>
internal struct Range
{
    public int Start;
    public int End;
    public int X;
    public int Y;
}

/// <summary>
/// A word balloon. Port of CBalloon (balloon.h:125).
/// </summary>
/// <remarks>
/// Two bounding boxes matter and are easy to confuse:
/// <see cref="PanelElement.BBox"/> is the <i>text</i> box (inherited from Label), while
/// <see cref="TrueBox"/> is the actual cloud extent relative to the balloon's origin.
/// <see cref="RouteRgn"/> is the vertical corridor this balloon's tail reserves — the
/// mechanism that guarantees tails never cross.
/// </remarks>
public abstract class Balloon : Label
{
    /// <summary>The body this balloon's tail points at.</summary>
    public Body Speaker { get; set; } = null!;

    public Spline? BalloonSpline { get; protected set; }
    public FormatInfo? FInfo { get; protected set; }

    /// <summary>The cloud's true extent, relative to the balloon's origin. balloon.h:131.</summary>
    public SRect TrueBox;

    /// <summary>The corridor reserved for this balloon's tail. Only x is significant. balloon.h:132.</summary>
    public SRect RouteRgn;

    public Traj? BalloonTraj { get; protected set; }

    protected Balloon(string text, FontInfo fontInfo) : base(text, fontInfo) { }

    protected Balloon(Balloon other) : base(other)
    {
        Speaker = other.Speaker;
        TrueBox = other.TrueBox;
        RouteRgn = other.RouteRgn;
        BalloonSpline = other.BalloonSpline?.Clone();
        FInfo = other.FInfo?.Clone();
        // Traj is rebuilt on demand; cloning it would alias the spline segment.
    }

    public override PeType GetElementType() => PeType.Balloon;

    public abstract Balloon Clone();

    /// <summary>Rebuild line breaks, outline and cloud bbox. Port of ComputeInternals (balloon.cpp).</summary>
    public abstract bool ComputeInternals(LayoutContext ctx);

    /// <summary>Port of CBalloon::ComputeCloudBBox (balloon.cpp) — the outline's control-point hull.</summary>
    public virtual void ComputeCloudBBox()
    {
        if (BalloonSpline is null) return;

        var cps = BalloonSpline.ControlPoints;
        int left = int.MaxValue, right = int.MinValue, top = int.MinValue, bottom = int.MaxValue;
        foreach (var p in cps)
        {
            left = Math.Min(left, p.X);
            right = Math.Max(right, p.X);
            top = Math.Max(top, p.Y);
            bottom = Math.Min(bottom, p.Y);
        }
        TrueBox = new SRect(left, top, right, bottom);
    }

    /// <summary>Port of CBalloon::GetCloudBBox (balloon.cpp) — TrueBox translated to panel coords.</summary>
    public SRect GetCloudBBox() =>
        new(TrueBox.Left + BBox.Left,
            TrueBox.Top + BBox.Top,
            TrueBox.Right + BBox.Left,
            TrueBox.Bottom + BBox.Top);

    /// <summary>
    /// Port of CBalloon::SetBBox (balloon.cpp). Only rebuilds the outline when the
    /// dimensions actually change; otherwise it just moves the origin.
    /// </summary>
    public virtual bool SetBBox(int left, int bottom, int right, int top, LayoutContext ctx)
    {
        if (BBox.Right - BBox.Left != right - left ||
            BBox.Top - BBox.Bottom != top - bottom)
        {
            BBox.Left = 0;
            BBox.Right = (right - left) - 2 * BalloonConst.XBorder;   // estimate
            BBox.Top = 0;

            // Deliberately ignores the incoming bbox except for width: the height that
            // results from breaking the text is what decides the real bottom.
            if (!ComputeInternals(ctx))
                return false;

            bottom = top + TrueBox.Bottom - TrueBox.Top;
        }

        BBox.Left = left - TrueBox.Left;
        BBox.Right = right - TrueBox.Left;
        BBox.Top = top - TrueBox.Top;
        BBox.Bottom = bottom - TrueBox.Top;
        return true;
    }

    /// <summary>Port of CBalloon::DockAtTop (balloon.cpp).</summary>
    public virtual void DockAtTop(int height)
    {
        int oldHeight = BBox.Top - BBox.Bottom;
        BBox.Top = height + BalloonConst.TopBorder;
        BBox.Bottom = BBox.Top - oldHeight;
    }

    /// <summary>
    /// Port of Dock (balloon.cpp:568). Nudges a cloud box down by the outline's top padding
    /// so a balloon placed beneath it clears the wavy edge rather than the text box.
    /// </summary>
    public static SRect Dock(SRect rect)
    {
        int delta = BalloonConst.TopBorder + BalloonConst.YBorder + BalloonConst.HWaveHeight;
        rect.Top += delta;
        rect.Bottom += delta;
        return rect;
    }

    /// <summary>
    /// Port of CBalloon::QueryRouteRgn (balloon.cpp:1358). Reports how far left/right a new
    /// balloon may extend without blocking this one's tail.
    /// </summary>
    /// <remarks>
    /// This is the crux of the non-crossing guarantee: rather than intersecting geometry, the
    /// engine keeps a 1-D interval per tail and hands out half-planes. A speaker to our right
    /// may not encroach left of our corridor, and vice versa.
    /// </remarks>
    public virtual void QueryRouteRgn(int otherToX, out int leftAllowance, out int rightAllowance)
    {
        int toX = Speaker.ArrowX;

        if (otherToX > toX)
        {
            leftAllowance = Math.Max(toX, RouteRgn.Left + BalloonConst.MinRouteWidth);
            rightAllowance = BalloonConst.LargeInteger;
        }
        else
        {
            leftAllowance = -BalloonConst.LargeInteger;
            rightAllowance = Math.Min(toX, RouteRgn.Right - BalloonConst.MinRouteWidth);
        }
    }

    /// <summary>Port of CBalloon::SetRouteRgn (balloon.cpp) — subtract a newer tail's corridor from ours.</summary>
    public virtual void SetRouteRgn(int otherToX, int left, int right)
    {
        int toX = Speaker.ArrowX;

        if (otherToX > toX)
            RouteRgn.Right = Math.Min(RouteRgn.Right, left);
        else
            RouteRgn.Left = Math.Max(RouteRgn.Left, right);
    }

    /// <summary>Split off any text that does not fit in <paramref name="height"/>; null if it all fits.</summary>
    public abstract string? SplitHeight(int height, LayoutContext ctx);

    /// <summary>Rebuild <see cref="BalloonTraj"/> — the drawable outline plus tail.</summary>
    public abstract void SetBalloonTraj();
}
