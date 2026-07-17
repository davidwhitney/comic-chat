using ComicChat.Core.Avatars;
using ComicChat.Core.Semantics;

namespace ComicChat.Tests;

/// <summary>
/// Tests for rule text loading (textpose.cpp:131-262) and the shipped rule data (chat.rc:2290-2308).
/// </summary>
public class RuleSetTests
{
    // ------------------------------------------------------------------
    // The shipped data loads as expected
    // ------------------------------------------------------------------

    [Fact]
    public void DefaultRuleSet_LoadsEveryShippedRule()
    {
        var rules = RuleSet.CreateDefault();

        // FindString: "!!!", "HEHE", ":)", ":-)", ":(", ":-(", ";-)", ";)"
        Assert.Equal(8, rules.GeneralRules.Count);
        // CheckWord: ROTFL, LOL + 5 point-other phrases + 4 point-self phrases
        Assert.Equal(11, rules.WordRules.Count);
        // CheckStart: "You", "I", and 5 greetings
        Assert.Equal(7, rules.SentenceRules.Count);
        Assert.Equal(9, rules.CapsStrength);
        Assert.Equal(Em.Shout, rules.CapsEmotion);
        Assert.Equal(27, rules.Count);
    }

    [Fact]
    public void DefaultRuleSet_PutsEachRuleInTheRightBucket()
    {
        var rules = RuleSet.CreateDefault();

        Assert.All(rules.GeneralRules, r => Assert.Equal(RuleMatcherKind.FindString, r.Kind));
        Assert.All(rules.WordRules, r => Assert.Equal(RuleMatcherKind.CheckWord, r.Kind));
        Assert.All(rules.SentenceRules, r => Assert.Equal(RuleMatcherKind.CheckStart, r.Kind));
    }

    [Fact]
    public void CaseInsensitiveRules_HaveTheirArgLowercasedAtLoad()
    {
        // StringUnit (textpose.cpp:238) pre-lowercases so the hot path only folds the message.
        var rules = RuleSet.CreateDefault();
        var lol = Assert.Single(rules.WordRules, r => r.Arg == "lol");

        Assert.False(lol.CaseSensitive);   // declared as CheckWord*("ROTFL")-style, i.e. starred
        Assert.Equal(Em.Laugh, lol.Emotion);
        Assert.Equal(11, lol.Strength);
        Assert.Equal(3, lol.Length);
    }

    [Fact]
    public void CaseSensitiveRules_KeepTheirArgVerbatim()
    {
        var rules = new RuleSet();
        rules.LoadCompositeRule(Em.Happy, "FindString(\"Yay\");6");

        var rule = Assert.Single(rules.GeneralRules);
        Assert.True(rule.CaseSensitive);
        Assert.Equal("Yay", rule.Arg);
    }

    [Fact]
    public void EmoticonsAreJustFindStringRules()
    {
        // There is no separate emoticon table anywhere in the original.
        var rules = RuleSet.CreateDefault();
        string[] emoticons = [":)", ":-)", ":(", ":-(", ";)", ";-)"];

        foreach (var e in emoticons)
        {
            var rule = Assert.Single(rules.GeneralRules, r => r.Arg == e);
            Assert.Equal(10, rule.Strength);
            Assert.True(rule.CaseSensitive);
        }
    }

    [Fact]
    public void TheShippedLadder_IsWhatWeThinkItIs()
    {
        var rules = RuleSet.CreateDefault();
        int Strength(string arg) =>
            rules.GeneralRules.Concat(rules.WordRules).Concat(rules.SentenceRules)
                 .Single(r => r.Arg == arg).Strength;

        Assert.Equal(11, Strength("lol"));
        Assert.Equal(10, Strength(":)"));
        Assert.Equal(9, rules.CapsStrength);
        Assert.Equal(8, Strength("are you"));
        Assert.Equal(7, Strength("i'm"));
        Assert.Equal(5, Strength("hello"));
        Assert.Equal(4, Strength("you"));
        Assert.Equal(3, Strength("i"));
        Assert.Equal(2, Strength("hi"));
    }

    [Fact]
    public void AllCapsRule_HasNoArgumentAndLivesInScalars()
    {
        var rules = RuleSet.CreateDefault();

        Assert.Equal(9, rules.CapsStrength);
        Assert.DoesNotContain(rules.GeneralRules.Concat(rules.WordRules).Concat(rules.SentenceRules),
                              r => r.Kind == RuleMatcherKind.AllCaps);
    }

    [Fact]
    public void EmptyRuleString_RegistersNothing()
    {
        // ANGRY, SCARED and BORED ship as `""` -- two quote characters, no '(' -- which is why
        // they are unreachable from text.
        var rules = new RuleSet();
        rules.LoadCompositeRule(Em.Angry, "\"\"");

        Assert.Equal(0, rules.Count);
        Assert.Equal(0, rules.CapsStrength);
    }

    // ------------------------------------------------------------------
    // Parser: the real strings, verbatim
    // ------------------------------------------------------------------

    [Fact]
    public void Parser_HandlesAMultiEntryRuleString()
    {
        var rules = new RuleSet();
        rules.LoadCompositeRule(Em.Wave,
            "CheckStart*(\"Hi\");2\nCheckStart*(\"Bye\");3\nCheckStart*(\"Hello\");5\nCheckStart*(\"Welcome\");5\nCheckStart*(\"Howdy\");5");

        Assert.Equal(5, rules.SentenceRules.Count);
        Assert.Equal(["hi", "bye", "hello", "welcome", "howdy"], rules.SentenceRules.Select(r => r.Arg));
        Assert.Equal([2, 3, 5, 5, 5], rules.SentenceRules.Select(r => r.Strength));
    }

    [Fact]
    public void Parser_HandlesAnEmptyArgument()
    {
        var rules = new RuleSet();
        rules.LoadCompositeRule(Em.Shout, "AllCaps(\"\");9");

        Assert.Equal(9, rules.CapsStrength);
        Assert.Equal(Em.Shout, rules.CapsEmotion);
    }

    [Fact]
    public void Parser_HandlesArgumentsContainingPunctuationAndSpaces()
    {
        var rules = new RuleSet();
        rules.LoadCompositeRule(Em.PointOther, "CheckWord*(\"aren't you\");8");

        var rule = Assert.Single(rules.WordRules);
        Assert.Equal("aren't you", rule.Arg);
    }

    [Fact]
    public void Parser_IgnoresUnknownFunctions()
    {
        var rules = new RuleSet();
        rules.LoadCompositeRule(Em.Happy, "Nonsense(\"x\");5\nFindString(\"y\");6");

        Assert.Single(rules.GeneralRules);
        Assert.Equal("y", rules.GeneralRules[0].Arg);
    }

    [Fact]
    public void Parser_FunctionNamesAreCaseInsensitive()
    {
        // RegisterRule dispatches with stricmp (textpose.cpp:246).
        var rules = new RuleSet();
        rules.LoadCompositeRule(Em.Happy, "findstring(\"y\");6\nCHECKWORD*(\"z\");7");

        Assert.Single(rules.GeneralRules);
        Assert.Single(rules.WordRules);
    }

    [Fact]
    public void Parser_MultiDigitStrengthsParse()
    {
        var rules = new RuleSet();
        rules.LoadCompositeRule(Em.Laugh, "CheckWord*(\"LOL\");11");

        Assert.Equal(11, rules.WordRules[0].Strength);
    }

    // ------------------------------------------------------------------
    // Parser: malformed input must not hang (the original's infinite loop)
    // ------------------------------------------------------------------

    /// <summary>
    /// Every one of these would spin forever, smash the stack, or run off the end of the buffer in
    /// the original LoadSingleRule (textpose.cpp:173). The assertion is simply that we return.
    /// </summary>
    [Theory]
    [InlineData("FindString(\"x\");9x")]                 // <-- the infinite loop (textpose.cpp:196)
    [InlineData("FindString(\"x\");9 ")]                 // trailing space: also loops in the original
    [InlineData("FindString(\"x\"); 9")]                 // leading space before the digit
    [InlineData("FindString(\"x\");x9x\nFindString(\"y\");4")]
    [InlineData("FindString(\"x\");")]                   // no strength at all
    [InlineData("FindString(\"x\")")]                    // no semicolon
    [InlineData("FindString(\"x")]                       // unterminated quote
    [InlineData("FindString(")]                          // truncated
    [InlineData("FindString")]                           // no paren
    [InlineData("")]                                     // empty
    [InlineData("\n\n\n")]                               // newlines only
    [InlineData(";;;;")]                                 // junk
    [InlineData("\"\"")]                                 // the shipped ANGRY/SCARED/BORED string
    [InlineData("AllCaps(\"\");")]
    public async Task Parser_MalformedInputTerminates(string ruleText)
    {
        var rules = new RuleSet();

        // If this hangs, the port has reintroduced the original's bug.
        var parse = Task.Run(() => rules.LoadCompositeRule(Em.Happy, ruleText));
        var finished = await Task.WhenAny(parse, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(ReferenceEquals(finished, parse), $"Parsing hung on: {ruleText}");
        await parse;
    }

    [Fact]
    public void Parser_MalformedStrengthKeepsTheDigitsItFound()
    {
        // "9x": the original spun here forever. We take the 9 and move on.
        var rules = new RuleSet();
        rules.LoadCompositeRule(Em.Happy, "FindString(\"x\");9x");

        var rule = Assert.Single(rules.GeneralRules);
        Assert.Equal(9, rule.Strength);
    }

    [Fact]
    public void Parser_RecoversAndContinuesAfterAMalformedStrength()
    {
        var rules = new RuleSet();
        rules.LoadCompositeRule(Em.Happy, "FindString(\"x\");9x\nFindString(\"y\");4");

        Assert.Equal(2, rules.GeneralRules.Count);
        Assert.Equal(9, rules.GeneralRules[0].Strength);
        Assert.Equal(4, rules.GeneralRules[1].Strength);
    }

    [Fact]
    public void Parser_MissingStrengthIsZero()
    {
        var rules = new RuleSet();
        rules.LoadCompositeRule(Em.Happy, "FindString(\"x\");");

        // atoi("") == 0 in the original too. A zero-priority rule can never win a slot, which is
        // the same "not active" convention CapsStrength uses.
        Assert.Equal(0, Assert.Single(rules.GeneralRules).Strength);
    }

    [Fact]
    public void Parser_ToleratesAVeryLongArgument()
    {
        // The original strcpy'd into arg[200] with no bound (textpose.cpp:174).
        var rules = new RuleSet();
        string longArg = new('a', 5000);
        rules.LoadCompositeRule(Em.Happy, $"FindString(\"{longArg}\");5");

        Assert.Equal(longArg, Assert.Single(rules.GeneralRules).Arg);
    }

    [Fact]
    public void Parser_ToleratesAVeryLongFunctionName()
    {
        // ...and into function[20].
        var rules = new RuleSet();
        rules.LoadCompositeRule(Em.Happy, $"{new string('f', 5000)}(\"x\");5");

        Assert.Equal(0, rules.Count);   // unrecognised, ignored, no crash
    }
}
