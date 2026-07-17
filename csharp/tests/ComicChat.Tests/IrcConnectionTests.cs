using System.IO.Pipelines;
using System.Text;
using ComicChat.Irc;

namespace ComicChat.Tests;

/// <summary>
/// A bidirectional in-memory stream pair standing in for a socket, so the whole connection
/// lifecycle can be exercised with no network.
/// </summary>
internal sealed class FakeServer : IAsyncDisposable
{
    private readonly Pipe _toClient = new();
    private readonly Pipe _toServer = new();

    /// <summary>The stream the client attaches to.</summary>
    public Stream ClientStream { get; }

    public FakeServer()
    {
        ClientStream = new DuplexStream(_toClient.Reader.AsStream(), _toServer.Writer.AsStream());
    }

    /// <summary>Push a line at the client, as a server would.</summary>
    public async Task SendToClientAsync(string line, string terminator = "\r\n")
    {
        var bytes = Encoding.Latin1.GetBytes(line + terminator);
        await _toClient.Writer.WriteAsync(bytes);
        await _toClient.Writer.FlushAsync();
    }

    /// <summary>Push raw bytes, for framing tests.</summary>
    public async Task SendRawAsync(string raw)
    {
        await _toClient.Writer.WriteAsync(Encoding.Latin1.GetBytes(raw));
        await _toClient.Writer.FlushAsync();
    }

    /// <summary>Read the lines the client has sent, waiting until <paramref name="count"/> arrive.</summary>
    public async Task<List<string>> ReadLinesAsync(int count, TimeSpan? timeout = null)
    {
        var lines = new List<string>();
        var sb = new StringBuilder();
        var reader = _toServer.Reader;
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));

        while (lines.Count < count)
        {
            var result = await reader.ReadAsync(cts.Token);
            foreach (var segment in result.Buffer)
                sb.Append(Encoding.Latin1.GetString(segment.Span));

            reader.AdvanceTo(result.Buffer.End);

            while (lines.Count < count)
            {
                var text = sb.ToString();
                int nl = text.IndexOf('\n');
                if (nl < 0) break;

                lines.Add(text[..nl].TrimEnd('\r'));
                sb.Remove(0, nl + 1);
            }

            if (result.IsCompleted) break;
        }

        return lines;
    }

    public async ValueTask DisposeAsync()
    {
        await _toClient.Writer.CompleteAsync();
        await _toServer.Writer.CompleteAsync();
        await ClientStream.DisposeAsync();
    }

    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            read.ReadAsync(buffer, ct);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            write.WriteAsync(buffer, ct);

        public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
        public override void Flush() => write.Flush();

        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

public class IrcConnectionTests
{
    private static async Task<T> Eventually<T>(Func<T?> probe, TimeSpan? timeout = null) where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var v = probe();
            if (v is not null) return v;
            await Task.Delay(5);
        }
        throw new TimeoutException("condition not met");
    }

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

    // ---- Registration -------------------------------------------------------------------------

    [Fact]
    public async Task Registration_sends_the_probe_nick_and_user_in_order()
    {
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();
        conn.Attach(server.ClientStream);

        await conn.RegisterAsync(new IrcRegistration
        {
            Nick = "djk87",
            UserName = "myuser",
            RealName = "Dave K",
            HostName = "tide22",
        });

        var lines = await server.ReadLinesAsync(3);

        Assert.Equal("MODE ISIRCX", lines[0]);
        Assert.Equal("NICK djk87", lines[1]);

        // The third USER parameter is a LITERAL '.', not the servername (ircsock.cpp:653).
        Assert.Equal("USER myuser tide22 . :Dave K", lines[2]);
    }

    [Fact]
    public async Task Registration_sends_PASS_only_when_a_password_is_set()
    {
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();
        conn.Attach(server.ClientStream);

        await conn.RegisterAsync(new IrcRegistration { Nick = "n", Password = "secret", ProbeIrcx = false });

        var lines = await server.ReadLinesAsync(3);
        Assert.Equal("PASS secret", lines[0]);
        Assert.Equal("NICK n", lines[1]);
        Assert.StartsWith("USER ", lines[2]);
    }

    [Fact]
    public async Task An_empty_password_sends_no_PASS()
    {
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();
        conn.Attach(server.ClientStream);

        await conn.RegisterAsync(new IrcRegistration { Nick = "n", Password = "", ProbeIrcx = false });

        var lines = await server.ReadLinesAsync(2);
        Assert.Equal("NICK n", lines[0]);
        Assert.StartsWith("USER ", lines[1]);
    }

    [Fact]
    public async Task The_ircx_probe_can_be_skipped()
    {
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();
        conn.Attach(server.ClientStream);

        await conn.RegisterAsync(new IrcRegistration { Nick = "n", ProbeIrcx = false });

        var lines = await server.ReadLinesAsync(2);
        Assert.DoesNotContain("MODE ISIRCX", lines);
    }

    [Theory]
    [InlineData("my user", "myuser")]
    [InlineData("a b c", "abc")]
    [InlineData("plain", "plain")]
    public void Username_spaces_are_stripped(string input, string expected)
    {
        // A space would split USER into extra parameters (ircsock.cpp:616-624).
        Assert.Equal(expected, IrcConnection.StripSpaces(input));
    }

    // ---- Framing ------------------------------------------------------------------------------

    [Fact]
    public async Task Frames_inbound_lines_on_newline_only()
    {
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();

        var received = new List<string>();
        conn.RawReceived += (_, e) => { lock (received) received.Add(e.Line); };
        conn.Attach(server.ClientStream);

        // A bare LF with no CR must still frame — the original splits on '\n' alone.
        await server.SendRawAsync("PING :one\nPING :two\r\n");

        await Eventually(() => { lock (received) return received.Count >= 2; });

        lock (received)
        {
            Assert.Equal("PING :one", received[0]);
            Assert.Equal("PING :two", received[1]);
        }
    }

    [Fact]
    public async Task Reassembles_a_line_split_across_reads()
    {
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();

        IrcMessage? got = null;
        conn.MessageReceived += (_, e) => got = e.Message;
        conn.Attach(server.ClientStream);

        await server.SendRawAsync(":nick!u@h PRIV");
        await Task.Delay(20);
        Assert.Null(got); // nothing dispatched until the newline arrives

        await server.SendRawAsync("MSG #room :hello\r\n");

        var msg = await Eventually(() => got);
        Assert.Equal("PRIVMSG", msg.Command);
        Assert.Equal("hello", msg.Trailing);
    }

    [Fact]
    public async Task Dispatches_multiple_lines_from_a_single_read()
    {
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();

        var count = 0;
        conn.MessageReceived += (_, _) => Interlocked.Increment(ref count);
        conn.Attach(server.ClientStream);

        await server.SendRawAsync("PING :a\r\nPING :b\r\nPING :c\r\n");
        await Eventually(() => Volatile.Read(ref count) >= 3);
    }

    [Fact]
    public async Task Outbound_lines_are_terminated_with_crlf()
    {
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();
        conn.Attach(server.ClientStream);

        await conn.SendAsync("JOIN #room");

        // ReadLinesAsync strips the CR, so assert on the raw framing separately.
        var lines = await server.ReadLinesAsync(1);
        Assert.Equal("JOIN #room", lines[0]);
    }

    // ---- PING / PONG --------------------------------------------------------------------------

    [Fact]
    public async Task Replies_to_PING_by_reflecting_the_token()
    {
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();
        conn.Attach(server.ClientStream);

        await server.SendToClientAsync("PING :irc.stealth.net");

        var lines = await server.ReadLinesAsync(1);
        Assert.Equal("PONG :irc.stealth.net", lines[0]);
    }

    [Fact]
    public async Task A_PING_with_no_payload_still_gets_a_PONG()
    {
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();
        conn.Attach(server.ClientStream);

        await server.SendToClientAsync("PING");

        var lines = await server.ReadLinesAsync(1);
        Assert.Equal("PONG :", lines[0]);
    }

    // ---- IRCX negotiation ---------------------------------------------------------------------

    [Fact]
    public async Task An_800_reply_marks_the_server_as_IRCX_and_negotiates_the_length()
    {
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();
        conn.Attach(server.ClientStream);

        Assert.False(conn.IsIrcxServer);
        Assert.Equal(512, conn.MaxMessageLength);

        // The real capture from irc.txt.
        await server.SendToClientAsync(":chloe1 800 * 0 0 NTLM,ANON 512 *");

        await Eventually(() => conn.IsIrcxServer);
        Assert.Equal(512, conn.MaxMessageLength);
        Assert.Equal(["NTLM"], conn.ServerSecurityPackages);
        Assert.True(conn.AnonymousAllowed);

        // Having detected IRCX we switch the server into it.
        var lines = await server.ReadLinesAsync(1);
        Assert.Equal("IRCX", lines[0]);
    }

    [Fact]
    public async Task An_800_reply_can_grow_the_max_message_length()
    {
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();
        conn.Attach(server.ClientStream);

        await server.SendToClientAsync(":chloe1 800 * 0 0 NTLM,ANON 1024 *");

        await Eventually(() => conn.MaxMessageLength == 1024);
    }

    [Fact]
    public async Task The_max_message_length_never_shrinks()
    {
        // The original guards with `if (m_nMaxMsgLength < nMaxMsgLength)` (ircsock.cpp:2877).
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();
        conn.Attach(server.ClientStream);

        await server.SendToClientAsync(":chloe1 800 * 0 0 NTLM,ANON 256 *");
        await Eventually(() => conn.IsIrcxServer);

        Assert.Equal(512, conn.MaxMessageLength);
    }

    [Fact]
    public async Task A_plain_server_error_to_the_ISIRCX_probe_is_swallowed()
    {
        // ircsock.cpp:575 — "Don't want to expose this error to the user, it comes from the
        // MODE ISIRCX\r\n command on an IRC server".
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();
        conn.Attach(server.ClientStream);

        await conn.RegisterAsync(new IrcRegistration { Nick = "djk87" });
        await server.ReadLinesAsync(3);

        // A plain RFC1459 server rejects the probe. This must not throw or disconnect.
        await server.SendToClientAsync(":irc.stealth.net 461 djk87 MODE :Not enough parameters");
        await Task.Delay(50);

        Assert.False(conn.IsIrcxServer);
        Assert.True(conn.IsConnected);
        Assert.Equal(512, conn.MaxMessageLength);
    }

    [Fact]
    public async Task The_second_800_acknowledgement_does_not_re_trigger_the_switch()
    {
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();
        conn.Attach(server.ClientStream);

        // args[2] == '1' is the ack of the switch, not the probe reply.
        await server.SendToClientAsync(":chloe1 800 * 1 0 NTLM,ANON 512 *");
        await Task.Delay(50);

        Assert.False(conn.IsIrcxServer);
    }

    // ---- Disconnect ---------------------------------------------------------------------------

    [Fact]
    public async Task Disconnect_sends_QUIT_by_default()
    {
        // A documented DIVERGENCE: the original never sends QUIT.
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();
        conn.Attach(server.ClientStream);

        await conn.DisconnectAsync("bye");

        var lines = await server.ReadLinesAsync(1);
        Assert.Equal("QUIT :bye", lines[0]);
        Assert.False(conn.IsConnected);
    }

    [Fact]
    public async Task Disconnect_can_reproduce_the_originals_silent_drop()
    {
        await using var server = new FakeServer();
        await using var conn = new IrcConnection();
        conn.Attach(server.ClientStream);

        await conn.SendAsync("PART #room");
        await conn.FlushAsync();
        await conn.DisconnectAsync(sendQuit: false);

        var lines = await server.ReadLinesAsync(1);
        Assert.Equal("PART #room", lines[0]);
        Assert.DoesNotContain(lines, l => l.StartsWith("QUIT"));
    }

    [Fact]
    public async Task Disconnected_fires_on_EOF()
    {
        var server = new FakeServer();
        await using var conn = new IrcConnection();

        var disconnected = false;
        conn.Disconnected += (_, _) => disconnected = true;
        conn.Attach(server.ClientStream);

        await server.DisposeAsync();

        await Eventually(() => disconnected);
    }
}
