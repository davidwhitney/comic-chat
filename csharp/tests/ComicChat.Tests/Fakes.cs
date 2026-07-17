using ComicChat.Core.Comic;

namespace ComicChat.Tests;

/// <summary>
/// A monospace text measurer. Keeps layout tests deterministic and independent of any
/// real font stack, which is exactly why Core takes ITextMeasurer rather than a DC.
/// </summary>
public sealed class FakeTextMeasurer(int charWidth = 120, int lineHeight = 260) : ITextMeasurer
{
    public int CharWidth { get; } = charWidth;
    public int LineHeight { get; } = lineHeight;

    public FontInfo Font => new()
    {
        LineHeight = (short)LineHeight,
        Leading = 20,
        BaseAdd = 60,
        TopOffset = 40,
        ContinuationWidth = (short)(CharWidth * 3),
    };

    public int MeasureWidth(string text, FontInfo font) => text.Length * CharWidth;

    public (int charsFit, int width) BreakAtWidth(string text, FontInfo font, int maxWidth)
    {
        if (maxWidth < CharWidth) return (0, 0);

        int maxChars = maxWidth / CharWidth;
        if (maxChars >= text.Length) return (text.Length, text.Length * CharWidth);

        // Break at the last space that fits, mirroring the real measurer's contract.
        int brk = text.LastIndexOf(' ', Math.Min(maxChars, text.Length - 1));
        if (brk <= 0) brk = maxChars;   // a single word longer than the line: hard-break it

        return (brk, brk * CharWidth);
    }
}

/// <summary>A fixed-size body standing in for real avatar art.</summary>
public sealed class FakeBody : Body
{
    public short Width { get; init; } = 800;
    public short Height { get; init; } = 1600;
    public short HeadHeight { get; init; } = 400;

    public override Body Clone() => new FakeBody
    {
        AvatarId = AvatarId,
        Flip = Flip,
        Requested = Requested,
        ArrowX = ArrowX,
        BBox = BBox,
        Width = Width,
        Height = Height,
        HeadHeight = HeadHeight,
    };

    public override bool IsSame(Body other) => other is FakeBody f && f.AvatarId == AvatarId;

    public override void GetDimInfo(out short xdim, out short ydim, out short normHeight,
                                    out short headHeight, out short faceX)
    {
        xdim = Width;
        ydim = Height;
        normHeight = Height;
        headHeight = HeadHeight;
        faceX = (short)(Width / 2);
    }
}
