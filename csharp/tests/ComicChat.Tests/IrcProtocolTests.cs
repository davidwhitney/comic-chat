using ComicChat.Irc;

namespace ComicChat.Tests;

public class IrcProtocolTests
{
    private static async Task Eventually(Func<bool> probe, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (probe()) return;
            await Task.Delay(5);
        }
        throw new TimeoutException("condition not met");
    }

    private static (IrcConnection, IrcProtocol) NewProtocol(FakeServer server, string nick = "me")
    {
        var conn = new IrcConnection();
        var proto = new IrcProtocol(conn) { MyNick = nick, MyUserName = "user" };
        conn.Attach(server.ClientStream);
        return (conn, proto);
    }

    private static readonly Annotations Pose = new()
    {
        GestureIndex = 3, GestureEmotion = 12, GestureIntensity = 9,
        ExpressionIndex = 0, ExpressionEmotion = 10, ExpressionIntensity = 5,
        Requested = true, Mode = SayMode.Say, TalkTos = [],
    };

    // ---- The two send strategies --------------------------------------------------------------

    [Fact]
    public async Task On_plain_IRC_the_annotation_is_prepended_into_the_privmsg()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server);
        await using var _ = conn;

        Assert.False(proto.IsIrcx);
        await proto.SayAsync("#room", "hello world", Pose);

        var lines = await server.ReadLinesAsync(1);

        // One line: annotation concatenated with the text inside the trailing parameter.
        Assert.Equal("PRIVMSG #room :(#G3<9E0:5RM1) hello world", lines[0]);
    }

    [Fact]
    public async Task On_IRCX_the_annotation_goes_out_of_band_and_the_text_stays_clean()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server);
        await using var _ = conn;

        await server.SendToClientAsync(":chloe1 800 * 0 0 NTLM,ANON 512 *");
        await Eventually(() => proto.IsIrcx);
        await server.ReadLinesAsync(1); // consume the "IRCX" switch

        await proto.SayAsync("#room", "hello world", Pose);

        var lines = await server.ReadLinesAsync(2);

        // The DATA form has NO parens and NO trailing space.
        Assert.Equal("DATA #room CCUDI1 :#G3<9E0:5RM1", lines[0]);
        Assert.Equal("PRIVMSG #room :hello world", lines[1]);
    }

    [Fact]
    public async Task An_over_budget_message_is_split_and_every_chunk_carries_the_annotation()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server);
        await using var _ = conn;

        var body = string.Join(' ', Enumerable.Range(0, 200).Select(i => $"word{i}"));
        await proto.SayAsync("#room", body, Pose);

        var lines = await server.ReadLinesAsync(2);

        Assert.All(lines, l =>
        {
            Assert.StartsWith("PRIVMSG #room :(#G3<9E0:5RM1) ", l);
            // Every chunk must fit the 512 line once the server prepends our mask.
            int prefix = MessageBudget.ReceivingPrefixLength("me", "user");
            Assert.True(l.Length + prefix <= 512, $"line of {l.Length} + prefix {prefix} overflows");
        });
    }

    [Fact]
    public async Task Whisper_forces_whisper_mode_on_the_outgoing_annotation()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server);
        await using var _ = conn;

        await proto.WhisperAsync("bob", "psst", Pose with { Mode = SayMode.Say });

        var lines = await server.ReadLinesAsync(1);
        Assert.Contains("M2", lines[0]); // SM_WHISPER
    }

    [Fact]
    public async Task Announcing_an_avatar_puts_the_verb_in_the_annotation_slot_with_no_body()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server);
        await using var _ = conn;

        proto.MyAvatarName = "ARMANDO";
        await proto.AnnounceAvatarAsync("#italia");

        var lines = await server.ReadLinesAsync(1);

        // Matches the real capture shape (ircorig.txt:2951).
        Assert.Equal("PRIVMSG #italia :# Appears as ARMANDO", lines[0]);
    }

    [Fact]
    public async Task Basic_commands_are_formatted_correctly()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server);
        await using var _ = conn;

        await proto.JoinAsync("#room");
        await proto.JoinAsync("#secret", "key123");
        await proto.PartAsync("#room");
        await proto.TopicAsync("#room", "the topic");
        await proto.KickAsync("#room", "bob", "spam");
        await proto.InviteAsync("bob", "#room");
        await proto.AwayAsync("brb");
        await proto.NamesAsync("#room");

        var lines = await server.ReadLinesAsync(8);

        Assert.Equal([
            "JOIN #room",
            "JOIN #secret key123",
            "PART #room",
            "TOPIC #room :the topic",
            "KICK #room bob :spam",
            "INVITE bob #room",
            "AWAY :brb",
            "NAMES #room",
        ], lines);
    }

    // ---- The receive path ---------------------------------------------------------------------

    [Fact]
    public async Task An_annotated_channel_message_arrives_cooked_with_clean_text()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server);
        await using var _ = conn;

        ComicMessage? got = null;
        proto.MessageReceived += (_, e) => got = e.Message;

        await server.SendToClientAsync(":nick!user@host PRIVMSG #room :(#G3<9E0:5RM1) hello world");
        await Eventually(() => got is not null);

        Assert.Equal("hello world", got!.Text);
        Assert.True(got.Cooked);
        Assert.Equal("nick", got.Sender.Name);
        Assert.Equal(SayMode.Say, got.Annotations.Mode);
        Assert.True(got.IsChannelMessage);
    }

    [Fact]
    public async Task A_plain_mirc_message_arrives_not_cooked()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server);
        await using var _ = conn;

        ComicMessage? got = null;
        proto.MessageReceived += (_, e) => got = e.Message;

        // A real line from the ircorig.txt capture.
        await server.SendToClientAsync(":Umano!-XXXX@208.163.252.20 PRIVMSG #italia :Cosa fa l'Inter");
        await Eventually(() => got is not null);

        Assert.Equal("Cosa fa l'Inter", got!.Text);
        Assert.False(got.Cooked); // the renderer must synthesise a gesture locally
    }

    [Fact]
    public async Task A_private_message_is_forced_to_whisper()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server, nick: "me");
        await using var _ = conn;

        ComicMessage? got = null;
        proto.MessageReceived += (_, e) => got = e.Message;

        // Claims Say (M1) but is addressed to us directly — anti-spoof must override.
        await server.SendToClientAsync(":nick!user@host PRIVMSG me :(#G115E223M1) psst");
        await Eventually(() => got is not null);

        Assert.Equal(SayMode.Whisper, got!.Annotations.Mode);
        Assert.False(got.IsChannelMessage);
    }

    [Fact]
    public async Task An_unannotated_private_message_is_also_a_whisper()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server, nick: "me");
        await using var _ = conn;

        ComicMessage? got = null;
        proto.MessageReceived += (_, e) => got = e.Message;

        await server.SendToClientAsync(":nick!user@host PRIVMSG me :hello");
        await Eventually(() => got is not null);

        Assert.Equal(SayMode.Whisper, got!.Annotations.Mode);
        Assert.False(got.Cooked);
    }

    // ---- The IRCX DATA latch ------------------------------------------------------------------

    [Fact]
    public async Task A_DATA_line_arms_the_latch_and_the_next_message_wears_the_pose()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server);
        await using var _ = conn;

        ComicMessage? got = null;
        proto.MessageReceived += (_, e) => got = e.Message;

        // Join first so the user is known in the room.
        await server.SendToClientAsync(":nick!user@host JOIN #room");

        await server.SendToClientAsync(":nick!user@host DATA #room CCUDI1 :#G3<9E0:5RM1");
        await Eventually(() => proto.FindRoom("#room")?.Find("nick")?.ValidUdi == true);

        await server.SendToClientAsync(":nick!user@host PRIVMSG #room :hello world");
        await Eventually(() => got is not null);

        Assert.Equal("hello world", got!.Text);
        Assert.True(got.Cooked);
        Assert.Equal(12, got.Annotations.GestureEmotion); // POINTSELF, from the DATA line
        Assert.True(got.Annotations.Requested);
    }

    [Fact]
    public async Task The_latch_is_one_shot_and_the_message_after_next_is_not_cooked()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server);
        await using var _ = conn;

        var messages = new List<ComicMessage>();
        proto.MessageReceived += (_, e) => { lock (messages) messages.Add(e.Message); };

        await server.SendToClientAsync(":nick!user@host JOIN #room");
        await server.SendToClientAsync(":nick!user@host DATA #room CCUDI1 :#G3<9E0:5RM1");
        await Eventually(() => proto.FindRoom("#room")?.Find("nick")?.ValidUdi == true);

        await server.SendToClientAsync(":nick!user@host PRIVMSG #room :first");
        await server.SendToClientAsync(":nick!user@host PRIVMSG #room :second");

        await Eventually(() => { lock (messages) return messages.Count >= 2; });

        lock (messages)
        {
            // The pose decays after exactly one message rather than leaking into later ones.
            Assert.True(messages[0].Cooked);
            Assert.False(messages[1].Cooked);
        }

        Assert.False(proto.FindRoom("#room")!.Find("nick")!.ValidUdi);
    }

    // ---- Verb handling ------------------------------------------------------------------------

    [Fact]
    public async Task A_channel_announcement_is_answered_privately_with_a_deferred_url()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server);
        await using var _ = conn;

        proto.MyAvatarName = "Jim";
        proto.MyAvatarUrl = "http://h/Jim.avb";

        await server.SendToClientAsync(":lupis!~anpagano@Lucca3-19.tin.it PRIVMSG #italia :# Appears as ARMANDO.");

        var lines = await server.ReadLinesAsync(1);

        // Private reply (to the nick, not the channel), and with "?" not the real URL.
        Assert.Equal("PRIVMSG lupis :# Appears as Jim.?", lines[0]);
    }

    [Fact]
    public async Task A_GetCharInfo_request_is_answered_with_the_real_url()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server);
        await using var _ = conn;

        proto.MyAvatarName = "Jim";
        proto.MyAvatarUrl = "http://h/Jim.avb";

        await server.SendToClientAsync(":lupis!~a@h PRIVMSG me :# GetCharInfo");

        var lines = await server.ReadLinesAsync(1);
        Assert.Equal("PRIVMSG lupis :# Appears as Jim.http://h/Jim.avb", lines[0]);
    }

    [Fact]
    public async Task A_verb_message_does_not_surface_as_a_comic_message()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server);
        await using var _ = conn;

        ComicMessage? got = null;
        ComicVerb? verb = null;
        proto.MessageReceived += (_, e) => got = e.Message;
        proto.VerbReceived += (_, e) => verb = e.Verb;

        await server.SendToClientAsync(":lupis!~a@h PRIVMSG #italia :# Appears as ARMANDO.");
        await Eventually(() => verb is not null);

        Assert.Null(got); // never rendered as speech
        Assert.Equal(ComicVerbKind.AppearsAs, verb!.Value.Kind);
        Assert.Equal("ARMANDO", verb.Value.Name);
    }

    // ---- Room / user model --------------------------------------------------------------------

    [Fact]
    public async Task Tracks_joins_parts_quits_and_nick_changes()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server);
        await using var _ = conn;

        await server.SendToClientAsync(":alice!a@h JOIN #room");
        await server.SendToClientAsync(":bob!b@h JOIN #room");
        await Eventually(() => proto.FindRoom("#room")?.Users.Count == 2);

        await server.SendToClientAsync(":alice!a@h NICK :alice2");
        await Eventually(() => proto.FindRoom("#room")?.Find("alice2") is not null);
        Assert.Null(proto.FindRoom("#room")!.Find("alice"));

        await server.SendToClientAsync(":bob!b@h PART #room");
        await Eventually(() => proto.FindRoom("#room")?.Users.Count == 1);

        await server.SendToClientAsync(":alice2!a@h QUIT :bye");
        await Eventually(() => proto.FindRoom("#room")?.Users.Count == 0);
    }

    [Fact]
    public async Task A_NAMES_reply_populates_the_room_with_status_flags()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server);
        await using var _ = conn;

        await server.SendToClientAsync(":srv 353 me = #room :@alice +bob carol >dave");
        await Eventually(() => proto.FindRoom("#room")?.Users.Count == 4);

        var room = proto.FindRoom("#room")!;
        Assert.True(room.Find("alice")!.IsOperator);
        Assert.True(room.Find("bob")!.HasVoice);
        Assert.True(room.Find("carol")!.IsSpeaker);
        Assert.True(room.Find("dave")!.IsSpectator);
    }

    [Fact]
    public async Task A_topic_numeric_updates_the_room()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server);
        await using var _ = conn;

        await server.SendToClientAsync(":srv 332 me #room :Welcome to the room");
        await Eventually(() => proto.FindRoom("#room")?.Topic is not null);

        Assert.Equal("Welcome to the room", proto.FindRoom("#room")!.Topic);
    }

    [Fact]
    public async Task RPL_WELCOME_adopts_the_servers_canonical_nick()
    {
        await using var server = new FakeServer();
        var (conn, proto) = NewProtocol(server, nick: "wanted");
        await using var _ = conn;

        await server.SendToClientAsync(":irc.stealth.net 001 djk87 :Welcome to the Internet Relay Network");
        await Eventually(() => proto.MyNick == "djk87");
    }
}
