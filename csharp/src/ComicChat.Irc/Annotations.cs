using System.Text;
using ComicChat.Core.Avatars;

namespace ComicChat.Irc;

/// <summary>
/// The "say mode" byte carried in an annotation's <c>M</c> field. Port of SM_* (defines.h:57-61).
/// </summary>
public enum SayMode : byte
{
    Say = 1,
    Whisper = 2,
    Think = 3,
    Shout = 4,
    Action = 5,
}

/// <summary>
/// The balloon-mode bitfield the client actually reasons with. Port of BM_* (defines.h:63-71).
/// </summary>
/// <remarks>
/// The wire carries the compact <see cref="SayMode"/>; the client widens it to these flags on
/// receive (SM2BM, protsupp.cpp:1006) and narrows it again on send (BM2SM, protsupp.cpp:1021).
/// The two are deliberately not 1:1 — SM_SHOUT has no BM_ counterpart and decodes to BM_SAY.
/// </remarks>
[Flags]
public enum BalloonModes : ushort
{
    None = 0,
    Say = 0x0001,
    Whisper = 0x0002,
    Think = 0x0004,
    Action = 0x0008,
    Sound = 0x0010,
    Away = 0x0020,
    HeresInfo = 0x0040,
    NoFormat = 0x0080,
    ExChan = 0x0100,
}

/// <summary>
/// Wire-format helpers shared by the annotation encoder and decoder.
/// </summary>
public static class WireBytes
{
    /// <summary>
    /// Port of IndexToByte (protsupp.cpp:994). Every numeric field in an annotation travels as a
    /// SINGLE byte biased by '0' — not as decimal text.
    /// </summary>
    /// <remarks>
    /// WHY: the annotation is prepended to the visible message body of a 512-byte IRC line, so the
    /// format is optimised for compactness and for a fixed-width parser that can just walk bytes
    /// (see the pointer-bumping reader in ProcessSay, protsupp.cpp:1567-1590). One byte per field
    /// means no delimiters and no length prefix. The bias by '0' keeps small values inside the
    /// printable ASCII range so the payload survives servers and clients that mangle control bytes.
    /// The consequence is that values above 9 are NOT digits: emotion index 12 encodes as '&lt;'
    /// (12 + 48 = 60), and the value range is capped at 207 before it leaves 8-bit territory.
    /// </remarks>
    public static byte IndexToByte(sbyte value) => unchecked((byte)(value + '0'));

    /// <summary>Port of ByteToIndex (protsupp.cpp:1000). Inverse of <see cref="IndexToByte"/>.</summary>
    /// <remarks>
    /// The original returns BYTE but every caller stores the result into a signed CHAR field, so a
    /// byte below '0' becomes negative — that is exactly how the "-1" sentinel that suppresses the
    /// Cooked flag arrives. Returning sbyte here reproduces that without a hidden conversion.
    /// </remarks>
    public static sbyte ByteToIndex(char value) => unchecked((sbyte)(value - '0'));

    /// <summary>
    /// Port of SM2BM (protsupp.cpp:1006). Note SM_SHOUT has no balloon flag and falls through to Say.
    /// </summary>
    public static BalloonModes ToBalloonModes(SayMode mode) => mode switch
    {
        SayMode.Whisper => BalloonModes.Whisper,
        SayMode.Think => BalloonModes.Think,
        SayMode.Action => BalloonModes.Action,
        _ => BalloonModes.Say,
    };

    /// <summary>Port of BM2SM (protsupp.cpp:1021). Sound collapses into Action; order matters.</summary>
    public static SayMode ToSayMode(BalloonModes modes)
    {
        if ((modes & BalloonModes.Action) != 0 || (modes & BalloonModes.Sound) != 0) return SayMode.Action;
        if ((modes & BalloonModes.Whisper) != 0) return SayMode.Whisper;
        if ((modes & BalloonModes.Think) != 0) return SayMode.Think;
        return SayMode.Say;
    }

    /// <summary>
    /// Port of EmotionToBytes (avatario.cpp:69). Returns the RAW wire bytes, already '0'-biased.
    /// </summary>
    /// <remarks>
    /// Intensity is a float in [0,1] squashed to one digit by truncation — the original's own comment
    /// on <c>(BYTE)(em.m_intensity * 10)</c> is "good 'nuff". Float (not double) arithmetic is kept
    /// because the original's emotion match is an exact float compare.
    /// </remarks>
    public static void EmotionToBytes(Emotion em, out byte emotion, out byte intensity)
    {
        var emVal = (sbyte)Em.FloatToEmotionIndex(em.EmotionValue);
        var inVal = (sbyte)(em.Intensity * 10f);
        emotion = IndexToByte(emVal);
        intensity = IndexToByte(inVal);
    }

    /// <summary>
    /// Port of BytesToEmotion (avatario.cpp:83). Takes DECODED indices, not raw wire bytes.
    /// </summary>
    /// <remarks>
    /// The original's parameters are BYTE, so a negative index from <see cref="ByteToIndex"/> wraps
    /// to a large value, trips the range check and lands on Neutral. Casting through byte here keeps
    /// that behaviour rather than throwing.
    /// </remarks>
    public static Emotion BytesToEmotion(sbyte emIndex, sbyte inIndex)
    {
        var idx = unchecked((byte)emIndex);
        float emotion = idx >= Em.EmFloats.Length ? Em.Neutral : Em.EmFloats[idx];
        return new Emotion(unchecked((byte)inIndex) / 10.0, emotion);
    }
}

/// <summary>
/// The Comic Chat wire annotation — the gesture/expression/mode payload that rides along with an
/// otherwise ordinary IRC message. Encoder is bInsertAnnotations (protsupp.cpp:3028); decoders are
/// ProcessSay (protsupp.cpp:1545) for the inline form and ProcessUDIData (protsupp.cpp:1485) for the
/// IRCX DATA form. Pure and allocation-light so it can be exhaustively unit tested.
/// </summary>
/// <remarks>
/// Grammar (sprintf at protsupp.cpp:3048):
/// <code>
///   "(#" G &lt;torsoIdx&gt;&lt;torsoEmo&gt;&lt;torsoInt&gt;
///        E &lt;faceIdx&gt;&lt;faceEmo&gt;&lt;faceInt&gt;
///        [R] M &lt;mode&gt; [T&lt;nick&gt;,&lt;nick&gt;...] ") "
/// </code>
/// Every numeric is one '0'-biased byte (see <see cref="WireBytes.IndexToByte"/>). "G" is the TORSO
/// (gesture) and "E" is the FACE (expression) — note the encoder writes torso first even though it
/// reads <c>GetIndices(faceIndex, torsoIndex, ...)</c> face-first, which is an easy field to invert.
/// The IRCX DATA form is byte-identical minus the enclosing parens and the trailing space.
/// </remarks>
public readonly record struct Annotations
{
    public const int MaxAnnotations = 256;

    public const char GesturePrefix = 'G';
    public const char ExpressionPrefix = 'E';
    public const char RequestedPrefix = 'R';
    public const char ModePrefix = 'M';
    public const char TalkToPrefix = 'T';

    /// <summary>The literal that opens an inline annotation and the sentinel ProcessSay sniffs for.</summary>
    public const string OpenToken = "(#";

    /// <summary>
    /// The literal that closes an inline annotation. The TRAILING SPACE IS PART OF THE PROTOCOL.
    /// </summary>
    /// <remarks>
    /// WHY: ProcessSay (protsupp.cpp:1604) locates the end of the annotation with
    /// <c>strstr(szStart, ") ")</c> and then does <c>szMesg = szStart + 2</c> — it skips BOTH bytes.
    /// A bare ')' is therefore not a terminator at all: the message would be treated as unannotated
    /// and the ')' plus everything before it would be shown to the user. Equally, the space is
    /// consumed as a delimiter and is NOT a leading space on the visible text, so an encoder that
    /// emits ")" and relies on the message's own leading space produces text that is off by one.
    /// </remarks>
    public const string CloseToken = ") ";

    /// <summary>Torso/body art index. -1 means "unset" (CUserDisplayInfo::Reset, userinfo.h:42).</summary>
    public sbyte GestureIndex { get; init; }

    /// <summary>Index into <see cref="Em.EmFloats"/> (0..17) for the torso.</summary>
    public sbyte GestureEmotion { get; init; }

    /// <summary>Torso intensity as a single digit; the float is <c>value / 10.0</c>.</summary>
    public sbyte GestureIntensity { get; init; }

    /// <summary>Face art index. -1 means "unset".</summary>
    public sbyte ExpressionIndex { get; init; }

    /// <summary>Index into <see cref="Em.EmFloats"/> (0..17) for the face.</summary>
    public sbyte ExpressionEmotion { get; init; }

    /// <summary>Face intensity as a single digit; the float is <c>value / 10.0</c>.</summary>
    public sbyte ExpressionIntensity { get; init; }

    /// <summary>
    /// The "R" flag — presence-only, it carries no value.
    /// </summary>
    /// <remarks>
    /// WHY: it means the sender PINNED the pose on the emotion wheel rather than letting the local
    /// text analysis pick one. The receiver needs to know this because a requested pose is authored
    /// intent and must be honoured verbatim, whereas an inferred pose may be re-derived locally.
    /// It is a bare flag because it only ever has two states, so a value byte would be wasted.
    /// </remarks>
    public bool Requested { get; init; }

    /// <summary>The "M" field. Unknown byte values decode to <see cref="SayMode.Say"/> (SM2BM's default).</summary>
    public SayMode Mode { get; init; }

    /// <summary>The "T" field: nicknames being addressed, comma separated on the wire.</summary>
    public IReadOnlyList<string> TalkTos { get; init; }

    /// <summary>
    /// True when this annotation was actually decoded off the wire, i.e. the sender is a Comic Chat
    /// client. Port of the m_bbCooked latch (protsupp.cpp:1605, 1537).
    /// </summary>
    /// <remarks>
    /// WHY IT MATTERS: a message from a plain mIRC user arrives with no annotation and is marked
    /// "not cooked". The receiving client then runs its OWN text analysis to synthesise a gesture,
    /// which is what lets non-Comic-Chat users appear as comic characters at all. So Cooked is not a
    /// diagnostic — it selects between "trust the sender's pose" and "invent one locally".
    /// The original's condition is <c>m_chGestI != -1 &amp;&amp; m_chExprI != -1</c>: the -1 sentinel
    /// is the only thing that suppresses it, so a structurally present but empty annotation still
    /// counts as cooked.
    /// </remarks>
    public bool Cooked { get; init; }

    /// <summary>An unset/neutral annotation. Mirrors CUserDisplayInfo::Reset (userinfo.h:40).</summary>
    public static Annotations Reset() => new()
    {
        GestureIndex = -1,
        ExpressionIndex = -1,
        GestureEmotion = 0,
        GestureIntensity = 0,
        ExpressionEmotion = 0,
        ExpressionIntensity = 0,
        Requested = false,
        Cooked = false,
        Mode = SayMode.Say,
        TalkTos = [],
    };

    public IReadOnlyList<string> SafeTalkTos => TalkTos ?? [];

    /// <summary>The torso emotion as a float pair.</summary>
    public Emotion Gesture => WireBytes.BytesToEmotion(GestureEmotion, GestureIntensity);

    /// <summary>The face emotion as a float pair.</summary>
    public Emotion Expression => WireBytes.BytesToEmotion(ExpressionEmotion, ExpressionIntensity);

    /// <summary>The balloon-mode flags this annotation's <see cref="Mode"/> widens to.</summary>
    public BalloonModes Modes => WireBytes.ToBalloonModes(Mode);

    /// <summary>
    /// Build an annotation from float emotions, applying the same quantisation the original does.
    /// Mirrors the EmotionToBytes calls in bInsertAnnotations (protsupp.cpp:3044-3046).
    /// </summary>
    public static Annotations FromEmotions(
        sbyte gestureIndex,
        Emotion torso,
        sbyte expressionIndex,
        Emotion face,
        bool requested = false,
        SayMode mode = SayMode.Say,
        IReadOnlyList<string>? talkTos = null)
    {
        WireBytes.EmotionToBytes(torso, out var gEmo, out var gInt);
        WireBytes.EmotionToBytes(face, out var eEmo, out var eInt);

        return new Annotations
        {
            GestureIndex = gestureIndex,
            GestureEmotion = WireBytes.ByteToIndex((char)gEmo),
            GestureIntensity = WireBytes.ByteToIndex((char)gInt),
            ExpressionIndex = expressionIndex,
            ExpressionEmotion = WireBytes.ByteToIndex((char)eEmo),
            ExpressionIntensity = WireBytes.ByteToIndex((char)eInt),
            Requested = requested,
            Mode = mode,
            TalkTos = talkTos ?? [],
            Cooked = true,
        };
    }

    /// <summary>
    /// Encode to the wire. Port of bInsertAnnotations (protsupp.cpp:3028).
    /// </summary>
    /// <param name="includeParenthesis">
    /// True for the inline PRIVMSG form <c>"(#...) "</c>; false for the IRCX <c>DATA ... CCUDI1</c>
    /// form, which is the same payload starting at '#' with no parens and no trailing space
    /// (the original passes bIncludeParenthesis=FALSE for that path and ProcessUDIData asserts '#').
    /// </param>
    public string Encode(bool includeParenthesis = true)
    {
        var sb = new StringBuilder(64);

        if (includeParenthesis) sb.Append('(');
        sb.Append('#');

        sb.Append(GesturePrefix);
        sb.Append((char)WireBytes.IndexToByte(GestureIndex));
        sb.Append((char)WireBytes.IndexToByte(GestureEmotion));
        sb.Append((char)WireBytes.IndexToByte(GestureIntensity));

        sb.Append(ExpressionPrefix);
        sb.Append((char)WireBytes.IndexToByte(ExpressionIndex));
        sb.Append((char)WireBytes.IndexToByte(ExpressionEmotion));
        sb.Append((char)WireBytes.IndexToByte(ExpressionIntensity));

        if (Requested) sb.Append(RequestedPrefix);

        sb.Append(ModePrefix);
        sb.Append((char)WireBytes.IndexToByte((sbyte)Mode));

        // The original appends the T list only when there are addressees; an empty "T" is never sent.
        var talkTos = SafeTalkTos;
        if (talkTos.Count > 0)
        {
            sb.Append(TalkToPrefix);
            // GetWhisperedAddressees (protsupp.cpp:2996) clips at the first 4 to bound the buffer.
            for (int i = 0; i < talkTos.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(talkTos[i]);
            }
        }

        if (includeParenthesis) sb.Append(CloseToken);

        return sb.ToString();
    }

    /// <summary>
    /// Decode the IRCX <c>DATA &lt;target&gt; CCUDI1 :&lt;payload&gt;</c> form. Port of ProcessUDIData
    /// (protsupp.cpp:1485). The payload starts at '#' and has no parens or trailing space.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="TryDecodeInline"/> this carries no anti-spoof mode forcing — the original
    /// does not apply it here, because DATA is an IRCX out-of-band command whose target the server
    /// itself validates.
    /// </remarks>
    public static Annotations DecodeData(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0 || payload[0] != '#')
            return Reset();

        var a = Reset();
        int i = 1;
        ParseFields(payload, ref i, ref a, terminateTalkTosAtParen: false);
        return Finish(a);
    }

    /// <summary>
    /// Try to strip and decode an inline annotation from a received message body.
    /// Port of the parenthetical block in ProcessSay (protsupp.cpp:1564-1613).
    /// </summary>
    /// <param name="body">The PRIVMSG/NOTICE trailing parameter.</param>
    /// <param name="isPrivateMessage">
    /// True when the message was addressed to us rather than to a channel. Drives the anti-spoof
    /// forcing described below.
    /// </param>
    /// <param name="annotations">The decoded annotation, or <see cref="Reset"/> when absent.</param>
    /// <param name="text">The body with the annotation removed, ready to display.</param>
    /// <returns>True when an annotation was found AND consumed.</returns>
    /// <remarks>
    /// The sniff is <c>!strncmp(szMesg,"(#",2) &amp;&amp; strstr(szMesg+2,") ")</c> — BOTH halves are
    /// required, so a message that merely opens with "(#" but never closes is left completely intact
    /// and shown verbatim.
    ///
    /// KNOWN QUIRK, reproduced faithfully: the sniff is the ONLY gate. Ordinary user text that
    /// happens to open with "(#" and contain ") " — e.g. "(#1 fan) of this" — is therefore swallowed
    /// as an annotation, and the visible text becomes "of this". Every field is optional, so nothing
    /// downstream rejects it: field parsing simply matches no prefixes, leaves the pose at its
    /// Reset() values, and Cooked still latches because Reset() leaves the intensities at 0 rather
    /// than the -1 sentinel that would suppress it. The original accepts this collision — the
    /// alternative would cost validation bytes on a 512-byte line — and so do we.
    /// </remarks>
    public static bool TryDecodeInline(
        string body,
        bool isPrivateMessage,
        out Annotations annotations,
        out string text)
    {
        ArgumentNullException.ThrowIfNull(body);

        annotations = Reset();
        text = body;

        if (!body.StartsWith(OpenToken, StringComparison.Ordinal)) return false;
        if (body.IndexOf(CloseToken, 2, StringComparison.Ordinal) < 0) return false;

        var a = Reset();
        int i = 2;
        ParseFields(body, ref i, ref a, terminateTalkTosAtParen: true, isPrivateMessage: isPrivateMessage);

        // The original re-searches for ") " from wherever field parsing stopped, so the terminator
        // must follow the fields; a ") " that appeared earlier does not count.
        int close = body.IndexOf(CloseToken, i, StringComparison.Ordinal);
        if (close < 0) return false;

        // Cooked is gated on the intensity sentinels, not on having found the terminator.
        if (a.GestureIntensity == -1 || a.ExpressionIntensity == -1)
        {
            annotations = a;
            return false;
        }

        annotations = a with { Cooked = true };
        text = body[(close + CloseToken.Length)..];
        return true;
    }

    /// <summary>Shared field walker for both wire forms. Each field is optional and order-dependent.</summary>
    private static void ParseFields(
        string s,
        ref int i,
        ref Annotations a,
        bool terminateTalkTosAtParen,
        bool isPrivateMessage = false)
    {
        // The original guards every single byte read with `if (*szTmp)`, so a truncated annotation
        // leaves the remaining fields at their Reset() values instead of running off the end.
        if (i < s.Length && s[i] == GesturePrefix)
        {
            i++;
            if (i < s.Length) a = a with { GestureIndex = WireBytes.ByteToIndex(s[i++]) };
            if (i < s.Length) a = a with { GestureEmotion = WireBytes.ByteToIndex(s[i++]) };
            if (i < s.Length) a = a with { GestureIntensity = WireBytes.ByteToIndex(s[i++]) };
        }

        if (i < s.Length && s[i] == ExpressionPrefix)
        {
            i++;
            if (i < s.Length) a = a with { ExpressionIndex = WireBytes.ByteToIndex(s[i++]) };
            if (i < s.Length) a = a with { ExpressionEmotion = WireBytes.ByteToIndex(s[i++]) };
            if (i < s.Length) a = a with { ExpressionIntensity = WireBytes.ByteToIndex(s[i++]) };
        }

        if (i < s.Length && s[i] == RequestedPrefix)
        {
            i++;
            a = a with { Requested = true };
        }

        if (i < s.Length && s[i] == ModePrefix)
        {
            i++;
            if (i < s.Length)
            {
                var raw = WireBytes.ByteToIndex(s[i++]);
                var mode = Enum.IsDefined(typeof(SayMode), unchecked((byte)raw))
                    ? (SayMode)unchecked((byte)raw)
                    : SayMode.Say;

                // ANTI-SPOOF (protsupp.cpp:1585-1590). A private message may not claim to be a Say
                // or a Think: the mode byte is attacker-controlled, and a Say balloon rendered from
                // a private message would put words in someone's mouth in the shared comic strip
                // that other users can see, i.e. a spoofed public utterance. Whisper is the only
                // mode whose rendering is honest about the message having been sent privately, so
                // the receiver overrides rather than trusts. Applied only when an M field is
                // actually present, matching the original's nesting.
                if (isPrivateMessage) mode = SayMode.Whisper;

                a = a with { Mode = mode };
            }
        }

        if (i < s.Length && s[i] == TalkToPrefix)
        {
            i++;
            a = a with { TalkTos = ParseTalkTos(s, i, terminateTalkTosAtParen) };
        }
    }

    /// <summary>
    /// Port of GetTalkTos (protsupp.cpp:1066 inline form / :1086 DATA form).
    /// </summary>
    /// <remarks>
    /// The inline form stops at ')' and splits on GetToken's default separators ",.)"; the DATA form
    /// has no closing paren and runs to end of string splitting on ','. Neither can contain '.', ','
    /// or ')' — the same separator set that constrains avatar names.
    /// </remarks>
    private static List<string> ParseTalkTos(string s, int start, bool terminateAtParen)
    {
        var result = new List<string>();
        var sb = new StringBuilder();

        for (int i = start; i <= s.Length; i++)
        {
            bool end = i == s.Length;
            char c = end ? '\0' : s[i];

            if (terminateAtParen && c == ')') end = true;

            if (end || c == ',' || c == '.')
            {
                if (sb.Length > 0) result.Add(sb.ToString());
                sb.Clear();
                if (end) break;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                // GetToken skips whitespace between tokens rather than embedding it.
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                continue;
            }

            sb.Append(c);
        }

        return result;
    }

    /// <summary>Applies the shared Cooked rule (protsupp.cpp:1536, 1605).</summary>
    private static Annotations Finish(Annotations a) =>
        a with { Cooked = a.GestureIntensity != -1 && a.ExpressionIntensity != -1 };
}
