using ComicChat.Core.Geometry;

namespace ComicChat.Core.Comic;

/// <summary>Backdrop flags. Port of BF_NOZOOM (backdrop.h:34).</summary>
[Flags]
public enum BackDropMode : byte
{
    None = 0,

    /// <summary>This backdrop opts out of the camera zoom and stays fixed.</summary>
    NoZoom = 1,
}

/// <summary>
/// The panel's background. Port of CBackDrop (backdrop.h:38).
/// </summary>
/// <remarks>
/// The zoom is implemented here rather than by scaling a bitmap: the backdrop's logical bbox
/// is shrunk about the characters' head line, and drawing maps that bbox to a fraction of the
/// source image. So zooming in crops a smaller source rect and stretches it over the panel —
/// a real camera, not a resize.
/// </remarks>
public sealed class BackDrop : PanelElement
{
    /// <summary>Id into the backdrop art registry.</summary>
    public ushort BackId { get; set; }

    public BackDropMode Mode { get; set; }

    public BackDrop()
    {
        BBox = new SRect(0, 0, 4860, -4860);
    }

    public BackDrop(BackDrop other)
    {
        BackId = other.BackId;
        Mode = other.Mode;
        BBox = other.BBox;
    }

    public override PeType GetElementType() => PeType.BackDrop;

    /// <summary>
    /// The source rectangle, in image pixels, that should be stretched over the panel.
    /// Port of the mapping in CBackDrop::Draw (backdrop.cpp:341-348).
    /// </summary>
    /// <remarks>
    /// <paramref name="panelHeight"/> is a positive device height; the engine's y is
    /// negative-down, which is why Top/Bottom divide through to negative fractions and
    /// still land the right way up.
    /// </remarks>
    public (int srcLeft, int srcTop, int srcWidth, int srcHeight) GetSourceRect(
        int panelWidth, int panelHeight, int bitWidth, int bitHeight)
    {
        static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

        int srcLeft = Round((double)BBox.Left / panelWidth * bitWidth);
        int srcTop = Round((double)-BBox.Top / panelHeight * bitHeight);
        int srcRight = Round((double)BBox.Right / panelWidth * bitWidth);
        int srcBottom = Round((double)-BBox.Bottom / panelHeight * bitHeight);

        return (srcLeft, srcTop, srcRight - srcLeft, srcBottom - srcTop);
    }
}
