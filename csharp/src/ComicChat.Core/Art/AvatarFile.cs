using ComicChat.Core.Avatars;

namespace ComicChat.Core.Art;

/// <summary>
/// A face pose row. Port of FACEREC (avatar.h) as filled by CAvatarComplex::LoadFaceRecs
/// (avbfile.cpp:1166).
/// </summary>
/// <param name="PoseId">1-based index into <see cref="AvatarFile.Poses"/>; 0 is INVALID_POSE_ID.</param>
/// <param name="EmotionIndex">Raw index into <see cref="Em.EmFloats"/> as stored in the file.</param>
/// <param name="Emotion">The resolved wheel angle or gesture sentinel.</param>
/// <param name="Intensity">byIntensity / 255.</param>
/// <param name="Cx">Torso attachment point, x (avbfile.cpp:1221, fRec.xCX).</param>
/// <param name="Cy">Torso attachment point, y.</param>
/// <param name="CxDelta">Per-frame attachment drift, x.</param>
/// <param name="CyDelta">Per-frame attachment drift, y.</param>
/// <param name="FaceX">Balloon tail anchor, x. Truncated to a byte by the original.</param>
/// <param name="FaceY">Balloon tail anchor, y.</param>
public readonly record struct FaceRec(
    ushort PoseId,
    ushort EmotionIndex,
    float Emotion,
    float Intensity,
    short Cx,
    short Cy,
    short CxDelta,
    short CyDelta,
    byte FaceX,
    byte FaceY) : IPoseRecord
{
    int IPoseRecord.PoseId => PoseId;
}

/// <summary>
/// A torso pose row. Port of BODYREC as filled by CAvatarComplex::LoadTorsoRecs (avbfile.cpp:1240).
/// </summary>
public readonly record struct TorsoRec(
    ushort PoseId,
    ushort EmotionIndex,
    float Emotion,
    float Intensity,
    short Cx,
    short Cy) : IPoseRecord
{
    int IPoseRecord.PoseId => PoseId;
}

/// <summary>
/// A whole-body pose row for simple avatars. Port of RBODYREC as filled by
/// CAvatarSimple::LoadBodyRecs (avbfile.cpp:1062).
/// </summary>
public readonly record struct BodyRec(
    ushort PoseId,
    ushort EmotionIndex,
    float Emotion,
    float Intensity,
    byte FaceX,
    byte FaceY) : IPoseRecord
{
    int IPoseRecord.PoseId => PoseId;
}

/// <summary>
/// The common shape of a loaded .avb file. Port of CAvatarX (avatar.h, avbfile.cpp:743).
/// </summary>
public abstract class AvatarFile
{
    public ushort MagicNumber { get; internal set; }

    public AvatarFileType Type { get; internal set; }

    public ushort Version { get; internal set; }

    /// <summary>AK_NAME. Capped at 60 characters by the original.</summary>
    public string? Name { get; internal set; }

    /// <summary>AK_COPYRIGHT. Capped at 256.</summary>
    public string? Copyright { get; internal set; }

    /// <summary>AK_ORIGINAL_URL — where the art was published. Capped at 512.</summary>
    public string? OriginalUrl { get; internal set; }

    /// <summary>AK_OVERRIDE_URL — a redirect applied by whoever redistributed it. Capped at 512.</summary>
    public string? OverrideUrl { get; internal set; }

    /// <summary>AK_STYLE, truncated from a u16 to a byte exactly as avbfile.cpp:900 does.</summary>
    public byte Style { get; internal set; }

    /// <summary>AK_FLAGS, likewise truncated (avbfile.cpp:910).</summary>
    public AvatarFlags Flags { get; internal set; }

    /// <summary>Which pose table layout this file uses, for <see cref="AvatarPoseResolver"/>.</summary>
    public abstract AvatarPoseStyle PoseStyle { get; }

    public byte UsageFlags { get; internal set; }

    /// <summary>
    /// The running total from every AK_OFFSET_ADJUSTMENT seen so far. See
    /// <see cref="AvbReader"/> for why this exists.
    /// </summary>
    public int OffsetAdjustment { get; internal set; }

    /// <summary>The file-level palette from AK_COLORPALETTE, shared by AIP_GLOBALPALETTE images.</summary>
    public ArtPalette GlobalPalette { get; internal set; } = ArtPalette.Empty;

    /// <summary>Every distinct image in the file. Pose IDs are 1-based indices into this list.</summary>
    public IReadOnlyList<Pose> Poses => PoseList;

    internal readonly List<Pose> PoseList = [];

    /// <summary>The pose ID of the avatar's icon, or 0 if the file has none.</summary>
    public ushort IconPoseId { get; internal set; }

    public Pose? Icon => GetPose(IconPoseId);

    /// <summary>Resolves a 1-based pose ID, tolerating INVALID_POSE_ID.</summary>
    public Pose? GetPose(ushort poseId) =>
        poseId == AvbConstants.InvalidPoseId || poseId > PoseList.Count ? null : PoseList[poseId - 1];
}

/// <summary>
/// An avatar drawn as a face plus a torso, matched independently. Port of CAvatarComplex
/// (avatar.h, avbfile.cpp:1134).
/// </summary>
public sealed class AvatarComplex : AvatarFile, IPoseTable
{
    public override AvatarPoseStyle PoseStyle => AvatarPoseStyle.Complex;

    public IReadOnlyList<FaceRec> Faces => FaceList;

    public IReadOnlyList<TorsoRec> Torsos => TorsoList;

    internal List<FaceRec> FaceList = [];
    internal List<TorsoRec> TorsoList = [];

    IReadOnlyList<IPoseRecord> IPoseTable.Faces => FaceList.Cast<IPoseRecord>().ToList();

    IReadOnlyList<IPoseRecord> IPoseTable.Torsos => TorsoList.Cast<IPoseRecord>().ToList();
}

/// <summary>
/// An avatar drawn as one whole-body image per emotion. Port of CAvatarSimple
/// (avatar.h, avbfile.cpp:1036).
/// </summary>
public sealed class AvatarSimple : AvatarFile, IPoseTable
{
    public override AvatarPoseStyle PoseStyle => AvatarPoseStyle.Simple;

    public IReadOnlyList<BodyRec> Bodies => BodyList;

    internal List<BodyRec> BodyList = [];

    IReadOnlyList<IPoseRecord> IPoseTable.Faces => [];

    /// <summary>
    /// A simple avatar's whole-body poses live in the torso slot — that is the convention
    /// <see cref="IPoseTable"/> defines, and what the resolver reads for
    /// <see cref="AvatarPoseStyle.Simple"/>.
    /// </summary>
    IReadOnlyList<IPoseRecord> IPoseTable.Torsos => BodyList.Cast<IPoseRecord>().ToList();
}

/// <summary>
/// A room background. Port of CChatBackdrop (backdrop.h, avbfile.cpp:1669).
/// </summary>
public sealed class ChatBackdrop
{
    /// <summary>The magic number the file actually started with: 'BM' or 0x8181.</summary>
    public ushort MagicNumber { get; internal set; }

    public string? Copyright { get; internal set; }

    public string? OriginalUrl { get; internal set; }

    public string? OverrideUrl { get; internal set; }

    public byte UsageFlags { get; internal set; }

    public int OffsetAdjustment { get; internal set; }

    /// <summary>The backdrop image. Port of CChatBackdrop::GetDrawing (backdrop.h:11).</summary>
    public ArtDib Dib { get; internal set; } = null!;

    public int Width => Dib.Width;

    public int Height => Dib.Height;

    private byte[]? _bgra;

    /// <summary>Top-down 32-bit BGRA. Backdrops are opaque — they carry no mask.</summary>
    public byte[] Bgra => _bgra ??= Dib.ToBgra();
}
