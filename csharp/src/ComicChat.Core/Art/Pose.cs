namespace ComicChat.Core.Art;

/// <summary>
/// One drawable pose: an image plus its mask and aura. Port of CPose (avatar.h:11, avbfile.cpp:1399).
/// </summary>
/// <remarks>
/// The three slots are fixed by the format — index 0 image, 1 mask, 2 aura — and stay in that order
/// here because the two 2bpp expansion paths are defined by which slot they write into.
/// </remarks>
public sealed class Pose
{
    private const int ImageSlot = 0;
    private const int MaskSlot = 1;
    private const int AuraSlot = 2;

    internal readonly uint[] Offsets = new uint[3];
    internal readonly AvatarImageFormat[] Formats = new AvatarImageFormat[3];
    internal readonly AvatarImagePalette[] PaletteTypes = new AvatarImagePalette[3];

    private readonly ArtDib?[] _dibs = new ArtDib?[3];
    private byte[]? _bgra;

    internal Pose(ReadOnlySpan<uint> offsets, ReadOnlySpan<AvatarImageFormat> formats,
                  ReadOnlySpan<AvatarImagePalette> paletteTypes)
    {
        offsets.CopyTo(Offsets);
        formats.CopyTo(Formats);
        paletteTypes.CopyTo(PaletteTypes);
    }

    /// <summary>The drawing itself. Port of CPose::GetDrawing (avatar.h:30).</summary>
    public ArtDib? Image => _dibs[ImageSlot];

    /// <summary>1bpp opacity mask; a set bit is opaque. Port of CPose::GetMask (avatar.h:32).</summary>
    public ArtDib? Mask => _dibs[MaskSlot];

    /// <summary>1bpp outline used to lift the figure off the backdrop. Port of CPose::GetAura (avatar.h:34).</summary>
    public ArtDib? Aura => _dibs[AuraSlot];

    public int Width => Image?.Width ?? 0;

    public int Height => Image?.Height ?? 0;

    /// <summary>The image decoded to top-down 32-bit BGRA with the mask applied. Cached.</summary>
    public byte[] Bgra => _bgra ??= Image?.ToBgra(Mask) ?? [];

    /// <summary>
    /// Loads all three images and runs whichever 2bpp expansion the palette types call for.
    /// Port of CPose::Load (avbfile.cpp:1399).
    /// </summary>
    internal void Load(AvbReader reader, ArtPalette? globalPalette)
    {
        for (int i = 0; i < 3; i++)
        {
            if (Offsets[i] == 0)
                continue;

            _dibs[i] = reader.ReadImage(Offsets[i], Formats[i], PaletteTypes[i], globalPalette);
        }

        // avbfile.cpp:1445-1455. Only one of these can apply: a masked-mono image carries its own
        // mask, so it never has separate mask/aura resources to expand.
        if (PaletteTypes[ImageSlot] == AvatarImagePalette.MaskedMono && _dibs[ImageSlot] is { } masked)
            ConvertFromMaskedMono(masked);
        else if (PaletteTypes[MaskSlot] == AvatarImagePalette.DualMask && _dibs[MaskSlot] is { } dual)
            ConvertFromDualMask(dual);
    }

    /// <summary>
    /// Explodes one 2bpp bitmap into image, mask and aura. Port of CPose::ConvertFromMaskedMono
    /// (avbfile.cpp:1510).
    /// </summary>
    private void ConvertFromMaskedMono(ArtDib src)
    {
        var planes = ConvertMasksCommon(src, 3);
        _dibs[ImageSlot] = planes[0];
        _dibs[MaskSlot] = planes[1];
        _dibs[AuraSlot] = planes[2];
    }

    /// <summary>
    /// Explodes one 2bpp bitmap into mask and aura, leaving the image alone. Port of
    /// CPose::ConvertFromDualMask (avbfile.cpp:1526). This is the common case in the shipped art.
    /// </summary>
    private void ConvertFromDualMask(ArtDib src)
    {
        var planes = ConvertMasksCommon(src, 2);
        _dibs[MaskSlot] = planes[0];
        _dibs[AuraSlot] = planes[1];
    }

    /// <summary>
    /// Splits a 2bpp bitmap into 2 or 3 monochrome planes. Port of CPose::ConvertMasksCommon
    /// (avbfile.cpp:1542).
    /// </summary>
    /// <remarks>
    /// Per 2-bit pair: <c>00 =&gt; 0,0,0 · 01 =&gt; 1,0,1 · 10 =&gt; 0,1,1 · 11 =&gt; 1,1,1</c>.
    /// Bit 0 drives the first plane, bit 1 the second, and either bit set drives the aura.
    /// <para>
    /// The original does this with a pair of 256-entry nybble lookup tables (avbfile.cpp:1471, 1488),
    /// reading the source two bytes at a time. That was a 1996 speed trick against a table-lookup
    /// budget; a per-pixel loop produces identical planes and is worth the clarity here.
    /// </para>
    /// </remarks>
    private static ArtDib[] ConvertMasksCommon(ArtDib src, int numPlanes)
    {
        if (src.BitCount != 2)
            throw new InvalidDataException($"Expected a 2bpp source to expand, got {src.BitCount}bpp.");

        int stride = ArtDib.StorageWidth(src.Width, 1);
        var bits = new byte[numPlanes][];
        for (int i = 0; i < numPlanes; i++)
            bits[i] = new byte[stride * src.Height];

        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                int pair = src.GetIndex(x, y);
                int bit = 7 - (x & 7);
                int at = y * stride + (x >> 3);

                if ((pair & 1) != 0)
                    bits[0][at] |= (byte)(1 << bit);
                if ((pair & 2) != 0)
                    bits[1][at] |= (byte)(1 << bit);
                if (numPlanes == 3 && pair != 0)
                    bits[2][at] |= (byte)(1 << bit);
            }

            if (numPlanes == 3)
            {
                // avbfile.cpp:1651: "Bug in drawing code does not allow the image's pixels to be
                // black in the area where the aura is." The image plane is forced to white wherever
                // the mask bit is clear. Kept because it is not a rendering tweak — the shipped art
                // was authored against this behaviour, so dropping it changes how the figures look.
                for (int i = y * stride; i < (y + 1) * stride; i++)
                    bits[0][i] &= bits[1][i];
            }
        }

        // The expansion always writes top-down, whatever the source row order was.
        var planes = new ArtDib[numPlanes];
        for (int i = 0; i < numPlanes; i++)
            planes[i] = new ArtDib(src.Width, src.Height, 1, isTopDown: true, ArtPalette.Monochrome, bits[i]);

        return planes;
    }
}
