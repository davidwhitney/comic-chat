using ComicChat.Core.Geometry;

namespace ComicChat.Core.Comic;

/// <summary>Element type flags. Port of the PE_* constants (pe.h).</summary>
[Flags]
public enum PeType
{
    None = 0,
    Balloon = 1,
    Box = 2,
    Label = 4,
    Body = 8,
    BackDrop = 16,
    Frame = 32,
}

/// <summary>Anything with a bounding box that can draw itself into a panel. Port of CPanelElement (pe.h:10).</summary>
public abstract class PanelElement
{
    public SRect BBox;

    public virtual PeType GetElementType() => PeType.None;

    public virtual void SetBBox(int left, int bottom, int right, int top)
    {
        BBox.Left = left;
        BBox.Bottom = bottom;
        BBox.Right = right;
        BBox.Top = top;
    }

    public virtual SRect GetBBox() => BBox;
}

/// <summary>
/// A posed character occupying space in a panel. Port of CBody (avatar.h:88).
/// </summary>
/// <remarks>
/// The engine only ever sees a Body through this shape — dimensions, facing, and the tail
/// attach point. The actual art lives behind <see cref="PoseSource"/>, which the Art layer
/// supplies, keeping layout independent of image decoding.
/// </remarks>
public abstract class Body : PanelElement
{
    public uint AvatarId { get; set; }

    /// <summary>Facing. FALSE = facing right, TRUE = facing left (flipped). Chosen by EvalPlacement (panel.cpp:390).</summary>
    public bool Flip { get; set; }

    /// <summary>
    /// True if this body is here because it spoke (or was explicitly required), rather than
    /// having been pulled in as an addressee. IsSpeaker (panel.cpp:823) keeps only these.
    /// </summary>
    public bool Requested { get; set; } = true;

    /// <summary>
    /// Absolute x where this character's balloon tail attaches.
    /// Carried through scaling as a fraction of body width, then re-absolutised (panel.cpp:759, 814).
    /// </summary>
    public short ArrowX { get; set; }

    public override PeType GetElementType() => PeType.Body;

    public abstract Body Clone();

    public abstract bool IsSame(Body other);

    /// <summary>
    /// Port of CBody::GetDimInfo (avatar.h:96). Supplies the raw art dimensions the
    /// zoom/placement pass scales.
    /// </summary>
    /// <param name="xdim">Art width.</param>
    /// <param name="ydim">Art height.</param>
    /// <param name="normHeight">Nominal height used to normalise characters against each other.</param>
    /// <param name="headHeight">Head height; clamps zoom so the framing never "cuts at the neck".</param>
    /// <param name="faceX">Tail attach x, measured from the left of the bitmap.</param>
    public abstract void GetDimInfo(out short xdim, out short ydim, out short normHeight,
                                    out short headHeight, out short faceX);
}
