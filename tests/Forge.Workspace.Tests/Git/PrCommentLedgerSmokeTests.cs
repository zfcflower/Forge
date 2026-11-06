using Xunit;

namespace Forge.Workspace.Tests.Git;

public sealed class PrCommentLedgerSmokeTests
{
    private const string Sha = "0123abc";

    [Fact]
    public void Marker_lines_have_the_documented_visible_format()
    {
        Assert.Equal("**Verdict: apply** (0123abc)", PrReplyLedger.FormatMarker(PrReplyVerdict.Apply, Sha));
        Assert.Equal("**Verdict: push_back**", PrReplyLedger.FormatMarker(PrReplyVerdict.PushBack, null));
        Assert.Equal("**Verdict: clarify**", PrReplyLedger.FormatMarker(PrReplyVerdict.Clarify, null));

        var reply = PrReplyLedger.FormatReply(PrReplyVerdict.Apply, Sha, "  Fixed the null check.\n");
        Assert.Equal("**Verdict: apply** (0123abc)\n\nFixed the null check.", reply);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("xyz1234")]
    [InlineData("abc12")]
    public void An_apply_marker_without_a_valid_sha_is_refused(string? commit)
    {
        Assert.Throws<ArgumentException>(() => PrReplyLedger.FormatMarker(PrReplyVerdict.Apply, commit));
    }

    [Fact]
    public void An_empty_reply_body_is_refused()
    {
        Assert.Throws<ArgumentException>(() => PrReplyLedger.FormatReply(PrReplyVerdict.Clarify, null, "   "));
    }

    [Theory]
    [InlineData("**Verdict: apply** (0123abc)\n\nbody", PrReplyVerdict.Apply, "0123abc")]
    [InlineData("**Verdict: apply** (0123456789abcdef0123456789abcdef01234567)", PrReplyVerdict.Apply, "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("  \n**Verdict: apply** (ABCDEF1)\r\nbody", PrReplyVerdict.Apply, "ABCDEF1")]
    [InlineData("**Verdict: push_back**\n\nout of scope", PrReplyVerdict.PushBack, null)]
    [InlineData("**Verdict: clarify**", PrReplyVerdict.Clarify, null)]
    public void Valid_markers_parse_from_the_first_non_blank_line(string body, PrReplyVerdict verdict, string? commit)
    {
        Assert.True(PrReplyLedger.TryParseMarker(body, out var record));
        Assert.Equal(verdict, record.Verdict);
        Assert.Equal(commit, record.Commit);
        Assert.Null(record.CommentId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Verdict: apply (0123abc)")]
    [InlineData("**Verdict: apply**")]
    [InlineData("**Verdict: apply** (0123ab)")]
    [InlineData("**Verdict: apply** (0123abcz)")]
    [InlineData("**Verdict: apply** (0123abc")]
    [InlineData("**Verdict: apply** 0123abc")]
    [InlineData("**Verdict: apply** ()")]
    [InlineData("**Verdict: push_back** (0123abc)")]
    [InlineData("**Verdict: clarify** (0123abc)")]
    [InlineData("**Verdict: Apply** (0123abc)")]
    [InlineData("**Verdict: ** (0123abc)")]
    [InlineData("**Verdict: maybe**")]
    [InlineData("Thanks!\n**Verdict: apply** (0123abc)")]
    [InlineData("**Verdict: apply** (0123abc) and more")]
    [InlineData("**Verdict: apply** (01234567890123456789012345678901234567890)")]
    public void Anything_that_is_not_exactly_a_marker_does_not_parse(string? body)
    {
        Assert.False(PrReplyLedger.TryParseMarker(body, out _));
    }

    [Theory]
    [InlineData("apply", true, PrReplyVerdict.Apply)]
    [InlineData("push_back", true, PrReplyVerdict.PushBack)]
    [InlineData("clarify", true, PrReplyVerdict.Clarify)]
    [InlineData("Apply", false, PrReplyVerdict.Clarify)]
    [InlineData("pushback", false, PrReplyVerdict.Clarify)]
    [InlineData("", false, PrReplyVerdict.Clarify)]
    public void Verdict_names_are_exact(string text, bool parsed, PrReplyVerdict expected)
    {
        Assert.Equal(parsed, PrReplyLedger.TryParseVerdict(text, out var verdict));
        Assert.Equal(expected, verdict);
    }

    [Theory]
    [InlineData("abcdef1", true)]
    [InlineData("0123456789abcdef0123456789ABCDEF01234567", true)]
    [InlineData("abcdef", false)]
    [InlineData("abcdefg", false)]
    [InlineData("", false)]
    [InlineData("01234567890123456789012345678901234567890", false)]
    public void Shas_have_seven_to_forty_hex_digits(string text, bool expected)
    {
        Assert.Equal(expected, PrReplyLedger.IsHexSha(text));
    }

    [Fact]
    public void Record_and_get_remember_the_last_verdict_per_thread()
    {
        var ledger = new PrReplyLedger();

        Assert.Null(ledger.Get("t1"));
        ledger.Record("t1", PrReplyVerdict.Clarify, null, "c1");
        ledger.Record("t2", PrReplyVerdict.PushBack, null, "c2");
        ledger.Record("t1", PrReplyVerdict.Apply, "0123abc0123abc0123abc0123abc0123abc0123a", "c3");

        Assert.Equal(PrReplyVerdict.Apply, ledger.Get("t1")!.Verdict);
        Assert.Equal("c3", ledger.Get("t1")!.CommentId);
        Assert.Equal(PrReplyVerdict.PushBack, ledger.Get("t2")!.Verdict);
        Assert.Null(ledger.Get("t3"));
        Assert.Null(ledger.Get(""));
        Assert.Throws<ArgumentException>(() => ledger.Record(" ", PrReplyVerdict.Clarify, null, null));
    }

    [Fact]
    public void The_current_user_is_learned_from_a_login_and_ignores_blanks()
    {
        var ledger = new PrReplyLedger();
        Assert.Null(ledger.CurrentUser);

        ledger.NoteCurrentUser(null);
        ledger.NoteCurrentUser("  ");
        Assert.Null(ledger.CurrentUser);

        ledger.NoteCurrentUser("octo");
        ledger.NoteCurrentUser(null);
        Assert.Equal("octo", ledger.CurrentUser);
    }

    [Fact]
    public void A_marker_reply_by_the_current_user_is_recovered_without_any_memory()
    {
        var thread = PrCommentHarness.Thread(
            "t1",
            comments:
            [
                PrCommentHarness.Comment("c0", "reviewer", "please fix"),
                PrCommentHarness.Comment("c1", "Octo", PrReplyLedger.FormatReply(PrReplyVerdict.Apply, Sha, "done"), 1),
            ]);

        var record = new PrReplyLedger().Evaluate(thread, "octo");

        Assert.NotNull(record);
        Assert.Equal(PrReplyVerdict.Apply, record.Verdict);
        Assert.Equal(Sha, record.Commit);
        Assert.Equal("c1", record.CommentId);
    }

    [Fact]
    public void A_marker_typed_by_somebody_else_proves_nothing()
    {
        var thread = PrCommentHarness.Thread(
            "t1",
            comments:
            [
                PrCommentHarness.Comment("c0", "reviewer", "please fix"),
                PrCommentHarness.Comment("c1", "mallory", PrReplyLedger.FormatReply(PrReplyVerdict.PushBack, null, "trust me"), 1),
            ]);

        Assert.True(PrReplyLedger.HasMarkerReply(thread));
        Assert.Null(new PrReplyLedger().Evaluate(thread, "octo"));
        Assert.Null(new PrReplyLedger().Evaluate(thread, null));
    }

    [Fact]
    public void The_opening_comment_of_a_thread_never_counts_even_when_it_looks_like_a_marker()
    {
        var thread = PrCommentHarness.Thread(
            "t1",
            comments: [PrCommentHarness.Comment("c0", "octo", PrReplyLedger.FormatReply(PrReplyVerdict.PushBack, null, "forged"))]);

        Assert.False(PrReplyLedger.HasMarkerReply(thread));
        Assert.Null(new PrReplyLedger().Evaluate(thread, "octo"));
    }

    [Fact]
    public void Without_a_known_user_only_the_comment_this_process_created_counts()
    {
        var thread = PrCommentHarness.Thread(
            "t1",
            comments:
            [
                PrCommentHarness.Comment("c0", "reviewer", "please fix"),
                PrCommentHarness.Comment("c1", "octo", PrReplyLedger.FormatReply(PrReplyVerdict.PushBack, null, "no"), 1),
                PrCommentHarness.Comment("c2", "octo", PrReplyLedger.FormatReply(PrReplyVerdict.Clarify, null, "why?"), 2),
            ]);
        var ledger = new PrReplyLedger();
        ledger.Record("t1", PrReplyVerdict.Clarify, null, "c2");

        var record = ledger.Evaluate(thread, null);

        Assert.NotNull(record);
        Assert.Equal(PrReplyVerdict.Clarify, record.Verdict);
        Assert.Equal("c2", record.CommentId);
    }

    [Fact]
    public void A_deleted_reply_is_not_remembered_as_evidence()
    {
        // The ledger remembers comment c9, but the platform no longer shows it.
        var ledger = new PrReplyLedger();
        ledger.Record("t1", PrReplyVerdict.PushBack, null, "c9");
        var thread = PrCommentHarness.Thread("t1");

        Assert.Null(ledger.Evaluate(thread, "octo"));
    }

    [Fact]
    public void The_last_verdict_reply_wins()
    {
        var thread = PrCommentHarness.Thread(
            "t1",
            comments:
            [
                PrCommentHarness.Comment("c0", "reviewer", "please fix"),
                PrCommentHarness.Comment("c1", "octo", PrReplyLedger.FormatReply(PrReplyVerdict.Clarify, null, "which one?"), 1),
                PrCommentHarness.Comment("c2", "reviewer", "this one", 2),
                PrCommentHarness.Comment("c3", "octo", PrReplyLedger.FormatReply(PrReplyVerdict.Apply, Sha, "done"), 3),
            ]);

        var record = new PrReplyLedger().Evaluate(thread, "octo");

        Assert.NotNull(record);
        Assert.Equal(PrReplyVerdict.Apply, record.Verdict);
        Assert.Equal("c3", record.CommentId);
    }

    [Fact]
    public void Plain_replies_without_a_marker_are_not_verdicts()
    {
        var thread = PrCommentHarness.Thread(
            "t1",
            comments:
            [
                PrCommentHarness.Comment("c0", "reviewer", "please fix"),
                PrCommentHarness.Comment("c1", "octo", "Done, see latest push.", 1),
            ]);

        Assert.False(PrReplyLedger.HasMarkerReply(thread));
        Assert.Null(new PrReplyLedger().Evaluate(thread, "octo"));
    }

    [Fact]
    public async Task The_ledger_is_safe_under_concurrent_use()
    {
        var ledger = new PrReplyLedger();
        var writers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                var id = "t" + (i % 20);
                ledger.Record(id, (PrReplyVerdict)(i % 3), null, "c" + worker + "-" + i);
                _ = ledger.Get(id);
                ledger.NoteCurrentUser("user" + worker);
            }
        }));

        await Task.WhenAll(writers);

        for (var i = 0; i < 20; i++)
        {
            Assert.NotNull(ledger.Get("t" + i));
        }

        Assert.StartsWith("user", ledger.CurrentUser);
    }
}
