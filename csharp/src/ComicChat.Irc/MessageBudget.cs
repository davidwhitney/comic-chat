namespace ComicChat.Irc;

/// <summary>
/// The outbound length budget and word-boundary splitter.
/// Port of the sizing block in CIrcProto::bChatSendToTarget (ircproto.cpp:485-535) and
/// nGetBreakingPoint (ircproto.cpp:398).
/// </summary>
/// <remarks>
/// SIMPLIFICATIONS (deliberate, documented per the brief): the original's DBCS/UTF-8 character
/// stepping (CharNext / SzNextUTF8Char) and its inline formatting-run bookkeeping (the ^k colour,
/// ^b bold, ^i italic escapes tracked through wFormatBegin/wFormatEnd, and the rule that a break
/// must not land mid-escape) are NOT reproduced. This splitter measures in chars and breaks only on
/// whitespace. The budget arithmetic and the 80% rule below ARE faithful.
/// </remarks>
public static class MessageBudget
{
    /// <summary>RFC1459's line limit and the original's default buffer (ircsock.cpp:515).</summary>
    public const int DefaultMaxMessageLength = 512;

    /// <summary>
    /// Fixed overhead the original charges for the command itself: "PRIVMSG  :\r\n" (ircproto.cpp:485).
    /// </summary>
    public const int CommandOverhead = 12;

    /// <summary>Fallback hostname length when our own ident is not yet known (ircproto.cpp:533).</summary>
    public const int UnknownHostnameLength = 32;

    /// <summary>
    /// Length of the prefix the SERVER will prepend on the RECEIVING side.
    /// Port of the nReceivingPrefixLen computation (ircproto.cpp:527-533).
    /// </summary>
    /// <remarks>
    /// WHY WE BUDGET FOR IT: we send "PRIVMSG #room :text", but every recipient receives
    /// ":nick!user@host PRIVMSG #room :text" — the server injects our mask. If we fill the line to
    /// 512 on the way out, the server truncates it on the way in to make room, and recipients see a
    /// clipped message while WE see a perfectly fine one. So the sender pre-pays for a prefix it
    /// never writes. The "2" covers the leading ':' and the trailing space.
    /// </remarks>
    public static int ReceivingPrefixLength(string myNick, string myUserName, int myIdentLength = 0)
    {
        int len = 2 + myNick.Length;

        // Once ident has told us our real mask length we use it; until then assume the worst-ish.
        len += myIdentLength > 0 ? myIdentLength : myUserName.Length + UnknownHostnameLength;

        return len;
    }

    /// <summary>
    /// Total length the message will occupy on the receiving side (ircproto.cpp:535).
    /// </summary>
    public static int TotalLength(string target, string annotations, string message, int receivingPrefixLength) =>
        CommandOverhead + target.Length + annotations.Length + message.Length + receivingPrefixLength;

    /// <summary>True when the message fits and can go in one shot (ircproto.cpp:537).</summary>
    public static bool Fits(
        string target,
        string annotations,
        string message,
        int receivingPrefixLength,
        int maxMessageLength = DefaultMaxMessageLength) =>
        TotalLength(target, annotations, message, receivingPrefixLength) <= maxMessageLength;

    /// <summary>
    /// Bytes available for the message body per chunk (ircproto.cpp:624).
    /// </summary>
    /// <remarks>
    /// The annotation is charged against EVERY chunk even on IRCX, where it actually travels in a
    /// separate DATA line — the original does the same, so IRCX chunks are merely conservative.
    /// </remarks>
    public static int BodyBudget(
        string target,
        string annotations,
        int receivingPrefixLength,
        int maxMessageLength = DefaultMaxMessageLength) =>
        maxMessageLength - receivingPrefixLength - CommandOverhead - target.Length - annotations.Length;

    /// <summary>
    /// Find the break point in <paramref name="body"/> for a chunk of at most
    /// <paramref name="maxLength"/> chars. Port of nGetBreakingPoint (ircproto.cpp:398).
    /// </summary>
    /// <remarks>
    /// The 80% rule (ircproto.cpp:423): a whitespace break is only taken when it falls at or beyond
    /// 80% of the budget. A space at 10% would technically be a valid word boundary but would waste
    /// most of the line, so the original prefers a hard mid-word cut to shipping near-empty chunks.
    /// The scan bound is <c>szTmp &lt; szBody + nMaxLength - 2</c>, reproduced here.
    /// </remarks>
    public static int GetBreakingPoint(string body, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (body.Length <= maxLength) return body.Length;
        if (maxLength <= 0) return 0;

        int validSpaceStart = (int)(maxLength * 0.8);
        int furthestSpaceStart = -1;
        bool inSpaces = false;

        int i = 0;
        int limit = Math.Max(0, maxLength - 2);

        // do/while: the original always inspects at least one char.
        do
        {
            if (IsSpace(body[i]))
            {
                // Record the START of a whitespace RUN, so the break drops the whole run rather
                // than leaving leading spaces on the next chunk.
                if (!inSpaces)
                {
                    furthestSpaceStart = i;
                    inSpaces = true;
                }
            }
            else
            {
                inSpaces = false;
            }

            i++;
        }
        while (i < limit && i < body.Length);

        if (furthestSpaceStart >= 0 && furthestSpaceStart >= validSpaceStart)
            return furthestSpaceStart;

        // No usable space: cut wherever the scan stopped.
        return i;
    }

    /// <summary>
    /// Split a body into chunks that each fit the budget, breaking at word boundaries where the 80%
    /// rule allows. Port of the chunking do/while (ircproto.cpp:620-660).
    /// </summary>
    public static IReadOnlyList<string> Split(string body, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (maxLength <= 0) throw new ArgumentOutOfRangeException(nameof(maxLength));
        if (body.Length == 0) return [];
        if (body.Length <= maxLength) return [body];

        var chunks = new List<string>();
        var rest = body;

        while (rest.Length > 0)
        {
            int bp = GetBreakingPoint(rest, maxLength);
            if (bp <= 0) bp = Math.Min(maxLength, rest.Length);

            chunks.Add(rest[..bp]);
            rest = rest[bp..];

            // The original leaves the break character in place and resumes from it; trimming the
            // whitespace run here keeps chunks from starting with the space we broke on.
            rest = rest.TrimStart(' ', '\t', '\r', '\n');
        }

        return chunks;
    }

    /// <summary>Port of the my_isspace macro (defines.h:159).</summary>
    private static bool IsSpace(char c) => c is ' ' or '\t' or '\r' or '\n';
}
