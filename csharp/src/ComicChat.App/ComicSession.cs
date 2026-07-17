using ComicChat.App.Rendering;
using ComicChat.Core.Art;
using ComicChat.Core.Avatars;
using ComicChat.Core.Comic;
using ComicChat.Core.History;
using ComicChat.Core.Semantics;

namespace ComicChat.App;

/// <summary>One participant in the comic: their avatar art, pose resolver and identity.</summary>
public sealed class Participant
{
    public required uint Id { get; init; }
    public required string Nick { get; set; }
    public required AvatarFile Avatar { get; init; }
    public required AvatarPoseResolver Resolver { get; init; }

    /// <summary>Name announced over IRC via "# Appears as". Art itself never crosses the wire.</summary>
    public string AvatarName => Avatar.Name ?? "NONE";

    /// <summary>Present in the room. Departed users stay known so their old panels still replay.</summary>
    public bool Present { get; set; } = true;
}

/// <summary>
/// A chat session: who is present, what they look like, the history, and the comic built from it.
/// Roughly the role of CChatDoc (chatdoc.h).
/// </summary>
/// <remarks>
/// The comic is a projection of <see cref="History"/>, not the source of truth. Anything that
/// changes the comic goes through the history so it can be replayed on resize and written to a
/// <c>.ccc</c> archive — which is exactly how the original worked.
/// </remarks>
public sealed class ComicSession : IChatDocument
{
    private readonly Dictionary<uint, Participant> _participants = [];
    private readonly Dictionary<string, uint> _nickToId = new(StringComparer.OrdinalIgnoreCase);
    private readonly AvatarDirectory _directory = new();
    private readonly TextPose _textPose;
    private readonly List<AvatarFile> _artPool = [];
    private uint _nextId = 1;

    public LayoutContext Ctx { get; }
    public UnitPanelPage Page { get; private set; }
    public IAvatarDirectory Directory => _directory;
    public IReadOnlyDictionary<uint, Participant> Participants => _participants;
    public ChatHistory History { get; }

    /// <summary>The local user's participant id.</summary>
    public uint SelfId { get; private set; }

    /// <summary>The strip's title, set by <see cref="StartHistory"/>.</summary>
    public string Title { get; private set; } = "";

    /// <summary>Raised when the comic changes and the view should redraw.</summary>
    public event Action? ComicChanged;

    /// <summary>Resolves a backdrop name to its registered id. Supplied by the UI.</summary>
    public Func<string, ushort>? BackDropIdByName { get; set; }

    public ComicSession(LayoutContext ctx, IEnumerable<AvatarFile> artPool, TextPose? textPose = null)
    {
        Ctx = ctx;
        _textPose = textPose ?? new TextPose();
        _artPool.AddRange(artPool);
        if (_artPool.Count == 0)
            throw new ArgumentException("Need at least one avatar to run a comic.", nameof(artPool));

        // After each panel, expire temporary freezes and return unfrozen avatars to neutral —
        // the original's ResetAvatar (panel.cpp:1127 → avatar.cpp:454). This is what makes a
        // wheel-picked pose last exactly one line.
        _directory.ResetHandler = id =>
        {
            if (_participants.TryGetValue(id, out var p)) p.Resolver.ResetAvatar();
        };

        Page = NewPage();
        History = new ChatHistory(this);
    }

    private UnitPanelPage NewPage() => new(Ctx, _directory) { BodyFactory = CreateBody };

    /// <summary>
    /// Throw the comic away, keeping the history. Port of ResetExistingPanels (pageview.cpp:1111).
    /// </summary>
    /// <remarks>
    /// Avatar layout state is reset too: the staging hysteresis (who stood where last panel)
    /// is per-panel history, so replaying with stale values would stage the rebuilt strip
    /// differently from the original run.
    /// </remarks>
    public void ResetComic()
    {
        ushort backId = Page.CurrentBackDropId;
        var backMode = Page.CurrentBackDropMode;
        uint docSeed = Page.DocumentSeed;

        Page = NewPage();
        Page.DocumentSeed = docSeed;
        Page.CurrentBackDropId = backId;
        Page.CurrentBackDropMode = backMode;

        _directory.ResetAll();
    }

    /// <summary>Rebuild the comic from history. Call after changing the panel size.</summary>
    public void Reload()
    {
        History.Reload();
        ComicChanged?.Invoke();
    }

    // ---- participants -------------------------------------------------------

    public Participant GetOrCreate(string nick, string? avatarName = null)
    {
        if (_nickToId.TryGetValue(nick, out var existingId))
        {
            var existing = _participants[existingId];
            if (avatarName is null ||
                existing.AvatarName.Equals(avatarName, StringComparison.OrdinalIgnoreCase))
                return existing;

            var replaced = Create(nick, avatarName, existingId);
            _participants[existingId] = replaced;
            return replaced;
        }

        uint id = _nextId++;
        var p = Create(nick, avatarName, id);
        _participants[id] = p;
        _nickToId[nick] = id;
        _directory.GetOrAdd(id, _ => CreateBody(id));
        return p;
    }

    private Participant Create(string nick, string? avatarName, uint id)
    {
        var art = PickArt(avatarName, id);
        return new Participant
        {
            Id = id,
            Nick = nick,
            Avatar = art,
            Resolver = new AvatarPoseResolver((IPoseTable)art, art.PoseStyle) { Flags = art.Flags },
        };
    }

    /// <summary>
    /// Resolve an avatar by announced name, else deal one out round-robin.
    /// </summary>
    /// <remarks>
    /// Non-Comic-Chat users get a character automatically — the original assigns one so a plain
    /// mIRC user still appears in the strip rather than breaking the comic.
    /// </remarks>
    private AvatarFile PickArt(string? avatarName, uint id)
    {
        if (avatarName is not null)
        {
            var match = _artPool.FirstOrDefault(a =>
                string.Equals(a.Name, avatarName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return _artPool[(int)((id - 1) % (uint)_artPool.Count)];
    }

    public Participant? ByNick(string nick) =>
        _nickToId.TryGetValue(nick, out var id) ? _participants[id] : null;

    public void SetSelf(string nick, string? avatarName = null) => SelfId = GetOrCreate(nick, avatarName).Id;

    private Body CreateBody(uint id)
    {
        var p = _participants[id];
        var body = new AvatarBody(id, p.Avatar, p.Resolver);
        body.Apply(p.Resolver.GetNeutralBody());
        return body;
    }

    // ---- recording ----------------------------------------------------------

    /// <summary>
    /// Say something: record it in history and draw it.
    /// </summary>
    /// <param name="pose">
    /// The pose decoded from the wire when the sender was a Comic Chat client. Null means the
    /// message came from a plain IRC client, and the expert system will synthesise a pose from
    /// the words at replay time (chatdoc.cpp:451).
    /// </param>
    public void Say(string nick, string text, BalloonMode mode = BalloonMode.Say,
                    ResolvedBody? pose = null, IEnumerable<string>? talkTos = null,
                    Emotion? emotions = null)
    {
        var p = GetOrCreate(nick);

        var sayPose = new SayPose { Modes = mode, TalkTos = [.. talkTos ?? []] };

        if (pose is { } rb)
        {
            // A pose that came off the wire: record the indices so replay reproduces it exactly.
            sayPose.ExpressionIndex = rb.FaceIndex;
            sayPose.GestureIndex = rb.TorsoIndex;

            var em = emotions ?? new Emotion(0, Em.Neutral);
            sayPose.ExpressionEmotion = Em.FloatToEmotionIndex(em.EmotionValue);
            sayPose.GestureEmotion = sayPose.ExpressionEmotion;
            sayPose.ExpressionIntensity = (int)(em.Intensity * 10);
            sayPose.GestureIntensity = sayPose.ExpressionIntensity;
            sayPose.Cooked = true;
        }

        SayWithPose(nick, text, sayPose);
    }

    /// <summary>
    /// Record and draw a line whose pose is already in archive form — the inbound wire path.
    /// </summary>
    /// <remarks>
    /// The caller maps the wire annotation onto <see cref="SayPose"/>. Resolution happens in
    /// <see cref="ProcessLine"/>, so it runs identically on the live line and on every replay.
    /// </remarks>
    public void SayWithPose(string nick, string text, SayPose pose)
    {
        GetOrCreate(nick);
        History.AddAndExecute(new SayEntry(nick, text, pose));
        ComicChanged?.Invoke();
    }

    public void RecordJoin(string nick, string? fullName = null) =>
        History.AddAndExecute(new JoinEntry(nick, fullName));

    public void RecordPart(string nick) => History.AddAndExecute(new PartEntry(nick));

    public void RecordNick(string oldNick, string newNick) =>
        History.AddAndExecute(new NickEntry(oldNick, newNick));

    public void RecordAvatarChange(string nick, string avatarName, string? url = null) =>
        History.AddAndExecute(new ChangeAvatarEntry(nick, avatarName, url));

    public void RecordBackDrop(string backdropName, string? url = null) =>
        History.AddAndExecute(new ChangeBackDropEntry(backdropName, url));

    public void RecordStart(string nick, string avatarName, string title) =>
        History.AddAndExecute(new StartHistoryEntry(nick, avatarName, title));

    // ---- IChatDocument: how history entries act on the comic -----------------

    /// <summary>
    /// Port of CChatDoc::ProcessLine (chatdoc.cpp:447).
    /// </summary>
    /// <remarks>
    /// The "cooked" branch is the interop rule: a pose that arrived on the wire is honoured
    /// verbatim, but a message with no pose — i.e. from a plain IRC client — gets one
    /// synthesised locally from its text. That is why text-only users still appear expressive.
    /// </remarks>
    public void ProcessLine(string nick, string text, in SayPose pose, HistoryMode mode)
    {
        var p = GetOrCreate(nick);
        var state = _directory.GetOrAdd(p.Id, _ => CreateBody(p.Id));

        state.TalkTos.Clear();
        foreach (var t in pose.TalkTos)
            if (ByNick(t) is { } target)
                state.TalkTos.Add(target.Id);

        if (pose.Cooked && pose.HasPose)
        {
            // The sender already decided; honour it verbatim.
            if ((p.Avatar.Flags & AvatarFlags.OtherMapped) == 0 && pose.ExpressionIndex >= 0)
            {
                // Raw art indices are meaningful on this exact character (histent.cpp:95).
                p.Resolver.UpdateBody(new ResolvedBody(pose.ExpressionIndex, pose.GestureIndex));
            }
            else
            {
                // Re-match by emotion so a pose authored for one character reads on another.
                var expr = new Emotion(pose.ExpressionIntensity / 10.0,
                                       Em.EmotionToFloat(pose.ExpressionEmotion));
                var gest = new Emotion(pose.GestureIntensity / 10.0,
                                       Em.EmotionToFloat(pose.GestureEmotion));

                var opts = new EmotionOpts();
                opts.Add(expr.EmotionValue, expr.Intensity, 10);
                opts.Add(gest.EmotionValue, gest.Intensity, 9);
                p.Resolver.UpdateBody(p.Resolver.GetBodyFromEmotion(opts));
            }
        }
        else if (_textPose.ChatPreSendText(text, p.Resolver) is { } inferred)
        {
            // Unfrozen: the expert system reads the words and poses the avatar.
            p.Resolver.UpdateBody(inferred);
        }
        // Otherwise the avatar is frozen and keeps the pose the user picked on the wheel —
        // ChatPreSendText declined to vote (textpose.cpp:125), so the body simply stands.

        var resolved = p.Resolver.CurrentBody;

        Page.BodyFactory = id =>
        {
            var body = new AvatarBody(id, _participants[id].Avatar, _participants[id].Resolver);
            body.Apply(id == p.Id ? resolved : _participants[id].Resolver.GetNeutralBody());
            return body;
        };

        Page.AddLine(p.Id, text, pose.Modes);
    }

    public void Join(string nick, string? fullName, HistoryMode mode)
    {
        var p = GetOrCreate(nick);
        p.Present = true;
    }

    /// <summary>
    /// A departing user is marked absent but kept.
    /// </summary>
    /// <remarks>
    /// Their panels are already in the strip and must survive a replay, so the participant and
    /// its art cannot be discarded — the original keeps the CUserInfo alive for the same reason
    /// (UF_DEPARTED, userinfo.h).
    /// </remarks>
    public void Part(string nick, HistoryMode mode)
    {
        if (ByNick(nick) is { } p) p.Present = false;
    }

    public void ChangeAvatar(string nick, string avatarName, string? avatarUrl, HistoryMode mode) =>
        GetOrCreate(nick, avatarName);

    public void ChangeBackDrop(string backdropName, string? backdropUrl, HistoryMode mode)
    {
        var id = BackDropIdByName?.Invoke(backdropName) ?? 0;
        Page.SetBackDrop(id);
    }

    public void ChangeNick(string oldNick, string newNick, HistoryMode mode)
    {
        if (!_nickToId.TryGetValue(oldNick, out var id)) return;
        _nickToId.Remove(oldNick);
        _nickToId[newNick] = id;
        _participants[id].Nick = newNick;
    }

    public void ShowInfo(string nick, string info, HistoryMode mode) { }

    public void ShowComicCharacter(string nick, HistoryMode mode) { }

    public void StartHistory(string nick, string avatarName, string title, HistoryMode mode)
    {
        Title = title;
        SetSelf(nick, string.IsNullOrEmpty(avatarName) ? null : avatarName);
    }
}
