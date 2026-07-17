using ComicChat.Core.Geometry;

namespace ComicChat.Core.Comic;

/// <summary>
/// The vertical font metrics the balloon outline generator needs.
/// Port of CFontInfo (balloon.h:47).
/// </summary>
/// <remarks>
/// The original built this by selecting the font into a DC and calling GetTextMetrics.
/// Core stays UI-agnostic, so these are supplied by an <see cref="ITextMeasurer"/> instead.
/// All values are in TWIPS, matching the panel coordinate system.
/// </remarks>
public sealed class FontInfo
{
    /// <summary>Opaque handle to the real font, owned by the UI layer.</summary>
    public object? Font { get; init; }

    public uint DefaultForeColor { get; init; } = 0xFF000000;

    public short Leading { get; init; }
    public short LineHeight { get; init; }

    /// <summary>
    /// Em size in TWIPS. The renderer must draw at exactly the size the measurer measured at,
    /// or the text will not match the balloon that was sized around it.
    /// </summary>
    public short FontSize { get; init; }

    /// <summary>Descent added below the baseline; PermuteFilters offsets the outline by it.</summary>
    public short BaseAdd { get; init; }

    public short ContinuationWidth { get; init; }

    /// <summary>Distance from the top of the line box to the first baseline.</summary>
    public short TopOffset { get; init; }
}

/// <summary>
/// Measures text so the engine can break lines and size balloons.
/// </summary>
/// <remarks>
/// Replaces the original's direct CDC calls (GetTextExtent / bGetTextExtentExPointFormatted,
/// balloon.cpp). Implemented by the UI layer; Core depends only on this interface.
/// </remarks>
public interface ITextMeasurer
{
    /// <summary>Width of <paramref name="text"/> in TWIPS.</summary>
    int MeasureWidth(string text, FontInfo font);

    /// <summary>
    /// Longest prefix of <paramref name="text"/> that fits in <paramref name="maxWidth"/>,
    /// broken at a space where possible. Returns the character count that fits and its width.
    /// Port of the bGetTextExtentExPointFormatted contract (balloon.cpp:40).
    /// </summary>
    (int charsFit, int width) BreakAtWidth(string text, FontInfo font, int maxWidth);
}

/// <summary>
/// The result of breaking a balloon's text into lines.
/// Port of CFormatInfo (balloon.h:11).
/// </summary>
/// <remarks>
/// Consumed by both the outline generator (GetFilters walks the per-line left/width to shape
/// the cloud) and the text renderer, so the two always agree on where the words sit.
/// </remarks>
public sealed class FormatInfo
{
    /// <summary>Port of MAXLINES (balloon.h:7). A balloon never exceeds this; excess text splits into the next panel.</summary>
    public const int MaxLines = 10;

    public int NLines;
    public readonly int[] Lengths = new int[MaxLines];
    public readonly int[] Widths = new int[MaxLines];
    public readonly int[] LeftX = new int[MaxLines];
    public readonly string[] Starts = new string[MaxLines];
    public int MaxWidth;
    public SRect BBox;

    public FormatInfo Clone()
    {
        var c = new FormatInfo { NLines = NLines, MaxWidth = MaxWidth, BBox = BBox };
        Array.Copy(Lengths, c.Lengths, MaxLines);
        Array.Copy(Widths, c.Widths, MaxLines);
        Array.Copy(LeftX, c.LeftX, MaxLines);
        Array.Copy(Starts, c.Starts, MaxLines);
        return c;
    }
}
