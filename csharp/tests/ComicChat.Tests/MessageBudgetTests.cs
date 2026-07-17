using ComicChat.Irc;

namespace ComicChat.Tests;

public class MessageBudgetTests
{
    [Fact]
    public void Receiving_prefix_uses_the_32_byte_fallback_when_ident_is_unknown()
    {
        // 2 (leading ':' + trailing space) + nick + user + 32 (ircproto.cpp:527-533)
        Assert.Equal(2 + 4 + 5 + 32, MessageBudget.ReceivingPrefixLength("djk8", "myusr"));
    }

    [Fact]
    public void Receiving_prefix_uses_the_real_ident_length_once_known()
    {
        Assert.Equal(2 + 4 + 20, MessageBudget.ReceivingPrefixLength("djk8", "myusr", myIdentLength: 20));
    }

    [Fact]
    public void Total_length_charges_for_the_prefix_the_server_will_prepend()
    {
        // We never write the prefix, but every recipient receives it — so we pre-pay for it or the
        // server truncates our message on the way in.
        int prefix = MessageBudget.ReceivingPrefixLength("nick", "user");
        int total = MessageBudget.TotalLength("#room", "(#G115E223M1) ", "hello", prefix);

        Assert.Equal(MessageBudget.CommandOverhead + 5 + 14 + 5 + prefix, total);
    }

    [Fact]
    public void A_short_message_fits()
    {
        int prefix = MessageBudget.ReceivingPrefixLength("nick", "user");
        Assert.True(MessageBudget.Fits("#room", "(#G115E223M1) ", "hello", prefix));
    }

    [Fact]
    public void A_long_message_does_not_fit()
    {
        int prefix = MessageBudget.ReceivingPrefixLength("nick", "user");
        Assert.False(MessageBudget.Fits("#room", "(#G115E223M1) ", new string('x', 500), prefix));
    }

    [Fact]
    public void The_budget_leaves_room_for_everything_the_receiver_will_see()
    {
        int prefix = MessageBudget.ReceivingPrefixLength("nick", "user");
        const string target = "#room";
        const string annotations = "(#G115E223M1) ";

        int budget = MessageBudget.BodyBudget(target, annotations, prefix);
        var body = new string('x', budget);

        Assert.True(MessageBudget.Fits(target, annotations, body, prefix));

        // One more char must not fit — the budget is tight, not merely safe.
        Assert.False(MessageBudget.Fits(target, annotations, body + "x", prefix));
    }

    // ---- Breaking points ----------------------------------------------------------------------

    [Fact]
    public void A_body_that_fits_is_not_broken()
    {
        Assert.Equal(5, MessageBudget.GetBreakingPoint("hello", 100));
    }

    [Fact]
    public void Breaks_at_a_space_near_the_end()
    {
        // "aaaa...aaa bbb" — the space sits well past 80% of the budget.
        var body = new string('a', 90) + " " + new string('b', 40);
        int bp = MessageBudget.GetBreakingPoint(body, 100);

        Assert.Equal(90, bp);
        Assert.Equal(new string('a', 90), body[..bp]);
    }

    [Fact]
    public void Ignores_a_space_that_is_too_early_and_cuts_hard_instead()
    {
        // The 80% rule (ircproto.cpp:423): a space at 10% would waste most of the line, so the
        // original prefers a mid-word cut to shipping a near-empty chunk.
        var body = "aa " + new string('b', 200);
        int bp = MessageBudget.GetBreakingPoint(body, 100);

        Assert.True(bp > 80, $"expected a hard cut near the budget, got {bp}");
    }

    [Fact]
    public void Breaks_at_the_START_of_a_whitespace_run()
    {
        var body = new string('a', 90) + "   " + new string('b', 40);
        Assert.Equal(90, MessageBudget.GetBreakingPoint(body, 100));
    }

    [Fact]
    public void A_single_long_word_is_cut_hard()
    {
        var body = new string('a', 300);
        int bp = MessageBudget.GetBreakingPoint(body, 100);

        Assert.True(bp > 0 && bp <= 100);
    }

    // ---- Splitting ----------------------------------------------------------------------------

    [Fact]
    public void A_short_body_yields_one_chunk()
    {
        Assert.Equal(["hello"], MessageBudget.Split("hello", 100));
    }

    [Fact]
    public void An_empty_body_yields_no_chunks()
    {
        Assert.Empty(MessageBudget.Split("", 100));
    }

    [Fact]
    public void Splits_a_long_message_at_word_boundaries_with_every_chunk_within_budget()
    {
        var words = Enumerable.Range(0, 200).Select(i => $"word{i}");
        var body = string.Join(' ', words);

        const int budget = 80;
        var chunks = MessageBudget.Split(body, budget);

        Assert.True(chunks.Count > 1);

        foreach (var chunk in chunks)
        {
            Assert.True(chunk.Length <= budget, $"chunk of {chunk.Length} exceeds budget {budget}");
            Assert.False(chunk.StartsWith(' '), $"chunk starts with a space: '{chunk}'");
        }

        // No word is torn in half: rejoining reproduces the original word sequence.
        var rejoined = string.Join(' ', chunks.Select(c => c.Trim()));
        Assert.Equal(body, rejoined);
    }

    [Fact]
    public void Splitting_loses_no_non_whitespace_content()
    {
        var body = string.Join(' ', Enumerable.Range(0, 100).Select(i => $"w{i}"));
        var chunks = MessageBudget.Split(body, 40);

        var original = body.Replace(" ", "");
        var recombined = string.Concat(chunks).Replace(" ", "");

        Assert.Equal(original, recombined);
    }

    [Fact]
    public void Splits_a_single_enormous_word_into_budget_sized_chunks()
    {
        var chunks = MessageBudget.Split(new string('x', 250), 100);

        Assert.All(chunks, c => Assert.True(c.Length <= 100));
        Assert.Equal(250, chunks.Sum(c => c.Length));
    }

    [Fact]
    public void A_real_over_budget_send_splits_into_chunks_that_all_fit_the_512_line()
    {
        int prefix = MessageBudget.ReceivingPrefixLength("nick", "user");
        const string target = "#room";
        const string annotations = "(#G3<9E0:5RM1) ";

        var body = string.Join(' ', Enumerable.Range(0, 300).Select(i => $"word{i}"));
        Assert.False(MessageBudget.Fits(target, annotations, body, prefix));

        int budget = MessageBudget.BodyBudget(target, annotations, prefix);
        var chunks = MessageBudget.Split(body, budget);

        Assert.True(chunks.Count > 1);

        // Each chunk, once re-annotated and prefixed by the server, must fit inside 512.
        foreach (var chunk in chunks)
            Assert.True(MessageBudget.Fits(target, annotations, chunk, prefix),
                $"chunk of {chunk.Length} would overflow the line");
    }

    [Fact]
    public void Split_rejects_a_non_positive_budget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MessageBudget.Split("hello world", 0));
    }
}
