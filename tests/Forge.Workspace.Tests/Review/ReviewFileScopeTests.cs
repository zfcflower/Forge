using Xunit;

namespace Forge.Workspace.Tests.Review;

public sealed class ReviewFileScopeTests
{
    private const string Workspace = "/work/alpha";
    private const string Session = "session-1";

    private static NewReviewComment New(string file, int line, string body = "needs work", string session = Session, string workspace = Workspace) => new()
    {
        WorkspacePath = workspace,
        SessionId = session,
        FilePath = file,
        NewLineStart = line,
        Body = body,
        BaseCommit = "abc1234",
    };

    private static DiffAnchor SampleAnchor()
    {
        var file = Assert.Single(UnifiedDiffParser.Parse("""
            diff --git a/src/a.cs b/src/a.cs
            --- a/src/a.cs
            +++ b/src/a.cs
            @@ -1,2 +1,2 @@
             one
            -two
            +TWO
            """));
        return DiffAnchor.Create(file, 2, 1);
    }

    [Fact]
    public async Task File_drafts_are_limited_to_the_file_workspace_and_session_and_exclude_sent_comments()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var wanted1 = await store.AddAsync(New("src/a.cs", 4));
        var wanted2 = await store.AddAsync(New("src/a.cs", 9));
        await store.AddAsync(New("src/b.cs", 4));
        await store.AddAsync(New("src/a.cs", 4, session: "session-2"));
        await store.AddAsync(New("src/a.cs", 4, workspace: "/work/beta"));
        var sent = await store.AddAsync(New("src/a.cs", 6));
        await store.MarkSentAsync([sent.Id]);

        var drafts = await store.GetDraftsAsync(Workspace, Session, "src/a.cs");

        Assert.Equal([wanted1.Id, wanted2.Id], drafts.Select(static c => c.Id).Order());
        Assert.All(drafts, static c =>
        {
            Assert.Equal("src/a.cs", c.FilePath);
            Assert.Equal(ReviewCommentStatus.Draft, c.Status);
        });
        Assert.Empty(await store.GetDraftsAsync(Workspace, Session, "src/missing.cs"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetDraftsAsync(Workspace, Session, "  "));
    }

    [Fact]
    public async Task Update_location_moves_only_the_draft_and_persists_range_and_anchor()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var anchor = SampleAnchor();
        var draft = await store.AddAsync(New("src/a.cs", 2, body: "keep") with { NewLineEnd = 3 });

        Assert.True(await store.UpdateLocationAsync(draft.Id, 5, 7, anchor));
        var moved = await store.GetAsync(draft.Id);
        Assert.NotNull(moved);
        Assert.Equal((5, 7), (moved.NewLineStart, moved.NewLineEnd));
        Assert.Equal(anchor, moved.Anchor);
        Assert.Equal("keep", moved.Body);
        Assert.Equal("abc1234", moved.BaseCommit);
        Assert.Equal(ReviewCommentStatus.Draft, moved.Status);
        Assert.True(moved.UpdatedAt >= draft.UpdatedAt);

        Assert.True(await store.UpdateLocationAsync(draft.Id, 8, 8, null));
        var cleared = await store.GetAsync(draft.Id);
        Assert.NotNull(cleared);
        Assert.Equal((8, 8), (cleared.NewLineStart, cleared.NewLineEnd));
        Assert.Null(cleared.Anchor);

        Assert.False(await store.UpdateLocationAsync(long.MaxValue, 1, 1, null));
    }

    [Fact]
    public async Task Update_location_rejects_an_invalid_range_without_touching_the_draft_and_never_moves_sent_comments()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var anchor = SampleAnchor();
        var draft = await store.AddAsync(New("src/a.cs", 4) with { Anchor = anchor });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.UpdateLocationAsync(draft.Id, 0, 1, null));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.UpdateLocationAsync(draft.Id, 5, 4, null));
        var untouched = await store.GetAsync(draft.Id);
        Assert.NotNull(untouched);
        Assert.Equal((4, 4), (untouched.NewLineStart, untouched.NewLineEnd));
        Assert.Equal(anchor, untouched.Anchor);

        var sent = Assert.Single(await store.MarkSentAsync([draft.Id]));
        Assert.False(await store.UpdateLocationAsync(draft.Id, 20, 21, null));
        var after = await store.GetAsync(draft.Id);
        Assert.NotNull(after);
        Assert.Equal(ReviewCommentStatus.Sent, after.Status);
        Assert.Equal((4, 4), (after.NewLineStart, after.NewLineEnd));
        Assert.Equal(anchor, after.Anchor);
        Assert.Equal(sent.SentAt, after.SentAt);
    }

    [Fact]
    public async Task Build_file_renders_every_draft_of_that_file_in_line_then_id_order_with_the_live_quote_and_marks_nothing_sent()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var dispatcher = new ReviewDispatcher(store);
        await store.AddAsync(New("src/a.cs", 9, body: "first"));
        await store.AddAsync(New("src/a.cs", 3, body: "second"));
        await store.AddAsync(New("src/a.cs", 3, body: "third"));
        await store.AddAsync(New("src/b.cs", 1, body: "other file"));
        await store.AddAsync(New("src/a.cs", 1, body: "other session", session: "session-2"));
        await store.AddAsync(New("src/a.cs", 1, body: "other workspace", workspace: "/work/beta"));
        var sent = await store.AddAsync(New("src/a.cs", 2, body: "already sent"));
        await store.MarkSentAsync([sent.Id]);

        var dispatch = await dispatcher.BuildFileAsync(Workspace, Session, "src/a.cs", static comment => "live:" + comment.NewLineStart);

        Assert.NotNull(dispatch);
        Assert.Equal(3, dispatch.CommentIds.Count);
        Assert.Equal(
            "## Review comments (3)\n"
            + "\n### 1. `src/a.cs:3`\nbase: `abc1234`\n\n```text\nlive:3\n```\n\nsecond\n"
            + "\n### 2. `src/a.cs:3`\nbase: `abc1234`\n\n```text\nlive:3\n```\n\nthird\n"
            + "\n### 3. `src/a.cs:9`\nbase: `abc1234`\n\n```text\nlive:9\n```\n\nfirst\n",
            dispatch.Text);
        Assert.Equal(3, (await store.GetDraftsAsync(Workspace, Session, "src/a.cs")).Count);
        Assert.All(await store.QueryAsync(new ReviewQuery { WorkspacePath = Workspace, SessionId = Session, FilePath = "src/a.cs", Status = ReviewCommentStatus.Draft }), static c => Assert.Null(c.SentAt));
    }

    [Fact]
    public async Task Build_file_fences_a_live_quote_past_its_backticks_omits_the_quote_when_absent_and_returns_null_without_drafts()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var dispatcher = new ReviewDispatcher(store);
        await store.AddAsync(New("src/a.cs", 3, body: "note"));

        var fenced = await dispatcher.BuildFileAsync(Workspace, Session, "src/a.cs", static _ => "var s = \"```\";");
        Assert.NotNull(fenced);
        Assert.Contains("\n````text\nvar s = \"```\";\n````\n", fenced.Text, StringComparison.Ordinal);

        var plain = await dispatcher.BuildFileAsync(Workspace, Session, "src/a.cs");
        Assert.NotNull(plain);
        Assert.Equal("## Review comments (1)\n\n### 1. `src/a.cs:3`\nbase: `abc1234`\n\nnote\n", plain.Text);

        Assert.Null(await dispatcher.BuildFileAsync(Workspace, Session, "src/none.cs"));
        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.BuildFileAsync(Workspace, Session, " "));
        Assert.Single(await store.GetDraftsAsync(Workspace, Session));
    }
}
