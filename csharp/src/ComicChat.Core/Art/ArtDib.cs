namespace ComicChat.Core.Art;

/// <summary>
/// A device-independent bitmap decoded out of an .avb/.bgb resource.
/// Port of CAvatarDIB / CDIB (avbfile.h:376, dib.cpp).
/// </summary>
/// <remarks>
/// <see cref="Bits"/> is kept exactly as it sits in the file — bottom-up, row-padded to the
/// original's <see cref="StorageWidth(int, int)"/> — because the mask/aura expansion and the size
/// assertions in the loader are all defined in those terms. Row order is resolved on access, in
/// <see cref="GetIndex"/> and <see cref="ToBgra"/>.
/// </remarks>
public sealed class ArtDib
{
    /// <summary>Width in pixels. Always positive.</summary>
    public int Width { get; }

    /// <summary>Height in pixels. Always positive; see <see cref="IsTopDown"/> for row order.</summary>
    public int Height { get; }

    public ushort BitCount { get; }

    /// <summary>True when biHeight was negative, i.e. the rows are already stored top-down.</summary>
    public bool IsTopDown { get; }

    /// <summary>Colour table. Empty for 24/32bpp.</summary>
    public ArtPalette Palette { get; }

    /// <summary>Raw DIB bits in file order, <see cref="Stride"/> bytes per row.</summary>
    public byte[] Bits { get; }

    /// <summary>Bytes per scan line, per the original's rounding rule.</summary>
    public int Stride { get; }

    public ArtDib(int width, int height, ushort bitCount, bool isTopDown, ArtPalette palette, byte[] bits)
    {
        Width = width;
        Height = height;
        BitCount = bitCount;
        IsTopDown = isTopDown;
        Palette = palette;
        Bits = bits;
        Stride = StorageWidth(width, bitCount);
    }

    /// <summary>
    /// Bytes per scan line. Verbatim port of DIBStorageWidth (dib.cpp:1026).
    /// </summary>
    /// <remarks>
    /// This is deliberately NOT the textbook <c>((w * bpp + 31) / 32) * 4</c>. For sub-byte depths
    /// the original pads the pixel count up to a whole byte first (<c>w += 8 / bpp - 1</c>) and only
    /// then rounds the byte count to a DWORD. The two agree for most widths and disagree for some —
    /// and every size check in the file format is written against this one, so a "correction" here
    /// would reject real files.
    /// </remarks>
    public static int StorageWidth(int nWidth, int nBitCount)
    {
        if (nBitCount < 8)
            nWidth += (8 / nBitCount) - 1;

        return (((nWidth * nBitCount) / 8) + 3) & ~3;
    }

    /// <summary>
    /// Colour table length for a bit depth. Port of NumDIBColorEntries (dib.cpp:70).
    /// </summary>
    public static int NumColorEntries(ushort bitCount, uint clrUsed)
    {
        int colours = bitCount switch { 1 => 2, 4 => 16, 8 => 256, _ => 0 };
        if (clrUsed != 0)
            colours = (int)clrUsed;
        return colours;
    }

    /// <summary>Palette index (or packed pixel, for 16bpp+) at top-down coordinates.</summary>
    public byte GetIndex(int x, int y)
    {
        int row = IsTopDown ? y : Height - 1 - y;
        int b = row * Stride;

        return BitCount switch
        {
            1 => (byte)((Bits[b + (x >> 3)] >> (7 - (x & 7))) & 1),
            4 => (byte)((x & 1) == 0 ? Bits[b + (x >> 1)] >> 4 : Bits[b + (x >> 1)] & 0x0F),
            8 => Bits[b + x],
            2 => (byte)((Bits[b + (x >> 2)] >> (6 - 2 * (x & 3))) & 3),
            _ => throw new NotSupportedException($"{BitCount}bpp has no palette index."),
        };
    }

    private uint Lookup(byte index) =>
        index < Palette.Count ? Palette.Entries[index] : 0u;

    /// <summary>
    /// Decodes to 32-bit BGRA, top-down, one row of <c>Width * 4</c> bytes at a time.
    /// </summary>
    /// <param name="mask">
    /// Optional 1bpp mask. Where the mask index is 0 the output pixel is fully transparent.
    /// </param>
    /// <remarks>
    /// Mask polarity comes from the mask palette being {0 = white, 1 = black} plus the drawing-code
    /// workaround at avbfile.cpp:1651, which ANDs the image plane with the mask plane so the image
    /// can only be black where the mask bit is set. That only reads as a fix if a set mask bit means
    /// "opaque", so index != 0 is opaque here.
    /// </remarks>
    public byte[] ToBgra(ArtDib? mask = null)
    {
        var outBits = new byte[Width * Height * 4];

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                uint rgb;
                switch (BitCount)
                {
                    case 1:
                    case 2:
                    case 4:
                    case 8:
                        rgb = Lookup(GetIndex(x, y));
                        break;
                    case 24:
                    case 32:
                    {
                        int row = IsTopDown ? y : Height - 1 - y;
                        int src = row * Stride + x * (BitCount / 8);
                        rgb = ArtPalette.Rgb(Bits[src + 2], Bits[src + 1], Bits[src]);
                        break;
                    }
                    default:
                        throw new NotSupportedException($"Unsupported bit depth {BitCount}.");
                }

                bool opaque = mask is null || mask.GetIndex(x, y) != 0;

                int d = (y * Width + x) * 4;
                outBits[d + 0] = (byte)rgb;               // B
                outBits[d + 1] = (byte)(rgb >> 8);        // G
                outBits[d + 2] = (byte)(rgb >> 16);       // R
                outBits[d + 3] = opaque ? (byte)255 : (byte)0;
            }
        }

        return outBits;
    }
}
