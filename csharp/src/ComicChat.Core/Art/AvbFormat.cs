namespace ComicChat.Core.Art;

/// <summary>
/// Avatar file types. Port of AT_SIMPLE / AT_COMPLEX / AT_BACKDROP (avbfile.h:58-60).
/// </summary>
public enum AvatarFileType : ushort
{
    Simple = 1,
    Complex = 2,
    Backdrop = 3,
}

/// <summary>
/// How an image resource is encoded at its stream offset. Port of AVATARIMAGEFORMAT (avbfile.h:78).
/// </summary>
public enum AvatarImageFormat : byte
{
    /// <summary>A complete embedded .BMP file (BITMAPFILEHEADER and all) at the offset.</summary>
    Dib = 0,

    /// <summary>A bare zlib-deflated DIB bit payload with a detached palette. This is what ships.</summary>
    LzDeflate = 1,
}

/// <summary>
/// Where an image's colour table comes from. Port of AVATARIMAGEPALETTE (avbfile.h:97).
/// </summary>
public enum AvatarImagePalette : byte
{
    NoPalette = 0,
    GlobalPalette = 1,
    LocalPalette = 2,
    Monochrome = 3,

    /// <summary>2bpp: image, mask and aura interleaved in one bitmap.</summary>
    MaskedMono = 4,

    /// <summary>2bpp: bit 0 is the mask, bit 1 is the aura.</summary>
    DualMask = 5,
}

/// <summary>
/// Tags in the header record stream. Port of AVATARRECORDTYPE (avbfile.h:252).
/// </summary>
/// <remarks>
/// The numbering is the format's forward-compatibility scheme, not an accident: tags below
/// <see cref="IconNew"/> (256) are the original 1.0 records and carry no size, so an unknown one is
/// unskippable and fatal. Tags at 256 and above are followed by a u16 skip length, which is why a
/// newer file still loads in an older client.
/// </remarks>
public enum AvatarRecordType : ushort
{
    Name = 1,
    Flags = 2,
    Icon = 3,
    NFaces = 4,
    NTorsos = 5,
    StartData = 6,
    EndData = 7,
    Style = 8,
    NBodies = 9,
    NFaces2 = 10,
    NTorsos2 = 11,
    NBodies2 = 12,

    IconNew = 256,
    ColorPalette = 257,
    Backdrop = 258,
    Copyright = 259,
    OriginalUrl = 260,
    OverrideUrl = 261,
    UsageFlags = 262,
    OffsetAdjustment = 263,
}

/// <summary>Magic numbers, limits and sentinels from avbfile.h:52-69.</summary>
public static class AvbConstants
{
    /// <summary>The 1.0 magic number, still accepted on read (avbfile.h:52).</summary>
    public const ushort MagicNum = 0x0081;

    /// <summary>The current magic number (avbfile.h:53).</summary>
    public const ushort MagicNumNew = 0x8181;

    /// <summary>'BM' — a backdrop may just be a plain Windows .BMP (avbfile.cpp:1701).</summary>
    public const ushort BmpMagic = 0x4D42;

    public const ushort CurrentVersion = 2;

    /// <summary>Pose IDs are 1-based so that 0 can mean "no pose" (avbfile.h:66).</summary>
    public const ushort InvalidPoseId = 0;

    /// <summary>2Mb ceiling on either side of a zlib blob, to bound damage from hostile files (avbfile.h:68).</summary>
    public const int MaxCompressBufferSize = 2048 * 1024;

    public const int MaxPaletteSize = 2048;

    public const int MaxAvatarName = 60;
    public const int MaxUrl = 512;
    public const int MaxCopyright = 256;
}
