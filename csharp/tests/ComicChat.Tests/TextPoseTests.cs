using ComicChat.Core.Avatars;
using ComicChat.Core.Semantics;

namespace ComicChat.Tests;

/// <summary>
/// Tests for the text -> emotion expert system (textpose.cpp).
/// </summary>
public class TextPoseTests
{
    private static readonly TextPose Engine = new();

    /// <summary>Priority the given emotion was proposed at, or -1 if it wasn't proposed at all.</summary>
    private static int PriorityOf(EmotionOpts opts, float emotion)
    {
        for (int i = 0; i < opts.Count; i++)
            if (opts.Emotions[i].EmotionValue == emotion)
                return opts.Priorities[i];
        return -1;
    }

    private static EmotionOpts Eval(string text) => Engine.GetEmotionsFromString(text);

    private static void AssertProposes(string text, float emotion, int priority)
    {
        var opts = Eval(text);
        Assert.Equal(priority, PriorityOf(opts, emotion));
    }

    private static void AssertDoesNotPropose(string text, float emotion) =>
        Assert.Equal(-1, PriorityOf(Eval(text), emotion));

    // ------------------------------------------------------------------
    // The shipped ladder
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("LOL")]
    [InlineData("lol")]
    [InlineData("rotfl")]
    [InlineData("ROTFL")]
    [InlineData("haha lol yes")]
    [InlineData("that's funny, hehe")]
    public void Laughter_ProposesLaughAtEleven(string text) =>
        AssertProposes(text, Em.Laugh, 11);

    [Theory]
    [InlineData(":)")]
    [InlineData("nice one :)")]
    [InlineData("well :-) indeed")]
    public void HappyEmoticon_ProposesHappyAtTen(string text) =>
        AssertProposes(text, Em.Happy, 10);

    [Theory]
    [InlineData(":(")]
    [InlineData("oh no :-(")]
    public void SadEmoticon_ProposesSadAtTen(string text) =>
        AssertProposes(text, Em.Sad, 10);

    [Theory]
    [InlineData(";)")]
    [InlineData("maybe ;-)")]
    public void CoyEmoticon_ProposesCoyAtTen(string text) =>
        AssertProposes(text, Em.Coy, 10);

    [Fact]
    public void AllCapsMessage_ProposesShoutAtNine() =>
        AssertProposes("HELLO EVERYONE", Em.Shout, 9);

    [Fact]
    public void TripleBang_ProposesShoutAtNine() =>
        AssertProposes("look at this!!!", Em.Shout, 9);

    [Fact]
    public void ImHere_ProposesPointSelfAtSeven_BeatingTheSentenceStartRule()
    {
        // "I'm here" matches BOTH CheckWord*("i'm");7 and CheckStart*("I");3. They propose the
        // same emotion, so Add collapses them and the higher priority survives.
        var opts = Eval("I'm here");
        Assert.Equal(7, PriorityOf(opts, Em.PointSelf));
        Assert.Equal(1, opts.Count);   // deduped to a single candidate, not two
    }

    [Fact]
    public void AreYouSure_ProposesPointOtherAtEight() =>
        AssertProposes("Are you sure?", Em.PointOther, 8);

    [Theory]
    [InlineData("don't you think")]
    [InlineData("will you help")]
    [InlineData("did you see that")]
    [InlineData("aren't you cold")]
    public void OtherPointOtherWordRules_ProposeEight(string text) =>
        AssertProposes(text, Em.PointOther, 8);

    [Theory]
    [InlineData("i am here")]
    [InlineData("i will go")]
    [InlineData("i'll go")]
    public void OtherPointSelfWordRules_ProposeSeven(string text) =>
        AssertProposes(text, Em.PointSelf, 7);

    [Fact]
    public void HiThere_ProposesWaveAtTwo() =>
        AssertProposes("Hi there", Em.Wave, 2);

    [Fact]
    public void Hello_ProposesWaveAtFive() =>
        AssertProposes("Hello", Em.Wave, 5);

    [Theory]
    [InlineData("Bye now", 3)]
    [InlineData("Welcome friend", 5)]
    [InlineData("Howdy", 5)]
    public void OtherWaveSentenceRules(string text, int priority) =>
        AssertProposes(text, Em.Wave, priority);

    [Fact]
    public void YouStartingASentence_ProposesPointOtherAtFour() =>
        AssertProposes("You there", Em.PointOther, 4);

    [Fact]
    public void IStartingASentence_ProposesPointSelfAtThree() =>
        AssertProposes("I went home", Em.PointSelf, 3);

    // ------------------------------------------------------------------
    // AllCaps semantics (CheckForUppers, textpose.cpp:26)
    // ------------------------------------------------------------------

    [Fact]
    public void AnyLowercaseAtAll_DisqualifiesAllCaps() =>
        AssertDoesNotPropose("HELLO EVERYONEs", Em.Shout);

    [Fact]
    public void SingleCapital_IsNotShouting() =>
        // nUppers > 1 is required, so a lone "I" does not shout.
        AssertDoesNotPropose("I", Em.Shout);

    [Fact]
    public void TwoCapitals_IsShouting() =>
        AssertProposes("OK", Em.Shout, 9);

    [Fact]
    public void PunctuationAndDigits_DoNotDisqualifyAllCaps() =>
        AssertProposes("WHAT? 42!", Em.Shout, 9);

    [Fact]
    public void EmptyString_ProposesNothing() =>
        Assert.Equal(0, Eval("").Count);

    // ------------------------------------------------------------------
    // CheckWord is word-boundary
    // ------------------------------------------------------------------

    [Fact]
    public void CheckWord_MatchesAStandaloneWord() =>
        AssertProposes("haha lol yes", Em.Laugh, 11);

    [Theory]
    [InlineData("colonel")]          // lol appears mid-word
    [InlineData("lollipop")]         // lol starts the string but doesn't end a word
    [InlineData("i want a lollipop")]
    public void CheckWord_DoesNotMatchInsideAWord(string text) =>
        AssertDoesNotPropose(text, Em.Laugh);

    [Theory]
    [InlineData("lol")]              // whole string
    [InlineData("lol!")]             // trailing punctuation is a boundary
    [InlineData("ha lol.")]
    public void CheckWord_AcceptsEndOfStringAndPunctuationAsTheTrailingBoundary(string text) =>
        AssertProposes(text, Em.Laugh, 11);

    [Fact]
    public void CheckWord_LeadingBoundaryIsWhitespaceOnly()
    {
        // Faithful to CheckWord (textpose.cpp:40): the leading edge accepts only start-of-string
        // or isspace(), NOT punctuation -- unlike the trailing edge, which also accepts punctuation.
        AssertDoesNotPropose("(lol)", Em.Laugh);
    }

    [Fact]
    public void FindString_IsNotWordBounded()
    {
        // HEHE ships as FindString*, not CheckWord*, so it fires mid-word where LOL would not.
        AssertProposes("teheheh", Em.Laugh, 11);
    }

    // ------------------------------------------------------------------
    // CheckStart is per-sentence
    // ------------------------------------------------------------------

    [Fact]
    public void CheckStart_FiresOnASecondSentence()
    {
        // See TextPose.SentenceStarts: the original passed the wrong pointer to StartCompare2
        // (textpose.cpp:305) so only the first sentence was ever tested. EverySentence — the
        // default — does what the code was written to do.
        AssertProposes("No. Hi there", Em.Wave, 2);
    }

    [Fact]
    public void CheckStart_FirstSentenceOnlyReproducesTheShippedBug()
    {
        // Comic Chat 2.5 did NOT wave at "No. Hi there". This mode exists so that behaviour is
        // reachable deliberately rather than by accident.
        var shipped = new TextPose { SentenceScope = SentenceScope.FirstSentenceOnly };
        var opts = shipped.GetEmotionsFromString("No. Hi there");

        Assert.DoesNotContain(Enumerable.Range(0, opts.Count),
            i => opts.Emotions[i].EmotionValue == Em.Wave);
    }

    [Fact]
    public void CheckStart_FirstSentenceOnlyStillFiresOnTheFirstSentence()
    {
        var shipped = new TextPose { SentenceScope = SentenceScope.FirstSentenceOnly };
        var opts = shipped.GetEmotionsFromString("Hi there. No.");

        Assert.Contains(Enumerable.Range(0, opts.Count),
            i => opts.Emotions[i].EmotionValue == Em.Wave);
    }

    [Theory]
    [InlineData("Nope! Hello you")]
    [InlineData("Really? Hello you")]
    [InlineData("Yes. Hello you")]
    public void CheckStart_SplitsOnEachSentenceTerminator(string text) =>
        AssertProposes(text, Em.Wave, 5);

    [Fact]
    public void CheckStart_DoesNotFireMidSentence() =>
        AssertDoesNotPropose("say hi there", Em.Wave);

    [Fact]
    public void CheckStart_RequiresANonAlphanumericAfterTheKeyword()
    {
        // StartCompare2's !isalnum(sent[len]) guard (textpose.cpp:265).
        AssertDoesNotPropose("Hiking is fun", Em.Wave);
        AssertDoesNotPropose("Interesting stuff", Em.PointSelf);
    }

    [Fact]
    public void CheckStart_ApostropheIsNotAlphanumericSoIFiresOnIm() =>
        AssertProposes("I'm ok", Em.PointSelf, 7);   // 7 from CheckWord*, deduped over the 3

    [Fact]
    public void CheckStart_SkipsLeadingWhitespace() =>
        AssertProposes("   Hello", Em.Wave, 5);

    [Theory]
    [InlineData("...")]
    [InlineData(".")]
    [InlineData("?!?!?!")]
    [InlineData("     ")]
    public void PunctuationOnlyInput_Terminates(string text)
    {
        var opts = Eval(text);   // the point is that this returns at all
        Assert.True(opts.Count >= 0);
    }

    // ------------------------------------------------------------------
    // Case sensitivity: the '*' variants vs the plain ones
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("LOL")]
    [InlineData("lol")]
    [InlineData("LoL")]
    [InlineData("Hehe")]
    [InlineData("HEHE")]
    public void StarVariants_AreCaseInsensitive(string text) =>
        AssertProposes(text, Em.Laugh, 11);

    [Theory]
    [InlineData("hi there")]
    [InlineData("HI THERE")]
    [InlineData("hI there")]
    public void CheckStartStar_IsCaseInsensitive(string text) =>
        AssertProposes(text, Em.Wave, 2);

    [Fact]
    public void PlainVariants_AreCaseSensitive()
    {
        // Every shipped FindString rule is plain (case-sensitive), and its arg is punctuation,
        // so prove case sensitivity with a rule of our own.
        var rules = new RuleSet();
        rules.LoadCompositeRule(Em.Happy, "FindString(\"Yay\");6");
        var engine = new TextPose(rules);

        Assert.Equal(6, PriorityOf(engine.GetEmotionsFromString("Yay"), Em.Happy));
        Assert.Equal(-1, PriorityOf(engine.GetEmotionsFromString("yay"), Em.Happy));
        Assert.Equal(-1, PriorityOf(engine.GetEmotionsFromString("YAY"), Em.Happy));
    }

    [Fact]
    public void PlainCheckWord_IsCaseSensitive()
    {
        var rules = new RuleSet();
        rules.LoadCompositeRule(Em.Laugh, "CheckWord(\"LOL\");11");
        var engine = new TextPose(rules);

        Assert.Equal(11, PriorityOf(engine.GetEmotionsFromString("say LOL now"), Em.Laugh));
        Assert.Equal(-1, PriorityOf(engine.GetEmotionsFromString("say lol now"), Em.Laugh));
    }

    [Fact]
    public void PlainCheckStart_IsCaseSensitive()
    {
        var rules = new RuleSet();
        rules.LoadCompositeRule(Em.Wave, "CheckStart(\"Hi\");2");
        var engine = new TextPose(rules);

        Assert.Equal(2, PriorityOf(engine.GetEmotionsFromString("Hi there"), Em.Wave));
        Assert.Equal(-1, PriorityOf(engine.GetEmotionsFromString("hi there"), Em.Wave));
    }

    // ------------------------------------------------------------------
    // Angry / Scared / Bored are unreachable from text
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("I am so angry")]
    [InlineData("ANGRY!!!")]
    [InlineData("grrr I hate you")]
    [InlineData("I'm scared")]
    [InlineData("that is terrifying, help")]
    [InlineData("this is boring")]
    [InlineData("Bored. Bored. Bored.")]
    [InlineData("angry scared bored")]
    [InlineData(">:( argh")]
    public void AngryScaredBored_AreNeverProducedFromText(string text)
    {
        var opts = Eval(text);
        Assert.Equal(-1, PriorityOf(opts, Em.Angry));
        Assert.Equal(-1, PriorityOf(opts, Em.Scared));
        Assert.Equal(-1, PriorityOf(opts, Em.Bored));
    }

    [Fact]
    public void ShippedRuleSet_RegistersNoRulesForAngryScaredOrBored()
    {
        var rules = RuleSet.CreateDefault();
        float[] unreachable = [Em.Angry, Em.Scared, Em.Bored];

        var all = rules.GeneralRules.Concat(rules.WordRules).Concat(rules.SentenceRules).ToList();
        Assert.DoesNotContain(all, r => unreachable.Contains(r.Emotion));
        Assert.DoesNotContain(rules.CapsEmotion, unreachable);
    }

    // ------------------------------------------------------------------
    // Multiple candidates
    // ------------------------------------------------------------------

    [Fact]
    public void MultipleRules_AllContributeCandidates()
    {
        var opts = Eval("I'm laughing LOL");
        Assert.Equal(11, PriorityOf(opts, Em.Laugh));
        Assert.Equal(7, PriorityOf(opts, Em.PointSelf));
        Assert.Equal(2, opts.Count);
    }

    [Fact]
    public void AllFourBucketsRun_WithNoEarlyExit()
    {
        // AllCaps + FindString + CheckWord + CheckStart, all from one message.
        var opts = Eval("I'M SHOUTING AT YOU!!! :)");
        Assert.Equal(9, PriorityOf(opts, Em.Shout));       // AllCaps and/or "!!!"
        Assert.Equal(10, PriorityOf(opts, Em.Happy));      // FindString ":)"
        Assert.Equal(7, PriorityOf(opts, Em.PointSelf));   // CheckWord* "i'm" + CheckStart* "I"
    }

    [Fact]
    public void IntensityIsAlwaysOne()
    {
        var opts = Eval("I'm laughing LOL :) ;) !!!");
        for (int i = 0; i < opts.Count; i++)
            Assert.Equal(1.0f, opts.Emotions[i].Intensity);
    }

    [Fact]
    public void EvaluationResetsTheSuppliedOpts()
    {
        var opts = new EmotionOpts();
        Engine.GetEmotionsFromString("LOL", opts);
        Engine.GetEmotionsFromString("hello world", opts);

        Assert.Equal(-1, PriorityOf(opts, Em.Laugh));
        Assert.Equal(5, PriorityOf(opts, Em.Wave));
    }

    [Fact]
    public void OverflowPastTenCandidates_IsSilentlyDropped()
    {
        var rules = new RuleSet();
        for (int i = 0; i < 15; i++)
            rules.LoadCompositeRule(1000.0f + i, $"FindString(\"x\");{i + 1}");

        var opts = new TextPose(rules).GetEmotionsFromString("x");
        Assert.Equal(EmotionOpts.MaxEmOpts, opts.Count);
    }
}
