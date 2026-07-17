using ComicChat.Core.Avatars;

namespace ComicChat.Core.Semantics;

/// <summary>
/// Comic Chat's "expert system": the thing that reads what you typed and decides what your
/// avatar does about it. Port of textpose.cpp.
/// </summary>
/// <remarks>
/// <para>
/// The whole engine is four matchers over a flat rule list. There is no parsing, no grammar and
/// no scoring — every match contributes a candidate emotion at a fixed priority, and the highest
/// priority candidate that a given avatar can actually draw wins. That's it. The apparent
/// intelligence of the original came entirely from the ladder in the rule data being well chosen.
/// </para>
/// <para>
/// This type is immutable once constructed and holds no per-message state, so it is safe to share
/// across threads. The original could not say that: it evaluated into a single file-scope
/// <c>CEmotionOpts emo</c> (textpose.cpp:117) that pose resolution then destructively consumed.
/// Here the caller owns the <see cref="EmotionOpts"/>.
/// </para>
/// </remarks>
/// <summary>
/// Which sentences <c>CheckStart</c> rules are tested against.
/// </summary>
/// <remarks>
/// This exists because the shipped product had a bug here, and "clone the original" and "do what
/// the original meant" give different answers. Rather than pick one silently, both are available
/// and the choice is explicit. See <see cref="TextPose.SentenceStarts"/> for the details of the bug.
/// </remarks>
public enum SentenceScope
{
    /// <summary>
    /// Test every sentence — what the code was written to do. "No. Hi there" waves.
    /// </summary>
    EverySentence,

    /// <summary>
    /// Test only the first sentence — what Comic Chat 2.5 actually shipped. "No. Hi there" does
    /// not wave. Choose this for bug-compatible output.
    /// </summary>
    FirstSentenceOnly,
}

public sealed class TextPose(RuleSet rules)
{
    /// <summary>Sentence terminators (textpose.cpp:85).</summary>
    private const string SentenceTerminator = ".!?";

    /// <summary>
    /// Whether CheckStart rules see every sentence or only the first.
    /// Defaults to <see cref="SentenceScope.EverySentence"/> — the intended behaviour.
    /// </summary>
    public SentenceScope SentenceScope { get; init; } = SentenceScope.EverySentence;

    /// <summary>
    /// The intensity every text-derived rule proposes. Hardcoded to 1.0 in all four call sites of
    /// GetEmotionsFromString (textpose.cpp:275-308) — the expert system has no notion of degree,
    /// only of kind. The <c>#if 0</c>'d earlier draft did vary it (LOL at .6, points at .8,
    /// textpose.cpp:52-66), and that variation was dropped when rules became data-driven.
    /// Intensity survives only as the tie-breaker in pose lookup and on the manual wheel.
    /// </summary>
    private const double RuleIntensity = 1.0;

    /// <summary>The loaded rules. Defaults to the shipped set.</summary>
    public RuleSet Rules { get; } = rules;

    public TextPose() : this(RuleSet.CreateDefault()) { }

    /// <summary>
    /// Evaluate <paramref name="text"/> into a fresh <see cref="EmotionOpts"/>.
    /// </summary>
    public EmotionOpts GetEmotionsFromString(string text)
    {
        var opts = new EmotionOpts();
        GetEmotionsFromString(text, opts);
        return opts;
    }

    /// <summary>
    /// Run every rule against <paramref name="text"/> and collect the candidates.
    /// Port of GetEmotionsFromString (textpose.cpp:268).
    /// </summary>
    /// <remarks>
    /// All four buckets run unconditionally — there is no early exit and no "best rule wins here"
    /// step. A message can and routinely does propose several emotions at once; sorting that out
    /// is pose resolution's job, not the matcher's. See
    /// <see cref="Avatars.AvatarPoseResolver.GetBodyFromEmotion(EmotionOpts)"/>.
    /// </remarks>
    public void GetEmotionsFromString(string text, EmotionOpts opts)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(opts);

        // Lowercase once up front and hand the case-insensitive rules the copy (textpose.cpp:270).
        string lower = ToLower(text);
        opts.Reset();

        // --- AllCaps ---------------------------------------------------
        // Note this alone tests the RAW text: shouting is a property of the typing, not the words.
        if (Rules.CapsStrength != 0 && CheckForUppers(text))
            opts.Add(Rules.CapsEmotion, RuleIntensity, Rules.CapsStrength);

        // --- FindString: raw substring ---------------------------------
        foreach (var unit in Rules.GeneralRules)
        {
            string haystack = unit.CaseSensitive ? text : lower;
            if (haystack.Contains(unit.Arg, StringComparison.Ordinal))
                opts.Add(unit.Emotion, RuleIntensity, unit.Strength);
        }

        // --- CheckWord: substring on a word boundary --------------------
        foreach (var unit in Rules.WordRules)
        {
            string haystack = unit.CaseSensitive ? text : lower;
            if (CheckWord(haystack, unit.Arg))
                opts.Add(unit.Emotion, RuleIntensity, unit.Strength);
        }

        // --- CheckStart: prefix of each sentence ------------------------
        foreach (int start in SentenceStarts(text, SentenceScope))
        {
            foreach (var unit in Rules.SentenceRules)
            {
                string haystack = unit.CaseSensitive ? text : lower;
                if (StartCompare2(haystack, start, unit.Arg))
                    opts.Add(unit.Emotion, RuleIntensity, unit.Strength);
            }
        }
    }

    /// <summary>
    /// The expert system's entry point: text in, a decision about the avatar's body out.
    /// Port of ChatPreSendText (textpose.cpp:119).
    /// </summary>
    /// <returns>The resolved body, or <c>null</c> if the avatar's pose is not ours to touch.</returns>
    /// <remarks>
    /// <para>
    /// The freeze check is the important line. If the user has picked a pose off the emotion wheel
    /// themselves, the avatar is frozen and the expert system does not get a vote — it bails
    /// before evaluating a single rule. A guess never overrides an instruction.
    /// </para>
    /// <para>
    /// Two deliberate divergences from the original:
    /// the shared global <c>CEmotionOpts emo</c> (textpose.cpp:117) is a local, and applying the
    /// body (the original's <c>UpdateBody</c> + rendering) is left to the caller. Note that
    /// <see cref="AvatarPoseResolver.RecordBody"/> is intentionally NOT called here: the original
    /// only records at panel layout time (panel.cpp:619), so the round-robin advances per drawn
    /// panel, not per keystroke.
    /// </para>
    /// </remarks>
    public ResolvedBody? ChatPreSendText(string text, AvatarPoseResolver? avatar)
    {
        if (avatar is null || avatar.Freeze != AvatarFreezeState.Unfrozen) return null;

        var opts = new EmotionOpts();
        GetEmotionsFromString(text, opts);
        return avatar.GetBodyFromEmotion(opts);
    }

    // ------------------------------------------------------------------
    // Matchers
    // ------------------------------------------------------------------

    /// <summary>
    /// True when the message is being shouted. Port of CheckForUppers (textpose.cpp:26).
    /// </summary>
    /// <remarks>
    /// Any lowercase letter at all disqualifies it, and a single capital isn't enough (<c>nUppers &gt; 1</c>),
    /// so "I" and "OK" behave sensibly. Digits and punctuation are ignored, which is why
    /// "HELLO EVERYONE!!!" and "WHAT?" both count.
    /// </remarks>
    public static bool CheckForUppers(string buff)
    {
        int nUppers = 0;
        foreach (char c in buff)
        {
            if (char.IsLower(c)) return false;
            if (char.IsUpper(c)) nUppers++;
        }
        return nUppers > 1;   // only bother if there's more than one upper char
    }

    /// <summary>
    /// True when <paramref name="substr"/> occurs in <paramref name="buff"/> as a whole word.
    /// Port of CheckWord (textpose.cpp:37).
    /// </summary>
    /// <remarks>
    /// "Word" here means: preceded by start-of-string or whitespace, and followed by
    /// end-of-string, whitespace or punctuation. Note the asymmetry — the leading edge accepts
    /// only whitespace while the trailing edge also accepts punctuation. That is why "lol" fires
    /// in "haha lol!" but not in "lollipop" or "(lol", and why the shipped rules can get away with
    /// needles that contain a space and an apostrophe, like <c>"don't you"</c>.
    /// </remarks>
    public static bool CheckWord(string buff, string substr)
    {
        if (substr.Length == 0) return false;

        int loc = 0;
        while ((loc = buff.IndexOf(substr, loc, StringComparison.Ordinal)) >= 0)
        {
            if (loc == 0 || char.IsWhiteSpace(buff[loc - 1]))            // that starts a word
            {
                int end = loc + substr.Length;
                if (end >= buff.Length || char.IsWhiteSpace(buff[end]) || IsPunct(buff[end]))
                    return true;                                          // that is a word
            }
            loc++;
        }
        return false;
    }

    /// <summary>
    /// True when the rule's argument is a prefix of the sentence at <paramref name="start"/> and
    /// isn't merely the head of a longer word. Port of StartCompare2 (textpose.cpp:264).
    /// </summary>
    /// <remarks>
    /// The <c>!isalnum(sent[len])</c> tail check is what stops <c>CheckStart*("I");3</c> from
    /// firing on "Interesting" while still letting it fire on "I'm" and "I." — an apostrophe is
    /// punctuation, not alphanumeric.
    /// </remarks>
    private static bool StartCompare2(string sent, int start, string substring)
    {
        int len = substring.Length;
        if (start + len > sent.Length) return false;
        if (string.CompareOrdinal(sent, start, substring, 0, len) != 0) return false;

        int after = start + len;
        return after >= sent.Length || !char.IsLetterOrDigit(sent[after]);
    }

    /// <summary>
    /// Yields the index of each sentence's first character. Port of the loop in
    /// GetEmotionsFromString (textpose.cpp:297-311) driven by GetNextSentenceStart (textpose.cpp:99).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Leading whitespace is pruned, then each subsequent start is "first character after the next
    /// run of <c>.!?</c> and whitespace". Splitting on punctuation this crudely is why
    /// "Mr. Smith" is two sentences — nobody cared, the cost of a wrong guess is one slightly odd
    /// comic panel.
    /// </para>
    /// <para>
    /// BUG vs. the original: textpose.cpp walked sentences correctly and even computed the
    /// lowercased sentence pointer (<c>char *lptr = lower + (bptr - buff);</c>, textpose.cpp:300),
    /// but then passed <c>buff</c>/<c>lower</c> — the start of the whole message — to
    /// StartCompare2 at lines 305 and 307. <c>bptr</c> and <c>lptr</c> were never used in the
    /// compare. The effect: CheckStart rules only ever tested the FIRST sentence, and re-tested it
    /// once per sentence (harmless, since Add dedupes). "No. Hi there" therefore never waved in
    /// the shipped product.
    /// </para>
    /// <para>
    /// The dead <c>lptr</c> assignment is proof of intent, so
    /// <see cref="SentenceScope.EverySentence"/> (the default) does what the code meant to do.
    /// <see cref="SentenceScope.FirstSentenceOnly"/> reproduces the shipped behaviour exactly, for
    /// callers who want bug-compatible output.
    /// </para>
    /// </remarks>
    private static IEnumerable<int> SentenceStarts(string buff, SentenceScope scope)
    {
        int i = 0;
        while (i < buff.Length && char.IsWhiteSpace(buff[i])) i++;   // prune off leading white space

        while (i < buff.Length)
        {
            yield return i;

            // The shipped product re-tested the first sentence once per sentence; since Add
            // dedupes, that is indistinguishable from testing it once and stopping.
            if (scope == SentenceScope.FirstSentenceOnly) yield break;

            int t = buff.IndexOfAny(SentenceTerminator.ToCharArray(), i);
            if (t < 0) yield break;

            int next = t;
            while (next < buff.Length && (IsPunct(buff[next]) || char.IsWhiteSpace(buff[next]))) next++;

            // The terminator itself is punctuation, so `next` is always past `t` and thus past `i`;
            // this can't stall. Belt and braces anyway -- rule text is user-editable.
            if (next <= i) yield break;
            i = next;
        }
    }

    /// <summary>C <c>ispunct</c>: printable, not alphanumeric, not space.</summary>
    private static bool IsPunct(char c) =>
        c is > ' ' and < (char)0x7F && !char.IsLetterOrDigit(c);

    /// <summary>Port of ToLower (textpose.cpp:106). Invariant casing — the original was ASCII-only.</summary>
    private static string ToLower(string buff) => buff.ToLowerInvariant();
}
