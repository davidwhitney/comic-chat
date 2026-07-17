namespace ComicChat.Core.Art;

/// <summary>
/// A colour table for an art resource. Port of CAvatarPalette (avbfile.h:351, avbfile.cpp:224).
/// </summary>
/// <remarks>
/// Entries are plain 0x00RRGGBB.
/// <para>
/// The original stores COLORREFs (0x00BBGGRR) and pushes them through
/// SET_RGBQUAD_FROM_COLORREF (avbfile.h:10) when building a DIB colour table, which swaps R and B
/// a second time. Reading three disk bytes into a little-endian COLORREF already put them in
/// R, G, B order, so the two swaps cancel and the on-disk byte order is simply red, green, blue.
/// Modelling that as one plain RGB value drops a pair of no-op conversions without changing a pixel.
/// </para>
/// </remarks>
public sealed class ArtPalette
{
    /// <summary>Colour entries, 0x00RRGGBB.</summary>
    public uint[] Entries { get; }

    public int Count => Entries.Length;

    public ArtPalette(uint[] entries) => Entries = entries;

    public static ArtPalette Empty { get; } = new([]);

    /// <summary>Packs a colour the way the Win32 RGB macro does, but in RGB order.</summary>
    public static uint Rgb(byte r, byte g, byte b) => ((uint)r << 16) | ((uint)g << 8) | b;

    /// <summary>Port of MonochromePalette (avbfile.cpp:15). Index 0 is white, index 1 is black.</summary>
    public static ArtPalette Monochrome { get; } = new([Rgb(255, 255, 255), Rgb(0, 0, 0)]);

    /// <summary>
    /// Port of MaskedMonoPalette (avbfile.cpp:16), used for both AIP_MASKEDMONO and AIP_DUALMASK.
    /// </summary>
    /// <remarks>
    /// Entries 2 and 3 are never seen on screen: a 2bpp image carrying one of these palette types is
    /// always exploded into 1bpp planes before it is drawn, and those planes get
    /// <see cref="Monochrome"/> instead. They exist so the colour count comes out at 4.
    /// </remarks>
    public static ArtPalette MaskedMono { get; } = new(
        [Rgb(255, 255, 255), Rgb(0, 0, 0), Rgb(128, 0, 0), Rgb(0, 0, 128)]);

    /// <summary>
    /// Reads an inline palette record body: a u16 entry count then three bytes per entry.
    /// Port of CAvatarPalette::Read (avbfile.cpp:224).
    /// </summary>
    /// <remarks>
    /// Three bytes per entry rather than four because, as the original notes, palettes barely
    /// compress — so the fourth padding byte would be paid for in full.
    /// </remarks>
    public static ArtPalette Read(BinaryReader reader)
    {
        int entries = reader.ReadUInt16();
        if (entries > AvbConstants.MaxPaletteSize)
            throw new InvalidDataException($"Palette abnormally big ({entries} entries).");

        var colours = new uint[entries];
        for (int i = 0; i < entries; i++)
        {
            byte r = reader.ReadByte();
            byte g = reader.ReadByte();
            byte b = reader.ReadByte();
            colours[i] = Rgb(r, g, b);
        }

        return new ArtPalette(colours);
    }
}
