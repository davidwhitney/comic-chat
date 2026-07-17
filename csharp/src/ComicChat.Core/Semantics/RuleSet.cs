using ComicChat.Core.Avatars;

namespace ComicChat.Core.Semantics;

/// <summary>
/// The loaded expert system: three rule buckets plus the AllCaps scalars.
/// Port of the file-scope lists in textpose.cpp:208-212.
/// </summary>
/// <remarks>
/// <para>
/// The original held these as four globals populated once by InitializeEmotionRules
/// (textpose.cpp:131) and torn down by DestroyEmotionRules. Here they are instance state so a
/// test — or a future per-user rule set, which the shipped UI had dialogs for
/// (IDD_RULESPAGE, chat.rc:1085) — can have its own without touching global state.
/// </para>
/// <para>
/// Rules stay in three separate buckets rather than one list because the matchers are
/// genuinely different operations, and GetEmotionsFromString runs all three unconditionally.
/// </para>
/// </remarks>
public sealed class RuleSet
{
    private readonly List<EmotionRule> _generalRules = [];
    private readonly List<EmotionRule> _wordRules = [];
    private readonly List<EmotionRule> _sentenceRules = [];

    /// <summary>Rules matched with a raw substring search.</summary>
    public IReadOnlyList<EmotionRule> GeneralRules => _generalRules;

    /// <summary>Rules matched on a word boundary.</summary>
    public IReadOnlyList<EmotionRule> WordRules => _wordRules;

    /// <summary>Rules matched against the start of each sentence.</summary>
    public IReadOnlyList<EmotionRule> SentenceRules => _sentenceRules;

    /// <summary>
    /// Priority of the AllCaps rule, or 0 when no AllCaps rule was loaded.
    /// Zero doubles as the "rule not active" test, exactly as in the original (textpose.cpp:211).
    /// </summary>
    public int CapsStrength { get; private set; }

    /// <summary>Emotion proposed by the AllCaps rule. Only meaningful when <see cref="CapsStrength"/> is non-zero.</summary>
    public float CapsEmotion { get; private set; }

    /// <summary>Total rules loaded, AllCaps included. Diagnostics only.</summary>
    public int Count => _generalRules.Count + _wordRules.Count + _sentenceRules.Count + (CapsStrength > 0 ? 1 : 0);

    // ------------------------------------------------------------------
    // Rule text loading
    // ------------------------------------------------------------------

    /// <summary>
    /// Load a '\n'-separated block of <c>Function("arg");Strength</c> entries, all proposing the
    /// same emotion. Port of LoadCompositeRule (textpose.cpp:142).
    /// </summary>
    public void LoadCompositeRule(float emotion, string rule)
    {
        int pos = 0;
        while (LoadSingleRule(emotion, rule, ref pos)) { }
    }

    /// <summary>
    /// Parse one <c>Function("arg");Strength</c> entry starting at <paramref name="pos"/>,
    /// register it, and leave <paramref name="pos"/> at the next entry.
    /// Returns false when there is nothing further to parse. Port of LoadSingleRule (textpose.cpp:173).
    /// </summary>
    /// <remarks>
    /// <para>
    /// SAFETY: the original is not safe here and this port deliberately diverges.
    /// (1) textpose.cpp:196 — <c>while (*sptr != '\n' &amp;&amp; *sptr) if (isdigit(*sptr)) *strPtr++ = *sptr++;</c>
    /// only advances the pointer when the character is a digit, so any non-digit before the
    /// newline (a strength of <c>"9x"</c>, a stray space) spins forever. We always advance and
    /// simply ignore non-digits.
    /// (2) The keyword and argument were copied with unbounded writes into <c>function[20]</c>
    /// and <c>arg[200]</c> (textpose.cpp:174), so a long rule string smashed the stack. Slices
    /// here are bounded by construction.
    /// </para>
    /// <para>
    /// Everything else is faithful, including the lenient failure mode: a malformed entry stops
    /// the load of the remainder rather than raising. Rule text came from a resource string the
    /// user could edit, so a bad rule must never take the client down.
    /// </para>
    /// </remarks>
    private bool LoadSingleRule(float emotion, string rule, ref int pos)
    {
        // proceed to start -- skip control/whitespace padding between entries
        while (pos < rule.Length && !IsPrintable(rule[pos])) pos++;
        if (pos >= rule.Length) return false;

        // parse keyword: everything up to '('
        int fnStart = pos;
        while (pos < rule.Length && rule[pos] != '(') pos++;
        if (pos >= rule.Length) return false;   // no '(' -- e.g. the empty ANGRY/SCARED/BORED rules
        string function = rule[fnStart..pos];

        pos++;   // increment past (

        // parse arg: the text between the next two double quotes (ReadString, textpose.cpp:155)
        string arg = ReadQuotedString(rule, ref pos);

        while (pos < rule.Length && rule[pos] != ';') pos++;
        if (pos >= rule.Length) return false;

        // parse strength: digits up to the newline. The original's loop is the infinite one.
        pos++;   // increment past ;
        int strength = 0;
        while (pos < rule.Length && rule[pos] != '\n')
        {
            if (char.IsAsciiDigit(rule[pos]))
                strength = strength * 10 + (rule[pos] - '0');
            pos++;   // <-- unconditional; the original only did this for digits
        }

        while (pos < rule.Length && rule[pos] == '\n') pos++;

        RegisterRule(emotion, function, arg, strength);

        return pos < rule.Length;
    }

    /// <summary>
    /// Read the text between the next two <c>"</c> characters. Port of ReadString (textpose.cpp:155),
    /// including its comment that quote escapes are not handled — none of the shipped rules need them.
    /// On a missing quote the original returned an empty arg and did not advance; same here.
    /// </summary>
    private static string ReadQuotedString(string s, ref int pos)
    {
        int first = s.IndexOf('"', pos);
        if (first < 0) return string.Empty;

        int second = s.IndexOf('"', first + 1);
        if (second < 0) return string.Empty;

        string result = s[(first + 1)..second];
        pos = second + 1;
        return result;
    }

    /// <summary>
    /// Dispatch a parsed rule into its bucket. Port of RegisterRule (textpose.cpp:245).
    /// An unrecognised function name is silently ignored, as in the original.
    /// </summary>
    public void RegisterRule(float emotion, string function, string arg, int strength)
    {
        switch (function.Trim())
        {
            case var f when Eq(f, "AllCaps"):
                CapsStrength = strength;
                CapsEmotion = emotion;
                break;
            case var f when Eq(f, "FindString"):
                _generalRules.Add(MakeRule(RuleMatcherKind.FindString, emotion, arg, strength, caseSensitive: true));
                break;
            case var f when Eq(f, "FindString*"):
                _generalRules.Add(MakeRule(RuleMatcherKind.FindString, emotion, arg, strength, caseSensitive: false));
                break;
            case var f when Eq(f, "CheckWord"):
                _wordRules.Add(MakeRule(RuleMatcherKind.CheckWord, emotion, arg, strength, caseSensitive: true));
                break;
            case var f when Eq(f, "CheckWord*"):
                _wordRules.Add(MakeRule(RuleMatcherKind.CheckWord, emotion, arg, strength, caseSensitive: false));
                break;
            case var f when Eq(f, "CheckStart"):
                _sentenceRules.Add(MakeRule(RuleMatcherKind.CheckStart, emotion, arg, strength, caseSensitive: true));
                break;
            case var f when Eq(f, "CheckStart*"):
                _sentenceRules.Add(MakeRule(RuleMatcherKind.CheckStart, emotion, arg, strength, caseSensitive: false));
                break;
        }

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Port of StringUnit (textpose.cpp:235). Note the case-insensitive variants lowercase the
    /// argument at load time so matching only ever has to lowercase the message.
    /// </summary>
    private static EmotionRule MakeRule(RuleMatcherKind kind, float emotion, string arg, int strength, bool caseSensitive) =>
        new(kind, caseSensitive ? arg : arg.ToLowerInvariant(), strength, emotion, caseSensitive);

    // ------------------------------------------------------------------
    // The shipped rule data
    // ------------------------------------------------------------------

    /// <summary>
    /// The rules Comic Chat shipped with, verbatim from the chat.rc string table (chat.rc:2290-2297
    /// and 2306-2308), paired with their emotion by the parallel arrays ruleIDs[]/ruleEMs[]
    /// (textpose.cpp:19-24). Strings are exactly what the resource compiler would hand
    /// <c>CString::LoadString</c>: RC's doubled <c>""</c> escapes resolve to single quotes.
    /// </summary>
    /// <remarks>
    /// ANGRY, SCARED and BORED ship with a rule string of <c>""</c> — two literal quote characters
    /// and no <c>(</c>, so the parser bails immediately and registers nothing. This is not an
    /// oversight in this port: those three emotions are simply <b>unreachable from text</b> in the
    /// original. They exist only on the manual emotion wheel. It's a design statement — the expert
    /// system will make you wave, laugh and shout at people, but it will never decide on your
    /// behalf that you are angry.
    /// </remarks>
    public static readonly (float Emotion, string Rule)[] ShippedRules =
    [
        (Em.Shout,      "AllCaps(\"\");9\nFindString(\"!!!\");9"),
        (Em.Laugh,      "CheckWord*(\"ROTFL\");11\nCheckWord*(\"LOL\");11\nFindString*(\"HEHE\");11"),
        (Em.Happy,      "FindString(\":)\");10\nFindString(\":-)\");10"),
        (Em.Sad,        "FindString(\":(\");10\nFindString(\":-(\");10"),
        (Em.PointOther, "CheckStart*(\"You\");4\nCheckWord*(\"are you\");8\nCheckWord*(\"will you\");8\nCheckWord*(\"did you\");8\nCheckWord*(\"aren't you\");8\nCheckWord*(\"don't you\");8"),
        (Em.PointSelf,  "CheckStart*(\"I\");3\nCheckWord*(\"i'm\");7\nCheckWord*(\"i will\");7\nCheckWord*(\"i'll\");7\nCheckWord*(\"i am\");7"),
        (Em.Wave,       "CheckStart*(\"Hi\");2\nCheckStart*(\"Bye\");3\nCheckStart*(\"Hello\");5\nCheckStart*(\"Welcome\");5\nCheckStart*(\"Howdy\");5"),
        (Em.Coy,        "FindString(\";-)\");10\nFindString(\";)\");10"),
        (Em.Angry,      "\"\""),
        (Em.Scared,     "\"\""),
        (Em.Bored,      "\"\""),
    ];

    /// <summary>
    /// Build a rule set from the shipped rule strings. Port of InitializeEmotionRules (textpose.cpp:131).
    /// </summary>
    public static RuleSet CreateDefault()
    {
        var set = new RuleSet();
        foreach (var (emotion, rule) in ShippedRules)
            set.LoadCompositeRule(emotion, rule);
        return set;
    }

    /// <summary>C <c>isprint</c>: printable ASCII including space (textpose.cpp:178).</summary>
    private static bool IsPrintable(char c) => c is >= ' ' and < (char)0x7F;
}
