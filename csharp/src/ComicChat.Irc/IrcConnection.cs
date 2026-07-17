using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace ComicChat.Irc;

/// <summary>Credentials and identity for registration. Port of the args to CIrcSocket::Register.</summary>
public sealed record IrcRegistration
{
    public required string Nick { get; init; }
    public string UserName { get; init; } = "user";
    public string RealName { get; init; } = "Comic Chat User";
    public string HostName { get; init; } = "localhost";
    public string? Password { get; init; }

    /// <summary>
    /// Send "MODE ISIRCX" before registering to sniff for an IRCX server (ircsock.cpp:1050).
    /// </summary>
    public bool ProbeIrcx { get; init; } = true;
}

public sealed class IrcMessageEventArgs(IrcMessage message) : EventArgs
{
    public IrcMessage Message { get; } = message;
}

public sealed class IrcRawEventArgs(string line) : EventArgs
{
    public string Line { get; } = line;
}

/// <summary>
/// The transport: framing, the send queue, PING/PONG and the registration handshake.
/// Port of CIrcSocket (ircsock.cpp / ircsock.h), restructured around async/await rather than the
/// original's MFC OnReceive/OnConnect callbacks.
/// </summary>
/// <remarks>
/// Deliberately takes a <see cref="Stream"/> rather than owning a socket, so the whole lifecycle can
/// be driven from a MemoryStream or a pipe in tests with no network.
/// </remarks>
public sealed class IrcConnection : IAsyncDisposable
{
    private readonly Channel<string> _sendQueue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private Stream? _stream;
    private TcpClient? _tcp;
    private bool _ownsStream;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private Task? _writeLoop;

    /// <summary>
    /// The wire encoding. IRC predates Unicode and the original treats the stream as raw bytes; the
    /// Comic Chat annotation in particular is byte-oriented ('0'-biased single bytes, some above
    /// 0x7F), so Latin-1 is used to map bytes to chars 1:1 without any lossy transcoding.
    /// </summary>
    private static readonly Encoding Wire = Encoding.Latin1;

    /// <summary>Fires for every parsed inbound line.</summary>
    public event EventHandler<IrcMessageEventArgs>? MessageReceived;

    /// <summary>Fires with each raw inbound line, before parsing. Useful for logging.</summary>
    public event EventHandler<IrcRawEventArgs>? RawReceived;

    /// <summary>Fires with each raw outbound line, before the CRLF is appended.</summary>
    public event EventHandler<IrcRawEventArgs>? RawSent;

    public event EventHandler? Disconnected;

    /// <summary>True once a 800 numeric identified the peer as an IRCX server (ircsock.cpp:2844).</summary>
    public bool IsIrcxServer { get; private set; }

    /// <summary>
    /// Negotiated max line length. Starts at RFC1459's 512 and only ever GROWS, per the
    /// <c>if (m_nMaxMsgLength &lt; nMaxMsgLength)</c> guard at ircsock.cpp:2877.
    /// </summary>
    public int MaxMessageLength { get; private set; } = MessageBudget.DefaultMaxMessageLength;

    /// <summary>Security packages the IRCX server advertised in the 800 reply (e.g. NTLM).</summary>
    public IReadOnlyList<string> ServerSecurityPackages { get; private set; } = [];

    /// <summary>True when the 800 reply listed ANON among its packages (ircsock.cpp:2865).</summary>
    public bool AnonymousAllowed { get; private set; }

    public bool IsConnected => _stream is not null;

    public string? Nick { get; private set; }

    /// <summary>Connect a real socket.</summary>
    public async Task ConnectAsync(string host, int port = 6667, CancellationToken ct = default)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        Attach(_tcp.GetStream(), ownsStream: true);
    }

    /// <summary>
    /// Drive the connection over an arbitrary duplex stream. The seam that makes this testable.
    /// </summary>
    public void Attach(Stream stream, bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (_stream is not null) throw new InvalidOperationException("Already attached.");

        _stream = stream;
        _ownsStream = ownsStream;
        _cts = new CancellationTokenSource();

        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        _writeLoop = Task.Run(() => WriteLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Perform the registration handshake. Port of CIrcSocket::Register (ircsock.cpp:637-657).
    /// </summary>
    /// <remarks>
    /// The IRCX probe goes FIRST and is fire-and-forget: a plain RFC1459 server answers "MODE ISIRCX"
    /// with an error, and the original DELIBERATELY SWALLOWS it (ircsock.cpp:575, whose comment reads
    /// "Don't want to expose this error to the user, it comes from the MODE ISIRCX\r\n command on an
    /// IRC server"). An IRCX server instead replies 800 and <see cref="IsIrcxServer"/> flips.
    /// Registration does not wait for either — the probe overlaps with PASS/NICK/USER.
    /// </remarks>
    public async Task RegisterAsync(IrcRegistration registration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (registration.ProbeIrcx)
            await SendAsync("MODE ISIRCX", ct).ConfigureAwait(false);

        // PASS is only sent when non-empty; an empty PASS is an error on many servers.
        if (!string.IsNullOrEmpty(registration.Password))
            await SendAsync($"PASS {registration.Password}", ct).ConfigureAwait(false);

        Nick = registration.Nick;
        await SendAsync($"NICK {registration.Nick}", ct).ConfigureAwait(false);

        // Spaces are stripped from the username (the copy loop at ircsock.cpp:616-624) — a space
        // would split the USER command into extra parameters.
        var user = StripSpaces(registration.UserName);

        // The third parameter is a LITERAL '.', not the servername RFC1459 nominally puts there.
        // Faithful to the original's sprintf: "USER %s %s . :%s\r\n" (ircsock.cpp:653).
        await SendAsync($"USER {user} {registration.HostName} . :{registration.RealName}", ct)
            .ConfigureAwait(false);
    }

    /// <summary>Port of the space-stripping copy loop at ircsock.cpp:616-624.</summary>
    internal static string StripSpaces(string s)
    {
        if (!s.Contains(' ')) return s;
        return string.Concat(s.Where(c => c != ' '));
    }

    /// <summary>Queue a raw line. The CRLF is appended by the writer.</summary>
    public async Task SendAsync(string line, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        await _sendQueue.Writer.WriteAsync(line, ct).ConfigureAwait(false);
    }

    public Task SendAsync(IrcMessage message, CancellationToken ct = default) =>
        SendAsync(message.Format(), ct);

    /// <summary>
    /// Close the connection.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE FROM THE ORIGINAL, by choice: the original NEVER sends QUIT — it parts the channel
    /// and drops the socket, leaving the server to time the session out. We send QUIT by default
    /// because a clean quit gives other clients an immediate, correctly-attributed departure message
    /// instead of a ping-timeout several minutes later. Pass sendQuit: false for original behaviour.
    /// </remarks>
    public async Task DisconnectAsync(string? quitMessage = null, bool sendQuit = true, CancellationToken ct = default)
    {
        if (_stream is null) return;

        if (sendQuit)
        {
            try
            {
                await SendAsync(quitMessage is null ? "QUIT" : $"QUIT :{quitMessage}", ct).ConfigureAwait(false);
                await FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Peer already gone; nothing useful to do.
            }
        }

        await ShutdownAsync().ConfigureAwait(false);
    }

    /// <summary>Wait until the send queue has drained to the stream.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        // The writer loop is the only reader; poll briefly for the queue to empty.
        while (_sendQueue.Reader.TryPeek(out _))
            await Task.Delay(1, ct).ConfigureAwait(false);

        if (_stream is not null)
            await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var line in _sendQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var stream = _stream;
                if (stream is null) break;

                RawSent?.Invoke(this, new IrcRawEventArgs(line));

                // Outbound framing is ALWAYS CRLF, even though inbound is split on '\n' alone.
                var bytes = Wire.GetBytes(line + "\r\n");
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    /// <summary>
    /// Accumulate bytes and dispatch complete lines. Port of CIrcSocket::OnReceive (ircsock.cpp:1000).
    /// </summary>
    /// <remarks>
    /// Frames on '\n' ONLY — <c>strchr(m_szInput, '\n')</c> — never on '\r'. A stray '\r' is left on
    /// the line and stripped downstream (ParseIt's separator sets include it). Tolerating a bare LF
    /// is what lets the client talk to servers and test harnesses that skip the CR.
    /// </remarks>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var accumulator = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stream = _stream;
                if (stream is null) break;

                int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0) break; // EOF

                accumulator.Append(Wire.GetString(buffer, 0, read));

                while (true)
                {
                    var text = accumulator.ToString();
                    int nl = text.IndexOf('\n');
                    if (nl < 0) break;

                    var line = text[..nl];
                    accumulator.Remove(0, nl + 1);

                    line = line.TrimEnd('\r');
                    if (line.Length == 0) continue;

                    Dispatch(line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        finally
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Dispatch(string line)
    {
        RawReceived?.Invoke(this, new IrcRawEventArgs(line));

        var msg = IrcMessage.Parse(line);

        HandlePing(msg);
        HandleIrcx(msg);

        MessageReceived?.Invoke(this, new IrcMessageEventArgs(msg));
    }

    /// <summary>
    /// Port of the PING branch (ircsock.cpp:1696). Pure reflection of the trailing parameter.
    /// </summary>
    /// <remarks>
    /// The original does <c>sprintf("PONG :%s\r\n", pParse-&gt;lastString ? pParse-&gt;lastString : "")</c>
    /// — it echoes lastString and nothing else, and sends "PONG :" when there is no payload. The
    /// token is opaque; reflecting it verbatim is the whole contract.
    /// </remarks>
    private void HandlePing(IrcMessage msg)
    {
        if (!msg.Command.Equals("PING", StringComparison.OrdinalIgnoreCase)) return;

        _ = SendAsync($"PONG :{msg.Trailing ?? string.Empty}");
    }

    /// <summary>
    /// Port of the RPL_IRCX (800) handler (ircsock.cpp:2817-2884).
    /// </summary>
    /// <remarks>
    /// Shape: <c>:chloe1 800 * &lt;state&gt; &lt;version&gt; &lt;pkgs&gt; &lt;maxmsglen&gt; &lt;options&gt;</c>,
    /// verified against the real capture at irc.txt (":chloe1 800 * 0 0 NTLM,ANON 512 *").
    ///
    /// args[2] is the state: '0' means "still in IRC mode" and is the reply to our probe — that is
    /// the one that sets IsIrcxServer and triggers the actual switch ("IRCX" command). '1' is the
    /// acknowledgement of the switch itself, and the capture shows both arriving in turn.
    ///
    /// Max message length is read as <c>args[nArgs-2]</c> — indexed from the END, not a fixed slot,
    /// because the trailing options field is variable. Hence <see cref="IrcMessage.Args"/>, which
    /// numbers args the original's way (args[0] == the numeric).
    /// </remarks>
    private void HandleIrcx(IrcMessage msg)
    {
        if (msg.Numeric != 800) return;

        var args = msg.Args;
        if (args.Count < 3) return;

        if (args[2].Length > 0 && args[2][0] == '0')
        {
            IsIrcxServer = true;

            if (args.Count >= 7)
            {
                var packages = new List<string>();
                foreach (var p in args[4].Split(','))
                {
                    if (p.Equals("ANON", StringComparison.OrdinalIgnoreCase)) AnonymousAllowed = true;
                    else if (p.Length > 0) packages.Add(p);
                }
                ServerSecurityPackages = packages;
            }

            if (args.Count >= 2 && int.TryParse(args[^2], out var maxLen) && MaxMessageLength < maxLen)
                MaxMessageLength = maxLen;

            // Now actually switch the server into IRCX mode (bExecuteQuery(qpIrcX, ...)).
            _ = SendAsync("IRCX");
        }
    }

    private async Task ShutdownAsync()
    {
        _sendQueue.Writer.TryComplete();

        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);

        var stream = _stream;
        _stream = null;

        if (stream is not null && _ownsStream) await stream.DisposeAsync().ConfigureAwait(false);

        _tcp?.Dispose();
        _tcp = null;

        foreach (var t in new[] { _readLoop, _writeLoop })
        {
            if (t is null) continue;
            try { await t.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _readLoop = _writeLoop = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);
}
