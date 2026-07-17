using ComicChat.Irc;

namespace ComicChat.Tests;

public class IrcMessageTests
{
    [Fact]
    public void Parses_a_full_prefixed_privmsg()
    {
        var m = IrcMessage.Parse(":nick!user@host PRIVMSG #room :hello world");

        Assert.True(m.HasPrefix);
        Assert.Equal("nick!user@host", m.Prefix);
        Assert.Equal("nick", m.Nick);
        Assert.Equal("user", m.User);
        Assert.Equal("host", m.Host);
        Assert.Equal("PRIVMSG", m.Command);
        Assert.Equal(["#room"], m.Parameters);
        Assert.Equal("hello world", m.Trailing);
    }

    [Fact]
    public void Parses_a_real_capture_line()
    {
        // ircorig.txt:2951
        var m = IrcMessage.Parse(":lupis!~anpagano@Lucca3-19.tin.it PRIVMSG #italia :# Appears as ARMANDO.");

        Assert.Equal("lupis", m.Nick);
        Assert.Equal("~anpagano", m.User);
        Assert.Equal("Lucca3-19.tin.it", m.Host);
        Assert.Equal("#italia", m.Arg(0));
        Assert.Equal("# Appears as ARMANDO.", m.Trailing);
    }

    [Fact]
    public void A_bare_server_name_prefix_becomes_the_whole_nick()
    {
        var m = IrcMessage.Parse(":irc.stealth.net 001 djk87 :Welcome to the Internet Relay Network");

        Assert.Equal("irc.stealth.net", m.Nick);
        Assert.Null(m.User);
        Assert.Null(m.Host);
        Assert.Equal(1, m.Numeric);
        Assert.Equal("djk87", m.Arg(0));
    }

    [Fact]
    public void Parses_a_line_with_no_prefix()
    {
        var m = IrcMessage.Parse("PING :12345");

        Assert.False(m.HasPrefix);
        Assert.Null(m.Prefix);
        Assert.Equal("PING", m.Command);
        Assert.Equal("12345", m.Trailing);
    }

    [Fact]
    public void Parses_a_line_with_no_trailing()
    {
        var m = IrcMessage.Parse(":nick!u@h JOIN #room");

        Assert.Equal("JOIN", m.Command);
        Assert.Equal(["#room"], m.Parameters);
        Assert.Null(m.Trailing);
    }

    [Fact]
    public void Trailing_keeps_its_spaces_and_any_further_colons()
    {
        var m = IrcMessage.Parse(":n!u@h PRIVMSG #c :look: a colon : and  double  spaces");
        Assert.Equal("look: a colon : and  double  spaces", m.Trailing);
    }

    [Fact]
    public void An_empty_trailing_is_distinct_from_no_trailing()
    {
        Assert.Equal(string.Empty, IrcMessage.Parse("PING :").Trailing);
        Assert.Null(IrcMessage.Parse("PING").Trailing);
    }

    [Fact]
    public void Strips_trailing_crlf()
    {
        var m = IrcMessage.Parse(":n!u@h PRIVMSG #c :hi\r\n");
        Assert.Equal("hi", m.Trailing);
    }

    [Fact]
    public void Handles_multiple_middle_parameters()
    {
        var m = IrcMessage.Parse(":srv 353 me = #chan :@alice +bob carol");

        Assert.Equal(353, m.Numeric);
        Assert.Equal(["me", "=", "#chan"], m.Parameters);
        Assert.Equal("@alice +bob carol", m.Trailing);
    }

    [Fact]
    public void Parses_the_ircx_800_reply_from_the_real_capture()
    {
        // irc.txt — ":chloe1 800 * 0 0 NTLM,ANON 512 *"
        var m = IrcMessage.Parse(":chloe1 800 * 0 0 NTLM,ANON 512 *");

        Assert.Equal(800, m.Numeric);
        Assert.Equal("chloe1", m.Nick);

        // The original indexes args the way it counts them: args[0] is the numeric itself.
        var args = m.Args;
        Assert.Equal(7, args.Count);
        Assert.Equal("800", args[0]);
        Assert.Equal("*", args[1]);
        Assert.Equal("0", args[2]);   // state: still in IRC mode
        Assert.Equal("NTLM,ANON", args[4]);

        // Max message length is args[nArgs-2], indexed from the END (ircsock.cpp:2874).
        Assert.Equal("512", args[^2]);
    }

    [Fact]
    public void Numeric_is_zero_for_a_verb()
    {
        Assert.Equal(0, IrcMessage.Parse("PING :x").Numeric);
        Assert.Equal(0, IrcMessage.Parse(":n!u@h PRIVMSG #c :hi").Numeric);
    }

    [Fact]
    public void Arg_tolerates_an_out_of_range_index()
    {
        var m = IrcMessage.Parse("PING :x");
        Assert.Null(m.Arg(0));
        Assert.Null(m.Arg(99));
        Assert.Null(m.Arg(-1));
    }

    [Fact]
    public void A_prefix_with_only_a_host_still_parses()
    {
        var m = IrcMessage.Parse(":nick@host PRIVMSG #c :hi");
        Assert.Equal("nick", m.Nick);
        Assert.Equal("host", m.Host);
        Assert.Null(m.User);
    }

    [Theory]
    [InlineData('#', true)]
    [InlineData('%', true)]
    [InlineData('&', true)]
    [InlineData('!', false)]
    [InlineData('a', false)]
    public void Recognises_channel_prefixes(char c, bool expected)
    {
        Assert.Equal(expected, IrcMessage.IsChannelPrefix(c));
    }

    // ---- Formatting ---------------------------------------------------------------------------

    [Fact]
    public void Formats_a_trailing_with_its_colon()
    {
        var m = IrcMessage.Create("PRIVMSG", ["#room"], "hello world");
        Assert.Equal("PRIVMSG #room :hello world", m.Format());
    }

    [Fact]
    public void A_single_word_trailing_still_gets_a_colon()
    {
        // The original's sprintf templates hardcode " :%s" for every send.
        var m = IrcMessage.Create("PRIVMSG", ["#room"], "hi");
        Assert.Equal("PRIVMSG #room :hi", m.Format());
    }

    [Fact]
    public void Formats_without_a_trailing()
    {
        Assert.Equal("JOIN #room", IrcMessage.Create("JOIN", ["#room"]).Format());
    }

    [Fact]
    public void Round_trips_parse_then_format()
    {
        foreach (var line in new[]
        {
            "PING :12345",
            ":nick!user@host PRIVMSG #room :hello world",
            "JOIN #room",
            ":srv 353 me = #chan :@alice +bob",
            ":n!u@h PRIVMSG #c :(#G3<9E0:5RM1) hello",
        })
        {
            Assert.Equal(line, IrcMessage.Parse(line).Format());
        }
    }

    [Fact]
    public void An_annotation_survives_a_parse_format_round_trip_byte_for_byte()
    {
        const string line = ":n!u@h PRIVMSG #c :(#G3<9E0:5RM1) hello world";
        var m = IrcMessage.Parse(line);

        // The trailing must keep the annotation's trailing space intact.
        Assert.StartsWith("(#G3<9E0:5RM1) ", m.Trailing);
        Assert.Equal(line, m.Format());
    }
}
