using System.Text;

namespace ComicChat.Core.History;

/// <summary>
/// The conversation log, and the <c>.ccc</c> archive reader/writer.
/// Port of the history half of CChatDoc (chatdoc.h:22, histent.cpp:614-735).
/// </summary>
/// <remarks>
/// The comic on screen is a <i>projection</i> of this list, never the source of truth. That is
/// the whole design: resizing the window changes the panel size, so the strip must be thrown
/// away and rebuilt by replaying every entry (<see cref="ExecuteHistory"/> with
/// <see cref="HistoryMode.Reload"/>), and saving the strip is just writing this list out.
/// </remarks>
public sealed class ChatHistory(IChatDocument document)
{
    private readonly List<HistoryEntry> _entries = [];

    public IChatDocument Document { get; } = document;
    public IReadOnlyList<HistoryEntry> Entries => _entries;

    /// <summary>True once anything has been recorded that would be lost on close.</summary>
    public bool Modified { get; private set; }

    /// <summary>True when this history came from a file rather than a live session.</summary>
    public bool Archived { get; private set; }

    /// <summary>Set while a Reload/Load replay is running; the UI uses it to suppress scrolling and sounds.</summary>
    public bool Replaying { get; private set; }

    /// <summary>
    /// Record an entry and execute it live. Port of AddAndExecute (histent.cpp:614).
    /// </summary>
    public void AddAndExecute(HistoryEntry entry)
    {
        _entries.Add(entry);
        Modified = true;
        entry.Execute(HistoryMode.Live, Document);
    }

    /// <summary>Record without executing. Port of AddEntry (histent.cpp:637).</summary>
    public void Add(HistoryEntry entry)
    {
        _entries.Add(entry);
        Modified = true;
    }

    public void Clear()
    {
        _entries.Clear();
        Modified = false;
        Archived = false;
    }

    /// <summary>
    /// Replay every entry. Port of CChatDoc::ExecuteHistory (chatdoc.cpp:737).
    /// </summary>
    /// <remarks>
    /// The original suppressed refresh for any non-live mode ("turn off refresh if not live");
    /// <see cref="Replaying"/> exposes the same signal. Note this does <b>not</b> reset the
    /// comic — the caller does that first, exactly as SetPanelsWide calls ResetExistingPanels
    /// before ExecuteHistory (pageview.cpp:1111). Use <see cref="Reload"/> for the pair.
    /// </remarks>
    public void ExecuteHistory(HistoryMode mode)
    {
        bool wasReplaying = Replaying;
        if (mode != HistoryMode.Live) Replaying = true;

        try
        {
            foreach (var entry in _entries)
                entry.Execute(mode, Document);
        }
        finally
        {
            Replaying = wasReplaying;
        }
    }

    /// <summary>
    /// Throw the comic away and rebuild it from history. Port of the
    /// ResetExistingPanels + ExecuteHistory(HM_RELOAD) pair (pageview.cpp:1111-1113).
    /// </summary>
    public void Reload()
    {
        Document.ResetComic();
        ExecuteHistory(HistoryMode.Reload);
    }

    /// <summary>Port of CChatDoc::ChatSaveConversation (histent.cpp:653).</summary>
    public void Save(TextWriter w)
    {
        w.Write(CccFormat.ConversationHeader + CccFormat.LineEnding);
        foreach (var entry in _entries)
            entry.WriteSelf(w);
    }

    public void Save(string path)
    {
        // The format is CRLF-delimited ANSI; Latin1 round-trips every byte the original wrote.
        using var w = new StreamWriter(path, false, Encoding.Latin1);
        Save(w);
        Modified = false;
    }

    /// <summary>
    /// Port of CChatDoc::ChatLoadConversation (histent.cpp:670).
    /// </summary>
    /// <remarks>
    /// Entries are added <i>and executed</i> as they are read, so the comic builds up during the
    /// load. Unknown keywords are collected rather than thrown: the original popped a message
    /// box per bad field and carried on, and refusing to open an otherwise-good archive over one
    /// stray record would be worse than reporting it.
    /// </remarks>
    public bool Load(TextReader r, out IReadOnlyList<string> unknownKeywords)
    {
        var unknown = new List<string>();
        unknownKeywords = unknown;

        var header = r.ReadLine();
        if (header is null ||
            !header.StartsWith(CccFormat.ConversationHeader, StringComparison.OrdinalIgnoreCase))
            return false;

        Archived = true;
        Replaying = true;

        try
        {
            while (r.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;

                var entry = ParseEntry(line);
                if (entry is null)
                {
                    var key = CccFormat.Keyword(line);
                    if (key.Length > 0 && !unknown.Contains(key)) unknown.Add(key);
                    continue;
                }

                _entries.Add(entry);
                entry.Execute(HistoryMode.Load, Document);
            }
        }
        finally
        {
            Replaying = false;
        }

        Modified = false;
        return true;
    }

    public bool Load(string path, out IReadOnlyList<string> unknownKeywords)
    {
        using var r = new StreamReader(path, Encoding.Latin1);
        return Load(r, out unknownKeywords);
    }

    /// <summary>Port of the keyword dispatch in ChatLoadConversation (histent.cpp:688).</summary>
    public static HistoryEntry? ParseEntry(string line) =>
        CccFormat.Keyword(line).ToLowerInvariant() switch
        {
            "say" => SayEntry.Parse(line),
            "join" or "ejoin" => JoinEntry.Parse(line),
            "part" => PartEntry.Parse(line),
            "changeavatar" => ChangeAvatarEntry.Parse(line),
            "getinfo" => GetInfoEntry.Parse(line),
            "comicchar" => ComicCharacterEntry.Parse(line),
            "nick" => NickEntry.Parse(line),
            "backdrop" => ChangeBackDropEntry.Parse(line),
            "starthistory" => StartHistoryEntry.Parse(line),
            _ => null,
        };
}
