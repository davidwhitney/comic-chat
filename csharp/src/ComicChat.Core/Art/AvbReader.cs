using System.IO.Compression;
using System.Text;
using ComicChat.Core.Avatars;

namespace ComicChat.Core.Art;

/// <summary>
/// Reads Comic Chat .avb avatar files and .bgb backdrop files.
/// Port of CAvatarX::LoadAvatar (avbfile.cpp:743) and CChatBackdrop::LoadBackdrop (avbfile.cpp:1669).
/// </summary>
/// <remarks>
/// Despite the MFC lineage there is no CArchive here and never was: the format is a flat
/// little-endian packed record stream, so a <see cref="BinaryReader"/> over the bytes is the whole
/// story. The file is buffered up front because resources are reached by absolute offset and the
/// loader hops back and forth between the tag stream and the image pool.
/// </remarks>
public sealed class AvbReader
{
    private readonly byte[] _data;
    private readonly MemoryStream _stream;
    private readonly BinaryReader _reader;

    /// <summary>
    /// Running total of every AK_OFFSET_ADJUSTMENT seen so far. Port of nResourcesAdjustment
    /// (avbfile.cpp:747).
    /// </summary>
    /// <remarks>
    /// This is the format's escape hatch for tools that do not understand it: something that splices
    /// bytes into the front of a file (a copyright stamp, a redistributor's URL) cannot fix up the
    /// resource offsets it just invalidated, so instead it drops in a record saying "everything
    /// after me has moved by N". It accumulates rather than assigns, so several such tools can stack.
    /// The shipped comicart/*.avb carry one (+0x4D); artpack1/*.avb do not.
    /// </remarks>
    private int _resourcesAdjustment;

    private AvbReader(byte[] data)
    {
        _data = data;
        _stream = new MemoryStream(data, writable: false);
        _reader = new BinaryReader(_stream);
    }

    // ========================================================================
    // Entry points

    public static AvatarFile LoadAvatar(string path) => LoadAvatar(File.ReadAllBytes(path));

    public static AvatarFile LoadAvatar(byte[] data) => new AvbReader(data).LoadAvatarCore();

    public static ChatBackdrop LoadBackdrop(string path) => LoadBackdrop(File.ReadAllBytes(path));

    public static ChatBackdrop LoadBackdrop(byte[] data) => new AvbReader(data).LoadBackdropCore();

    // ========================================================================
    // Avatar

    /// <summary>Port of CAvatarX::LoadAvatar (avbfile.cpp:743).</summary>
    private AvatarFile LoadAvatarCore()
    {
        var (magic, type, version) = ReadHeader();

        if (magic != AvbConstants.MagicNum && magic != AvbConstants.MagicNumNew)
            throw new InvalidDataException($"Not an avatar file (magic 0x{magic:X4}).");

        AvatarFile avatar = type switch
        {
            (ushort)AvatarFileType.Complex => new AvatarComplex(),
            (ushort)AvatarFileType.Simple => new AvatarSimple(),
            _ => throw new InvalidDataException($"Invalid avatar type {type}."),
        };

        // The original checks HIWORD(version) != 0 (avbfile.cpp:792) — any file sharing our major
        // version is loadable, on the theory that minor revisions only ever append tags.
        if ((version >> 16) != 0)
            throw new InvalidDataException($"Unsupported version 0x{version:X}.");

        avatar.MagicNumber = magic;
        avatar.Type = (AvatarFileType)type;
        avatar.Version = version;

        while (true)
        {
            ushort tag = _reader.ReadUInt16();
            ushort size = 0;
            if (tag >= (ushort)AvatarRecordType.IconNew)
                size = _reader.ReadUInt16();

            if (tag == (ushort)AvatarRecordType.StartData)
                break;

            HandleLoadTag(avatar, tag, size);
        }

        avatar.OffsetAdjustment = _resourcesAdjustment;

        // The original defers this until something asks to draw the pose (CPose::Load is called
        // lazily). We load eagerly, after the tag stream is fully parsed rather than during it, so
        // that seeking off to the image pool cannot disturb the parse position.
        foreach (var pose in avatar.PoseList)
            pose.Load(this, avatar.GlobalPalette);

        return avatar;
    }

    /// <summary>
    /// Port of CAvatarX::HandleLoadTag (avbfile.cpp:863) merged with the CAvatarSimple
    /// (avbfile.cpp:1036) and CAvatarComplex (avbfile.cpp:1134) overrides.
    /// </summary>
    private void HandleLoadTag(AvatarFile avatar, ushort tag, ushort size)
    {
        switch ((AvatarRecordType)tag)
        {
            case AvatarRecordType.Name:
                avatar.Name = ReadString(AvbConstants.MaxAvatarName);
                break;

            case AvatarRecordType.Copyright:
                avatar.Copyright = ReadString(AvbConstants.MaxCopyright);
                break;

            case AvatarRecordType.OriginalUrl:
                avatar.OriginalUrl = ReadString(AvbConstants.MaxUrl);
                break;

            case AvatarRecordType.OverrideUrl:
                avatar.OverrideUrl = ReadString(AvbConstants.MaxUrl);
                break;

            case AvatarRecordType.UsageFlags:
                avatar.UsageFlags = _reader.ReadByte();
                break;

            // Both of these are written as a u16 and truncated to a byte on read
            // (avbfile.cpp:900, 910) — the high byte has never carried anything.
            case AvatarRecordType.Style:
                avatar.Style = (byte)_reader.ReadUInt16();
                break;

            case AvatarRecordType.Flags:
                avatar.Flags = (AvatarFlags)(byte)_reader.ReadUInt16();
                break;

            case AvatarRecordType.Icon:
            case AvatarRecordType.IconNew:
            {
                uint offset;
                AvatarImageFormat format;
                AvatarImagePalette palette;

                if (tag == (ushort)AvatarRecordType.Icon)
                {
                    // The 1.0 icon record is an offset and nothing else; it always pointed at a
                    // whole .BMP (avbfile.cpp:921).
                    offset = _reader.ReadUInt32();
                    format = AvatarImageFormat.Dib;
                    palette = AvatarImagePalette.NoPalette;
                }
                else
                {
                    offset = _reader.ReadUInt32();
                    format = (AvatarImageFormat)_reader.ReadByte();
                    palette = (AvatarImagePalette)_reader.ReadByte();
                }

                offset = AdjustOffset(offset);
                avatar.IconPoseId = CreatePose(avatar, offset, format, palette);
                break;
            }

            case AvatarRecordType.ColorPalette:
                avatar.GlobalPalette = ArtPalette.Read(_reader);
                break;

            case AvatarRecordType.OffsetAdjustment:
                _resourcesAdjustment += _reader.ReadInt32();
                break;

            case AvatarRecordType.NFaces:
            case AvatarRecordType.NFaces2:
                LoadFaceRecs((AvatarComplex)avatar, oldTag: tag == (ushort)AvatarRecordType.NFaces);
                break;

            case AvatarRecordType.NTorsos:
            case AvatarRecordType.NTorsos2:
                LoadTorsoRecs((AvatarComplex)avatar, oldTag: tag == (ushort)AvatarRecordType.NTorsos);
                break;

            case AvatarRecordType.NBodies:
            case AvatarRecordType.NBodies2:
                LoadBodyRecs((AvatarSimple)avatar, oldTag: tag == (ushort)AvatarRecordType.NBodies);
                break;

            default:
                // avbfile.cpp:961. A new tag carries its own length, so we can step over it and keep
                // going. An old one does not, and there is no way to find the next tag boundary.
                if (tag >= (ushort)AvatarRecordType.IconNew)
                    _stream.Seek(size, SeekOrigin.Current);
                else
                    throw new InvalidDataException($"Unrecognized tag {tag} — can't skip, aborting.");
                break;
        }
    }

    // ========================================================================
    // Pose tables

    /// <summary>
    /// Old-format pose records pad out to a fixed size with a trailing byPadding[16] where the new
    /// ones carry six format/palette bytes (avbfile.h:144, 184, 224).
    /// </summary>
    private const int OldPadding = 16;

    private const int FaceRecSize = 33;

    /// <summary>
    /// 25 bytes: three u32 offsets, u16 emotion, u8 intensity, two i16 coordinates, six format bytes.
    /// </summary>
    private const int TorsoRecSize = 25;

    private const int BodyRecSize = 25;

    /// <summary>Port of CAvatarComplex::LoadFaceRecs (avbfile.cpp:1166).</summary>
    private void LoadFaceRecs(AvatarComplex avatar, bool oldTag)
    {
        int count = _reader.ReadUInt16();
        uint prevImageOffset = 0;
        ushort prevPoseId = AvbConstants.InvalidPoseId;

        for (int i = 0; i < count; i++)
        {
            long recStart = _stream.Position;

            var res = ReadResourceRefs(avatar, recStart, FaceRecSize, ref prevImageOffset, ref prevPoseId);
            short cx = _reader.ReadInt16();
            short cy = _reader.ReadInt16();
            short cxDelta = _reader.ReadInt16();
            short cyDelta = _reader.ReadInt16();
            short x = _reader.ReadInt16();
            short y = _reader.ReadInt16();

            avatar.FaceList.Add(new FaceRec(
                res.PoseId, res.EmotionIndex, Em.EmotionToFloat(res.EmotionIndex), res.Intensity,
                cx, cy, cxDelta, cyDelta,
                // avbfile.cpp:1225 truncates these i16s to a UCHAR. Kept, because the balloon-tail
                // anchor is a percentage of the pose box and never legitimately exceeds 255.
                (byte)x, (byte)y));

            _stream.Position = recStart + FaceRecSize + (oldTag ? OldPadding : 0);
        }
    }

    /// <summary>Port of CAvatarComplex::LoadTorsoRecs (avbfile.cpp:1240).</summary>
    private void LoadTorsoRecs(AvatarComplex avatar, bool oldTag)
    {
        int count = _reader.ReadUInt16();
        uint prevImageOffset = 0;
        ushort prevPoseId = AvbConstants.InvalidPoseId;

        for (int i = 0; i < count; i++)
        {
            long recStart = _stream.Position;

            var res = ReadResourceRefs(avatar, recStart, TorsoRecSize, ref prevImageOffset, ref prevPoseId);
            short cx = _reader.ReadInt16();
            short cy = _reader.ReadInt16();

            avatar.TorsoList.Add(new TorsoRec(
                res.PoseId, res.EmotionIndex, Em.EmotionToFloat(res.EmotionIndex), res.Intensity, cx, cy));

            _stream.Position = recStart + TorsoRecSize + (oldTag ? OldPadding : 0);
        }
    }

    /// <summary>Port of CAvatarSimple::LoadBodyRecs (avbfile.cpp:1062).</summary>
    private void LoadBodyRecs(AvatarSimple avatar, bool oldTag)
    {
        int count = _reader.ReadUInt16();
        uint prevImageOffset = 0;
        ushort prevPoseId = AvbConstants.InvalidPoseId;

        for (int i = 0; i < count; i++)
        {
            long recStart = _stream.Position;

            var res = ReadResourceRefs(avatar, recStart, BodyRecSize, ref prevImageOffset, ref prevPoseId);
            short x = _reader.ReadInt16();
            short y = _reader.ReadInt16();

            avatar.BodyList.Add(new BodyRec(
                res.PoseId, res.EmotionIndex, Em.EmotionToFloat(res.EmotionIndex), res.Intensity,
                (byte)x, (byte)y));

            _stream.Position = recStart + BodyRecSize + (oldTag ? OldPadding : 0);
        }
    }

    private readonly record struct ResourceRefs(ushort PoseId, ushort EmotionIndex, float Intensity);

    /// <summary>
    /// Reads the leading fields every pose record shares — three offsets, emotion, intensity — and
    /// resolves them to a pose ID, applying the ditto rule.
    /// </summary>
    /// <remarks>
    /// The ditto rule (avbfile.cpp:1197, 1271, 1093): a record whose image offset repeats the
    /// previous record's reuses that pose outright, and its own mask/aura/format bytes are read but
    /// ignored. This is how one drawing serves several emotions — the art files list every emotion
    /// in order and lean on the run of repeats, so it is a compression scheme, not an optimisation
    /// you may skip. Without it the pose table would hold a dozen copies of the same bitmap.
    /// <para>
    /// The format bytes still have to be consumed here to keep the stream aligned, even in the
    /// ditto case, because the record is fixed-width either way.
    /// </para>
    /// <para>
    /// Deviation: the original compares the RAW offset just read against a previous value it has
    /// already adjusted (it assigns dwPrevImageOffset after ADJUST_OFFSET has rewritten the field in
    /// place). Whenever an AK_OFFSET_ADJUSTMENT is in force the two are never equal, so every ditto
    /// misses and the loader builds duplicate poses off the same bytes — same pixels, wasted memory.
    /// We adjust first and compare like against like, which is plainly what was meant and makes the
    /// ditto work on comicart/*.avb as it already does on artpack1/*.avb.
    /// </para>
    /// </remarks>
    private ResourceRefs ReadResourceRefs(AvatarFile avatar, long recStart, int recSize,
                                          ref uint prevImageOffset, ref ushort prevPoseId)
    {
        uint imageOffset = AdjustOffset(_reader.ReadUInt32());
        uint maskOffset = AdjustOffset(_reader.ReadUInt32());
        uint auraOffset = AdjustOffset(_reader.ReadUInt32());
        ushort emotion = _reader.ReadUInt16();
        byte intensity = _reader.ReadByte();

        ushort poseId;
        if (imageOffset != prevImageOffset)
        {
            var (formats, palettes) = ReadFormatBytes(recStart, recSize);
            poseId = CreatePoseWithMask(avatar, [imageOffset, maskOffset, auraOffset], formats, palettes);
            prevImageOffset = imageOffset;
            prevPoseId = poseId;
        }
        else
        {
            poseId = prevPoseId;
        }

        // avbfile.cpp:1116 — the wire scaling is a /10, but the file stores a full byte ramp.
        return new ResourceRefs(poseId, emotion, intensity / 255.0f);
    }

    /// <summary>
    /// Peeks the six format/palette bytes that sit at the tail of the fixed-width record.
    /// </summary>
    /// <remarks>
    /// They are read out of the backing array rather than off the stream because the fields between
    /// them and the current position differ per record type — face records carry four more
    /// coordinates than torso records do. Reading from the tail keeps the shared prefix handling in
    /// one place instead of threading a format-byte parameter through all three loaders.
    /// </remarks>
    private (AvatarImageFormat[] Formats, AvatarImagePalette[] Palettes) ReadFormatBytes(long recStart, int recSize)
    {
        int at = (int)(recStart + recSize - 6);
        return (
            [(AvatarImageFormat)_data[at], (AvatarImageFormat)_data[at + 1], (AvatarImageFormat)_data[at + 2]],
            [(AvatarImagePalette)_data[at + 3], (AvatarImagePalette)_data[at + 4], (AvatarImagePalette)_data[at + 5]]);
    }

    /// <summary>Port of CAvatarX::CreatePose (avbfile.cpp:983) — one image, no mask.</summary>
    private ushort CreatePose(AvatarFile avatar, uint offset, AvatarImageFormat format, AvatarImagePalette palette)
    {
        return CreatePoseWithMask(avatar,
            [offset, 0, 0],
            [format, default, default],
            [palette, default, default]);
    }

    /// <summary>Port of CAvatarX::CreatePoseWithMask (avbfile.cpp:1007). Pose IDs are 1-based.</summary>
    private ushort CreatePoseWithMask(AvatarFile avatar, uint[] offsets, AvatarImageFormat[] formats,
                                      AvatarImagePalette[] palettes)
    {
        avatar.PoseList.Add(new Pose(offsets, formats, palettes));
        return (ushort)avatar.PoseList.Count;
    }

    private uint AdjustOffset(uint offset) => AdjustOffset(offset, _resourcesAdjustment);

    /// <summary>
    /// Port of the ADJUST_OFFSET macro (avbfile.cpp:19). A zero offset means "no such resource" and
    /// must stay zero, which is why the guard is there rather than an unconditional add.
    /// </summary>
    internal static uint AdjustOffset(uint offset, int by) =>
        offset != 0 ? (uint)((int)offset + by) : 0;

    // ========================================================================
    // Backdrop

    /// <summary>Port of CChatBackdrop::LoadBackdrop (avbfile.cpp:1669) and ::Load (avbfile.cpp:1774).</summary>
    private ChatBackdrop LoadBackdropCore()
    {
        ushort magic = BitConverter.ToUInt16(_data, 0);
        var backdrop = new ChatBackdrop { MagicNumber = magic };

        // A .bgb is allowed to just be a .BMP — the sniff is on the first two bytes and nothing else
        // (avbfile.cpp:1700).
        if (magic == AvbConstants.BmpMagic)
        {
            _stream.Position = 0;
            backdrop.Dib = ReadBmpFile();
            return backdrop;
        }

        if (magic != AvbConstants.MagicNumNew)
            throw new InvalidDataException($"Invalid backdrop file (magic 0x{magic:X4}).");

        var (_, type, version) = ReadHeader();

        if (type != (ushort)AvatarFileType.Backdrop)
            throw new InvalidDataException("Not a backdrop file.");
        if ((version >> 16) != 0)
            throw new InvalidDataException($"Unsupported version 0x{version:X}.");

        while (true)
        {
            ushort tag = _reader.ReadUInt16();

            if (tag == (ushort)AvatarRecordType.StartData)
                throw new InvalidDataException("No backdrop found in file.");

            // Backdrops are a later addition to the format, so they were never allowed to contain a
            // 1.0-era record. An old tag here is a hard error, not something to skip
            // (avbfile.cpp:1820) — unlike in an avatar, where the same tags are legal.
            if (tag < (ushort)AvatarRecordType.IconNew)
                throw new InvalidDataException($"Old tag {tag} not supported in a backdrop file.");

            ushort size = _reader.ReadUInt16();
            bool handled = false;

            switch ((AvatarRecordType)tag)
            {
                case AvatarRecordType.OriginalUrl:
                    backdrop.OriginalUrl = ReadString(AvbConstants.MaxUrl);
                    handled = true;
                    break;
                case AvatarRecordType.OverrideUrl:
                    backdrop.OverrideUrl = ReadString(AvbConstants.MaxUrl);
                    handled = true;
                    break;
                case AvatarRecordType.Copyright:
                    backdrop.Copyright = ReadString(AvbConstants.MaxCopyright);
                    handled = true;
                    break;
                case AvatarRecordType.UsageFlags:
                    backdrop.UsageFlags = _reader.ReadByte();
                    handled = true;
                    break;
                case AvatarRecordType.OffsetAdjustment:
                    _resourcesAdjustment += _reader.ReadInt32();
                    handled = true;
                    break;
            }

            if (tag == (ushort)AvatarRecordType.Backdrop)
            {
                uint offset = _reader.ReadUInt32();
                var format = (AvatarImageFormat)_reader.ReadByte();
                var palette = (AvatarImagePalette)_reader.ReadByte();

                // A backdrop is opaque by definition, so it has no global palette to inherit and no
                // mask to key against (avbfile.cpp:1882).
                if (palette is not (AvatarImagePalette.LocalPalette or AvatarImagePalette.NoPalette))
                    throw new InvalidDataException($"Backdrop palette type {palette} not allowed.");

                offset = AdjustOffset(offset);

                var pose = new Pose([offset, 0, 0], [format, default, default], [palette, default, default]);
                pose.Load(this, null);

                backdrop.Dib = pose.Image ?? throw new InvalidDataException("Backdrop image failed to load.");
                backdrop.OffsetAdjustment = _resourcesAdjustment;

                // Everything we came for; the rest of the tag stream is not our business.
                return backdrop;
            }

            if (!handled)
                _stream.Seek(size, SeekOrigin.Current);
        }
    }

    // ========================================================================
    // Images

    /// <summary>
    /// Loads one image resource from an absolute offset. Port of the CAvatarFileDIBImage /
    /// CAvatarFileZlibImage dispatch in CPose::Load (avbfile.cpp:1423).
    /// </summary>
    internal ArtDib ReadImage(uint offset, AvatarImageFormat format, AvatarImagePalette paletteType,
                             ArtPalette? globalPalette)
    {
        _stream.Position = offset;

        return format switch
        {
            AvatarImageFormat.Dib => ReadBmpFile(),
            AvatarImageFormat.LzDeflate => ReadZlibImage(paletteType, globalPalette),
            _ => throw new InvalidDataException($"Unknown image format {format}."),
        };
    }

    /// <summary>
    /// Resolves the colour table for an image. Port of CAvatarFileImage::GetProperPalette
    /// (avbfile.cpp:526).
    /// </summary>
    private ArtPalette GetProperPalette(AvatarImagePalette paletteType, ArtPalette? globalPalette)
    {
        switch (paletteType)
        {
            case AvatarImagePalette.NoPalette:
                return ArtPalette.Empty;

            case AvatarImagePalette.GlobalPalette:
                return globalPalette ?? ArtPalette.Empty;

            case AvatarImagePalette.LocalPalette:
            {
                // A local palette is a complete AK_COLORPALETTE record inlined immediately before the
                // bitmap data. The tag is re-checked rather than assumed: it is the only thing
                // confirming we seeked to a real resource and not into the middle of one.
                ushort tag = _reader.ReadUInt16();
                if (tag != (ushort)AvatarRecordType.ColorPalette)
                    throw new InvalidDataException($"No palette here — expected AK_COLORPALETTE, saw {tag}.");
                _ = _reader.ReadUInt16(); // record size, unused: we parse the body directly
                return ArtPalette.Read(_reader);
            }

            case AvatarImagePalette.Monochrome:
                return ArtPalette.Monochrome;

            case AvatarImagePalette.MaskedMono:
            case AvatarImagePalette.DualMask:
                return ArtPalette.MaskedMono;

            default:
                throw new InvalidDataException($"Unknown palette type {paletteType}.");
        }
    }

    /// <summary>
    /// Reads a deflate-compressed image. Port of CAvatarFileZlibImage::Read (avbfile.cpp:630).
    /// </summary>
    /// <remarks>
    /// The payload is bare bottom-up BI_RGB DIB bits: no file header, no colour table (the palette
    /// was read separately above), no RLE. Splitting the palette out is what lets many poses share
    /// one global table, and it is also why the compressed blob is worth compressing — colour tables
    /// famously do not deflate.
    /// </remarks>
    private ArtDib ReadZlibImage(AvatarImagePalette paletteType, ArtPalette? globalPalette)
    {
        var palette = GetProperPalette(paletteType, globalPalette);

        uint headerSize = _reader.ReadUInt32();
        if (headerSize < 40 || headerSize > 40 * 6)
            throw new InvalidDataException($"Apparent bad bitmap info header ({headerSize}).");

        // The BITMAPINFOHEADER minus its biSize field: biSize is not stored again, it *is*
        // headerSize (avbfile.cpp:680-684).
        int width = _reader.ReadInt32();
        int height = _reader.ReadInt32();
        _ = _reader.ReadUInt16();                 // biPlanes
        ushort bitCount = _reader.ReadUInt16();
        _ = _reader.ReadUInt32();                 // biCompression — always BI_RGB here
        _ = _reader.ReadUInt32();                 // biSizeImage
        _ = _reader.ReadInt32();                  // biXPelsPerMeter
        _ = _reader.ReadInt32();                  // biYPelsPerMeter
        _ = _reader.ReadUInt32();                 // biClrUsed
        _ = _reader.ReadUInt32();                 // biClrImportant
        if (headerSize > 40)
            _stream.Seek(headerSize - 40, SeekOrigin.Current);

        if (bitCount == 0)
            throw new InvalidDataException("Invalid image: zero bit count.");

        byte[] bits = ReadCompressedBuffer();

        bool topDown = height < 0;
        int absHeight = Math.Abs(height);

        int expected = ArtDib.StorageWidth(width, bitCount) * absHeight;
        if (bits.Length != expected)
            throw new InvalidDataException($"Image size mismatch: got {bits.Length}, expected {expected}.");

        return new ArtDib(width, absHeight, bitCount, topDown, palette, bits);
    }

    /// <summary>
    /// Port of CAvatarStream::AllocAndReadCompressedBuffer (avbfile.cpp:53). Raw zlib, so the blob
    /// starts 78 DA at level 9.
    /// </summary>
    private byte[] ReadCompressedBuffer()
    {
        uint uncompressedSize = _reader.ReadUInt32();
        uint compressedSize = _reader.ReadUInt32();

        if (uncompressedSize == 0)
            return [];

        if (uncompressedSize > AvbConstants.MaxCompressBufferSize ||
            compressedSize > AvbConstants.MaxCompressBufferSize)
            throw new InvalidDataException("Too big a buffer to read.");

        byte[] compressed = _reader.ReadBytes((int)compressedSize);
        if (compressed.Length != compressedSize)
            throw new EndOfStreamException("Truncated compressed buffer.");

        var output = new byte[uncompressedSize];
        using var zlib = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
        zlib.ReadExactly(output);

        return output;
    }

    /// <summary>
    /// Reads a complete .BMP from the current position. Port of CAvatarDIB::Load (avbfile.cpp:291).
    /// </summary>
    private ArtDib ReadBmpFile()
    {
        long fileStart = _stream.Position;

        ushort bfType = _reader.ReadUInt16();
        if (bfType != AvbConstants.BmpMagic)
            throw new InvalidDataException("Not a bitmap file.");

        _ = _reader.ReadUInt32();                 // bfSize
        _ = _reader.ReadUInt16();                 // bfReserved1
        _ = _reader.ReadUInt16();                 // bfReserved2
        uint bfOffBits = _reader.ReadUInt32();

        uint biSize = _reader.ReadUInt32();
        int width;
        int height;
        ushort bitCount;
        uint clrUsed;

        if (biSize == 40)
        {
            width = _reader.ReadInt32();
            height = _reader.ReadInt32();
            _ = _reader.ReadUInt16();             // biPlanes
            bitCount = _reader.ReadUInt16();
            uint compression = _reader.ReadUInt32();
            _ = _reader.ReadUInt32();             // biSizeImage
            _ = _reader.ReadInt32();
            _ = _reader.ReadInt32();
            clrUsed = _reader.ReadUInt32();
            _ = _reader.ReadUInt32();             // biClrImportant

            if (compression != 0)
                throw new NotSupportedException($"RLE-compressed DIBs (biCompression {compression}) are not supported.");
        }
        else if (biSize == 12)
        {
            // BITMAPCOREHEADER — the OS/2 layout. Widened to a Windows header, as avbfile.cpp:353 does.
            width = _reader.ReadUInt16();
            height = _reader.ReadUInt16();
            _ = _reader.ReadUInt16();             // bcPlanes
            bitCount = _reader.ReadUInt16();
            clrUsed = 0;
        }
        else
        {
            throw new InvalidDataException($"File is not Windows or PM DIB format (biSize {biSize}).");
        }

        if (bitCount == 0)
            throw new InvalidDataException("Bitmap bad: zero bit count.");

        int colours = ArtDib.NumColorEntries(bitCount, clrUsed);
        var entries = new uint[colours];
        for (int i = 0; i < colours; i++)
        {
            if (biSize == 12)
            {
                // PM entries are three bytes, blue first (avbfile.cpp:419).
                byte b = _reader.ReadByte(), g = _reader.ReadByte(), r = _reader.ReadByte();
                entries[i] = ArtPalette.Rgb(r, g, b);
            }
            else
            {
                byte b = _reader.ReadByte(), g = _reader.ReadByte(), r = _reader.ReadByte();
                _ = _reader.ReadByte();           // rgbReserved
                entries[i] = ArtPalette.Rgb(r, g, b);
            }
        }

        bool topDown = height < 0;
        int absHeight = Math.Abs(height);

        _stream.Position = fileStart + bfOffBits;

        int stride = ArtDib.StorageWidth(width, bitCount);
        byte[] bits = _reader.ReadBytes(stride * absHeight);
        if (bits.Length != stride * absHeight)
            throw new EndOfStreamException("Truncated bitmap bits.");

        return new ArtDib(width, absHeight, bitCount, topDown, new ArtPalette(entries), bits);
    }

    // ========================================================================
    // Primitives

    private (ushort Magic, ushort Type, ushort Version) ReadHeader()
    {
        _stream.Position = 0;
        return (_reader.ReadUInt16(), _reader.ReadUInt16(), _reader.ReadUInt16());
    }

    /// <summary>
    /// Reads a NUL-terminated string, stopping at the cap. Port of CAvatarStream::ReadString
    /// (avbfile.cpp:26).
    /// </summary>
    /// <remarks>
    /// Decoded as Windows-1252, not ASCII or UTF-8: this is 1996 Win32 ANSI text, and the copyright
    /// notices in the shipped art contain a bare 0xA9 for the © sign, which is neither valid UTF-8
    /// nor representable in ASCII.
    /// </remarks>
    private string ReadString(int maxLength)
    {
        Span<byte> buffer = stackalloc byte[maxLength];
        int length = 0;

        while (length < maxLength)
        {
            byte b = _reader.ReadByte();
            if (b == 0)
                break;
            buffer[length++] = b;
        }

        return Cp1252.GetString(buffer[..length]);
    }

    /// <summary>
    /// Windows-1252 decoding without pulling in System.Text.Encoding.CodePages.
    /// </summary>
    /// <remarks>
    /// .NET Core dropped the legacy code pages from the base library, and taking a package
    /// dependency on the whole CodePages provider to decode one byte range is a poor trade.
    /// CP1252 is Latin-1 everywhere except 0x80-0x9F, so the table below is the entire difference.
    /// </remarks>
    private static class Cp1252
    {
        private static readonly char[] HighRange =
        [
            '€', '', '‚', 'ƒ', '„', '…', '†', '‡',
            'ˆ', '‰', 'Š', '‹', 'Œ', '', 'Ž', '',
            '', '‘', '’', '“', '”', '•', '–', '—',
            '˜', '™', 'š', '›', 'œ', '', 'ž', 'Ÿ',
        ];

        public static string GetString(ReadOnlySpan<byte> bytes)
        {
            var sb = new StringBuilder(bytes.Length);
            foreach (byte b in bytes)
                sb.Append(b is >= 0x80 and <= 0x9F ? HighRange[b - 0x80] : (char)b);
            return sb.ToString();
        }
    }
}
