using ComicChat.Core.Avatars;

namespace ComicChat.Core.Comic;

/// <summary>
/// The per-avatar state the layout engine reads and writes.
/// Port of the layout-relevant members of CAvatarX (avatar.h) plus the talk-tos on CUserInfo.
/// </summary>
/// <remarks>
/// The last* fields are the staging hysteresis: after each panel, UpdateHistoresis
/// (panel.cpp:435) records where everyone stood and which way they faced, and the next panel's
/// placement search is biased toward reproducing it. Without this, characters would hop from
/// side to side between panels and the comic would be unreadable.
/// </remarks>
public sealed class AvatarLayoutState
{
    public uint Id { get; init; }

    /// <summary>Facing used in the previous panel; breaks ties in EvalPlacement (panel.cpp:397).</summary>
    public bool LastDir { get; set; }

    /// <summary>Avatar that stood to this one's right last panel.</summary>
    public uint LastRight { get; set; }

    /// <summary>Avatar that stood to this one's left last panel.</summary>
    public uint LastLeft { get; set; }

    /// <summary>Avatars this one is addressing; pulls them into the panel and drives facing.</summary>
    public List<uint> TalkTos { get; } = [];

    /// <summary>Build a neutral body for this avatar — used when dragging an addressee into a panel.</summary>
    public Func<Emotion, Body?>? BodyFromEmotion { get; init; }
}

/// <summary>
/// Resolves avatar ids to layout state. Port of the global GetAvatar() (avatar.cpp).
/// </summary>
/// <remarks>
/// The original used a process-wide avatar table. An interface keeps the layout engine
/// testable with fake avatars and free of any dependency on the art loader.
/// </remarks>
public interface IAvatarDirectory
{
    AvatarLayoutState? Get(uint avatarId);

    /// <summary>Return the avatar to a neutral pose after it has spoken. Port of ResetAvatar (panel.cpp:1127).</summary>
    void ResetAvatar(uint avatarId);
}

/// <summary>A simple in-memory directory. Sufficient for tests and for the app's live session.</summary>
public sealed class AvatarDirectory : IAvatarDirectory
{
    private readonly Dictionary<uint, AvatarLayoutState> _states = [];

    /// <summary>
    /// Invoked by <see cref="ResetAvatar"/> once a panel has been laid out.
    /// </summary>
    /// <remarks>
    /// The layout engine knows <i>when</i> to reset an avatar but not <i>how</i> — the pose state
    /// lives in the Avatars layer, which Comic must not depend on. The owner supplies the action;
    /// see AvatarPoseResolver.ResetAvatar for what the original did (avatar.cpp:454).
    /// </remarks>
    public Action<uint>? ResetHandler { get; set; }

    public AvatarLayoutState GetOrAdd(uint id, Func<Emotion, Body?>? bodyFactory = null)
    {
        if (_states.TryGetValue(id, out var s)) return s;
        s = new AvatarLayoutState { Id = id, BodyFromEmotion = bodyFactory };
        _states[id] = s;
        return s;
    }

    public AvatarLayoutState? Get(uint avatarId) => _states.GetValueOrDefault(avatarId);

    public void ResetAvatar(uint avatarId) => ResetHandler?.Invoke(avatarId);

    /// <summary>
    /// Clear every avatar's staging hysteresis, keeping the avatars themselves.
    /// </summary>
    /// <remarks>
    /// Needed before replaying history into a fresh comic: LastDir/LastLeft/LastRight describe
    /// where everyone stood in the panel before, so replaying with values left over from the
    /// discarded strip would stage the rebuilt one differently from the original run.
    /// </remarks>
    public void ResetAll()
    {
        foreach (var s in _states.Values)
        {
            s.LastDir = false;
            s.LastLeft = 0;
            s.LastRight = 0;
            s.TalkTos.Clear();
        }
    }
}
