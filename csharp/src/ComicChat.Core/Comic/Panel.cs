using ComicChat.Core.Avatars;
using ComicChat.Core.Geometry;

namespace ComicChat.Core.Comic;

/// <summary>Placement priorities for a body in a panel. Port of BR_* (panel.cpp:35-38).</summary>
internal enum BodyPriority : byte
{
    Speaker = 0,
    Important = 1,
    GoodIdea = 2,
    Ok = 3,
}

/// <summary>Scratch record used while ordering bodies. Port of CBodyRecord (panel.cpp:67).</summary>
internal sealed class BodyRecord
{
    public Body Body = null!;
    public BodyPriority Priority;
}

/// <summary>Panel tuning constants. Port of the #defines at panel.cpp:31-46.</summary>
internal static class PanelConst
{
    public const int MaxBdyPerFrame = 20;

    /// <summary>Below this width, a balloon just uses its natural (single-line) width.</summary>
    public const int OneLineThreshold = 500;

    public const int MinHookHeight = 100;

    /// <summary>"FALSE for SIG PIX" (panel.cpp:46) — the zoom-in pass is on in shipped builds.</summary>
    public const bool ZoomIn = true;

    /// <summary>Hard cap on characters per panel, enforced in AddTalkTos (panel.cpp:324).</summary>
    public const int MaxBodiesPerPanel = 5;
}

/// <summary>A single comic panel. Port of CPanel (panel.h:18).</summary>
public abstract class Panel
{
    /// <summary>The balloons in this panel.</summary>
    public List<Balloon> Elements { get; } = [];

    public List<Body> Bodies { get; } = [];

    /// <summary>Per-panel RNG seed. Makes this panel's layout reproducible on replay (panel.cpp:867).</summary>
    public uint Seed { get; set; }

    public bool HasBorder { get; set; } = true;

    public BackDrop BackDrop { get; set; } = new();

    protected Panel() { }

    protected Panel(Panel other)
    {
        Seed = other.Seed;
        HasBorder = other.HasBorder;
        BackDrop = new BackDrop(other.BackDrop);
        foreach (var b in other.Bodies) Bodies.Add(b.Clone());
        foreach (var e in other.Elements)
        {
            var clone = e.Clone();
            // Re-point the cloned balloon at the cloned body, not the original's.
            clone.Speaker = Bodies.FirstOrDefault(b => b.AvatarId == e.Speaker?.AvatarId) ?? clone.Speaker;
            Elements.Add(clone);
        }
    }

    public abstract Panel Clone();
    public abstract void LayoutAvatars(LayoutContext ctx, IAvatarDirectory dir);
    public abstract bool LayoutBalloons(LayoutContext ctx, out string? rest);
    public abstract SRect GetBalloonRect(LayoutContext ctx);

    /// <summary>Port of CPanel::AvatarInPanel (panel.cpp).</summary>
    public virtual bool AvatarInPanel(uint avId) => Bodies.Any(b => b.AvatarId == avId);

    /// <summary>Port of CPanel::FetchSpeaker (panel.cpp).</summary>
    public Body? FetchSpeaker(uint uID) => Bodies.FirstOrDefault(b => b.AvatarId == uID);

    /// <summary>Port of CPanel::ReplaceBody (panel.cpp) — swap in the avatar's current pose.</summary>
    public bool ReplaceBody(uint uID, Body newBody)
    {
        for (int i = 0; i < Bodies.Count; i++)
        {
            if (Bodies[i].AvatarId != uID) continue;
            Bodies[i] = newBody;
            foreach (var e in Elements)
                if (e.Speaker?.AvatarId == uID) e.Speaker = newBody;
            return true;
        }
        Bodies.Add(newBody);
        return false;
    }
}

/// <summary>
/// The square panel used by the comic grid. Port of CUnitPanel (panel.h:45).
/// This class holds the two algorithms that define Comic Chat's art direction:
/// character staging/zoom (<see cref="LayoutAvatars"/>) and balloon placement
/// (<see cref="LayoutBalloon"/>).
/// </summary>
public sealed class UnitPanel : Panel
{
    public UnitPanel() { }
    private UnitPanel(UnitPanel other) : base(other) { }

    public override Panel Clone() => new UnitPanel(this);

    /// <summary>Port of CUnitPanel::IsSpeaker (panel.cpp:822).</summary>
    public bool IsSpeaker(Body bdy)
    {
        if (bdy.Requested) return true;
        return Elements.Any(e => e.Speaker?.AvatarId == bdy.AvatarId);
    }

    /// <summary>
    /// Choose who appears, in what order, facing which way, and at what zoom.
    /// Port of CUnitPanel::LayoutAvatars (panel.cpp:726).
    /// </summary>
    public override void LayoutAvatars(LayoutContext ctx, IAvatarDirectory dir)
    {
        var m = ctx.Metrics;
        var bRecs = new List<BodyRecord>();

        // Only speakers survive; everyone else is dropped from the panel.
        foreach (var b in Bodies)
            if (IsSpeaker(b))
                bRecs.Add(new BodyRecord { Body = b, Priority = BodyPriority.Speaker });

        Bodies.Clear();
        if (bRecs.Count == 0) return;

        var placed = OrderAvatars(bRecs, dir);
        int bdyCount = placed.Count;

        int maxBodyHeight = (int)(m.UnitHeight / 1.9);
        const int minMargin = 0;

        var width = new int[bdyCount];
        var height = new int[bdyCount];
        var normHeight = new int[bdyCount];
        var headHeight = new int[bdyCount];
        var top = new int[bdyCount];
        var arrowX = new double[bdyCount];
        int maxNorm = 0;

        for (int i = 0; i < bdyCount; i++)
        {
            placed[i].Body.GetDimInfo(out short xdim, out short ydim, out short nh, out short hh, out short bitArrowX);
            width[i] = xdim;
            height[i] = ydim;
            normHeight[i] = nh;
            headHeight[i] = hh;
            // Store the attach point as a fraction of width so it survives scaling (panel.cpp:759).
            arrowX[i] = xdim == 0 ? 0.5 : (double)bitArrowX / xdim;
            maxNorm = Math.Max(maxNorm, nh);
        }

        // Normalise every character so the tallest reaches maxBodyHeight, preserving relative heights.
        int bdyWidth = 0;
        for (int i = 0; i < bdyCount; i++)
        {
            int newHeight = Round(maxBodyHeight * ((float)normHeight[i] / Math.Max(1, maxNorm)));
            float scaleRatio = height[i] == 0 ? 1f : (float)newHeight / height[i];
            height[i] = newHeight;
            width[i] = Round(scaleRatio * width[i]);
            top[i] = -m.UnitHeight + height[i];
            headHeight[i] = Round(scaleRatio * headHeight[i]);
            bdyWidth += width[i];
        }

        int sumWidth = bdyWidth + (bdyCount + 1) * minMargin;
        double zoomFactor = 1.0;

        if (sumWidth > m.UnitWidth)
        {
            // Too wide for the panel: shrink everyone to fit, and leave the backdrop alone.
            float reduction = (float)m.UnitWidth / sumWidth;
            bdyWidth = 0;
            for (int i = 0; i < bdyCount; i++)
            {
                height[i] = Round(height[i] * reduction);
                width[i] = Round(width[i] * reduction);
                top[i] = -m.UnitHeight + height[i];
                bdyWidth += width[i];
            }
            AdjustArtToCoord(ctx, 0, 1.0);
        }
        else if (PanelConst.ZoomIn && !Establishing)
        {
            // Room to spare: push in for a closer shot.
            zoomFactor = (double)m.UnitWidth / sumWidth;

            int maxHeadHeight = 0;
            for (int i = 0; i < bdyCount; i++)
                maxHeadHeight = Math.Max(maxHeadHeight, headHeight[i]);

            // Never frame so tight that heads leave the panel — "don't cut at neck" (panel.cpp:794).
            if (maxHeadHeight > 0)
            {
                double headFactor = maxBodyHeight / (maxHeadHeight * 1.2);
                zoomFactor = Math.Min(zoomFactor, headFactor);
            }

            // Deadband: a barely-there zoom would just jitter between panels, so snap it to 1.
            if (zoomFactor < 1.1) zoomFactor = 1.0;

            bdyWidth = 0;
            for (int i = 0; i < bdyCount; i++)
            {
                height[i] = Round(height[i] * zoomFactor);
                width[i] = Round(width[i] * zoomFactor);
                bdyWidth += width[i];
            }
        }

        AdjustArtToCoord(ctx, -m.UnitHeight + maxBodyHeight, zoomFactor);

        // Spread with equal margins, including against the panel borders.
        int margin = (m.UnitWidth - bdyWidth) / (bdyCount + 1);
        int xOffset = margin;
        for (int i = 0; i < bdyCount; i++)
        {
            var b = placed[i].Body;
            Bodies.Add(b);
            b.SetBBox(xOffset, top[i] - height[i], xOffset + width[i], top[i]);
            b.ArrowX = (short)(b.BBox.Left + Round(arrowX[i] * (b.BBox.Right - b.BBox.Left)));
            xOffset += width[i] + margin;
        }

        UpdateHistoresis(placed, dir);
    }

    /// <summary>
    /// True for the comic's opening panels, which are framed wide.
    /// Port of Establishing() (pageview.cpp:823). Set by the page before layout.
    /// </summary>
    public bool Establishing { get; set; }

    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Score one ordered pair of characters. Port of EvalPair (panel.cpp:279).
    /// Lower is better. This function is where Comic Chat's staging rules actually live.
    /// </summary>
    internal static int EvalPair(BodyRecord b1, BodyRecord b2, int deltaPlacement, IAvatarDirectory dir)
    {
        int rating = 0;
        bool desiredDir;

        if (deltaPlacement > 0)
        {
            desiredDir = false;
        }
        else
        {
            desiredDir = true;
            deltaPlacement = -deltaPlacement;
        }

        var st1 = dir.Get(b1.Body.AvatarId);
        if (st1 is null) return 0;

        int nTalkTos = st1.TalkTos.Count;
        if (nTalkTos == 0)
        {
            // Addressing the room: mild preference for facing each other.
            if (b1.Body.Flip != desiredDir) rating += 4;
            if (b2.Body.Flip == desiredDir) rating += 2;
        }
        else
        {
            foreach (var talkTo in st1.TalkTos)
            {
                if (talkTo != b2.Body.AvatarId) continue;

                if (b1.Body.Flip == desiredDir)
                    rating += 4 * (deltaPlacement - 1);   // facing them: reward adjacency
                else
                    rating += 40;                          // talking to someone's back: heavy penalty

                if (b2.Body.Flip == desiredDir)
                    rating += 4;                           // they're facing away from me: minor penalty
            }
        }

        return rating;
    }

    /// <summary>
    /// Penalise orderings that disagree with last panel's. Port of ComputeDisplacementPenalty (panel.cpp:259).
    /// </summary>
    private static int ComputeDisplacementPenalty(List<BodyRecord> arr, IAvatarDirectory dir)
    {
        int penalty = 0;
        for (int i = 0; i < arr.Count; i++)
        {
            var st = dir.Get(arr[i].Body.AvatarId);
            if (st is null) continue;

            if (i > 0 && st.LastRight != arr[i - 1].Body.AvatarId) penalty++;
            if (i < arr.Count - 1 && st.LastLeft != arr[i + 1].Body.AvatarId) penalty++;
        }
        return penalty;
    }

    /// <summary>
    /// Score inserting <paramref name="bdy"/> at <paramref name="index"/>, trying both facings.
    /// Port of EvalPlacement (panel.cpp:358).
    /// </summary>
    private static int EvalPlacement(List<BodyRecord> arr, BodyRecord bdy, int index,
                                     IAvatarDirectory dir, out bool bestDir)
    {
        arr.Insert(index, bdy);

        int penalty = ComputeDisplacementPenalty(arr, dir);
        int ratingR = penalty, ratingL = penalty;

        bdy.Body.Flip = false;
        for (int i = 0; i < arr.Count; i++)
            for (int j = i + 1; j < arr.Count; j++)
                ratingR += EvalPair(arr[i], arr[j], j - i, dir) + EvalPair(arr[j], arr[i], i - j, dir);

        bdy.Body.Flip = true;
        for (int i = 0; i < arr.Count; i++)
            for (int j = i + 1; j < arr.Count; j++)
                ratingL += EvalPair(arr[i], arr[j], j - i, dir) + EvalPair(arr[j], arr[i], i - j, dir);

        arr.RemoveAt(index);

        if (ratingR < ratingL)
        {
            bestDir = false;
            return ratingR;
        }
        if (ratingR > ratingL)
        {
            // NOTE: faithful to panel.cpp:395, which returns ratingL here (the better score).
            bestDir = true;
            return ratingL;
        }

        // Tie: keep facing the way this avatar faced last panel.
        bestDir = dir.Get(bdy.Body.AvatarId)?.LastDir ?? false;
        return ratingR;
    }

    /// <summary>
    /// Insert each body at its best-scoring slot. Port of DoGreedyOrdering (panel.cpp:403).
    /// </summary>
    private static List<BodyRecord> DoGreedyOrdering(List<BodyRecord> bdys, IAvatarDirectory dir)
    {
        var placed = new List<BodyRecord>();

        foreach (var bdy in bdys)
        {
            int bestRating = 1000;
            int bestPosition = 0;
            bool bestDir = false;

            for (int j = 0; j <= placed.Count; j++)
            {
                int rating = EvalPlacement(placed, bdy, j, dir, out bool dirTry);
                if (rating < bestRating)
                {
                    bestRating = rating;
                    bestPosition = j;
                    bestDir = dirTry;
                }
            }

            bdy.Body.Flip = bestDir;
            placed.Insert(bestPosition, bdy);
        }

        return placed;
    }

    /// <summary>Port of OrderAvatars (panel.cpp:424).</summary>
    private static List<BodyRecord> OrderAvatars(List<BodyRecord> bdys, IAvatarDirectory dir)
    {
        if (bdys.Count < PanelConst.MaxBodiesPerPanel)
            AddTalkTos(bdys, dir);
        return DoGreedyOrdering(bdys, dir);
    }

    /// <summary>
    /// Pull the people being addressed into the panel. Port of AddTalkTos (panel.cpp:316).
    /// Hard-capped at 5 bodies — "don't add more than 5 people to the panel!!!".
    /// </summary>
    private static void AddTalkTos(List<BodyRecord> bdys, IAvatarDirectory dir)
    {
        int initialCount = bdys.Count;

        for (int i = 0; i < initialCount; i++)
        {
            var st = dir.Get(bdys[i].Body.AvatarId);
            if (st is null) continue;

            foreach (var talkTo in st.TalkTos)
            {
                if (bdys.Count >= PanelConst.MaxBodiesPerPanel) return;
                if (bdys.Any(r => r.Body.AvatarId == talkTo)) continue;

                var theirState = dir.Get(talkTo);
                var body = theirState?.BodyFromEmotion?.Invoke(new Emotion(0.0, 0.0));
                if (body is null) continue;

                body.Requested = false;   // addressees are optional; they can be dropped
                bdys.Add(new BodyRecord { Body = body, Priority = BodyPriority.GoodIdea });
            }
        }
    }

    /// <summary>Record this panel's staging so the next one can reproduce it. Port of UpdateHistoresis (panel.cpp:435).</summary>
    private static void UpdateHistoresis(List<BodyRecord> placed, IAvatarDirectory dir)
    {
        for (int i = 0; i < placed.Count; i++)
        {
            var st = dir.Get(placed[i].Body.AvatarId);
            if (st is null) continue;

            st.LastDir = placed[i].Body.Flip;
            if (i > 0) st.LastRight = placed[i - 1].Body.AvatarId;
            if (i < placed.Count - 1) st.LastLeft = placed[i + 1].Body.AvatarId;
        }
    }

    /// <summary>
    /// Zoom the backdrop to match the characters. Port of CUnitPanel::AdjustArtToCoord (panel.cpp:948).
    /// Shrinks the backdrop's logical bbox by 1/zoom about a fixed y (the head line).
    /// </summary>
    public void AdjustArtToCoord(LayoutContext ctx, int fixedY, double zoomFactor)
    {
        if ((BackDrop.Mode & BackDropMode.NoZoom) != 0) zoomFactor = 1.0;
        if (zoomFactor <= 0) zoomFactor = 1.0;

        var m = ctx.Metrics;
        int logHeight = Round(m.UnitHeight / zoomFactor);
        int logWidth = Round(m.UnitWidth / zoomFactor);
        int newFixedY = Round(fixedY / zoomFactor);
        int delta = fixedY - newFixedY;

        BackDrop.SetBBox(0, -logHeight + delta, logWidth, delta);
    }

    /// <summary>
    /// The region balloons may occupy: the top half of the panel. Port of GetBalloonRect (panel.cpp:839).
    /// </summary>
    public override SRect GetBalloonRect(LayoutContext ctx)
    {
        var m = ctx.Metrics;
        var brect = new SRect(0, 0, m.UnitWidth, -m.UnitHeight / 2);

        if (HasBorder)
        {
            int penWidth = m.BorderWidth;
            brect.Left += penWidth;
            brect.Right -= penWidth;
            brect.Top -= penWidth;
        }

        return brect;
    }

    /// <summary>
    /// Place every balloon. Port of CUnitPanel::LayoutBalloons (panel.cpp:855).
    /// </summary>
    /// <remarks>
    /// Returns false to mean "this panel cannot hold these balloons" — the caller responds by
    /// starting a new panel and retrying. The single exception is a lone balloon that does not
    /// fit, which is force-fitted and split instead, since retrying it in a fresh panel would
    /// loop forever.
    /// </remarks>
    public override bool LayoutBalloons(LayoutContext ctx, out string? rest)
    {
        rest = null;
        var freeRect = GetBalloonRect(ctx);
        var balloons = Elements;

        // Reseed per panel so this panel always lays out the same way.
        ctx.Rng.Srand(Seed);

        for (int i = 0; i < balloons.Count; i++)
        {
            if (LayoutBalloon(ctx, balloons, i, freeRect)) continue;

            if (i == 0 && balloons.Count == 1)
            {
                ForceFitBalloon(ctx, balloons[0], freeRect, out rest);
                return true;
            }
            return false;
        }

        return true;
    }

    /// <summary>
    /// Last resort for a lone oversized balloon: give it the whole free rect and split off
    /// whatever will not fit. Port of ForceFitBalloon (panel.cpp:155).
    /// </summary>
    private static void ForceFitBalloon(LayoutContext ctx, Balloon balloon, SRect freeRect, out string? rest)
    {
        balloon.SetBBox(freeRect.Left, freeRect.Bottom, freeRect.Right, freeRect.Top, ctx);
        rest = balloon.SplitHeight(freeRect.Top - freeRect.Bottom, ctx);
        if (balloon.BBox.Top > -250)
            balloon.DockAtTop(freeRect.Top);
    }

    /// <summary>
    /// Estimate a balloon's width and x. Port of CUnitPanel::GetCloudEstimate (panel.cpp:885).
    /// </summary>
    /// <remarks>
    /// Width is deliberately randomised within a legal range so repeated lines do not produce
    /// identical clouds; x is randomised too, but constrained so the balloon still overlaps its
    /// speaker (otherwise the tail would have to stretch across the panel).
    /// </remarks>
    private void GetCloudEstimate(LayoutContext ctx, List<Balloon> balloons, int index,
                                  SRect freeRect, ref SRect brect)
    {
        var balloon = balloons[index];
        int area = balloon.AreaEstimate(ctx, out int len, out int lineHeight);
        int maxWidth = freeRect.Right - freeRect.Left;
        int goalWidth;

        if (len <= PanelConst.OneLineThreshold)
        {
            goalWidth = len;
        }
        else
        {
            int potentialHeight = LowestPreviousBottom(balloons, index, freeRect.Top)
                                  - freeRect.Bottom + PanelConst.MinHookHeight;
            if (potentialHeight <= 0) potentialHeight = 1;

            int minWidth = area / potentialHeight;
            minWidth = Math.Max(minWidth, balloon.WidestWord(ctx));
            goalWidth = minWidth + (int)(ctx.RandFloat() * (maxWidth - minWidth));
        }

        // The +200s are the original's acknowledged fudge factors (panel.cpp:909-910).
        goalWidth = Math.Min(goalWidth + 200, maxWidth);
        goalWidth = Math.Min(goalWidth, len + 200);
        if (goalWidth <= 0) goalWidth = Math.Min(maxWidth, 200);

        if ((balloon.GetElementType() & PeType.Box) != 0)
        {
            brect.Left = freeRect.Left;
        }
        else
        {
            int toPtX = balloon.Speaker.ArrowX;
            int leftLimit = toPtX - goalWidth;
            int rightLimit = toPtX;
            int startX = leftLimit + (int)(ctx.RandFloat() * (rightLimit - leftLimit));

            if (startX < freeRect.Left) startX = freeRect.Left;
            if (startX + goalWidth > freeRect.Right) startX = freeRect.Right - goalWidth;
            brect.Left = startX;
        }

        brect.Right = brect.Left + goalWidth;   // top/bottom are decided by GetInterveningBBox
    }

    /// <summary>Port of LowestPreviousBottom (panel.cpp:213).</summary>
    private static int LowestPreviousBottom(List<Balloon> balloons, int index, int lowY)
    {
        for (int i = 0; i < index; i++)
            lowY = Math.Min(lowY, balloons[i].BBox.Bottom);
        return lowY;
    }

    /// <summary>
    /// Fit a balloon's rect between the corridors already claimed by earlier tails.
    /// Port of GetInterveningBBox (panel.cpp:167).
    /// </summary>
    private static bool GetInterveningBBox(List<Balloon> balloons, int index, SRect freeRect, ref SRect irect)
    {
        int toPtX = balloons[index].Speaker.ArrowX;

        // Intersect every earlier balloon's allowance into one interval.
        int mostLeft = freeRect.Left;
        int mostRight = freeRect.Right;
        for (int i = 0; i < index; i++)
        {
            balloons[i].QueryRouteRgn(toPtX, out int leftAllowance, out int rightAllowance);
            mostLeft = Math.Max(leftAllowance, mostLeft);
            mostRight = Math.Min(rightAllowance, mostRight);
        }

        if (mostLeft > irect.Left || mostRight < irect.Right)
        {
            int potentialClearance = mostRight - mostLeft;
            if (potentialClearance >= irect.Right - irect.Left)
            {
                // It fits, just not here — slide it into the legal interval.
                int delta = mostLeft > irect.Left ? mostLeft - irect.Left : mostRight - irect.Right;
                irect.Left += delta;
                irect.Right += delta;
            }
            else
            {
                // It does not fit; take the maximal clearance available.
                irect.Left = mostLeft;
                irect.Right = mostRight;
            }
        }

        // Push the top below anything it would overlap.
        irect.Top = freeRect.Top;
        for (int i = 0; i < index; i++)
        {
            var cloudbox = balloons[i].GetCloudBBox();
            if (cloudbox.Right < irect.Left)
                irect.Top = Math.Min(irect.Top, cloudbox.Top);      // that cloud is clear to the left
            else
                irect.Top = Math.Min(irect.Top, Balloon.Dock(cloudbox).Bottom);
        }

        return true;
    }

    /// <summary>Subtract a newly placed tail's corridor from the earlier ones. Port of AdjustRouteRgns (panel.cpp:247).</summary>
    private static void AdjustRouteRgns(List<Balloon> balloons, int index)
    {
        int left = balloons[index].RouteRgn.Left;
        int right = balloons[index].RouteRgn.Right;
        int toX = balloons[index].Speaker.ArrowX;

        for (int i = 0; i < index; i++)
            balloons[i].SetRouteRgn(toX, left, right);
    }

    /// <summary>
    /// Place one balloon. Port of CUnitPanel::LayoutBalloon (panel.cpp:925).
    /// Returns false if it cannot be placed, which triggers a panel split upstream.
    /// </summary>
    public bool LayoutBalloon(LayoutContext ctx, List<Balloon> balloons, int index, SRect freeRect)
    {
        var brect = new SRect();
        GetCloudEstimate(ctx, balloons, index, freeRect, ref brect);

        if (!GetInterveningBBox(balloons, index, freeRect, ref brect))
            return false;

        var balloon = balloons[index];
        if (!balloon.SetBBox(brect.Left, brect.Bottom, brect.Right, brect.Top, ctx))
            return false;   // this text will not build a balloon at this width

        if (balloon.BBox.Top > -250)
            balloon.DockAtTop(freeRect.Top);

        balloon.RouteRgn = balloon.GetCloudBBox();   // y is not significant for a route region

        // No room left to route a tail down to the speaker.
        if (balloon.RouteRgn.Bottom < freeRect.Bottom + PanelConst.MinHookHeight)
            return false;

        AdjustRouteRgns(balloons, index);
        return true;
    }
}
