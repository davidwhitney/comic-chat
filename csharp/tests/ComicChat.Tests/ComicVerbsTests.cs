using ComicChat.Irc;

namespace ComicChat.Tests;

public class ComicVerbsTests
{
    // ---- Parsing "# Appears as" ---------------------------------------------------------------

    [Fact]
    public void Parses_the_real_capture_with_no_url()
    {
        // ircorig.txt:2951 — ":lupis!~anpagano@... PRIVMSG #italia :# Appears as ARMANDO."
        var v = ComicVerbs.Parse("# Appears as ARMANDO.");

        Assert.Equal(ComicVerbKind.AppearsAs, v.Kind);
        Assert.Equal("ARMANDO", v.Name);
        Assert.Null(v.Url); // the trailing '.' is the name/URL delimiter, not part of a URL
    }

    [Fact]
    public void Parses_a_name_and_url()
    {
        var v = ComicVerbs.Parse("# Appears as Jim.http://h/Jim.avb");

        Assert.Equal(ComicVerbKind.AppearsAs, v.Kind);
        Assert.Equal("Jim", v.Name);

        // The FIRST dot splits; the URL then keeps every later dot, because GetToken2's end
        // separators for the URL are only ",)" (protsupp.cpp:862).
        Assert.Equal("http://h/Jim.avb", v.Url);
    }

    [Fact]
    public void Parses_a_bare_name_with_no_trailing_dot()
    {
        var v = ComicVerbs.Parse("# Appears as ARMANDO");
        Assert.Equal("ARMANDO", v.Name);
        Assert.Null(v.Url);
    }

    [Fact]
    public void Parses_the_deferred_url_placeholder()
    {
        var v = ComicVerbs.Parse("# Appears as Jim.?");
        Assert.Equal("Jim", v.Name);
        Assert.Equal(ComicVerbs.DeferredUrlString, v.Url);
    }

    [Fact]
    public void A_name_terminates_at_a_comma_or_paren()
    {
        // Names cannot contain '.', ',' or ')' — GetToken's default separator set.
        Assert.Equal("Jim", ComicVerbs.Parse("# Appears as Jim,x").Name);
        Assert.Equal("Jim", ComicVerbs.Parse("# Appears as Jim)x").Name);
    }

    // ---- Formatting "# Appears as" ------------------------------------------------------------

    [Fact]
    public void Formats_a_name_with_no_url()
    {
        Assert.Equal("# Appears as ARMANDO", ComicVerbs.FormatAppearsAs("ARMANDO", null));
    }

    [Fact]
    public void Formats_a_name_with_a_url_after_a_dot()
    {
        Assert.Equal("# Appears as Jim.http://h/Jim.avb",
            ComicVerbs.FormatAppearsAs("Jim", "http://h/Jim.avb"));
    }

    [Fact]
    public void An_empty_name_becomes_NONE()
    {
        Assert.Equal("# Appears as NONE", ComicVerbs.FormatAppearsAs("", null));
        Assert.Equal("# Appears as NONE", ComicVerbs.FormatAppearsAs(null, null));
    }

    [Fact]
    public void Appears_as_round_trips()
    {
        foreach (var (name, url) in new (string, string?)[]
        {
            ("ARMANDO", null),
            ("Jim", "http://h/Jim.avb"),
            ("Anna", ComicVerbs.DeferredUrlString),
        })
        {
            var v = ComicVerbs.Parse(ComicVerbs.FormatAppearsAs(name, url));
            Assert.Equal(name, v.Name);
            Assert.Equal(url, v.Url);
        }
    }

    // ---- The other verbs ----------------------------------------------------------------------

    [Fact]
    public void Parses_GetInfo()
    {
        Assert.Equal(ComicVerbKind.GetInfo, ComicVerbs.Parse("# GetInfo").Kind);
        Assert.Equal("# GetInfo", ComicVerbs.FormatGetInfo());
    }

    [Fact]
    public void Parses_GetCharInfo_and_does_not_confuse_it_with_GetInfo()
    {
        Assert.Equal(ComicVerbKind.GetCharInfo, ComicVerbs.Parse("# GetCharInfo").Kind);
        Assert.Equal("# GetCharInfo", ComicVerbs.FormatGetCharInfo());
    }

    [Fact]
    public void Parses_HeresInfo_with_its_profile()
    {
        var v = ComicVerbs.Parse("# HeresInfo: I like comics");
        Assert.Equal(ComicVerbKind.HeresInfo, v.Kind);
        Assert.Equal("I like comics", v.Name);
        Assert.Equal("# HeresInfo: I like comics", ComicVerbs.FormatHeresInfo("I like comics"));
    }

    [Fact]
    public void Parses_BDrop2_with_name_and_url()
    {
        var v = ComicVerbs.Parse("# BDrop2: City,http://h/city.avb");
        Assert.Equal(ComicVerbKind.BackdropDrop2, v.Kind);
        Assert.Equal("City", v.Name);
        Assert.Equal("http://h/city.avb", v.Url);
    }

    [Fact]
    public void Parses_legacy_BDrop()
    {
        var v = ComicVerbs.Parse("# BDrop: City");
        Assert.Equal(ComicVerbKind.BackdropDrop, v.Kind);
        Assert.Equal("City", v.Name);
    }

    [Fact]
    public void Formats_BDrop2()
    {
        Assert.Equal("# BDrop2: City,http://h/city.avb", ComicVerbs.FormatBackdrop("City", "http://h/city.avb"));
    }

    [Fact]
    public void Ordinary_text_is_not_a_verb()
    {
        Assert.False(ComicVerbs.IsComicVerb("hello world"));
        Assert.Equal(ComicVerbKind.None, ComicVerbs.Parse("hello world").Kind);
    }

    [Fact]
    public void An_unknown_hash_verb_is_None()
    {
        Assert.True(ComicVerbs.IsComicVerb("# Nonsense"));
        Assert.Equal(ComicVerbKind.None, ComicVerbs.Parse("# Nonsense").Kind);
    }

    // ---- The deferred-URL handshake -----------------------------------------------------------

    [Fact]
    public void A_channel_announcement_from_a_new_peer_is_answered_with_a_deferred_url()
    {
        var peer = new UserInfo("lupis");
        var verb = ComicVerbs.Parse("# Appears as ARMANDO.");

        var reply = ComicVerbs.OnAppearsAs(verb, peer, wasChannelSend: true,
            myAvatarName: "Jim", myAvatarUrl: "http://h/Jim.avb");

        // We send "?" instead of our real URL to avoid N^2 URL spam on a join storm.
        Assert.Equal("# Appears as Jim.?", reply);
        Assert.DoesNotContain("http://h/Jim.avb", reply);

        Assert.True(peer.IsComicUser);
        Assert.Equal("ARMANDO", peer.AvatarRealName);
    }

    [Fact]
    public void We_reply_with_a_bare_name_when_we_have_no_url_at_all()
    {
        // So "?" unambiguously means "deferred", never "absent".
        var peer = new UserInfo("lupis");
        var reply = ComicVerbs.OnAppearsAs(ComicVerbs.Parse("# Appears as ARMANDO."), peer,
            wasChannelSend: true, myAvatarName: "Jim", myAvatarUrl: null);

        Assert.Equal("# Appears as Jim", reply);
    }

    [Fact]
    public void A_private_announcement_provokes_no_reply()
    {
        // Only a CHANNEL send needs an introduction back; a private one is already a reply.
        var peer = new UserInfo("lupis");
        var reply = ComicVerbs.OnAppearsAs(ComicVerbs.Parse("# Appears as ARMANDO."), peer,
            wasChannelSend: false, myAvatarName: "Jim", myAvatarUrl: "http://h/Jim.avb");

        Assert.Null(reply);
        Assert.True(peer.IsComicUser);
        Assert.Equal("ARMANDO", peer.AvatarRealName);
    }

    [Fact]
    public void An_already_known_peer_provokes_no_reply()
    {
        // The IsComicUser gate is what stops two clients ping-ponging announcements forever.
        var peer = new UserInfo("lupis") { IsComicUser = true };

        var reply = ComicVerbs.OnAppearsAs(ComicVerbs.Parse("# Appears as ARMANDO."), peer,
            wasChannelSend: true, myAvatarName: "Jim", myAvatarUrl: "http://h/Jim.avb");

        Assert.Null(reply);
    }

    [Fact]
    public void An_ignored_peer_provokes_no_reply()
    {
        var peer = new UserInfo("lupis") { Ignored = true };
        Assert.Null(ComicVerbs.OnAppearsAs(ComicVerbs.Parse("# Appears as ARMANDO."), peer,
            wasChannelSend: true, myAvatarName: "Jim", myAvatarUrl: "http://h/Jim.avb"));
    }

    [Fact]
    public void GetCharInfo_is_answered_with_the_REAL_url()
    {
        // The second leg of the handshake: the peer that actually wants the art asks for it.
        var peer = new UserInfo("lupis");
        var reply = ComicVerbs.OnGetCharInfo(peer, "Jim", "http://h/Jim.avb");

        Assert.Equal("# Appears as Jim.http://h/Jim.avb", reply);
    }

    [Fact]
    public void The_full_handshake_ends_with_the_real_url()
    {
        // 1. Peer announces to the channel.
        var peer = new UserInfo("lupis");
        var announce = ComicVerbs.Parse("# Appears as ARMANDO.");

        // 2. We reply privately with a DEFERRED url.
        var deferred = ComicVerbs.OnAppearsAs(announce, peer, true, "Jim", "http://h/Jim.avb");
        Assert.Equal("# Appears as Jim.?", deferred);

        // 3. The peer sees the "?", decides it wants the art, and asks.
        var parsedDeferred = ComicVerbs.Parse(deferred!);
        Assert.Equal(ComicVerbs.DeferredUrlString, parsedDeferred.Url);

        // 4. We answer with the real thing.
        var real = ComicVerbs.OnGetCharInfo(peer, "Jim", "http://h/Jim.avb");
        Assert.Equal("http://h/Jim.avb", ComicVerbs.Parse(real!).Url);
    }

    [Fact]
    public void A_recorded_deferred_url_is_flagged_on_the_user()
    {
        var peer = new UserInfo("lupis");
        ComicVerbs.OnAppearsAs(ComicVerbs.Parse("# Appears as Jim.?"), peer, false, "Me", null);

        Assert.True(peer.HasDeferredUrl);
    }

    [Fact]
    public void GetInfo_is_answered_with_our_profile()
    {
        var peer = new UserInfo("lupis");
        Assert.Equal("# HeresInfo: I like comics", ComicVerbs.OnGetInfo(peer, "I like comics"));
    }

    [Fact]
    public void GetInfo_falls_back_to_a_default_profile()
    {
        var peer = new UserInfo("lupis");
        Assert.Equal("# HeresInfo: No profile", ComicVerbs.OnGetInfo(peer, null));
    }
}
