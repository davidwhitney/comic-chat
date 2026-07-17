using ComicChat.Core.Geometry;

namespace ComicChat.Core.Comic;

/// <summary>Text justification. Port of FT_LEFT_JUSTIFY (balloon.h:6).</summary>
[Flags]
public enum LabelFormat
{
    Center = 0,
    LeftJustify = 1,
}

/// <summary>
/// A run of text with a font. Port of CLabel (balloon.h:63).
/// Base of <see cref="Balloon"/>; also used directly for titles and "starring" credits.
/// </summary>
public class Label : PanelElement
{
    public FontInfo FontI { get; set; }
    public string Str { get; set; }
    public LabelFormat Format { get; set; }

    public Label(string text, FontInfo fontInfo)
    {
        Str = text;
        FontI = fontInfo;
    }

    protected Label(Label other)
    {
        Str = other.Str;
        FontI = other.FontI;
        Format = other.Format;
        BBox = other.BBox;
    }

    public override PeType GetElementType() => PeType.Label;

    public virtual int GetLeading() => FontI.Leading;

    /// <summary>
    /// Port of CLabel::AreaEstimate (balloon.cpp). Returns a padded area estimate used to
    /// choose a balloon width; the 1.3 factor is the original's slack for line breaking.
    /// </summary>
    public virtual int AreaEstimate(LayoutContext ctx, out int len, out int lineHeight)
    {
        int cx = ctx.Measurer.MeasureWidth(Str, FontI);
        int cy = FontI.LineHeight;
        len = cx;
        lineHeight = FontI.LineHeight;
        return (int)(1.3 * cx * (cy + lineHeight));
    }

    /// <summary>
    /// Port of CLabel::WidestWord (balloon.cpp). A balloon can never be narrower than its
    /// widest single word, so this floors the width search in GetCloudEstimate.
    /// </summary>
    public virtual int WidestWord(LayoutContext ctx)
    {
        int maxWidth = 0;
        foreach (var word in Str.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            maxWidth = Math.Max(maxWidth, ctx.Measurer.MeasureWidth(word, FontI));
        return maxWidth;
    }

    /// <summary>
    /// Port of CLabel::BreakIntoLines (balloon.cpp) plus the global ::BreakIntoLines (format.cpp).
    /// Fills <paramref name="fInfo"/> with per-line text, widths and the text bbox.
    /// Returns 0 if the text cannot be broken to the requested width (caller treats that as failure).
    /// </summary>
    public virtual int BreakIntoLines(FormatInfo fInfo, LayoutContext ctx)
    {
        int desiredWidth = BBox.Right - BBox.Left;
        if (desiredWidth <= 0) return 0;

        int nLines = BreakText(Str, FontI, desiredWidth, ctx, fInfo);
        fInfo.NLines = nLines;
        if (nLines == 0) return 0;

        fInfo.MaxWidth = 0;
        for (int i = 0; i < nLines; i++)
            if (fInfo.MaxWidth < fInfo.Widths[i])
                fInfo.MaxWidth = fInfo.Widths[i];

        fInfo.BBox.Top = BBox.Top;
        if ((Format & LabelFormat.LeftJustify) != 0)
        {
            fInfo.BBox.Left = BBox.Left;
            fInfo.BBox.Right = BBox.Left + fInfo.MaxWidth;
        }
        else
        {
            fInfo.BBox.Left = (desiredWidth - fInfo.MaxWidth) / 2 + BBox.Left;
            fInfo.BBox.Right = fInfo.BBox.Left + fInfo.MaxWidth;
        }

        fInfo.BBox.Bottom = fInfo.BBox.Top - nLines * FontI.LineHeight - FontI.BaseAdd;
        return nLines;
    }

    /// <summary>
    /// Greedy word-wrap against the measurer. Port of ::BreakIntoLines (format.cpp), minus the
    /// DBCS lead-byte and rich-formatting handling, which are not modelled here.
    /// Stops at <see cref="FormatInfo.MaxLines"/>; the caller splits the remainder into a new panel.
    /// </summary>
    private static int BreakText(string text, FontInfo font, int maxWidth, LayoutContext ctx, FormatInfo fInfo)
    {
        int nLines = 0;
        int pos = 0;

        while (pos < text.Length && nLines < FormatInfo.MaxLines)
        {
            while (pos < text.Length && text[pos] == ' ') pos++;
            if (pos >= text.Length) break;

            var remaining = text[pos..];
            var (charsFit, width) = ctx.Measurer.BreakAtWidth(remaining, font, maxWidth);

            // Not even one character fits — the requested width is unusable for this text.
            if (charsFit <= 0) return 0;

            var line = remaining[..charsFit].TrimEnd();
            if (line.Length == 0) line = remaining[..charsFit];

            fInfo.Starts[nLines] = line;
            fInfo.Lengths[nLines] = line.Length;
            fInfo.Widths[nLines] = charsFit == remaining.Length
                ? width
                : ctx.Measurer.MeasureWidth(line, font);
            nLines++;
            pos += charsFit;
        }

        return nLines;
    }

    /// <summary>
    /// Port of CLabel::ShiftLines (balloon.cpp:760). Randomly jitters each line horizontally.
    /// </summary>
    /// <remarks>
    /// MAXLEFTSHIFT and MAXCENTERSHIFT are both 0 in the shipped build, so the jitter is
    /// always zero and this only centres the lines. The randfloat() call is nevertheless kept,
    /// because it advances the panel's RNG stream — every balloon laid out after this one draws
    /// from that same stream, so skipping the call would desync their widths and x-placement
    /// from the original's.
    /// </remarks>
    public virtual void ShiftLines(FormatInfo fInfo, LayoutContext ctx)
    {
        const int maxLeftShift = 0;
        const int maxCenterShift = 0;

        if ((Format & LabelFormat.LeftJustify) != 0)
        {
            for (int i = 0; i < fInfo.NLines; i++)
            {
                int shiftLimit = fInfo.MaxWidth - fInfo.Widths[i];
                fInfo.LeftX[i] = (int)(ctx.RandFloat() * Math.Min(maxLeftShift, shiftLimit));
            }
        }
        else
        {
            for (int i = 0; i < fInfo.NLines; i++)
            {
                int shiftLimit = (fInfo.MaxWidth - fInfo.Widths[i]) / 2;
                int shift = (int)((ctx.RandFloat() * 2.0 - 1.0) * Math.Min(maxCenterShift, shiftLimit));
                fInfo.LeftX[i] = ((fInfo.BBox.Right - fInfo.BBox.Left) - fInfo.Widths[i]) / 2 + shift;
            }
        }
    }
}
