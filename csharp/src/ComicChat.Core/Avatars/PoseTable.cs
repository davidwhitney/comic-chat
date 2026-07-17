namespace ComicChat.Core.Avatars;

/// <summary>
/// One row of an avatar's pose table: "pose <see cref="PoseId"/> depicts
/// <see cref="Emotion"/> at <see cref="Intensity"/>".
/// Port of the shared shape of FACEREC / BODYREC / RBODYREC (avatar.h:103-129).
/// </summary>
/// <remarks>
/// The original records also carry layout geometry (attachment points, face offsets) that pose
/// selection never reads. Only these three fields participate in the decision, so this is all the
/// interface asks for — the .avb loader's richer types can implement it directly.
/// </remarks>
public interface IPoseRecord
{
    /// <summary>Wheel angle or gesture sentinel. See <see cref="Em"/>.</summary>
    float Emotion { get; }

    /// <summary>How strongly this pose expresses its emotion, in [0, 1].</summary>
    float Intensity { get; }

    /// <summary>Identifier of the artwork to draw.</summary>
    int PoseId { get; }
}

/// <summary>
/// An avatar's drawable poses, as selection sees them.
/// </summary>
/// <remarks>
/// Comic Chat had two avatar shapes (avatar.h:259, 290). A "complex" avatar is drawn as a head
/// plus a torso from two independent tables, which is what lets it laugh and point at the same
/// time. A "simple" avatar has one table of whole bodies. Both are modelled here: a simple avatar
/// leaves <see cref="Faces"/> empty and puts its bodies in <see cref="Torsos"/>, and
/// <see cref="AvatarPoseResolver"/> is constructed with the matching <see cref="AvatarPoseStyle"/>.
/// </remarks>
public interface IPoseTable
{
    /// <summary>Head poses (fRec, avatar.h:292). Empty for a simple avatar.</summary>
    IReadOnlyList<IPoseRecord> Faces { get; }

    /// <summary>Torso poses for a complex avatar, or whole bodies for a simple one (bRec, avatar.h:261/293).</summary>
    IReadOnlyList<IPoseRecord> Torsos { get; }
}

/// <summary>Which pose table layout an avatar uses.</summary>
public enum AvatarPoseStyle
{
    /// <summary>Independent head and torso, drawn together. CAvatarComplex (avatar.h:290).</summary>
    Complex,

    /// <summary>A single whole-body pose. CAvatarSimple (avatar.h:259).</summary>
    Simple,
}

/// <summary>Whether the user has pinned their avatar's pose by hand. Port of AF_* (avatar.h:180-182).</summary>
public enum AvatarFreezeState
{
    /// <summary>The expert system may choose poses. AF_UNFROZEN.</summary>
    Unfrozen = 1,

    /// <summary>Pinned for one message, then released by ResetAvatar (avatar.cpp:454). AF_TEMPFROZEN.</summary>
    TempFrozen = 2,

    /// <summary>Pinned until the user says otherwise. AF_FROZEN.</summary>
    Frozen = 3,
}

/// <summary>Avatar capability flags. Port of avatar.h:184-187.</summary>
[Flags]
public enum AvatarFlags
{
    None = 0,

    /// <summary>Avatar supplies head poses. HEADMASK.</summary>
    HeadMask = 1,

    /// <summary>Avatar supplies torso poses. TORSOMASK.</summary>
    TorsoMask = 2,

    /// <summary>Torso is drawn before the head. TORSOFIRST.</summary>
    TorsoFirst = 4,

    /// <summary>Avatar has poses that address another character. OTHERMAPPED.</summary>
    OtherMapped = 8,
}

/// <summary>
/// The outcome of pose selection: indices into an <see cref="IPoseTable"/>.
/// Stands in for CBodyDouble / CBodySingle (avatar.h:132, 151), minus the drawing.
/// </summary>
/// <param name="FaceIndex">Index into <see cref="IPoseTable.Faces"/>, or -1 for a simple avatar.</param>
/// <param name="TorsoIndex">Index into <see cref="IPoseTable.Torsos"/>.</param>
public readonly record struct ResolvedBody(int FaceIndex, int TorsoIndex);
