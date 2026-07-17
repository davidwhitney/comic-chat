namespace ComicChat.Irc;

/// <summary>Port of the MT_* message-type flags (defines.h:164-171).</summary>
[Flags]
public enum MessageTypes
{
    None = 0,

    /// <summary>Destination is the whole channel.</summary>
    ChannelSend = 0x01,

    /// <summary>Destination is us specifically, or a subset of members.</summary>
    PrivateMsg = 0x02,

    Whisper = 0x04,
    PrivMsg = 0x08,
    Notice = 0x10,
    Data = 0x20,
    DataRequest = 0x40,
    DataReply = 0x80,
}

/// <summary>A fully decoded incoming utterance, ready to render.</summary>
public sealed class ComicMessage
{
    public required UserInfo Sender { get; init; }
    public required string Target { get; init; }

    /// <summary>The visible text, with any annotation already stripped.</summary>
    public required string Text { get; init; }

    public required Annotations Annotations { get; init; }
    public required MessageTypes MessageType { get; init; }

    /// <summary>
    /// False when the sender is not a Comic Chat client and sent no pose. The renderer must then
    /// synthesise a gesture from the text itself. See <see cref="Irc.Annotations.Cooked"/>.
    /// </summary>
    public bool Cooked => Annotations.Cooked;

    public BalloonModes Modes => Annotations.Modes;
    public bool IsChannelMessage => MessageType.HasFlag(MessageTypes.ChannelSend);
}

public sealed class ComicMessageEventArgs(ComicMessage message) : EventArgs
{
    public ComicMessage Message { get; } = message;
}

public sealed class ComicVerbEventArgs(ComicVerb verb, UserInfo sender, bool wasChannelSend) : EventArgs
{
    public ComicVerb Verb { get; } = verb;
    public UserInfo Sender { get; } = sender;
    public bool WasChannelSend { get; } = wasChannelSend;
}

/// <summary>
/// The high-level protocol: commands, numerics, the room/user model and the comic extension.
/// Port of CIrcProto (ircproto.cpp / ircproto.h).
/// </summary>
public sealed class IrcProtocol
{
    private readonly IrcConnection _connection;
    private readonly Dictionary<string, RoomInfo> _rooms = new(StringComparer.OrdinalIgnoreCase);

    public IrcProtocol(IrcConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _connection.MessageReceived += OnMessageReceived;
    }

    public IrcConnection Connection => _connection;

    public IReadOnlyCollection<RoomInfo> Rooms => _rooms.Values;

    /// <summary>Our own nickname, needed for the receiving-prefix budget.</summary>
    public string MyNick { get; set; } = string.Empty;

    public string MyUserName { get; set; } = "user";

    /// <summary>Our avatar's character name, announced via "# Appears as".</summary>
    public string? MyAvatarName { get; set; }

    /// <summary>Our avatar's URL. Never sent unsolicited to a channel — see the deferred handshake.</summary>
    public string? MyAvatarUrl { get; set; }

    public string? MyProfile { get; set; }

    /// <summary>
    /// Our real mask length once ident is known; 0 means "unknown", which triggers the 32-byte
    /// fallback (ircproto.cpp:531).
    /// </summary>
    public int MyIdentLength { get; set; }

    /// <summary>True when the peer is an IRCX server. Port of CIrcProto::IsIRCX (ircproto.h:63).</summary>
    public bool IsIrcx => _connection.IsIrcxServer;

    public event EventHandler<ComicMessageEventArgs>? MessageReceived;
    public event EventHandler<ComicVerbEventArgs>? VerbReceived;
    public event EventHandler<IrcMessageEventArgs>? NumericReceived;

    public RoomInfo GetOrAddRoom(string name)
    {
        if (_rooms.TryGetValue(name, out var room)) return room;
        room = new RoomInfo(name);
        _rooms[name] = room;
        return room;
    }

    public RoomInfo? FindRoom(string name) => _rooms.GetValueOrDefault(name);

    // ---- Commands -----------------------------------------------------------------------------

    public Task JoinAsync(string channel, string? key = null, CancellationToken ct = default) =>
        _connection.SendAsync(key is null ? $"JOIN {channel}" : $"JOIN {channel} {key}", ct);

    public Task PartAsync(string channel, CancellationToken ct = default) =>
        _connection.SendAsync($"PART {channel}", ct);

    public Task TopicAsync(string channel, string topic, CancellationToken ct = default) =>
        _connection.SendAsync($"TOPIC {channel} :{topic}", ct);

    public Task ModeAsync(string target, string modes, CancellationToken ct = default) =>
        _connection.SendAsync($"MODE {target} {modes}", ct);

    public Task KickAsync(string channel, string nick, string? reason = null, CancellationToken ct = default) =>
        _connection.SendAsync(reason is null ? $"KICK {channel} {nick}" : $"KICK {channel} {nick} :{reason}", ct);

    public Task InviteAsync(string nick, string channel, CancellationToken ct = default) =>
        _connection.SendAsync($"INVITE {nick} {channel}", ct);

    public Task AwayAsync(string? message, CancellationToken ct = default) =>
        _connection.SendAsync(string.IsNullOrEmpty(message) ? "AWAY" : $"AWAY :{message}", ct);

    public Task NickAsync(string nick, CancellationToken ct = default)
    {
        MyNick = nick;
        return _connection.SendAsync($"NICK {nick}", ct);
    }

    public Task WhoAsync(string mask, CancellationToken ct = default) =>
        _connection.SendAsync($"WHO {mask}", ct);

    public Task WhoisAsync(string nick, CancellationToken ct = default) =>
        _connection.SendAsync($"WHOIS {nick}", ct);

    public Task ListAsync(string? mask = null, CancellationToken ct = default) =>
        _connection.SendAsync(mask is null ? "LIST" : $"LIST {mask}", ct);

    public Task NamesAsync(string channel, CancellationToken ct = default) =>
        _connection.SendAsync($"NAMES {channel}", ct);

    public Task NoticeAsync(string target, string text, CancellationToken ct = default) =>
        _connection.SendAsync($"NOTICE {target} :{text}", ct);

    // ---- The comic send path ------------------------------------------------------------------

    /// <summary>Say something to a channel with a pose.</summary>
    public Task SayAsync(string target, string text, Annotations annotations, CancellationToken ct = default) =>
        SendToTargetAsync(target, annotations.Encode(!IsIrcx), text, ct: ct);

    /// <summary>Whisper privately. The receiver forces Whisper mode regardless; see the anti-spoof note.</summary>
    public Task WhisperAsync(string nick, string text, Annotations annotations, CancellationToken ct = default) =>
        SendToTargetAsync(nick, (annotations with { Mode = SayMode.Whisper }).Encode(!IsIrcx), text, ct: ct);

    /// <summary>Send a CTCP ACTION. Real CTCP, unlike the '#' verbs.</summary>
    public Task ActionAsync(string target, string text, CancellationToken ct = default) =>
        SendToTargetAsync(target, string.Empty, $"\x01ACTION {text}\x01", ct: ct);

    /// <summary>
    /// Announce our avatar identity. Port of ChatAnnounceNewAvatar (protsupp.cpp:820).
    /// </summary>
    /// <param name="target">Channel to announce to, or a nick for a private reply.</param>
    /// <param name="url">
    /// The URL to advertise. Callers replying to a channel announcement should pass
    /// <see cref="ComicVerbs.DeferredUrlString"/> rather than the real URL.
    /// </param>
    /// <remarks>
    /// The body goes in the ANNOTATIONS slot with a NULL message, which is why the resulting line is
    /// "PRIVMSG &lt;target&gt; :# Appears as NAME" and not two concatenated fields. Verified against
    /// the real capture ircorig.txt:2951.
    /// </remarks>
    public Task AnnounceAvatarAsync(string target, string? avatarName = null, string? url = null, CancellationToken ct = default)
    {
        var body = ComicVerbs.FormatAppearsAs(avatarName ?? MyAvatarName, url);
        return SendToTargetAsync(target, body, string.Empty, ct: ct);
    }

    /// <summary>
    /// Tell the room the backdrop changed. Port of ChatAnnounceNewBackDrop (protsupp.cpp:3438).
    /// </summary>
    /// <remarks>
    /// Like the avatar announcement, only a name and a URL travel — the image itself is fetched
    /// out of band over HTTP, never sent over IRC.
    /// </remarks>
    public Task AnnounceBackdropAsync(string target, string backdropName, string? url = null,
                                      CancellationToken ct = default)
    {
        var body = ComicVerbs.FormatBackdrop(backdropName, url);
        return SendToTargetAsync(target, body, string.Empty, ct: ct);
    }

    public Task RequestCharInfoAsync(string nick, CancellationToken ct = default) =>
        SendToTargetAsync(nick, ComicVerbs.FormatGetCharInfo(), string.Empty, ct: ct);

    public Task RequestInfoAsync(string nick, CancellationToken ct = default) =>
        SendToTargetAsync(nick, ComicVerbs.FormatGetInfo(), string.Empty, ct: ct);

    /// <summary>
    /// The core send. Port of CIrcProto::bChatSendToTarget (ircproto.cpp:480).
    /// </summary>
    /// <remarks>
    /// TWO STRATEGIES, chosen by server type:
    ///
    /// IRCX — the annotation goes out-of-band in its own DATA command, then the text follows clean:
    ///   <c>DATA &lt;target&gt; CCUDI1 :&lt;annotations&gt;</c> then <c>PRIVMSG &lt;target&gt; :&lt;text&gt;</c>.
    ///   Non-Comic-Chat clients on an IRCX server never see the DATA line at all, so the text is
    ///   clean for them AND for us.
    ///
    /// Plain IRC — there is no out-of-band channel, so the annotation is PREPENDED into the PRIVMSG
    ///   trailing parameter and concatenated with the text:
    ///   <c>PRIVMSG &lt;target&gt; :(#G3&lt;9E0:5RM1) hello</c>.
    ///   A plain mIRC user therefore sees the raw "(#...) " gunk — the price of interop, and the
    ///   reason the annotation is kept as short as it is.
    ///
    /// The caller supplies the annotation string already encoded in the correct form, since only it
    /// knows whether to include the parens (Encode(!IsIrcx)).
    /// </remarks>
    public async Task SendToTargetAsync(
        string target,
        string annotations,
        string message,
        bool asNotice = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        annotations ??= string.Empty;
        message ??= string.Empty;

        var verb = asNotice ? "NOTICE" : "PRIVMSG";
        int prefixLen = MessageBudget.ReceivingPrefixLength(MyNick, MyUserName, MyIdentLength);
        int maxLen = _connection.MaxMessageLength;

        if (MessageBudget.Fits(target, annotations, message, prefixLen, maxLen))
        {
            if (annotations.Length > 0 && IsIrcx)
            {
                await _connection.SendAsync($"DATA {target} {ComicVerbs.Ccudi1} :{annotations}", ct)
                    .ConfigureAwait(false);

                if (message.Length > 0)
                    await _connection.SendAsync($"{verb} {target} :{message}", ct).ConfigureAwait(false);
            }
            else
            {
                await _connection.SendAsync($"{verb} {target} :{annotations}{message}", ct).ConfigureAwait(false);
            }

            return;
        }

        // Over budget: split at word boundaries and repeat the annotation on every chunk.
        int budget = MessageBudget.BodyBudget(target, annotations, prefixLen, maxLen);
        if (budget <= 0)
            throw new InvalidOperationException(
                $"Target '{target}' and annotations leave no room for a message body.");

        foreach (var chunk in MessageBudget.Split(message, budget))
        {
            if (annotations.Length > 0 && IsIrcx)
            {
                await _connection.SendAsync($"DATA {target} {ComicVerbs.Ccudi1} :{annotations}", ct)
                    .ConfigureAwait(false);
                await _connection.SendAsync($"{verb} {target} :{chunk}", ct).ConfigureAwait(false);
            }
            else
            {
                await _connection.SendAsync($"{verb} {target} :{annotations}{chunk}", ct).ConfigureAwait(false);
            }
        }
    }

    // ---- The receive path ---------------------------------------------------------------------

    private void OnMessageReceived(object? sender, IrcMessageEventArgs e)
    {
        var msg = e.Message;

        if (msg.Numeric != 0)
        {
            HandleNumeric(msg);
            NumericReceived?.Invoke(this, e);
            return;
        }

        switch (msg.Command.ToUpperInvariant())
        {
            case "PRIVMSG":
            case "NOTICE":
                HandlePrivmsg(msg, msg.Command.Equals("NOTICE", StringComparison.OrdinalIgnoreCase));
                break;

            case "DATA":
                HandleData(msg);
                break;

            case "JOIN": HandleJoin(msg); break;
            case "PART": HandlePart(msg); break;
            case "QUIT": HandleQuit(msg); break;
            case "NICK": HandleNick(msg); break;
            case "TOPIC": HandleTopic(msg); break;
            case "KICK": HandleKick(msg); break;
        }
    }

    /// <summary>
    /// Handle the IRCX out-of-band annotation. Port of the DATA dispatch (protsupp.cpp:4394) into
    /// ProcessUDIData (protsupp.cpp:1485).
    /// </summary>
    private void HandleData(IrcMessage msg)
    {
        // DATA <target> CCUDI1 :<annotations>
        if (msg.Nick is null || msg.Trailing is null) return;
        if (!string.Equals(msg.Arg(1), ComicVerbs.Ccudi1, StringComparison.OrdinalIgnoreCase)) return;

        var target = msg.Arg(0) ?? string.Empty;
        var user = ResolveUser(target, msg.Nick, msg.Prefix);

        var annotations = Annotations.DecodeData(msg.Trailing);

        user.Udi.Apply(annotations);

        // Arm the latch: the NEXT text message from this user wears this pose.
        user.ValidUdi = true;
    }

    private void HandlePrivmsg(IrcMessage msg, bool asNotice)
    {
        if (msg.Nick is null || msg.Trailing is null) return;

        var target = msg.Arg(0) ?? string.Empty;
        var body = msg.Trailing;

        bool isChannel = target.Length > 0 && IrcMessage.IsChannelPrefix(target[0]);

        var messageType = asNotice ? MessageTypes.Notice : MessageTypes.PrivMsg;
        messageType |= isChannel ? MessageTypes.ChannelSend : MessageTypes.PrivateMsg;

        var user = ResolveUser(target, msg.Nick, msg.Prefix);

        // '#' verbs are handled before ProcessSay in the original's dispatch and never render.
        if (ComicVerbs.IsComicVerb(body))
        {
            HandleComicVerb(body, user, isChannel);
            return;
        }

        var comic = ProcessSay(user, target, body, messageType);
        MessageReceived?.Invoke(this, new ComicMessageEventArgs(comic));
    }

    /// <summary>
    /// Decode one text message into a renderable utterance. Port of ProcessSay (protsupp.cpp:1545).
    /// </summary>
    /// <remarks>
    /// Three cases, in the original's order:
    ///  1. An inline "(#...) " annotation — parse it, strip it, mark cooked.
    ///  2. No inline annotation but the DATA latch is armed — keep the parked pose (do NOT reset).
    ///  3. No annotation and no latch — reset to a neutral, NOT-cooked pose so the renderer will
    ///     synthesise a gesture from the text. This is the plain-mIRC-user path.
    /// The latch is cleared unconditionally afterwards (protsupp.cpp:1626).
    /// </remarks>
    internal ComicMessage ProcessSay(UserInfo user, string target, string body, MessageTypes messageType)
    {
        bool isPrivate = messageType.HasFlag(MessageTypes.PrivateMsg);
        string text = body;

        if (Annotations.TryDecodeInline(body, isPrivate, out var annotations, out var stripped))
        {
            text = stripped;
            user.Udi.Apply(annotations);
        }
        else if (user.ValidUdi)
        {
            // Case 2: wear the pose that arrived out of band on the preceding DATA line.
            annotations = user.Udi.Annotations;
        }
        else
        {
            // Case 3: not cooked.
            user.Udi.Reset();
            annotations = user.Udi.Annotations;

            // The anti-spoof forcing also applies to the un-annotated private case
            // (protsupp.cpp:1618-1623), so a bare private message still renders as a whisper.
            if (isPrivate)
            {
                annotations = annotations with { Mode = SayMode.Whisper };
                user.Udi.Apply(annotations);
            }
        }

        user.ValidUdi = false;

        return new ComicMessage
        {
            Sender = user,
            Target = target,
            Text = text,
            Annotations = annotations,
            MessageType = messageType,
        };
    }

    /// <summary>Dispatch a '#' verb and send whatever reply it owes. Port of ProcessComment (protsupp.cpp:846).</summary>
    private void HandleComicVerb(string body, UserInfo user, bool wasChannelSend)
    {
        var verb = ComicVerbs.Parse(body);
        if (verb.Kind == ComicVerbKind.None) return;

        switch (verb.Kind)
        {
            case ComicVerbKind.AppearsAs:
            {
                var reply = ComicVerbs.OnAppearsAs(verb, user, wasChannelSend, MyAvatarName, MyAvatarUrl);

                // Replies always go PRIVATELY, even to a channel announcement — a channel reply would
                // make every member's introduction visible to every other member.
                if (reply is not null)
                    _ = SendToTargetAsync(user.Name, reply, string.Empty);
                break;
            }

            case ComicVerbKind.GetCharInfo:
            {
                var reply = ComicVerbs.OnGetCharInfo(user, MyAvatarName, MyAvatarUrl);
                if (reply is not null) _ = SendToTargetAsync(user.Name, reply, string.Empty);
                break;
            }

            case ComicVerbKind.GetInfo:
            {
                var reply = ComicVerbs.OnGetInfo(user, MyProfile);
                if (reply is not null) _ = SendToTargetAsync(user.Name, reply, string.Empty);
                break;
            }
        }

        VerbReceived?.Invoke(this, new ComicVerbEventArgs(verb, user, wasChannelSend));
    }

    /// <summary>
    /// Find the UserInfo for a sender. Private messages have no channel context, so fall back to
    /// searching every known room before creating a detached record.
    /// </summary>
    private UserInfo ResolveUser(string target, string nick, string? prefix)
    {
        if (target.Length > 0 && IrcMessage.IsChannelPrefix(target[0]))
            return GetOrAddRoom(target).GetOrAdd(nick, prefix);

        foreach (var room in _rooms.Values)
        {
            var existing = room.Find(nick);
            if (existing is not null)
            {
                if (prefix is not null) existing.FullName = prefix;
                return existing;
            }
        }

        return new UserInfo(nick, prefix) { Flags = UserFlags.External };
    }

    private void HandleJoin(IrcMessage msg)
    {
        if (msg.Nick is null) return;
        var channel = msg.Trailing ?? msg.Arg(0);
        if (channel is null) return;

        GetOrAddRoom(channel).GetOrAdd(msg.Nick, msg.Prefix);
    }

    private void HandlePart(IrcMessage msg)
    {
        if (msg.Nick is null) return;
        var channel = msg.Arg(0);
        if (channel is null) return;

        FindRoom(channel)?.Remove(msg.Nick);
    }

    private void HandleQuit(IrcMessage msg)
    {
        if (msg.Nick is null) return;
        foreach (var room in _rooms.Values) room.Remove(msg.Nick);
    }

    private void HandleNick(IrcMessage msg)
    {
        if (msg.Nick is null) return;
        var newNick = msg.Trailing ?? msg.Arg(0);
        if (newNick is null) return;

        foreach (var room in _rooms.Values) room.Rename(msg.Nick, newNick);
        if (string.Equals(msg.Nick, MyNick, StringComparison.OrdinalIgnoreCase)) MyNick = newNick;
    }

    private void HandleTopic(IrcMessage msg)
    {
        var channel = msg.Arg(0);
        if (channel is null) return;
        GetOrAddRoom(channel).Topic = msg.Trailing;
    }

    private void HandleKick(IrcMessage msg)
    {
        var channel = msg.Arg(0);
        var nick = msg.Arg(1);
        if (channel is null || nick is null) return;
        FindRoom(channel)?.Remove(nick);
    }

    private void HandleNumeric(IrcMessage msg)
    {
        switch (msg.Numeric)
        {
            case 1: // RPL_WELCOME — the server tells us our canonical nick.
                if (msg.Arg(0) is { } nick) MyNick = nick;
                break;

            case 332: // RPL_TOPIC: <me> <channel> :<topic>
                if (msg.Arg(1) is { } topicChan) GetOrAddRoom(topicChan).Topic = msg.Trailing;
                break;

            case 353: // RPL_NAMREPLY: <me> <=|*|@> <channel> :<nicks>
            {
                var chan = msg.Arg(2);
                if (chan is null || msg.Trailing is null) break;

                var room = GetOrAddRoom(chan);
                foreach (var entry in msg.Trailing.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    room.AddFromNames(entry);
                break;
            }
        }
    }
}
