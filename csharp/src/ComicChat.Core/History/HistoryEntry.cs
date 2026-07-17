using System.Text;
using ComicChat.Core.Comic;

namespace ComicChat.Core.History;

/// <summary>Execute modes. Port of HM_* (histent.h:159).</summary>
[Flags]
public enum HistoryMode
{
    /// <summary>Happening now — update the UI, play sounds, scroll.</summary>
    Live = 1,

    /// <summary>Rebuilding the comic from history, e.g. after a resize. Suppress side effects.</summary>
    Reload = 2,

    /// <summary>Reading an archive from disk.</summary>
    Load = 4,
}

/// <summary>Entry type tag. Port of HT_* (histent.h:1).</summary>
public enum HistoryEntryType
{
    Unspeced = 0,
    AvatarEntry = 1,
}

/// <summary>
/// What a history entry acts on. Implemented by the session that owns the comic.
/// </summary>
/// <remarks>
/// The original's entries reached out to globals (GetChatDoc(), GetView(), theApp) from inside
/// Execute. An interface keeps replay testable and lets the same history drive a headless
/// render or a live window.
/// </remarks>
public interface IChatDocument
{
    /// <summary>Add a line to the comic. Port of CChatDoc::ProcessLine (chatdoc.cpp:447).</summary>
    void ProcessLine(string nick, string text, in SayPose pose, HistoryMode mode);

    void Join(string nick, string? fullName, HistoryMode mode);
    void Part(string nick, HistoryMode mode);

    /// <summary>Port of ChangeAvatarEntry::Execute — the character a nick appears as.</summary>
    void ChangeAvatar(string nick, string avatarName, string? avatarUrl, HistoryMode mode);

    void ChangeBackDrop(string backdropName, string? backdropUrl, HistoryMode mode);
    void ChangeNick(string oldNick, string newNick, HistoryMode mode);
    void ShowInfo(string nick, string info, HistoryMode mode);
    void ShowComicCharacter(string nick, HistoryMode mode);

    /// <summary>Port of StartHistoryEntry::Execute — sets our nick, title and character.</summary>
    void StartHistory(string nick, string avatarName, string title, HistoryMode mode);

    /// <summary>Discard the comic so history can be replayed into it. Port of ResetExistingPanels (pageview.cpp:1111).</summary>
    void ResetComic();
}

/// <summary>One recorded event. Port of HistoryEntry (histent.h:4).</summary>
/// <remarks>
/// This is a command pattern: the comic is never the source of truth, the history is. Every
/// panel you see is a pure function of this list plus the current panel size, which is what
/// lets the whole strip be rebuilt on resize and written to an archive.
/// </remarks>
public abstract class HistoryEntry
{
    public abstract void Execute(HistoryMode mode, IChatDocument doc);

    /// <summary>Serialise one <c>.ccc</c> record, including its CRLF.</summary>
    public abstract void WriteSelf(TextWriter w);

    public virtual HistoryEntryType GetEntryType() => HistoryEntryType.Unspeced;

    /// <summary>Convenience for tests and diffing.</summary>
    public string ToRecord()
    {
        var sw = new StringWriter { NewLine = CccFormat.LineEnding };
        WriteSelf(sw);
        return sw.ToString();
    }
}

/// <summary>Someone said something. Port of SayEntry (histent.h:14).</summary>
public sealed class SayEntry : HistoryEntry
{
    public string Name { get; }
    public string Mesg { get; }
    public SayPose Pose;

    public SayEntry(string nick, string mesg, SayPose pose)
    {
        Name = nick;
        Mesg = mesg;
        Pose = pose;
    }

    public override void Execute(HistoryMode mode, IChatDocument doc) =>
        doc.ProcessLine(Name, Mesg, Pose, mode);

    /// <summary>Port of SayEntry::WriteSelf (histent.cpp:110).</summary>
    public override void WriteSelf(TextWriter w) =>
        w.Write($"say\t{Name}\t{FormatOtherArgs()}\t{CccFormat.QuoteReturns(Mesg)}{CccFormat.LineEnding}");

    /// <summary>
    /// Port of SayEntry::FormatOtherArgs (histent.cpp:143).
    /// </summary>
    /// <remarks>
    /// Note this is the <i>archive</i> encoding: plain decimal, and R carries a value. The wire
    /// encoding of the same data is different — see <see cref="SayPose"/>.
    /// </remarks>
    public string FormatOtherArgs()
    {
        var sb = new StringBuilder();
        sb.Append($"(G:{Pose.GestureIndex} {Pose.GestureEmotion} {Pose.GestureIntensity}");
        sb.Append($" E:{Pose.ExpressionIndex} {Pose.ExpressionEmotion} {Pose.ExpressionIntensity}");
        sb.Append($" R:{Pose.Requested} M:{(int)BalloonModeMap.ToSendMode(Pose.Modes)}");

        if (Pose.TalkTos.Count > 0)
            sb.Append(" T:").Append(string.Join(',', Pose.TalkTos));

        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>Port of SayEntry::SayEntry(CString&amp;) (histent.cpp) + ReadOtherArgs (histent.cpp:163).</summary>
    public static SayEntry Parse(string line)
    {
        var rest = line;
        CccFormat.BetweenTabs(ref rest);                      // keyword
        var name = CccFormat.BetweenTabs(ref rest);
        var args = CccFormat.BetweenTabs(ref rest);
        var mesg = CccFormat.UnquoteReturns(rest);

        return new SayEntry(name, mesg, ParseOtherArgs(args));
    }

    /// <summary>
    /// Parse the parenthesised pose. Port of ReadOtherArgs (histent.cpp:163).
    /// </summary>
    /// <remarks>
    /// Every field is optional and the original simply leaves the default in place when a
    /// prefix is missing, so a hand-written or truncated archive still loads.
    /// </remarks>
    public static SayPose ParseOtherArgs(string args)
    {
        var pose = new SayPose();
        if (!args.StartsWith('(')) return pose;

        int end = args.IndexOf(')');
        var body = end < 0 ? args[1..] : args[1..end];

        foreach (var field in body.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                  .Aggregate(new List<string>(), Regroup))
        {
            int colon = field.IndexOf(':');
            if (colon < 0) continue;

            var key = field[..colon];
            var val = field[(colon + 1)..];
            var nums = val.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            switch (key)
            {
                case "G":
                    if (nums.Length > 0) pose.GestureIndex = Atoi(nums[0]);
                    if (nums.Length > 1) pose.GestureEmotion = Atoi(nums[1]);
                    if (nums.Length > 2) pose.GestureIntensity = Atoi(nums[2]);
                    break;
                case "E":
                    if (nums.Length > 0) pose.ExpressionIndex = Atoi(nums[0]);
                    if (nums.Length > 1) pose.ExpressionEmotion = Atoi(nums[1]);
                    if (nums.Length > 2) pose.ExpressionIntensity = Atoi(nums[2]);
                    break;
                case "R":
                    if (nums.Length > 0) pose.Requested = Atoi(nums[0]);
                    break;
                case "M":
                    if (nums.Length > 0) pose.Modes = BalloonModeMap.FromSendMode((SendMode)Atoi(nums[0]));
                    break;
                case "T":
                    pose.TalkTos = [.. val.Split(',', StringSplitOptions.RemoveEmptyEntries)];
                    break;
            }
        }

        // Matches the original's m_bbCooked test: both intensities present (protsupp.cpp:1510).
        pose.Cooked = pose.HasPose;
        return pose;
    }

    /// <summary>Regroup "G:1 2 3" back together after splitting on spaces.</summary>
    private static List<string> Regroup(List<string> acc, string token)
    {
        if (token.Contains(':')) acc.Add(token);
        else if (acc.Count > 0) acc[^1] += " " + token;
        return acc;
    }

    /// <summary>C's atoi: a non-numeric string is 0, never an exception.</summary>
    private static int Atoi(string s) => int.TryParse(s, out int v) ? v : 0;
}

/// <summary>Maps between the balloon bitmask and the ordinal the archive stores.</summary>
/// <remarks>Port of BM2SM / SM2BM (protsupp.cpp:1006, 1022).</remarks>
public static class BalloonModeMap
{
    public static SendMode ToSendMode(BalloonMode m)
    {
        if ((m & BalloonMode.Whisper) != 0) return SendMode.Whisper;
        if ((m & BalloonMode.Think) != 0) return SendMode.Think;
        // BM_SOUND and BM_ACTION both collapse to SM_ACTION.
        if ((m & (BalloonMode.Action | BalloonMode.Sound)) != 0) return SendMode.Action;
        return SendMode.Say;
    }

    /// <summary>Unknown ordinals fall back to Say, as SM2BM does.</summary>
    public static BalloonMode FromSendMode(SendMode m) => m switch
    {
        SendMode.Whisper => BalloonMode.Whisper,
        SendMode.Think => BalloonMode.Think,
        SendMode.Shout => BalloonMode.Say,
        SendMode.Action => BalloonMode.Action,
        _ => BalloonMode.Say,
    };
}

/// <summary>Someone joined. Port of JoinEntry (histent.h:34).</summary>
public sealed class JoinEntry(string nick, string? fullName = null, bool therePrior = true) : HistoryEntry
{
    public string Name { get; } = nick;
    public string? FullName { get; } = fullName;

    /// <summary>False when they joined after us, which the original renders differently ("ejoin").</summary>
    public bool TherePrior { get; } = therePrior;

    public override void Execute(HistoryMode mode, IChatDocument doc) => doc.Join(Name, FullName, mode);

    /// <summary>Port of JoinEntry::WriteSelf (histent.cpp:290).</summary>
    public override void WriteSelf(TextWriter w)
    {
        var op = TherePrior ? "join" : "ejoin";
        w.Write(string.IsNullOrEmpty(FullName)
            ? $"{op}\t{Name}{CccFormat.LineEnding}"
            : $"{op}\t{Name}\t{FullName}{CccFormat.LineEnding}");
    }

    public static JoinEntry Parse(string line)
    {
        var rest = line;
        var op = CccFormat.BetweenTabs(ref rest);
        var name = CccFormat.BetweenTabs(ref rest);
        var full = rest.Length > 0 ? rest : null;
        return new JoinEntry(name, full, !op.Equals("ejoin", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Someone left. Port of PartEntry (histent.h:51).</summary>
public sealed class PartEntry(string nick) : HistoryEntry
{
    public string Name { get; } = nick;

    public override void Execute(HistoryMode mode, IChatDocument doc) => doc.Part(Name, mode);

    public override void WriteSelf(TextWriter w) => w.Write($"part\t{Name}{CccFormat.LineEnding}");

    public static PartEntry Parse(string line)
    {
        var rest = line;
        CccFormat.BetweenTabs(ref rest);
        return new PartEntry(CccFormat.BetweenTabs(ref rest));
    }
}

/// <summary>Someone changed character. Port of ChangeAvatarEntry (histent.h:69).</summary>
public sealed class ChangeAvatarEntry(string nick, string avatarName, string? avatarUrl) : HistoryEntry
{
    public string Name { get; } = nick;
    public string AvatarName { get; } = avatarName;
    public string? AvatarUrl { get; } = avatarUrl;

    public override HistoryEntryType GetEntryType() => HistoryEntryType.AvatarEntry;

    public override void Execute(HistoryMode mode, IChatDocument doc) =>
        doc.ChangeAvatar(Name, AvatarName, AvatarUrl, mode);

    /// <summary>Port of ChangeAvatarEntry::WriteSelf (histent.cpp:430).</summary>
    public override void WriteSelf(TextWriter w) =>
        w.Write($"changeavatar\t{Name}\t{AvatarName}\t{AvatarUrl ?? ""}{CccFormat.LineEnding}");

    public static ChangeAvatarEntry Parse(string line)
    {
        var rest = line;
        CccFormat.BetweenTabs(ref rest);
        var name = CccFormat.BetweenTabs(ref rest);
        var avName = CccFormat.BetweenTabs(ref rest);
        var avUrl = CccFormat.BetweenTabs(ref rest);
        return new ChangeAvatarEntry(name, avName, string.IsNullOrEmpty(avUrl) ? null : avUrl);
    }
}

/// <summary>The room's backdrop changed. Port of ChangeBackDropEntry (histent.h:128).</summary>
public sealed class ChangeBackDropEntry(string backName, string? backUrl) : HistoryEntry
{
    public string BackName { get; } = backName;
    public string? BackUrl { get; } = backUrl;

    public override void Execute(HistoryMode mode, IChatDocument doc) =>
        doc.ChangeBackDrop(BackName, BackUrl, mode);

    public override void WriteSelf(TextWriter w) =>
        w.Write($"backdrop\t{BackName}\t{BackUrl ?? ""}{CccFormat.LineEnding}");

    public static ChangeBackDropEntry Parse(string line)
    {
        var rest = line;
        CccFormat.BetweenTabs(ref rest);
        var name = CccFormat.BetweenTabs(ref rest);
        var url = CccFormat.BetweenTabs(ref rest);
        return new ChangeBackDropEntry(name, string.IsNullOrEmpty(url) ? null : url);
    }
}

/// <summary>Someone renamed. Port of NickEntry (histent.h:142).</summary>
public sealed class NickEntry(string oldNick, string newNick) : HistoryEntry
{
    public string OldNick { get; } = oldNick;
    public string NewNick { get; } = newNick;

    public override void Execute(HistoryMode mode, IChatDocument doc) =>
        doc.ChangeNick(OldNick, NewNick, mode);

    public override void WriteSelf(TextWriter w) =>
        w.Write($"nick\t{OldNick}\t{NewNick}{CccFormat.LineEnding}");

    public static NickEntry Parse(string line)
    {
        var rest = line;
        CccFormat.BetweenTabs(ref rest);
        var oldN = CccFormat.BetweenTabs(ref rest);
        var newN = CccFormat.BetweenTabs(ref rest);
        return new NickEntry(oldN, newN);
    }
}

/// <summary>A profile reply. Port of GetInfoEntry (histent.h:87).</summary>
public class GetInfoEntry(string nick, string info) : HistoryEntry
{
    public string Name { get; } = nick;
    public string Info { get; } = info;

    public override void Execute(HistoryMode mode, IChatDocument doc) => doc.ShowInfo(Name, Info, mode);

    public override void WriteSelf(TextWriter w) =>
        w.Write($"getinfo\t{Name}\t{Info}{CccFormat.LineEnding}");

    public static GetInfoEntry Parse(string line)
    {
        var rest = line;
        CccFormat.BetweenTabs(ref rest);
        var name = CccFormat.BetweenTabs(ref rest);
        return new GetInfoEntry(name, rest);
    }
}

/// <summary>A "show me their character" reply. Port of ComicCharacterEntry (histent.h:102).</summary>
public sealed class ComicCharacterEntry(string nick) : GetInfoEntry(nick, string.Empty)
{
    public override void Execute(HistoryMode mode, IChatDocument doc) => doc.ShowComicCharacter(Name, mode);

    public override void WriteSelf(TextWriter w) =>
        w.Write($"comicchar\t{Name}\t{Info}{CccFormat.LineEnding}");

    public static new ComicCharacterEntry Parse(string line)
    {
        var rest = line;
        CccFormat.BetweenTabs(ref rest);
        return new ComicCharacterEntry(CccFormat.BetweenTabs(ref rest));
    }
}

/// <summary>Opens a comic: our nick, our character, and the strip's title. Port of StartHistoryEntry (histent.h:112).</summary>
/// <remarks>
/// <see cref="RandStart"/> is captured because the original's constructor takes it, but note it
/// is <b>not serialised</b> (histent.cpp:531 writes only name, avName and title) — so a reloaded
/// archive does not restore the original random seed and its panels are re-randomised.
/// </remarks>
public sealed class StartHistoryEntry(string nick, string avatarName, string title, int randStart = 0) : HistoryEntry
{
    public string Name { get; } = nick;
    public string AvatarName { get; } = avatarName;
    public string Title { get; } = title;
    public int RandStart { get; } = randStart;

    public override void Execute(HistoryMode mode, IChatDocument doc) =>
        doc.StartHistory(Name, AvatarName, Title, mode);

    public override void WriteSelf(TextWriter w) =>
        w.Write($"starthistory\t{Name}\t{AvatarName}\t{Title}{CccFormat.LineEnding}");

    public static StartHistoryEntry Parse(string line)
    {
        var rest = line;
        CccFormat.BetweenTabs(ref rest);
        var nick = CccFormat.BetweenTabs(ref rest);
        var avName = CccFormat.BetweenTabs(ref rest);
        var title = CccFormat.BetweenTabs(ref rest);
        return new StartHistoryEntry(nick, avName, title);
    }
}
