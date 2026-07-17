using ComicChat.Core.Avatars;
using ComicChat.Irc;

namespace ComicChat.Tests;

/// <summary>
/// Wire-format tests for the comic annotation. These assert EXACT byte strings, because the format
/// is a byte-oriented interop contract with a 1997 client — any drift is a silent break.
/// </summary>
public class AnnotationsTests
{
    // ---- The '0'-biased single-byte encoding --------------------------------------------------

    [Theory]
    [InlineData(0, '0')]
    [InlineData(1, '1')]
    [InlineData(9, '9')]
    [InlineData(10, ':')]  // NOT "10" — one byte, 10 + '0' == 58 == ':'
    [InlineData(12, '<')]  // POINTSELF
    [InlineData(17, 'A')]  // the last emotion index
    [InlineData(-1, '/')]  // the "unset" sentinel
    public void IndexToByte_biases_by_ascii_zero(int value, char expected)
    {
        Assert.Equal((byte)expected, WireBytes.IndexToByte((sbyte)value));
        Assert.Equal((sbyte)value, WireBytes.ByteToIndex(expected));
    }

    [Fact]
    public void IndexToByte_round_trips_every_emotion_index()
    {
        for (sbyte i = 0; i < Em.EmFloats.Length; i++)
        {
            var b = WireBytes.IndexToByte(i);
            Assert.Equal(i, WireBytes.ByteToIndex((char)b));
        }
    }

    // ---- Exact encoding -----------------------------------------------------------------------

    /// <summary>
    /// The canonical example: torso idx 3 / emo 12 (POINTSELF) / int 0.9, face idx 0 / emo 10 (WAVE)
    /// / int 0.5, Requested, mode Say. Field order verified against the sprintf at protsupp.cpp:3048.
    /// </summary>
    [Fact]
    public void Encode_produces_the_exact_expected_bytes()
    {
        var a = new Annotations
        {
            GestureIndex = 3,
            GestureEmotion = 12,
            GestureIntensity = 9,
            ExpressionIndex = 0,
            ExpressionEmotion = 10,
            ExpressionIntensity = 5,
            Requested = true,
            Mode = SayMode.Say,
            TalkTos = [],
        };

        Assert.Equal("(#G3<9E0:5RM1) ", a.Encode());
    }

    [Fact]
    public void Encode_without_parens_is_the_ircx_data_form()
    {
        var a = new Annotations
        {
            GestureIndex = 3,
            GestureEmotion = 12,
            GestureIntensity = 9,
            ExpressionIndex = 0,
            ExpressionEmotion = 10,
            ExpressionIntensity = 5,
            Requested = true,
            Mode = SayMode.Say,
            TalkTos = [],
        };

        // Identical payload, no parens, NO trailing space — ProcessUDIData asserts the leading '#'.
        Assert.Equal("#G3<9E0:5RM1", a.Encode(includeParenthesis: false));
    }

    [Fact]
    public void Encode_omits_R_when_not_requested()
    {
        var a = Annotations.Reset() with
        {
            GestureIndex = 1, GestureEmotion = 1, GestureIntensity = 5,
            ExpressionIndex = 2, ExpressionEmotion = 2, ExpressionIntensity = 3,
            Requested = false, Mode = SayMode.Think,
        };

        Assert.Equal("(#G115E223M3) ", a.Encode());
    }

    [Fact]
    public void Encode_appends_comma_separated_talk_tos()
    {
        var a = Annotations.Reset() with
        {
            GestureIndex = 1, GestureEmotion = 1, GestureIntensity = 5,
            ExpressionIndex = 2, ExpressionEmotion = 2, ExpressionIntensity = 3,
            Mode = SayMode.Whisper,
            TalkTos = ["alice", "bob"],
        };

        Assert.Equal("(#G115E223M2Talice,bob) ", a.Encode());
    }

    [Fact]
    public void Encode_omits_the_T_field_entirely_when_there_are_no_addressees()
    {
        var a = Annotations.Reset() with
        {
            GestureIndex = 1, GestureEmotion = 1, GestureIntensity = 5,
            ExpressionIndex = 2, ExpressionEmotion = 2, ExpressionIntensity = 3,
            TalkTos = [],
        };

        Assert.DoesNotContain("T", a.Encode());
    }

    [Fact]
    public void Encoded_annotation_closes_with_paren_AND_space()
    {
        // ProcessSay finds ") " and skips BOTH bytes; a bare ')' is not a terminator.
        var encoded = Annotations.Reset().Encode();
        Assert.EndsWith(") ", encoded);
    }

    [Theory]
    [InlineData(SayMode.Say, '1')]
    [InlineData(SayMode.Whisper, '2')]
    [InlineData(SayMode.Think, '3')]
    [InlineData(SayMode.Shout, '4')]
    [InlineData(SayMode.Action, '5')]
    public void Mode_encodes_as_a_single_biased_byte(SayMode mode, char expected)
    {
        var encoded = (Annotations.Reset() with { Mode = mode }).Encode();
        Assert.Contains($"M{expected}", encoded);
    }

    // ---- Round trips --------------------------------------------------------------------------

    [Fact]
    public void Round_trips_every_emotion_index_and_intensity_combination()
    {
        for (sbyte gEmo = 0; gEmo < Em.EmFloats.Length; gEmo++)
        for (sbyte eEmo = 0; eEmo < Em.EmFloats.Length; eEmo++)
        for (sbyte inten = 0; inten <= 10; inten++)
        {
            var original = new Annotations
            {
                GestureIndex = 5,
                GestureEmotion = gEmo,
                GestureIntensity = inten,
                ExpressionIndex = 2,
                ExpressionEmotion = eEmo,
                ExpressionIntensity = (sbyte)(10 - inten),
                Requested = inten % 2 == 0,
                Mode = SayMode.Say,
                TalkTos = [],
            };

            var wire = original.Encode();
            Assert.True(Annotations.TryDecodeInline(wire + "body", false, out var decoded, out var text),
                $"failed to decode {wire}");

            Assert.Equal("body", text);
            Assert.Equal(original.GestureIndex, decoded.GestureIndex);
            Assert.Equal(original.GestureEmotion, decoded.GestureEmotion);
            Assert.Equal(original.GestureIntensity, decoded.GestureIntensity);
            Assert.Equal(original.ExpressionIndex, decoded.ExpressionIndex);
            Assert.Equal(original.ExpressionEmotion, decoded.ExpressionEmotion);
            Assert.Equal(original.ExpressionIntensity, decoded.ExpressionIntensity);
            Assert.Equal(original.Requested, decoded.Requested);
            Assert.Equal(original.Mode, decoded.Mode);
            Assert.True(decoded.Cooked);
        }
    }

    [Fact]
    public void Round_trips_through_the_ircx_data_form()
    {
        for (sbyte gEmo = 0; gEmo < Em.EmFloats.Length; gEmo++)
        {
            var original = Annotations.Reset() with
            {
                GestureIndex = 4, GestureEmotion = gEmo, GestureIntensity = 7,
                ExpressionIndex = 1, ExpressionEmotion = 3, ExpressionIntensity = 2,
                Mode = SayMode.Action, Requested = true,
            };

            var decoded = Annotations.DecodeData(original.Encode(includeParenthesis: false));

            Assert.Equal(original.GestureEmotion, decoded.GestureEmotion);
            Assert.Equal(original.GestureIntensity, decoded.GestureIntensity);
            Assert.Equal(original.ExpressionEmotion, decoded.ExpressionEmotion);
            Assert.Equal(SayMode.Action, decoded.Mode);
            Assert.True(decoded.Requested);
            Assert.True(decoded.Cooked);
        }
    }

    [Fact]
    public void Round_trips_talk_tos_in_both_forms()
    {
        var original = Annotations.Reset() with
        {
            GestureIndex = 1, GestureEmotion = 1, GestureIntensity = 1,
            ExpressionIndex = 1, ExpressionEmotion = 1, ExpressionIntensity = 1,
            Mode = SayMode.Say,
            TalkTos = ["alice", "bob", "carol"],
        };

        Assert.True(Annotations.TryDecodeInline(original.Encode() + "hi", false, out var inline, out _));
        Assert.Equal(new[] { "alice", "bob", "carol" }, inline.TalkTos);

        var data = Annotations.DecodeData(original.Encode(includeParenthesis: false));
        Assert.Equal(new[] { "alice", "bob", "carol" }, data.TalkTos);
    }

    // ---- Emotion float conversion -------------------------------------------------------------

    [Fact]
    public void EmotionToBytes_matches_the_original_quantisation()
    {
        // Intensity is truncated to one digit: (BYTE)(intensity * 10).
        WireBytes.EmotionToBytes(new Emotion(0.9, Em.PointSelf), out var emo, out var inten);

        Assert.Equal((byte)'<', emo);   // PointSelf is index 12 -> '<'
        Assert.Equal((byte)'9', inten); // 0.9 * 10 -> 9 -> '9'
    }

    [Fact]
    public void FromEmotions_builds_the_canonical_example()
    {
        var a = Annotations.FromEmotions(
            gestureIndex: 3,
            torso: new Emotion(0.9, Em.PointSelf),
            expressionIndex: 0,
            face: new Emotion(0.5, Em.Wave),
            requested: true,
            mode: SayMode.Say);

        Assert.Equal("(#G3<9E0:5RM1) ", a.Encode());
    }

    [Fact]
    public void Decoded_emotions_convert_back_to_floats()
    {
        Assert.True(Annotations.TryDecodeInline("(#G3<9E0:5RM1) hi", false, out var a, out _));

        Assert.Equal(Em.PointSelf, a.Gesture.EmotionValue);
        Assert.Equal(0.9f, a.Gesture.Intensity, 5);
        Assert.Equal(Em.Wave, a.Expression.EmotionValue);
        Assert.Equal(0.5f, a.Expression.Intensity, 5);
    }

    [Fact]
    public void Out_of_range_emotion_index_decodes_to_neutral()
    {
        // BytesToEmotion clamps anything >= EmFloats.Length to Neutral (avatario.cpp:84).
        var a = Annotations.Reset() with { GestureEmotion = 99 };
        Assert.Equal(Em.Neutral, a.Gesture.EmotionValue);
    }

    // ---- Decoding inbound bodies --------------------------------------------------------------

    [Fact]
    public void Decodes_a_full_inbound_line()
    {
        var msg = IrcMessage.Parse(":nick!user@host PRIVMSG #room :(#G3<9E0:5RM1) hello world");

        Assert.Equal("PRIVMSG", msg.Command);
        Assert.Equal("#room", msg.Arg(0));

        Assert.True(Annotations.TryDecodeInline(msg.Trailing!, false, out var a, out var text));

        Assert.Equal("hello world", text);
        Assert.True(a.Cooked);
        Assert.Equal(3, a.GestureIndex);
        Assert.Equal(12, a.GestureEmotion);
        Assert.Equal(9, a.GestureIntensity);
        Assert.Equal(0, a.ExpressionIndex);
        Assert.Equal(10, a.ExpressionEmotion);
        Assert.Equal(5, a.ExpressionIntensity);
        Assert.True(a.Requested);
        Assert.Equal(SayMode.Say, a.Mode);
    }

    [Fact]
    public void A_plain_mirc_message_is_not_cooked_and_keeps_its_text()
    {
        // Real capture shape from ircorig.txt — a plain mIRC user on #italia.
        var msg = IrcMessage.Parse(":Umano!-XXXX@208.163.252.20 PRIVMSG #italia :Cosa fa l'Inter");

        Assert.False(Annotations.TryDecodeInline(msg.Trailing!, false, out var a, out var text));

        Assert.Equal("Cosa fa l'Inter", text);
        Assert.False(a.Cooked); // the renderer must synthesise a gesture from the text
    }

    [Fact]
    public void An_unterminated_annotation_is_left_intact()
    {
        // Both "(#" AND ") " are required; without the terminator the body is shown verbatim.
        Assert.False(Annotations.TryDecodeInline("(#G3<9E0:5RM1 no terminator", false, out var a, out var text));
        Assert.Equal("(#G3<9E0:5RM1 no terminator", text);
        Assert.False(a.Cooked);
    }

    [Fact]
    public void A_body_that_merely_contains_a_close_token_is_not_an_annotation()
    {
        Assert.False(Annotations.TryDecodeInline("hello (#not) really", false, out _, out var text));
        Assert.Equal("hello (#not) really", text);
    }

    [Fact]
    public void Empty_text_after_the_annotation_is_allowed()
    {
        Assert.True(Annotations.TryDecodeInline("(#G115E223M1) ", false, out var a, out var text));
        Assert.Equal(string.Empty, text);
        Assert.True(a.Cooked);
    }

    [Fact]
    public void The_close_token_space_is_consumed_not_left_on_the_text()
    {
        Assert.True(Annotations.TryDecodeInline("(#G115E223M1) hello", false, out _, out var text));
        Assert.Equal("hello", text);          // not " hello"
        Assert.False(text.StartsWith(' '));
    }

    [Fact]
    public void An_unknown_mode_byte_decodes_to_say()
    {
        // SM2BM's default arm (protsupp.cpp:1017).
        Assert.True(Annotations.TryDecodeInline("(#G115E223M9) hi", false, out var a, out _));
        Assert.Equal(SayMode.Say, a.Mode);
    }

    [Fact]
    public void Known_quirk_ordinary_text_shaped_like_an_annotation_is_swallowed()
    {
        // Reproduced faithfully from the original: the "(#" + ") " sniff is the ONLY gate, so user
        // text that happens to match the shape is eaten. Every field is optional, so no field
        // parsing rejects it, and Cooked still latches because Reset() leaves the intensities at 0
        // rather than the -1 sentinel. Pinned so the behaviour is a decision, not an accident.
        Assert.True(Annotations.TryDecodeInline("(#1 fan) of this", false, out var a, out var text));
        Assert.Equal("of this", text);
        Assert.True(a.Cooked);

        // The pose is left entirely at its defaults, since no field prefix matched.
        Assert.Equal(-1, a.GestureIndex);
        Assert.Equal(-1, a.ExpressionIndex);
        Assert.Equal(SayMode.Say, a.Mode);
    }

    [Fact]
    public void A_minus_one_intensity_sentinel_suppresses_cooked()
    {
        // '/' == -1 after the bias. Cooked requires both intensities != -1 (protsupp.cpp:1605).
        Assert.False(Annotations.TryDecodeInline("(#G13/E223M1) hi", false, out var a, out _));
        Assert.Equal(-1, a.GestureIntensity);
        Assert.False(a.Cooked);
    }

    // ---- Anti-spoof ---------------------------------------------------------------------------

    [Fact]
    public void A_mode_byte_on_a_private_message_is_forced_to_whisper()
    {
        // A private message may not claim to be a public Say — it would spoof a public utterance
        // in the shared comic strip (protsupp.cpp:1585-1590).
        Assert.True(Annotations.TryDecodeInline("(#G115E223M1) psst", isPrivateMessage: true, out var a, out _));
        Assert.Equal(SayMode.Whisper, a.Mode);
        Assert.Equal(BalloonModes.Whisper, a.Modes);
    }

    [Fact]
    public void Think_on_a_private_message_is_also_forced_to_whisper()
    {
        Assert.True(Annotations.TryDecodeInline("(#G115E223M3) hmm", isPrivateMessage: true, out var a, out _));
        Assert.Equal(SayMode.Whisper, a.Mode);
    }

    [Fact]
    public void The_same_annotation_on_a_channel_message_keeps_its_mode()
    {
        Assert.True(Annotations.TryDecodeInline("(#G115E223M1) hi", isPrivateMessage: false, out var a, out _));
        Assert.Equal(SayMode.Say, a.Mode);
    }

    // ---- Mode widening ------------------------------------------------------------------------

    [Theory]
    [InlineData(SayMode.Say, BalloonModes.Say)]
    [InlineData(SayMode.Whisper, BalloonModes.Whisper)]
    [InlineData(SayMode.Think, BalloonModes.Think)]
    [InlineData(SayMode.Action, BalloonModes.Action)]
    [InlineData(SayMode.Shout, BalloonModes.Say)] // SM_SHOUT has no BM_ counterpart
    public void SM2BM_widens_the_wire_mode(SayMode sm, BalloonModes expected)
    {
        Assert.Equal(expected, WireBytes.ToBalloonModes(sm));
    }

    [Theory]
    [InlineData(BalloonModes.Action, SayMode.Action)]
    [InlineData(BalloonModes.Sound, SayMode.Action)] // Sound collapses into Action
    [InlineData(BalloonModes.Whisper, SayMode.Whisper)]
    [InlineData(BalloonModes.Think, SayMode.Think)]
    [InlineData(BalloonModes.Say, SayMode.Say)]
    public void BM2SM_narrows_back(BalloonModes bm, SayMode expected)
    {
        Assert.Equal(expected, WireBytes.ToSayMode(bm));
    }

    [Fact]
    public void Annotation_stays_within_the_buffer_the_original_allocates()
    {
        var a = Annotations.Reset() with { TalkTos = ["alice", "bob", "carol", "dave"] };
        Assert.True(a.Encode().Length < Annotations.MaxAnnotations);
    }
}
