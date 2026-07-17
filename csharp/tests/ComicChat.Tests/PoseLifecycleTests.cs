using ComicChat.Core.Avatars;
using ComicChat.Core.Semantics;
using Xunit;

namespace ComicChat.Tests;

/// <summary>
/// A pose table with distinct art per emotion, so a resolved pose's indices are meaningful.
/// </summary>
public sealed class FakePoseTable : IPoseTable
{
    private sealed record Rec(float Emotion, float Intensity, int PoseId) : IPoseRecord;

    private readonly List<IPoseRecord> _faces = [];
    private readonly List<IPoseRecord> _torsos = [];

    public IReadOnlyList<IPoseRecord> Faces => _faces;
    public IReadOnlyList<IPoseRecord> Torsos => _torsos;

    public FakePoseTable()
    {
        // A neutral plus one face per wheel spoke, and a torso per common gesture.
        _faces.Add(new Rec(Em.Neutral, 0f, 1));
        int id = 2;
        foreach (var e in new[] { Em.Happy, Em.Coy, Em.Bored, Em.Scared, Em.Sad, Em.Angry, Em.Shout, Em.Laugh })
            _faces.Add(new Rec(e, 1f, id++));

        _torsos.Add(new Rec(Em.Neutral, 0f, 100));
        foreach (var g in new[] { Em.PointOther, Em.PointSelf, Em.Wave })
            _torsos.Add(new Rec(g, 1f, id++));
        // A torso for angry/shout so gesture-less emotions can still move the body.
        _torsos.Add(new Rec(Em.Angry, 1f, id++));
    }
}

public class PoseLifecycleTests
{
    private static AvatarPoseResolver NewResolver() =>
        new(new FakePoseTable(), AvatarPoseStyle.Complex);

    [Fact]
    public void WheelResolvesADistinctPosePerEmotion()
    {
        // The core of the user's complaint: different wheel positions must give different bodies.
        var r = NewResolver();

        var neutral = r.GetBodyFromEmotion(new Emotion(0, Em.Neutral));
        var happy = r.GetBodyFromEmotion(new Emotion(1, Em.Happy));
        var sad = r.GetBodyFromEmotion(new Emotion(1, Em.Sad));
        var laugh = r.GetBodyFromEmotion(new Emotion(1, Em.Laugh));

        var faces = new[] { neutral.FaceIndex, happy.FaceIndex, sad.FaceIndex, laugh.FaceIndex };
        Assert.Equal(faces.Length, faces.Distinct().Count());
    }

    [Fact]
    public void UpdateBodyIsWhatFreezingPreserves()
    {
        // The wheel handler's job: apply the pose to the body. Before the fix it resolved a pose
        // and threw it away, so freezing had nothing to hold.
        var r = NewResolver();
        var laugh = r.GetBodyFromEmotion(new Emotion(1, Em.Laugh));

        r.UpdateBody(laugh);
        Assert.Equal(laugh.FaceIndex, r.CurrentBody.FaceIndex);
    }

    [Fact]
    public void FrozenTextAnalysisStandsDownAndTheBodyIsKept()
    {
        // Regression: a frozen avatar must KEEP its pose, not go neutral. ChatPreSendText declines
        // to vote (textpose.cpp:125), and the caller keeps CurrentBody.
        var r = NewResolver();
        var pinned = r.GetBodyFromEmotion(new Emotion(1, Em.Laugh));
        r.UpdateBody(pinned);
        r.Freeze = AvatarFreezeState.TempFrozen;

        var inferred = new TextPose().ChatPreSendText("hello there", r);

        Assert.Null(inferred);                              // the expert system stood down
        Assert.Equal(pinned.FaceIndex, r.CurrentBody.FaceIndex);   // the pose survived
    }

    [Fact]
    public void TempFreezeLastsExactlyOneLineThenAutoPosingResumes()
    {
        // ResetAvatar (avatar.cpp:454) expires a temporary freeze and returns to neutral after
        // the panel is drawn — so a wheel pose applies to one line, then text takes over again.
        var r = NewResolver();
        var pinned = r.GetBodyFromEmotion(new Emotion(1, Em.Laugh));
        r.UpdateBody(pinned);
        r.Freeze = AvatarFreezeState.TempFrozen;

        // Line 1: still frozen, pose held.
        Assert.Null(new TextPose().ChatPreSendText("first line", r));

        // Panel drawn -> ResetAvatar.
        r.ResetAvatar();

        Assert.Equal(AvatarFreezeState.Unfrozen, r.Freeze);
        // Line 2: the expert system is back in charge.
        Assert.NotNull(new TextPose().ChatPreSendText("Are you sure?", r));
    }

    [Fact]
    public void HardFreezeSurvivesResetAndKeepsThePose()
    {
        // The "Hold pose" toggle (AF_FROZEN) must persist across lines.
        var r = NewResolver();
        var pinned = r.GetBodyFromEmotion(new Emotion(1, Em.Angry));
        r.UpdateBody(pinned);
        r.Freeze = AvatarFreezeState.Frozen;

        r.ResetAvatar();
        r.ResetAvatar();

        Assert.Equal(AvatarFreezeState.Frozen, r.Freeze);
        Assert.Equal(pinned.FaceIndex, r.CurrentBody.FaceIndex);
        Assert.Null(new TextPose().ChatPreSendText("anything at all", r));
    }

    [Fact]
    public void ResetReturnsAnUnfrozenAvatarToNeutral()
    {
        var r = NewResolver();
        r.UpdateBody(r.GetBodyFromEmotion(new Emotion(1, Em.Laugh)));

        r.ResetAvatar();   // unfrozen: SetNeutral

        Assert.Equal(r.GetNeutralBody().FaceIndex, r.CurrentBody.FaceIndex);
    }
}
