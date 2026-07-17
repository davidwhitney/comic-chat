namespace ComicChat.Irc;

/// <summary>Port of the UF_* user flags (userinfo.h:4-16).</summary>
[Flags]
public enum UserFlags
{
    None = 0,
    Ignored = 1,
    ComicUser = 2,
    Departed = 4,
    Operator = 8,
    External = 16,
    Spectator = 32,
    RequestPing = 64,
    HasVoice = 128,
    Away = 256,
    ScreenName = 512,
    Owner = 1024,
    AutoDownload = 16384,
    InteractiveDownload = 32768,
}

/// <summary>
/// The most recent display state decoded for a user — pose, mode and addressees.
/// Port of CUserDisplayInfo (userinfo.h:27).
/// </summary>
/// <remarks>
/// This is per-USER rather than per-message because the IRCX transport splits one logical utterance
/// across two lines (a DATA carrying the pose, then a PRIVMSG carrying the text), so the pose has to
/// be parked somewhere between them. See <see cref="UserInfo.ValidUdi"/>.
/// </remarks>
public sealed class UserDisplayInfo
{
    public Annotations Annotations { get; set; } = Irc.Annotations.Reset();

    /// <summary>Balloon modes, widened from the wire's <see cref="SayMode"/>.</summary>
    public BalloonModes Modes { get; set; } = BalloonModes.Say;

    /// <summary>Nicknames this utterance is addressed to (the "T" field).</summary>
    public List<string> TalkTos { get; } = [];

    public bool Cooked => Annotations.Cooked;
    public bool Requested => Annotations.Requested;

    /// <summary>Port of CUserDisplayInfo::Reset (userinfo.h:40).</summary>
    public void Reset()
    {
        Annotations = Irc.Annotations.Reset();
        Modes = BalloonModes.Say; // the original resets m_uModes to 0x0001 == BM_SAY
        TalkTos.Clear();
    }

    /// <summary>Adopt a freshly decoded annotation, keeping the derived fields in step.</summary>
    public void Apply(Annotations a)
    {
        Annotations = a;
        Modes = a.Modes;
        TalkTos.Clear();
        TalkTos.AddRange(a.SafeTalkTos);
    }
}

/// <summary>
/// A user we know about. Port of CUserInfo (userinfo.h:64).
/// </summary>
public sealed class UserInfo(string nick, string? fullName = null)
{
    public string Name { get; set; } = nick;

    /// <summary>The full nick!user@host mask, used for nick-mask matching.</summary>
    public string? FullName { get; set; } = fullName;

    public string? ScreenName { get; set; }

    public UserFlags Flags { get; set; }

    /// <summary>Avatar NAME as announced by the peer. The art itself never travels over IRC.</summary>
    public string? AvatarRealName { get; private set; }

    /// <summary>Avatar URL as announced by the peer. May be the deferred "?" placeholder.</summary>
    public string? AvatarRealUrl { get; private set; }

    public UserDisplayInfo Udi { get; } = new();

    /// <summary>
    /// One-shot latch: a pose arrived out-of-band via IRCX DATA/CCUDI1 and arms the NEXT text
    /// message from this user. Port of m_bbValidUDI (userinfo.h:154).
    /// </summary>
    /// <remarks>
    /// WHY A LATCH: on IRCX the annotation and the text are two separate server messages, so the
    /// receiver has no framing that binds them. ProcessUDIData sets the latch (protsupp.cpp:1541);
    /// the next ProcessSay consumes it and unconditionally clears it (protsupp.cpp:1626), so an
    /// unclaimed pose decays after exactly one message instead of leaking into later ones. It also
    /// suppresses the reset that would otherwise mark the text "not cooked": ProcessSay only resets
    /// the UDI when there is no inline annotation AND the latch is clear (protsupp.cpp:1616).
    /// </remarks>
    public bool ValidUdi { get; set; }

    public bool Ignored
    {
        get => Flags.HasFlag(UserFlags.Ignored);
        set => SetFlag(UserFlags.Ignored, value);
    }

    /// <summary>True once we have seen this user announce a Comic Chat identity.</summary>
    public bool IsComicUser
    {
        get => Flags.HasFlag(UserFlags.ComicUser);
        set => SetFlag(UserFlags.ComicUser, value);
    }

    public bool IsOperator
    {
        get => Flags.HasFlag(UserFlags.Operator);
        set => SetFlag(UserFlags.Operator, value);
    }

    public bool IsOwner
    {
        get => Flags.HasFlag(UserFlags.Owner);
        set => SetFlag(UserFlags.Owner, value);
    }

    public bool IsSpectator
    {
        get => Flags.HasFlag(UserFlags.Spectator);
        set => SetFlag(UserFlags.Spectator, value);
    }

    public bool HasVoice
    {
        get => Flags.HasFlag(UserFlags.HasVoice);
        set => SetFlag(UserFlags.HasVoice, value);
    }

    public bool IsDeparted
    {
        get => Flags.HasFlag(UserFlags.Departed);
        set => SetFlag(UserFlags.Departed, value);
    }

    /// <summary>Port of IsSpeaker (userinfo.h:123).</summary>
    public bool IsSpeaker => !IsOperator && !IsSpectator;

    /// <summary>True when we still owe this peer an avatar download. Port of NeedsDownload (userinfo.h:107).</summary>
    public bool NeedsDownload =>
        Flags.HasFlag(UserFlags.AutoDownload) || Flags.HasFlag(UserFlags.InteractiveDownload);

    public void SetFlag(UserFlags flag, bool value) =>
        Flags = value ? Flags | flag : Flags & ~flag;

    public void SetAvatarRealInfo(string? name, string? url)
    {
        AvatarRealName = name;
        AvatarRealUrl = url;
    }

    /// <summary>True when the recorded URL is the deferred placeholder rather than a real address.</summary>
    public bool HasDeferredUrl =>
        AvatarRealUrl == ComicVerbs.DeferredUrlString;

    public override string ToString() => Name;
}
