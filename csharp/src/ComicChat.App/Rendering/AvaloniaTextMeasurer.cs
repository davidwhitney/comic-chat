using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ComicChat.Core.Comic;

namespace ComicChat.App.Rendering;

/// <summary>
/// Measures text with Avalonia's formatting stack, in the engine's TWIPS.
/// Implements <see cref="ITextMeasurer"/>, the seam that replaced the original's
/// direct CDC calls (GetClientDC / GetTextExtent, balloon.cpp).
/// </summary>
public sealed class AvaloniaTextMeasurer : ITextMeasurer
{
    /// <summary>TWIPS per point. The engine works in TWIPS (MM_TWIPS), Avalonia in DIPs.</summary>
    public const double TwipsPerPoint = 20.0;

    /// <summary>TWIPS per DIP, at 96 DPI: 1440/96.</summary>
    public const double TwipsPerDip = 15.0;

    private readonly Typeface _typeface;
    private readonly double _fontSizeDip;
    private readonly Dictionary<string, int> _cache = [];

    public AvaloniaTextMeasurer(string fontFamily = "Comic Sans MS", double fontSizePoints = 10)
    {
        _typeface = new Typeface(FontFamily.Parse(fontFamily));
        _fontSizeDip = fontSizePoints * 96.0 / 72.0;
    }

    /// <summary>Build the engine's font metrics from the real typeface.</summary>
    public FontInfo CreateFontInfo()
    {
        var probe = Layout("Hg");
        double lineHeightDip = probe.Height;
        double baselineDip = probe.Baseline;

        return new FontInfo
        {
            Font = _typeface,
            FontSize = (short)(_fontSizeDip * TwipsPerDip),
            LineHeight = (short)(lineHeightDip * TwipsPerDip),
            Leading = (short)((lineHeightDip - baselineDip) * TwipsPerDip),
            BaseAdd = (short)((lineHeightDip - baselineDip) * TwipsPerDip),
            TopOffset = (short)(baselineDip * TwipsPerDip * 0.25),
            ContinuationWidth = (short)(MeasureDip("...") * TwipsPerDip),
        };
    }

    private TextLayout Layout(string text) =>
        new(text, _typeface, _fontSizeDip, Brushes.Black);

    private double MeasureDip(string text)
    {
        if (text.Length == 0) return 0;
        return Layout(text).Width;
    }

    public int MeasureWidth(string text, FontInfo font)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (_cache.TryGetValue(text, out int cached)) return cached;

        int w = (int)Math.Round(MeasureDip(text) * TwipsPerDip);
        if (_cache.Count < 4096) _cache[text] = w;
        return w;
    }

    /// <summary>
    /// Longest prefix fitting <paramref name="maxWidth"/>, broken at a space where possible.
    /// Port of the bGetTextExtentExPointFormatted contract (balloon.cpp:40).
    /// </summary>
    public (int charsFit, int width) BreakAtWidth(string text, FontInfo font, int maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return (0, 0);

        int full = MeasureWidth(text, font);
        if (full <= maxWidth) return (text.Length, full);

        // Binary search the longest prefix that fits.
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (MeasureWidth(text[..mid], font) <= maxWidth) lo = mid;
            else hi = mid - 1;
        }

        if (lo == 0) return (0, 0);   // not even one character fits

        // Prefer a word boundary.
        int brk = text.LastIndexOf(' ', Math.Min(lo, text.Length - 1));
        if (brk > 0) lo = brk;

        return (lo, MeasureWidth(text[..lo], font));
    }
}
