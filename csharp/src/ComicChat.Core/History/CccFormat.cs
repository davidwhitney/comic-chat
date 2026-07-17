using System.Text;

namespace ComicChat.Core.History;

/// <summary>
/// Primitives of the <c>.ccc</c> archive format: the header, backslash quoting, and
/// tab-delimited field splitting. Port of the helpers in histent.cpp.
/// </summary>
public static class CccFormat
{
    /// <summary>Port of CONVERSATIONSTRING (histent.cpp:653). The first line of every archive.</summary>
    public const string ConversationHeader = "#CHATCONVERSATION";

    /// <summary>Records are CRLF-terminated, as written by the original's CArchive.</summary>
    public const string LineEnding = "\r\n";

    /// <summary>
    /// Escape the characters that would otherwise break the line/field structure.
    /// Port of QuoteReturns (histent.cpp:930).
    /// </summary>
    /// <remarks>
    /// Tabs separate fields and CRLF separates records, so a message containing either would
    /// corrupt the archive. Backslash is escaped because it is the escape character itself.
    /// </remarks>
    public static string QuoteReturns(string s)
    {
        if (s.AsSpan().IndexOfAny('\n', '\r', '\t') < 0 && !s.Contains('\\'))
            return s;

        var sb = new StringBuilder(s.Length * 2);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\\': sb.Append("\\\\"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reverse of <see cref="QuoteReturns"/>. Port of UnQuoteReturns (histent.cpp:968).
    /// </summary>
    /// <remarks>
    /// A trailing lone backslash, or an unknown escape, is passed through as a literal
    /// backslash — the original ASSERTs on the latter but still emits it, and a malformed
    /// archive must not throw.
    /// </remarks>
    public static string UnquoteReturns(string s)
    {
        if (!s.Contains('\\')) return s;

        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { sb.Append(s[i]); continue; }
            if (i + 1 >= s.Length) { sb.Append('\\'); break; }

            switch (s[++i])
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                default: sb.Append('\\').Append(s[i]); break;   // badly quoted: keep it visible
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Take the next tab-delimited field, advancing <paramref name="rest"/> past it.
    /// Port of BetweenTabs (histent.cpp:740).
    /// </summary>
    /// <remarks>
    /// When no tab remains, the whole of <paramref name="rest"/> is the field and the remainder
    /// becomes empty — which is how the last field of a record (a message that may itself
    /// contain no tabs) is read.
    /// </remarks>
    public static string BetweenTabs(ref string rest)
    {
        int tab = rest.IndexOf('\t');
        if (tab < 0)
        {
            var all = rest;
            rest = string.Empty;
            return all;
        }

        var field = rest[..tab];
        rest = rest[(tab + 1)..];
        return field;
    }

    /// <summary>The record keyword, i.e. the first whitespace-delimited token (the original's sscanf "%s").</summary>
    public static string Keyword(string line)
    {
        int i = line.IndexOfAny([' ', '\t', '\r', '\n']);
        return i < 0 ? line : line[..i];
    }
}
