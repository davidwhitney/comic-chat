namespace ComicChat.Core.Semantics;

/// <summary>
/// The four ways a rule can match text, plus the bucket it lives in.
/// Port of the <c>stricmp</c> ladder in RegisterRule (textpose.cpp:245).
/// </summary>
/// <remarks>
/// The original stored the case-sensitivity in a separate <c>BOOL</c> on the rule and
/// selected the bucket by function name, so <c>"FindString"</c> and <c>"FindString*"</c>
/// are the same kind with a different flag. That split is preserved here:
/// <see cref="EmotionRule.CaseSensitive"/> carries the star.
/// </remarks>
public enum RuleMatcherKind
{
    /// <summary>
    /// <c>AllCaps("")</c> — the whole message is shouted. Not a string match at all: it has no
    /// argument and the original kept it in two scalars rather than a list (textpose.cpp:211).
    /// </summary>
    AllCaps,

    /// <summary>
    /// <c>FindString</c> — raw substring, no boundary check (a bare <c>strstr</c>, textpose.cpp:283).
    /// This is how the emoticons ship: <c>:)</c> and <c>:(</c> are ordinary FindString rules,
    /// there is no separate emoticon table anywhere in the original.
    /// </summary>
    FindString,

    /// <summary>
    /// <c>CheckWord</c> — substring that sits on a word boundary (CheckWord, textpose.cpp:37).
    /// </summary>
    CheckWord,

    /// <summary>
    /// <c>CheckStart</c> — prefix of a sentence (StartCompare2, textpose.cpp:264).
    /// </summary>
    CheckStart,
}

/// <summary>
/// One loaded rule: "if <see cref="Arg"/> matches, propose <see cref="Emotion"/> at
/// priority <see cref="Strength"/>". Port of STRINGUNIT (textpose.cpp:214).
/// </summary>
/// <param name="Kind">Which matcher runs this rule.</param>
/// <param name="Arg">
/// The needle. Case-insensitive rules (the <c>*</c> variants) have this pre-lowercased at load
/// time by StringUnit (textpose.cpp:238) so the hot path never has to case-fold the rule, only
/// the message. Reproduced here for the same reason: message lowercasing happens once per call.
/// </param>
/// <param name="Strength">
/// The priority. Not a match score — nothing is ever summed. It is the rung on the ladder that
/// decides which of several simultaneous matches wins a face or torso slot. The shipped ladder:
/// LAUGH(11) &gt; HAPPY/SAD/COY(10) &gt; SHOUT(9) &gt; "are you"(8) &gt; "i'm"(7) &gt;
/// "Hello"(5) &gt; "You"(4) &gt; "I"(3) &gt; "Hi"(2).
/// </param>
/// <param name="Emotion">The wheel angle or gesture sentinel to propose. See <see cref="Avatars.Em"/>.</param>
/// <param name="CaseSensitive">False for the <c>*</c> variants, which match against the lowercased message.</param>
public sealed record EmotionRule(
    RuleMatcherKind Kind,
    string Arg,
    int Strength,
    float Emotion,
    bool CaseSensitive)
{
    /// <summary>Cached <c>strlen(arg)</c> (textpose.cpp:239), used by the sentence prefix compare.</summary>
    public int Length { get; } = Arg.Length;
}
