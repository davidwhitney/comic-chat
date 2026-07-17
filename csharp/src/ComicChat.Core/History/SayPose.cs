using ComicChat.Core.Comic;

namespace ComicChat.Core.History;

/// <summary>
/// The pose and mode carried by a <see cref="SayEntry"/>.
/// Port of the subset of CUserDisplayInfo (userinfo.h:27) that history records.
/// </summary>
/// <remarks>
/// This deliberately does not reuse <c>ComicChat.Irc.Annotations</c>, even though the two carry
/// the same six values. The file format and the wire format have <b>diverged</b>: the wire
/// encodes each field as one byte biased by '0' and treats "R" as a presence-only flag, whereas
/// the archive writes plain decimal and gives R a value (<c>R:%d</c>, histent.cpp:145). Sharing
/// one type would force one of the two formats to lie. Core also must not depend on the IRC
/// layer.
/// </remarks>
public struct SayPose()
{
    /// <summary>Torso art index (m_chGest).</summary>
    public int GestureIndex = -1;

    /// <summary>Index into <see cref="Avatars.Em.EmFloats"/> for the torso (m_chGestE).</summary>
    public int GestureEmotion = 0;

    /// <summary>Torso intensity; the float is <c>value / 10.0</c> (m_chGestI).</summary>
    public int GestureIntensity = -1;

    /// <summary>Face art index (m_chExpr).</summary>
    public int ExpressionIndex = -1;

    /// <summary>Index into <see cref="Avatars.Em.EmFloats"/> for the face (m_chExprE).</summary>
    public int ExpressionEmotion = 0;

    /// <summary>Face intensity; the float is <c>value / 10.0</c> (m_chExprI).</summary>
    public int ExpressionIntensity = -1;

    /// <summary>
    /// Whether the sender pinned the pose by hand.
    /// </summary>
    /// <remarks>
    /// The original hardcodes this to 1 when building an entry — "for now, ignore req
    /// parameter" (histent.cpp:56) — so in practice every archived line claims to be requested.
    /// Reproduced, because it is what the shipped format contains.
    /// </remarks>
    public int Requested = 1;

    public BalloonMode Modes = BalloonMode.Say;

    /// <summary>
    /// False when the speaker was a plain IRC client that sent no pose. The replay then runs the
    /// expert system over the text instead of honouring a pose (chatdoc.cpp:451).
    /// </summary>
    public bool Cooked = false;

    /// <summary>Nicks this line was addressed to (the "T:" field).</summary>
    public List<string> TalkTos = [];

    /// <summary>True once both intensities are set — the original's test for a usable pose.</summary>
    public readonly bool HasPose => GestureIntensity != -1 && ExpressionIntensity != -1;
}
