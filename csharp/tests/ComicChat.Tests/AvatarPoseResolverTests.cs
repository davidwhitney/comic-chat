using ComicChat.Core.Avatars;
using ComicChat.Core.Semantics;

namespace ComicChat.Tests;

/// <summary>
/// Tests for pose selection (avatar.cpp:222-452), driven through a fake pose table.
/// </summary>
public class AvatarPoseResolverTests
{
    private sealed record FakePose(float Emotion, float Intensity, int PoseId) : IPoseRecord;

    private sealed class FakeTable(IReadOnlyList<IPoseRecord> faces, IReadOnlyList<IPoseRecord> torsos) : IPoseTable
    {
        public IReadOnlyList<IPoseRecord> Faces { get; } = faces;
        public IReadOnlyList<IPoseRecord> Torsos { get; } = torsos;
    }

    // Pose ids are chosen to be readable in failure messages.
    private const int NeutralFace = 100, HappyFace = 101, LaughFace = 102, SadFace = 103, ShoutFace = 104;
    private const int NeutralTorso = 200, WaveTorso = 201, PointSelfTorso = 202, PointOtherTorso = 203;

    /// <summary>A complex avatar with a full set of faces and gesture torsos.</summary>
    private static AvatarPoseResolver MakeComplex() => new(new FakeTable(
        faces:
        [
            new FakePose(Em.Neutral, 0.0f, NeutralFace),
            new FakePose(Em.Happy, 1.0f, HappyFace),
            new FakePose(Em.Laugh, 1.0f, LaughFace),
            new FakePose(Em.Sad, 1.0f, SadFace),
            new FakePose(Em.Shout, 1.0f, ShoutFace),
        ],
        torsos:
        [
            new FakePose(Em.Neutral, 0.0f, NeutralTorso),
            new FakePose(Em.Wave, 1.0f, WaveTorso),
            new FakePose(Em.PointSelf, 1.0f, PointSelfTorso),
            new FakePose(Em.PointOther, 1.0f, PointOtherTorso),
        ]), AvatarPoseStyle.Complex);

    private static int FacePoseId(AvatarPoseResolver r, ResolvedBody b) => r.Table.Faces[b.FaceIndex].PoseId;
    private static int TorsoPoseId(AvatarPoseResolver r, ResolvedBody b) => r.Table.Torsos[b.TorsoIndex].PoseId;

    // ------------------------------------------------------------------
    // The headline behaviour: the constraint fill composes a face and a torso
    // ------------------------------------------------------------------

    [Fact]
    public void ImLaughingLol_LaughingHeadOnAPointingBody()
    {
        // The whole point of the expert system. LAUGH(11) is an expression so it takes the face
        // and leaves the torso free; POINTSELF(7) is a gesture so it takes the torso. Neither rule
        // described this pose -- the constraint fill composed it (avatar.cpp:354).
        var resolver = MakeComplex();
        var opts = new TextPose().GetEmotionsFromString("I'm laughing LOL");

        var body = resolver.GetBodyFromEmotion(opts);

        Assert.Equal(LaughFace, FacePoseId(resolver, body));
        Assert.Equal(PointSelfTorso, TorsoPoseId(resolver, body));
    }

    [Fact]
    public void EndToEnd_HiThereWithASmile_WavesHappily()
    {
        var resolver = MakeComplex();
        var opts = new TextPose().GetEmotionsFromString("Hi there :)");

        var body = resolver.GetBodyFromEmotion(opts);

        Assert.Equal(HappyFace, FacePoseId(resolver, body));   // HAPPY(10) claims the face
        Assert.Equal(WaveTorso, TorsoPoseId(resolver, body));  // WAVE(2) claims the torso
    }

    [Fact]
    public void ExpressionOnly_LeavesTheTorsoNeutral()
    {
        var resolver = MakeComplex();
        var body = resolver.GetBodyFromEmotion(new TextPose().GetEmotionsFromString("LOL"));

        Assert.Equal(LaughFace, FacePoseId(resolver, body));
        Assert.Equal(NeutralTorso, TorsoPoseId(resolver, body));
    }

    [Fact]
    public void GestureOnly_LeavesTheFaceNeutral()
    {
        var resolver = MakeComplex();
        var body = resolver.GetBodyFromEmotion(new TextPose().GetEmotionsFromString("Hi there"));

        Assert.Equal(NeutralFace, FacePoseId(resolver, body));
        Assert.Equal(WaveTorso, TorsoPoseId(resolver, body));
    }

    [Fact]
    public void NoRulesMatch_IsNeutralOnBothSlots()
    {
        var resolver = MakeComplex();
        var body = resolver.GetBodyFromEmotion(new TextPose().GetEmotionsFromString("the weather is fine"));

        Assert.Equal(NeutralFace, FacePoseId(resolver, body));
        Assert.Equal(NeutralTorso, TorsoPoseId(resolver, body));
    }

    [Fact]
    public void HighestPriorityExpressionWinsTheFace()
    {
        // LAUGH(11) and SAD(10) both want the face; the ladder decides.
        var resolver = MakeComplex();
        var opts = new EmotionOpts();
        opts.Add(Em.Sad, 1.0, 10);
        opts.Add(Em.Laugh, 1.0, 11);

        Assert.Equal(LaughFace, FacePoseId(resolver, resolver.GetBodyFromEmotion(opts)));
    }

    [Fact]
    public void HighestPriorityGestureWinsTheTorso()
    {
        var resolver = MakeComplex();
        var opts = new EmotionOpts();
        opts.Add(Em.Wave, 1.0, 2);
        opts.Add(Em.PointOther, 1.0, 8);

        Assert.Equal(PointOtherTorso, TorsoPoseId(resolver, resolver.GetBodyFromEmotion(opts)));
    }

    [Fact]
    public void ConstraintFill_ConsumesItsInputDestructively()
    {
        // avatar.cpp:369 zeroes each entry as it is used, and that zeroing is the loop's only
        // termination condition. Preserved deliberately -- so the opts are single-use.
        var resolver = MakeComplex();
        var opts = new TextPose().GetEmotionsFromString("I'm laughing LOL");
        Assert.Equal(2, opts.Count);

        resolver.GetBodyFromEmotion(opts);

        Assert.All(Enumerable.Range(0, opts.Count), i => Assert.Equal(0, opts.Priorities[i]));
    }

    [Fact]
    public void AvatarWithNoLaughArt_FallsBackToNearestFace()
    {
        // The face search is ungated (avatar.cpp:305), unlike the torso search: some head must be
        // drawn, so the nearest one always wins even if it isn't a good answer.
        var resolver = new AvatarPoseResolver(new FakeTable(
            faces: [new FakePose(Em.Neutral, 0.0f, NeutralFace), new FakePose(Em.Shout, 1.0f, ShoutFace)],
            torsos: [new FakePose(Em.Neutral, 0.0f, NeutralTorso)]));

        var opts = new EmotionOpts();
        opts.Add(Em.Laugh, 1.0, 11);

        // Laugh (7 spokes) is one spoke from Shout (6) and one from Neutral/0 -- ties break on
        // intensity, and the laugh's intensity of 1.0 matches the shout face exactly.
        Assert.Equal(ShoutFace, FacePoseId(resolver, resolver.GetBodyFromEmotion(opts)));
    }

    // ------------------------------------------------------------------
    // Simple avatars
    // ------------------------------------------------------------------

    [Fact]
    public void SimpleAvatar_FillsOneSlotAndStops()
    {
        // CAvatarSimple takes the highest-priority option that resolves at all and breaks
        // immediately (avatar.cpp:406) -- there is no second slot for the loser to claim.
        var resolver = new AvatarPoseResolver(new FakeTable(
            faces: [],
            torsos:
            [
                new FakePose(Em.Neutral, 0.0f, NeutralTorso),
                new FakePose(Em.Laugh, 1.0f, LaughFace),
                new FakePose(Em.PointSelf, 1.0f, PointSelfTorso),
            ]), AvatarPoseStyle.Simple);

        var body = resolver.GetBodyFromEmotion(new TextPose().GetEmotionsFromString("I'm laughing LOL"));

        Assert.Equal(-1, body.FaceIndex);
        Assert.Equal(LaughFace, TorsoPoseId(resolver, body));   // LAUGH(11) won; POINTSELF(7) never got a look
    }

    [Fact]
    public void SimpleAvatar_FallsBackToNeutralWhenNothingResolves()
    {
        var resolver = new AvatarPoseResolver(new FakeTable(
            faces: [],
            torsos: [new FakePose(Em.Neutral, 0.0f, NeutralTorso)]),
            AvatarPoseStyle.Simple);

        var opts = new EmotionOpts();
        opts.Add(Em.Wave, 1.0, 5);   // no wave art at all

        Assert.Equal(NeutralTorso, TorsoPoseId(resolver, resolver.GetBodyFromEmotion(opts)));
    }

    // ------------------------------------------------------------------
    // The wheel path: nearest neighbour, gated, exact for gestures
    // ------------------------------------------------------------------

    [Fact]
    public void WheelMatching_GatedAtPiOverEight()
    {
        // A torso a full spoke away is outside the PI/NEMOTIONS catchment, so it is not offered;
        // neutral answers instead (avatar.cpp:282).
        var resolver = new AvatarPoseResolver(new FakeTable(
            faces: [new FakePose(Em.Neutral, 0.0f, NeutralFace)],
            torsos: [new FakePose(Em.Neutral, 0.0f, NeutralTorso), new FakePose(Em.Sad, 1.0f, SadFace)]));

        var body = resolver.GetBodyFromEmotion(new Emotion(1.0, Em.Angry));   // one whole spoke from Sad
        Assert.Equal(NeutralTorso, TorsoPoseId(resolver, body));
    }

    [Fact]
    public void WheelMatching_InsideTheGateIsAccepted()
    {
        var resolver = new AvatarPoseResolver(new FakeTable(
            faces: [new FakePose(Em.Neutral, 0.0f, NeutralFace)],
            torsos: [new FakePose(Em.Neutral, 0.0f, NeutralTorso), new FakePose(Em.Sad, 1.0f, SadFace)]));

        // Just inside half a spoke of Sad.
        var body = resolver.GetBodyFromEmotion(new Emotion(1.0, Em.Sad + (Math.PI / 8) * 0.9));
        Assert.Equal(SadFace, TorsoPoseId(resolver, body));
    }

    [Fact]
    public void GestureMatching_IsExactNotAngular()
    {
        // Gestures resolve by exact float equality, never by angle (avatar.cpp:317).
        var resolver = MakeComplex();

        var wave = new EmotionOpts();
        wave.Add(Em.Wave, 1.0, 9);
        Assert.Equal(WaveTorso, TorsoPoseId(resolver, resolver.GetBodyFromEmotion(wave)));

        // Shrug is numerically adjacent to Wave (1005 vs 1001) but there is no such thing as a
        // near-miss gesture: no art, no pose. It falls back to neutral.
        var shrug = new EmotionOpts();
        shrug.Add(Em.Shrug, 1.0, 9);
        Assert.Equal(NeutralTorso, TorsoPoseId(resolver, resolver.GetBodyFromEmotion(shrug)));
    }

    [Fact]
    public void WheelPath_CannotProduceAGestureTorso()
    {
        // Faithful oddity: CAvatarComplex::GetBodyFromEmotion(CEmotion&) (avatar.cpp:279) skips
        // every row with emotion > 7, so the single-emotion wheel path can only ever select
        // expression torsos. Gestures reach the torso slot exclusively via the EmotionOpts path.
        var resolver = MakeComplex();
        var body = resolver.GetBodyFromEmotion(new Emotion(1.0, Em.Wave));

        Assert.Equal(NeutralTorso, TorsoPoseId(resolver, body));
    }

    [Fact]
    public void GestureEmotions_AreSkippedByTheWheelSearch()
    {
        // The `emotion > 7` guard (avatar.cpp:279) keeps gesture torsos out of angular matching,
        // where their value of 1001 would otherwise be compared as an angle.
        var resolver = MakeComplex();
        var body = resolver.GetBodyFromEmotion(new Emotion(1.0, Em.Happy));

        Assert.Equal(NeutralTorso, TorsoPoseId(resolver, body));   // not the wave/point torsos
    }

    [Fact]
    public void RoundRobin_DoesNotRepeatTheSamePoseOnConsecutiveLines()
    {
        // Three interchangeable sad torsos. The scan starts at LastTorso + 1 (avatar.cpp:278), so
        // consecutive lines cycle rather than striking the identical pose twice -- a comic strip
        // with two identical panels looks broken.
        var resolver = new AvatarPoseResolver(new FakeTable(
            faces: [new FakePose(Em.Neutral, 0.0f, NeutralFace)],
            torsos:
            [
                new FakePose(Em.Sad, 1.0f, 300),
                new FakePose(Em.Sad, 1.0f, 301),
                new FakePose(Em.Sad, 1.0f, 302),
            ]));

        var first = resolver.GetBodyFromEmotion(new Emotion(1.0, Em.Sad));
        resolver.RecordBody(first);
        var second = resolver.GetBodyFromEmotion(new Emotion(1.0, Em.Sad));
        resolver.RecordBody(second);
        var third = resolver.GetBodyFromEmotion(new Emotion(1.0, Em.Sad));

        Assert.NotEqual(first.TorsoIndex, second.TorsoIndex);
        Assert.NotEqual(second.TorsoIndex, third.TorsoIndex);
        Assert.Equal([0, 1, 2], new[] { first.TorsoIndex, second.TorsoIndex, third.TorsoIndex });
    }

    [Fact]
    public void RoundRobin_WrapsAround()
    {
        var resolver = new AvatarPoseResolver(new FakeTable(
            faces: [new FakePose(Em.Neutral, 0.0f, NeutralFace)],
            torsos: [new FakePose(Em.Sad, 1.0f, 300), new FakePose(Em.Sad, 1.0f, 301)]));

        var indices = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            var b = resolver.GetBodyFromEmotion(new Emotion(1.0, Em.Sad));
            resolver.RecordBody(b);
            indices.Add(b.TorsoIndex);
        }

        Assert.Equal([0, 1, 0, 1], indices);
    }

    [Fact]
    public void NeutralFallback_IsAlsoRoundRobined()
    {
        // SetTorsoNeutral scans from m_lastTorso + 1 (avatar.cpp:416) for the same reason.
        var resolver = new AvatarPoseResolver(new FakeTable(
            faces: [new FakePose(Em.Neutral, 0.0f, NeutralFace)],
            torsos: [new FakePose(Em.Neutral, 0.0f, 400), new FakePose(Em.Neutral, 0.0f, 401)]));

        var first = resolver.GetNeutralBody();
        resolver.RecordBody(first);
        var second = resolver.GetNeutralBody();

        Assert.NotEqual(first.TorsoIndex, second.TorsoIndex);
    }

    [Fact]
    public void NeutralFallback_UsesRowZeroWhenTheAvatarHasNoNeutralArt()
    {
        // "Oh well, just set it to first" (avatar.cpp:425).
        var resolver = new AvatarPoseResolver(new FakeTable(
            faces: [new FakePose(Em.Shout, 1.0f, ShoutFace)],
            torsos: [new FakePose(Em.Wave, 1.0f, WaveTorso)]));

        var body = resolver.GetNeutralBody();
        Assert.Equal(0, body.FaceIndex);
        Assert.Equal(0, body.TorsoIndex);
    }

    [Fact]
    public void SimpleAvatar_NeutralIsConsideredButNeverBeatsARealMatch()
    {
        // isFirstNeutral scores 1.5, deliberately worse than any real match (avatar.cpp:237).
        var resolver = new AvatarPoseResolver(new FakeTable(
            faces: [],
            torsos: [new FakePose(Em.Neutral, 0.0f, NeutralTorso), new FakePose(Em.Sad, 1.0f, SadFace)]),
            AvatarPoseStyle.Simple);

        var body = resolver.GetBodyFromEmotion(new Emotion(1.0, Em.Sad));
        Assert.Equal(SadFace, TorsoPoseId(resolver, body));
    }

    [Fact]
    public void SimpleAvatar_WheelPathFallsBackToNeutralWhenNothingIsInRange()
    {
        var resolver = new AvatarPoseResolver(new FakeTable(
            faces: [],
            torsos: [new FakePose(Em.Neutral, 0.0f, NeutralTorso), new FakePose(Em.Sad, 1.0f, SadFace)]),
            AvatarPoseStyle.Simple);

        var body = resolver.GetBodyFromEmotion(new Emotion(1.0, Em.Angry));
        Assert.Equal(NeutralTorso, TorsoPoseId(resolver, body));
    }

    // ------------------------------------------------------------------
    // ChatPreSendText: the entry point and the freeze gate
    // ------------------------------------------------------------------

    [Fact]
    public void ChatPreSendText_ResolvesABodyForAnUnfrozenAvatar()
    {
        var resolver = MakeComplex();
        var body = new TextPose().ChatPreSendText("LOL", resolver);

        Assert.NotNull(body);
        Assert.Equal(LaughFace, FacePoseId(resolver, body.Value));
    }

    [Theory]
    [InlineData(AvatarFreezeState.Frozen)]
    [InlineData(AvatarFreezeState.TempFrozen)]
    public void ChatPreSendText_BailsWhenTheUserHasPinnedThePose(AvatarFreezeState freeze)
    {
        // textpose.cpp:125. A manual choice from the emotion wheel outranks the expert system
        // entirely -- a guess never overrides an instruction.
        var resolver = MakeComplex();
        resolver.Freeze = freeze;

        Assert.Null(new TextPose().ChatPreSendText("LOL", resolver));
    }

    [Fact]
    public void ChatPreSendText_ToleratesAnUnknownAvatar() =>
        Assert.Null(new TextPose().ChatPreSendText("LOL", null));

    [Fact]
    public void ChatPreSendText_DoesNotAdvanceTheRoundRobin()
    {
        // RecordBody happens at panel layout time (panel.cpp:619), not at selection time.
        var resolver = MakeComplex();
        new TextPose().ChatPreSendText("LOL", resolver);

        Assert.Equal(-1, resolver.LastFace);
        Assert.Equal(-1, resolver.LastTorso);
    }

    [Fact]
    public void ChatPreSendText_UsesNoSharedState()
    {
        // The original evaluated into a file-scope global CEmotionOpts (textpose.cpp:117) that
        // pose resolution then zeroed. Two avatars must not be able to see each other's message.
        var engine = new TextPose();
        var a = MakeComplex();
        var b = MakeComplex();

        var bodyA = engine.ChatPreSendText("LOL", a);
        var bodyB = engine.ChatPreSendText("Hi there", b);

        Assert.Equal(LaughFace, FacePoseId(a, bodyA!.Value));
        Assert.Equal(NeutralFace, FacePoseId(b, bodyB!.Value));
        Assert.Equal(WaveTorso, TorsoPoseId(b, bodyB.Value));
    }
}
