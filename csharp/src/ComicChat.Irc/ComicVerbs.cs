using System.Text;

namespace ComicChat.Irc;

/// <summary>The kind of '#' verb carried in a message body.</summary>
public enum ComicVerbKind
{
    None = 0,

    /// <summary>"# Appears as &lt;name&gt;[.&lt;url&gt;]" — an avatar identity announcement.</summary>
    AppearsAs,

    /// <summary>"# GetInfo" — a request for our profile text.</summary>
    GetInfo,

    /// <summary>"# HeresInfo: &lt;profile&gt;" — the reply to GetInfo.</summary>
    HeresInfo,

    /// <summary>"# GetCharInfo" — a request for our avatar's real URL.</summary>
    GetCharInfo,

    /// <summary>"# BDrop2: &lt;name&gt;,&lt;url&gt;" — an operator changing the backdrop.</summary>
    BackdropDrop2,

    /// <summary>"# BDrop: &lt;name&gt;" — the legacy, URL-less backdrop verb.</summary>
    BackdropDrop,
}

/// <summary>A decoded '#' verb.</summary>
public readonly record struct ComicVerb(ComicVerbKind Kind, string? Name, string? Url)
{
    public static readonly ComicVerb None = new(ComicVerbKind.None, null, null);
}

/// <summary>
/// The Comic Chat '#' verb layer: avatar identity, profiles and backdrops.
/// Encoders live around protsupp.cpp:833 and :3419-3438; the decoder is ProcessComment
/// (protsupp.cpp:846).
/// </summary>
/// <remarks>
/// THIS IS NOT CTCP. It rides in the plain body of a PRIVMSG with a leading '#' and a space-padded
/// verb, with no 0x01 delimiters — a plain mIRC user simply sees the text. Real CTCP (\x01ACTION,
/// \x01VERSION, \x01PING, \x01SOUND, \x01DCC …) is handled separately and conventionally; see the
/// actionID/soundID/versionID tables at ircproto.h:91-102.
///
/// ART IS NEVER SENT OVER IRC. Only a name and an optional URL travel; the receiver fetches the
/// .avb over HTTP out of band. That keeps the protocol inside a 512-byte line and is why the
/// deferred-URL handshake below exists at all.
/// </remarks>
public static class ComicVerbs
{
    /// <summary>Port of APPEARSPREFIX (ircproto.h:83). The leading AND trailing spaces are part of it.</summary>
    public const string AppearsPrefix = " Appears as ";

    /// <summary>Port of GETINFOPREFIX (ircproto.h:84).</summary>
    public const string GetInfoPrefix = " GetInfo";

    /// <summary>Port of HERESINFOPREFIX (ircproto.h:85).</summary>
    public const string HeresInfoPrefix = " HeresInfo: ";

    /// <summary>Port of BACKGRNDPREFIX (ircproto.h:86).</summary>
    public const string BackdropPrefix = " BDrop: ";

    /// <summary>Port of NEWBACKGRNDPREFIX (ircproto.h:87).</summary>
    public const string NewBackdropPrefix = " BDrop2: ";

    /// <summary>Port of REQUESTCHARPREFIX (ircproto.h:88).</summary>
    public const string RequestCharPrefix = " GetCharInfo";

    /// <summary>Port of CCUDI1 (ircproto.h:89) — the IRCX DATA subcommand carrying annotations.</summary>
    public const string Ccudi1 = "CCUDI1";

    /// <summary>
    /// Port of DEFERRED_URL_STRING (ircproto.h:125). Stands in for our real avatar URL in a reply.
    /// </summary>
    /// <remarks>
    /// WHY: see <see cref="FormatAppearsAs"/> and the handshake note on <see cref="OnAppearsAs"/>.
    /// </remarks>
    public const string DeferredUrlString = "?";

    /// <summary>Port of GetToken's default separator set (protsupp.cpp:316).</summary>
    /// <remarks>
    /// This is exactly why an avatar name may not contain '.', ',' or ')': the tokeniser that reads
    /// the name terminates on all three. The name/URL boundary is a '.', which also means the FIRST
    /// dot wins — the URL then absorbs any further dots because GetToken2's end-separator set for it
    /// is only ",)" (protsupp.cpp:862).
    /// </remarks>
    public const string TokenSeparators = ",.)";

    /// <summary>True when a message body is a '#' verb rather than ordinary text.</summary>
    public static bool IsComicVerb(string body) => body.Length > 0 && body[0] == '#';

    /// <summary>
    /// Format an avatar announcement. Port of ChatAnnounceNewAvatar (protsupp.cpp:833-835).
    /// </summary>
    /// <param name="avatarName">
    /// Character name. An empty name becomes "NONE" — the original guarantees a name is always sent
    /// (protsupp.cpp:831) because the receiver's tokeniser has nothing to anchor on otherwise.
    /// </param>
    /// <param name="url">
    /// Real URL, <see cref="DeferredUrlString"/>, or null for none. The URL is appended after a '.'.
    /// </param>
    public static string FormatAppearsAs(string? avatarName, string? url)
    {
        if (string.IsNullOrEmpty(avatarName)) avatarName = "NONE";

        return !string.IsNullOrEmpty(url)
            ? $"#{AppearsPrefix}{avatarName}.{url}"
            : $"#{AppearsPrefix}{avatarName}";
    }

    /// <summary>Port of the GetInfo probe (protsupp.cpp:3419).</summary>
    public static string FormatGetInfo() => $"#{GetInfoPrefix}";

    /// <summary>Port of the GetCharInfo request (protsupp.cpp:3427).</summary>
    public static string FormatGetCharInfo() => $"#{RequestCharPrefix}";

    /// <summary>Port of the HeresInfo reply (protsupp.cpp:919).</summary>
    public static string FormatHeresInfo(string profile) => $"#{HeresInfoPrefix}{profile}";

    /// <summary>Port of the BDrop2 announcement (protsupp.cpp:3438).</summary>
    public static string FormatBackdrop(string backdropName, string? url) =>
        $"#{NewBackdropPrefix}{backdropName},{url ?? string.Empty}";

    /// <summary>
    /// Decode a '#' verb body. Port of ProcessComment (protsupp.cpp:846).
    /// </summary>
    /// <param name="body">The message body, including the leading '#'.</param>
    public static ComicVerb Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!IsComicVerb(body)) return ComicVerb.None;

        var s = body[1..]; // nuke the crosshatch

        if (s.StartsWith(AppearsPrefix, StringComparison.Ordinal))
        {
            var rest = s[AppearsPrefix.Length..];
            int next = 0;

            // GetToken(szVar, &szVar) — default separators ",.)".
            var name = GetToken(rest, ref next, TokenSeparators, TokenSeparators);

            // GetToken2(szVar, &szVar, ".,)", ",)") — skip the '.' delimiter, then read a URL that
            // is allowed to contain dots.
            var url = GetToken(rest, ref next, ".,)", ",)");

            return new ComicVerb(ComicVerbKind.AppearsAs, name, url);
        }

        if (s.StartsWith(RequestCharPrefix, StringComparison.Ordinal))
            return new ComicVerb(ComicVerbKind.GetCharInfo, null, null);

        if (s.StartsWith(HeresInfoPrefix, StringComparison.Ordinal))
            return new ComicVerb(ComicVerbKind.HeresInfo, s[HeresInfoPrefix.Length..], null);

        // GetInfo is tested before HeresInfo in the original but the prefixes are distinct
        // (" GetInfo" vs " HeresInfo: "), so order is not load-bearing. GetCharInfo is checked
        // first here regardless, since " GetInfo" is NOT a prefix of " GetCharInfo".
        if (s.StartsWith(GetInfoPrefix, StringComparison.Ordinal))
            return new ComicVerb(ComicVerbKind.GetInfo, null, null);

        if (s.StartsWith(NewBackdropPrefix, StringComparison.Ordinal))
        {
            var rest = s[NewBackdropPrefix.Length..];
            int next = 0;
            var name = GetToken(rest, ref next, ",", ",");
            var url = GetToken(rest, ref next, ",", ",)");
            return new ComicVerb(ComicVerbKind.BackdropDrop2, name, url);
        }

        if (s.StartsWith(BackdropPrefix, StringComparison.Ordinal))
        {
            var rest = s[BackdropPrefix.Length..];
            int next = 0;
            var name = GetToken(rest, ref next, TokenSeparators, TokenSeparators);
            return new ComicVerb(ComicVerbKind.BackdropDrop, name, null);
        }

        return ComicVerb.None;
    }

    /// <summary>
    /// Port of GetToken2 (protsupp.cpp:257): skip leading whitespace and <paramref name="sepsBegin"/>,
    /// then read until whitespace or a <paramref name="sepsEnd"/> character. Returns null when the
    /// remainder is empty, which is how "# Appears as ARMANDO." yields a null URL.
    /// </summary>
    private static string? GetToken(string s, ref int i, string sepsBegin, string sepsEnd)
    {
        while (i < s.Length && (char.IsWhiteSpace(s[i]) || sepsBegin.Contains(s[i]))) i++;

        if (i >= s.Length) return null;

        int start = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && !sepsEnd.Contains(s[i])) i++;

        return s[start..i];
    }

    /// <summary>
    /// The reply an "# Appears as" announcement should provoke, if any.
    /// Port of the AppearsAs branch of ProcessComment (protsupp.cpp:854-898).
    /// </summary>
    /// <param name="verb">The decoded announcement.</param>
    /// <param name="sender">The announcing peer.</param>
    /// <param name="wasChannelSend">True when the announcement went to the channel, not to us alone.</param>
    /// <param name="myAvatarName">Our own character name.</param>
    /// <param name="myAvatarUrl">Our own avatar URL, or null if we have none.</param>
    /// <returns>The body to send back PRIVATELY, or null when no reply is owed.</returns>
    /// <remarks>
    /// THE DEFERRED-URL HANDSHAKE, and why it is shaped like this:
    ///
    /// When someone joins, they announce to the channel. Every existing member must introduce
    /// themselves back, or the newcomer cannot draw them. Naively each member replies with its full
    /// "Name.URL" — so a room of N users costs N URL-bearing replies per join, and a join storm is
    /// O(N^2) in URL bytes on a 512-byte-line protocol. But the URL is only needed by peers that
    /// actually intend to DOWNLOAD the art, which is a small minority (most peers already have the
    /// character cached, or will never render it).
    ///
    /// So the reply sends <see cref="DeferredUrlString"/> ("?") in the URL slot: enough to say "I do
    /// have a URL, ask me if you want it", at a cost of one byte. The peer that genuinely needs it
    /// then sends "# GetCharInfo" privately and we answer with the real "# Appears as Name.URL"
    /// (see <see cref="OnGetCharInfo"/>). Cost moves from N^2 to N plus one round trip per actual
    /// download. Note the '?' is only sent when we HAVE a URL — a peer with no URL replies with a
    /// bare name, so "?" unambiguously means "deferred", never "absent".
    ///
    /// The reply is gated on <c>!pui-&gt;IsComicUser()</c>: we introduce ourselves only to peers we
    /// have not already met, which is what stops two clients ping-ponging announcements forever.
    /// </remarks>
    public static string? OnAppearsAs(
        ComicVerb verb,
        UserInfo sender,
        bool wasChannelSend,
        string? myAvatarName,
        string? myAvatarUrl)
    {
        if (verb.Kind != ComicVerbKind.AppearsAs) return null;
        if (sender.Ignored) return null;

        string? reply = null;

        if (!sender.IsComicUser)
        {
            if (wasChannelSend)
            {
                reply = FormatAppearsAs(
                    myAvatarName,
                    myAvatarUrl is not null ? DeferredUrlString : null);
            }

            sender.IsComicUser = true;
        }

        // Record what the peer told us. A deferred "?" is stored as-is so that a later download
        // attempt can tell "ask them" apart from "they have no art".
        sender.SetAvatarRealInfo(verb.Name, verb.Url);

        return reply;
    }

    /// <summary>
    /// The reply to "# GetCharInfo". Port of protsupp.cpp:926-939.
    /// This is the one place we send the REAL URL — see the handshake note on <see cref="OnAppearsAs"/>.
    /// </summary>
    public static string? OnGetCharInfo(UserInfo sender, string? myAvatarName, string? myAvatarUrl) =>
        sender.Ignored ? null : FormatAppearsAs(myAvatarName, myAvatarUrl);

    /// <summary>The reply to "# GetInfo". Port of protsupp.cpp:902-921.</summary>
    public static string? OnGetInfo(UserInfo sender, string? myProfile) =>
        sender.Ignored ? null : FormatHeresInfo(string.IsNullOrEmpty(myProfile) ? "No profile" : myProfile);
}
