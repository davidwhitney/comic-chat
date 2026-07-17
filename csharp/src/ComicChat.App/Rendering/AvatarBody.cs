using ComicChat.Core.Art;
using ComicChat.Core.Avatars;
using ComicChat.Core.Comic;

namespace ComicChat.App.Rendering;

/// <summary>
/// A <see cref="Body"/> backed by real .avb art. Port of CBodyDouble (avatar.h:144).
/// </summary>
/// <remarks>
/// This is the join between the three subsystems: the expert system picks face and torso
/// indices, the art loader supplies the poses, and the layout engine sees only the dimensions
/// it needs via <see cref="GetDimInfo"/>. A complex avatar composites a head onto a torso;
/// a simple one has a single whole-body pose (its face pose is null).
/// </remarks>
public sealed class AvatarBody : Body
{
    public AvatarFile Avatar { get; }
    public AvatarPoseResolver Resolver { get; }

    public int FaceIndex { get; private set; } = -1;
    public int TorsoIndex { get; private set; } = -1;

    public AvatarBody(uint avatarId, AvatarFile avatar, AvatarPoseResolver resolver)
    {
        AvatarId = avatarId;
        Avatar = avatar;
        Resolver = resolver;
    }

    private AvatarBody(AvatarBody other)
    {
        AvatarId = other.AvatarId;
        Avatar = other.Avatar;
        Resolver = other.Resolver;
        FaceIndex = other.FaceIndex;
        TorsoIndex = other.TorsoIndex;
        Flip = other.Flip;
        Requested = other.Requested;
        ArrowX = other.ArrowX;
        BBox = other.BBox;
    }

    public void SetIndices(int faceIndex, int torsoIndex)
    {
        FaceIndex = faceIndex;
        TorsoIndex = torsoIndex;
    }

    public void Apply(ResolvedBody rb) => SetIndices(rb.FaceIndex, rb.TorsoIndex);

    /// <summary>The torso pose, or for a simple avatar the whole body.</summary>
    public Pose? TorsoPose => Avatar switch
    {
        AvatarComplex c when TorsoIndex >= 0 && TorsoIndex < c.Torsos.Count
            => c.GetPose(c.Torsos[TorsoIndex].PoseId),
        AvatarSimple s when TorsoIndex >= 0 && TorsoIndex < s.Bodies.Count
            => s.GetPose(s.Bodies[TorsoIndex].PoseId),
        _ => null,
    };

    /// <summary>The head pose. Null for simple avatars, which draw a single image.</summary>
    public Pose? FacePose => Avatar is AvatarComplex c && FaceIndex >= 0 && FaceIndex < c.Faces.Count
        ? c.GetPose(c.Faces[FaceIndex].PoseId)
        : null;

    /// <summary>
    /// Where the head is pinned onto the torso, in torso-bitmap pixels (y down).
    /// Port of the xOffset/yOffset calculation in CBodyDouble::GetBodyBox (bodycam.cpp:608).
    /// </summary>
    /// <remarks>
    /// The head is <i>anchored</i> onto the torso and overlaps it — it is not stacked above.
    /// Both art pieces carry a registration point (Cx/Cy); the offset aligns the head's point
    /// with the torso's, and the face record's delta lets a particular expression nudge itself
    /// (a shout tilts the head back, say) without new torso art.
    /// </remarks>
    public (int x, int y) HeadOffset
    {
        get
        {
            if (Avatar is not AvatarComplex c) return (0, 0);
            if (FaceIndex < 0 || FaceIndex >= c.Faces.Count) return (0, 0);
            if (TorsoIndex < 0 || TorsoIndex >= c.Torsos.Count) return (0, 0);

            var f = c.Faces[FaceIndex];
            var t = c.Torsos[TorsoIndex];
            return (t.Cx + f.CxDelta - f.Cx, t.Cy + f.CyDelta - f.Cy);
        }
    }

    /// <summary>
    /// The composite's bounding box in bitmap pixels, with the torso's origin at (0,0).
    /// Port of the bitRect calculation in GetBodyBox (bodycam.cpp:611).
    /// </summary>
    public (int left, int top, int right, int bottom) CompositeRect
    {
        get
        {
            var torso = TorsoPose;
            var face = FacePose;

            int tw = torso?.Width ?? 0, th = torso?.Height ?? 0;

            if (face is null)   // simple avatar: the body is the whole picture
                return (0, 0, tw, th);

            var (ox, oy) = HeadOffset;
            return (Math.Min(0, ox),
                    Math.Min(0, oy),
                    Math.Max(tw, ox + face.Width),
                    Math.Max(th, oy + face.Height));
        }
    }

    public override Body Clone() => new AvatarBody(this);

    public override bool IsSame(Body other) =>
        other is AvatarBody b && b.AvatarId == AvatarId &&
        b.FaceIndex == FaceIndex && b.TorsoIndex == TorsoIndex;

    /// <summary>
    /// Composite dimensions, in <b>bitmap pixels</b>. Port of CBodyDouble::GetDimInfo
    /// (avatar.cpp:76) and CBodySingle::GetDimInfo.
    /// </summary>
    /// <remarks>
    /// These are deliberately raw pixel dimensions, not TWIPS: LayoutAvatars normalises every
    /// character to <c>maxBodyHeight</c> and derives its own scale factor from the ratio
    /// (panel.cpp:765), so handing it pre-scaled units would scale twice.
    ///
    /// <paramref name="normHeight"/> is a constant 100 for every character, exactly as in the
    /// original — its comment reads "right now, we're assuming characters are the same height".
    /// The effect is that everyone ends up the same height regardless of their art's dimensions.
    ///
    /// <paramref name="headHeight"/> is what stops the zoom pass framing so tight that heads
    /// leave the panel — "don't cut at neck" (panel.cpp:794).
    /// </remarks>
    public override void GetDimInfo(out short xdim, out short ydim, out short normHeight,
                                    out short headHeight, out short faceX)
    {
        normHeight = 100;

        var torso = TorsoPose;
        var face = FacePose;

        if (torso is null)
        {
            xdim = ydim = 100;
            headHeight = 50;
            faceX = 50;
            return;
        }

        var (left, top, right, bottom) = CompositeRect;
        xdim = (short)(right - left);
        ydim = (short)(bottom - top);

        if (face is null)
        {
            // Simple avatar: "for now, be conservative -- head = half body!"
            headHeight = (short)(ydim / 2);
            faceX = (short)(Avatar is AvatarSimple s && TorsoIndex >= 0 && TorsoIndex < s.Bodies.Count
                ? s.Bodies[TorsoIndex].FaceX
                : xdim / 2);
        }
        else
        {
            var (ox, oy) = HeadOffset;
            headHeight = (short)(oy + face.Height - top);
            faceX = (short)((Avatar is AvatarComplex c && FaceIndex < c.Faces.Count
                ? c.Faces[FaceIndex].FaceX
                : 0) + ox - left);
        }

        if (Flip) faceX = (short)(xdim - faceX);
    }
}
