using System.Text;

namespace ComicChat.Irc;

/// <summary>
/// A single parsed IRC line. Port of IRCPARSE (ircsock.h:313) and ParseIt (ircsock.cpp:137).
/// </summary>
/// <remarks>
/// The original keeps the trailing parameter OUT of the args array, in a separate
/// <c>lastString</c> field, and puts the command itself in <c>args[0]</c>. That split is preserved
/// here (<see cref="Command"/> / <see cref="Parameters"/> / <see cref="Trailing"/>) because a lot of
/// the protocol code indexes relative to it — most notably the IRCX 800 reply, which reads max
/// message length from <c>args[nArgs-2]</c>.
/// </remarks>
public sealed record IrcMessage
{
    /// <summary>Raw prefix with no leading ':', or null when the line had none.</summary>
    public string? Prefix { get; init; }

    /// <summary>Nickname from the prefix — or the whole prefix when it is a bare server name.</summary>
    public string? Nick { get; init; }

    /// <summary>Username from the prefix (between '!' and '@'), when present.</summary>
    public string? User { get; init; }

    /// <summary>Hostname from the prefix (after '@'), when present.</summary>
    public string? Host { get; init; }

    /// <summary>The command verb or numeric, i.e. the original's args[0].</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>Middle parameters — args[1..], excluding the trailing parameter.</summary>
    public IReadOnlyList<string> Parameters { get; init; } = [];

    /// <summary>The trailing parameter (after " :"), or null. The original's <c>lastString</c>.</summary>
    public string? Trailing { get; init; }

    /// <summary>Parsed numeric code, or 0 when <see cref="Command"/> is not a numeric.</summary>
    public int Numeric => int.TryParse(Command, out var n) && Command.Length == 3 ? n : 0;

    public bool HasPrefix => Prefix is not null;

    /// <summary>Parameter accessor that tolerates short lines, like reading a NULL args slot would not.</summary>
    public string? Arg(int index) => index >= 0 && index < Parameters.Count ? Parameters[index] : null;

    /// <summary>
    /// All args the way the original counts them: args[0] is the command. Provided because the IRCX
    /// 800 handler indexes from the END (<c>args[nArgs-2]</c>, ircsock.cpp:2874) and that arithmetic
    /// only works against the original's numbering.
    /// </summary>
    public IReadOnlyList<string> Args
    {
        get
        {
            var args = new List<string>(Parameters.Count + 1) { Command };
            args.AddRange(Parameters);
            return args;
        }
    }

    /// <summary>
    /// Parse one line. Port of ParseIt (ircsock.cpp:137). The line may still carry its CR/LF; the
    /// original trims at "\r\n" when capturing the trailing and relies on GetToken's separator set
    /// (" \r\n") for the middles, so both are stripped here.
    /// </summary>
    public static IrcMessage Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var s = line.TrimEnd('\r', '\n');
        int i = 0;

        string? prefix = null, nick = null, user = null, host = null;

        if (i < s.Length && s[i] == ':')
        {
            i++; // don't include the colon
            int sp = s.IndexOf(' ', i);
            if (sp < 0)
            {
                // The original ASSERTs "messages must have a body". Be lenient: prefix-only line.
                prefix = s[i..];
                ParsePrefix(prefix, out nick, out user, out host);
                return new IrcMessage { Prefix = prefix, Nick = nick, User = user, Host = host };
            }

            prefix = s[i..sp];
            ParsePrefix(prefix, out nick, out user, out host);
            i = sp;
        }

        var args = new List<string>();
        string? trailing = null;

        while (true)
        {
            while (i < s.Length && IsSpace(s[i])) i++;
            if (i >= s.Length) break;

            if (s[i] == ':')
            {
                // Everything after the colon is one parameter, spaces and all.
                trailing = s[(i + 1)..];
                break;
            }

            int start = i;
            while (i < s.Length && !IsSpace(s[i])) i++;
            args.Add(s[start..i]);
        }

        var command = args.Count > 0 ? args[0] : string.Empty;
        var parameters = args.Count > 1 ? args.GetRange(1, args.Count - 1) : [];

        return new IrcMessage
        {
            Prefix = prefix,
            Nick = nick,
            User = user,
            Host = host,
            Command = command,
            Parameters = parameters,
            Trailing = trailing,
        };
    }

    /// <summary>
    /// Split a prefix into nick!user@host. Port of the prefix block in ParseIt (ircsock.cpp:153-190).
    /// </summary>
    /// <remarks>
    /// A prefix with no '!' or '@' — a server name such as "irc.stealth.net" — is stored WHOLE as the
    /// nick, which is how the original tells server-sourced numerics apart from user traffic. The
    /// original also skips the split entirely when the prefix begins with a channel prefix character
    /// (CHANNELPREFIX, defines.h:162), so channel-sourced lines keep their name intact.
    /// </remarks>
    private static void ParsePrefix(string prefix, out string? nick, out string? user, out string? host)
    {
        nick = null; user = null; host = null;
        if (prefix.Length == 0) return;

        if (IsChannelPrefix(prefix[0]))
        {
            nick = prefix;
            return;
        }

        int bang = prefix.IndexOf('!');
        int at = prefix.IndexOf('@');

        // strpbrk(szStart, "!@") — whichever separator comes first.
        int sep = bang >= 0 && at >= 0 ? Math.Min(bang, at) : Math.Max(bang, at);
        if (sep < 0)
        {
            nick = prefix;
            return;
        }

        nick = prefix[..sep];

        if (prefix[sep] == '!')
        {
            int atAfter = prefix.IndexOf('@', sep + 1);
            if (atAfter >= 0)
            {
                user = prefix[(sep + 1)..atAfter];
                host = prefix[(atAfter + 1)..];
            }
            // The original only fills user/host when BOTH separators are present; a "nick!user" with
            // no '@' leaves them empty rather than guessing.
        }
        else
        {
            host = prefix[(sep + 1)..];
        }
    }

    /// <summary>Port of the CHANNELPREFIX macro (defines.h:162).</summary>
    public static bool IsChannelPrefix(char c) => c is '#' or '%' or '&';

    /// <summary>Port of the my_isspace macro (defines.h:159).</summary>
    private static bool IsSpace(char c) => c is ' ' or '\t' or '\r' or '\n';

    /// <summary>
    /// Render back to a wire line WITHOUT the terminating CRLF (<see cref="IrcConnection"/> adds it).
    /// </summary>
    /// <remarks>
    /// The trailing parameter always gets its ':' — the original's sprintf templates hardcode
    /// " :%s" for every send — so a trailing that happens to be a single word still round-trips as
    /// trailing rather than silently becoming a middle parameter.
    /// </remarks>
    public string Format()
    {
        var sb = new StringBuilder(128);

        if (Prefix is not null) sb.Append(':').Append(Prefix).Append(' ');
        sb.Append(Command);

        foreach (var p in Parameters) sb.Append(' ').Append(p);

        if (Trailing is not null) sb.Append(" :").Append(Trailing);

        return sb.ToString();
    }

    public override string ToString() => Format();

    /// <summary>Convenience builder for outbound lines.</summary>
    public static IrcMessage Create(string command, IEnumerable<string>? parameters = null, string? trailing = null) =>
        new()
        {
            Command = command,
            Parameters = parameters?.ToList() ?? [],
            Trailing = trailing,
        };
}
