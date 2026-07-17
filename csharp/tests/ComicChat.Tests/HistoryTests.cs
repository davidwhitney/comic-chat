using ComicChat.Core.Comic;
using ComicChat.Core.History;
using Xunit;

namespace ComicChat.Tests;

/// <summary>A document that just records what history asked it to do.</summary>
public sealed class FakeChatDocument : IChatDocument
{
    public List<string> Calls { get; } = [];
    public List<(string nick, string text, SayPose pose)> Lines { get; } = [];
    public int Resets { get; private set; }
    public string? BackDrop { get; private set; }

    public void ProcessLine(string nick, string text, in SayPose pose, HistoryMode mode)
    {
        Calls.Add($"say:{nick}:{mode}");
        Lines.Add((nick, text, pose));
    }

    public void Join(string nick, string? fullName, HistoryMode mode) => Calls.Add($"join:{nick}");
    public void Part(string nick, HistoryMode mode) => Calls.Add($"part:{nick}");

    public void ChangeAvatar(string nick, string avatarName, string? url, HistoryMode mode) =>
        Calls.Add($"avatar:{nick}:{avatarName}:{url}");

    public void ChangeBackDrop(string name, string? url, HistoryMode mode)
    {
        BackDrop = name;
        Calls.Add($"backdrop:{name}");
    }

    public void ChangeNick(string oldNick, string newNick, HistoryMode mode) =>
        Calls.Add($"nick:{oldNick}:{newNick}");

    public void ShowInfo(string nick, string info, HistoryMode mode) => Calls.Add($"info:{nick}");
    public void ShowComicCharacter(string nick, HistoryMode mode) => Calls.Add($"comicchar:{nick}");

    public void StartHistory(string nick, string avatarName, string title, HistoryMode mode) =>
        Calls.Add($"start:{nick}:{avatarName}:{title}");

    public void ResetComic()
    {
        Resets++;
        Calls.Add("reset");
    }
}

public class CccFormatTests
{
    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("has\ttab", @"has\ttab")]
    [InlineData("has\nnewline", @"has\nnewline")]
    [InlineData("has\r\ncrlf", @"has\r\ncrlf")]
    [InlineData(@"has\backslash", @"has\\backslash")]
    public void QuotingEscapesTheStructuralCharacters(string raw, string quoted)
    {
        // Tabs separate fields and CRLF separates records, so a message containing either
        // would corrupt the archive (QuoteReturns, histent.cpp:930).
        Assert.Equal(quoted, CccFormat.QuoteReturns(raw));
        Assert.Equal(raw, CccFormat.UnquoteReturns(quoted));
    }

    [Fact]
    public void QuotingRoundTripsAwkwardText()
    {
        var nasty = "say\t(G:1)\\r\nnot a record\r\nreally\\";
        Assert.Equal(nasty, CccFormat.UnquoteReturns(CccFormat.QuoteReturns(nasty)));
    }

    [Fact]
    public void UnquotingMalformedInputDoesNotThrow()
    {
        // A truncated archive must not take the app down.
        Assert.Equal("\\", CccFormat.UnquoteReturns("\\"));
        Assert.Equal("\\q", CccFormat.UnquoteReturns("\\q"));
    }

    [Fact]
    public void BetweenTabsTakesFieldsAndLeavesTheRemainder()
    {
        var rest = "say\tbolo\t(G:1)\tthe message";
        Assert.Equal("say", CccFormat.BetweenTabs(ref rest));
        Assert.Equal("bolo", CccFormat.BetweenTabs(ref rest));
        Assert.Equal("(G:1)", CccFormat.BetweenTabs(ref rest));
        Assert.Equal("the message", rest);
    }
}

public class HistoryEntryTests
{
    [Fact]
    public void SayEntryWritesTheShippedRecordFormat()
    {
        // histent.cpp:131 — "say\t%s\t%s\t%s\r\n" with FormatOtherArgs (histent.cpp:145).
        var pose = new SayPose
        {
            GestureIndex = 3,
            GestureEmotion = 12,
            GestureIntensity = 9,
            ExpressionIndex = 0,
            ExpressionEmotion = 10,
            ExpressionIntensity = 5,
            Requested = 1,
            Modes = BalloonMode.Say,
        };

        var record = new SayEntry("bolo", "hello world", pose).ToRecord();

        Assert.Equal("say\tbolo\t(G:3 12 9 E:0 10 5 R:1 M:1)\thello world\r\n", record);
    }

    [Fact]
    public void SayEntryRoundTrips()
    {
        var pose = new SayPose
        {
            GestureIndex = 3,
            GestureEmotion = 12,
            GestureIntensity = 9,
            ExpressionIndex = 1,
            ExpressionEmotion = 8,
            ExpressionIntensity = 6,
            Modes = BalloonMode.Whisper,
            TalkTos = ["anna", "kevin"],
        };

        var original = new SayEntry("bolo", "hi there", pose);
        var parsed = SayEntry.Parse(original.ToRecord().TrimEnd('\r', '\n'));

        Assert.Equal("bolo", parsed.Name);
        Assert.Equal("hi there", parsed.Mesg);
        Assert.Equal(3, parsed.Pose.GestureIndex);
        Assert.Equal(12, parsed.Pose.GestureEmotion);
        Assert.Equal(9, parsed.Pose.GestureIntensity);
        Assert.Equal(1, parsed.Pose.ExpressionIndex);
        Assert.Equal(BalloonMode.Whisper, parsed.Pose.Modes);
        Assert.Equal(["anna", "kevin"], parsed.Pose.TalkTos);
        Assert.True(parsed.Pose.Cooked);
    }

    [Fact]
    public void SayEntryRoundTripsAMessageContainingTabsAndNewlines()
    {
        var entry = new SayEntry("bolo", "line one\nline\ttwo", new SayPose());
        var line = entry.ToRecord().TrimEnd('\r', '\n');

        // The record must still be exactly four tab-delimited fields.
        Assert.Equal(3, line.Count(c => c == '\t'));
        Assert.Equal("line one\nline\ttwo", SayEntry.Parse(line).Mesg);
    }

    [Fact]
    public void MissingPoseFieldsLeaveDefaultsAndReadAsUncooked()
    {
        // ReadOtherArgs (histent.cpp:163) simply skips absent prefixes.
        var pose = SayEntry.ParseOtherArgs("(M:2)");

        Assert.Equal(BalloonMode.Whisper, pose.Modes);
        Assert.Equal(-1, pose.GestureIndex);
        Assert.False(pose.Cooked);      // no intensities => no usable pose
    }

    [Theory]
    [InlineData("join\tbolo\r\n", "join")]
    [InlineData("part\tbolo\r\n", "part")]
    [InlineData("nick\told\tnew\r\n", "nick")]
    [InlineData("backdrop\troom\t\r\n", "backdrop")]
    [InlineData("changeavatar\tbolo\tBolo\t\r\n", "changeavatar")]
    [InlineData("starthistory\tbolo\tBolo\tA Title\r\n", "starthistory")]
    [InlineData("comicchar\tbolo\t\r\n", "comicchar")]
    public void EveryEntryTypeRoundTripsThroughItsRecord(string record, string keyword)
    {
        Assert.Equal(keyword, CccFormat.Keyword(record));

        var entry = ChatHistory.ParseEntry(record.TrimEnd('\r', '\n'));
        Assert.NotNull(entry);
        Assert.Equal(record, entry!.ToRecord());
    }

    [Fact]
    public void EjoinIsPreservedDistinctlyFromJoin()
    {
        var entry = (JoinEntry)ChatHistory.ParseEntry("ejoin\tbolo")!;
        Assert.False(entry.TherePrior);
        Assert.Equal("ejoin\tbolo\r\n", entry.ToRecord());
    }
}

public class ChatHistoryTests
{
    private static ChatHistory NewHistory(out FakeChatDocument doc)
    {
        doc = new FakeChatDocument();
        return new ChatHistory(doc);
    }

    [Fact]
    public void AddAndExecuteRecordsAndRunsLive()
    {
        var history = NewHistory(out var doc);

        history.AddAndExecute(new JoinEntry("bolo"));
        history.AddAndExecute(new SayEntry("bolo", "hi", new SayPose()));

        Assert.Equal(2, history.Entries.Count);
        Assert.Equal(["join:bolo", "say:bolo:Live"], doc.Calls);
        Assert.True(history.Modified);
    }

    [Fact]
    public void ReloadResetsTheComicThenReplaysEverything()
    {
        // The pair from pageview.cpp:1111-1113 — the comic is rebuilt, not patched.
        var history = NewHistory(out var doc);
        history.AddAndExecute(new JoinEntry("bolo"));
        history.AddAndExecute(new SayEntry("bolo", "hi", new SayPose()));
        doc.Calls.Clear();

        history.Reload();

        Assert.Equal(1, doc.Resets);
        Assert.Equal(["reset", "join:bolo", "say:bolo:Reload"], doc.Calls);
        Assert.Equal(2, history.Entries.Count);   // replay must not duplicate history
    }

    [Fact]
    public void ReplayingIsSignalledDuringReloadAndClearedAfter()
    {
        var history = NewHistory(out _);
        bool seenReplaying = false;

        history.Add(new DelegateEntry(() => seenReplaying = history.Replaying));
        history.Reload();

        Assert.True(seenReplaying);
        Assert.False(history.Replaying);
    }

    [Fact]
    public void SaveThenLoadReproducesTheHistory()
    {
        var history = NewHistory(out _);
        history.AddAndExecute(new StartHistoryEntry("bolo", "Bolo", "My Comic"));
        history.AddAndExecute(new JoinEntry("anna", "anna!a@host"));
        history.AddAndExecute(new ChangeBackDropEntry("room", null));
        history.AddAndExecute(new SayEntry("bolo", "hello there", new SayPose
        {
            GestureIndex = 2,
            GestureEmotion = 10,
            GestureIntensity = 5,
            ExpressionIndex = 1,
            ExpressionEmotion = 1,
            ExpressionIntensity = 10,
        }));
        history.AddAndExecute(new PartEntry("anna"));

        var sw = new StringWriter();
        history.Save(sw);
        var text = sw.ToString();

        Assert.StartsWith(CccFormat.ConversationHeader, text);

        var reloaded = NewHistory(out var doc2);
        Assert.True(reloaded.Load(new StringReader(text), out var unknown));
        Assert.Empty(unknown);

        Assert.Equal(history.Entries.Count, reloaded.Entries.Count);
        Assert.Equal(
            history.Entries.Select(e => e.ToRecord()),
            reloaded.Entries.Select(e => e.ToRecord()));

        // Loading rebuilds the comic as it reads.
        Assert.Contains("say:bolo:Load", doc2.Calls);
        Assert.Equal("room", doc2.BackDrop);
        Assert.True(reloaded.Archived);
    }

    [Fact]
    public void LoadRejectsAFileWithoutTheHeader()
    {
        var history = NewHistory(out _);
        Assert.False(history.Load(new StringReader("say\tbolo\t()\thi\r\n"), out _));
    }

    [Fact]
    public void LoadReportsUnknownKeywordsInsteadOfThrowing()
    {
        // A stray record should not lose an otherwise-good archive.
        var text = CccFormat.ConversationHeader + "\r\n" +
                   "say\tbolo\t(M:1)\thello\r\n" +
                   "quantum\tfoo\r\n" +
                   "part\tbolo\r\n";

        var history = NewHistory(out _);
        Assert.True(history.Load(new StringReader(text), out var unknown));

        Assert.Equal(["quantum"], unknown);
        Assert.Equal(2, history.Entries.Count);
    }

    /// <summary>Runs an action when executed; lets a test observe replay state.</summary>
    private sealed class DelegateEntry(Action action) : HistoryEntry
    {
        public override void Execute(HistoryMode mode, IChatDocument doc) => action();
        public override void WriteSelf(TextWriter w) { }
    }
}
