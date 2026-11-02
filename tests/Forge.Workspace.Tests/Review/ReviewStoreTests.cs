using Xunit;

namespace Forge.Workspace.Tests.Review;

public sealed class ReviewStoreTests
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
    public async Task Add_returns_a_draft_with_defaults_applied()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);

        var comment = await store.AddAsync(New("src/a.cs", 4));

        Assert.True(comment.Id > 0);
        Assert.Equal(ReviewCommentStatus.Draft, comment.Status);
        Assert.False(comment.IsResolved);
        Assert.Equal(4, comment.NewLineStart);
        Assert.Equal(4, comment.NewLineEnd);
        Assert.Equal("needs work", comment.Body);
        Assert.Equal("abc1234", comment.BaseCommit);
        Assert.Null(comment.Anchor);
        Assert.Null(comment.SentAt);
        Assert.Equal(comment.CreatedAt, comment.UpdatedAt);
    }

    [Fact]
    public async Task Add_stores_explicit_range_and_anchor_and_round_trips_them()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var anchor = SampleAnchor();

        var added = await store.AddAsync(New("src/a.cs", 2) with { NewLineEnd = 3, Anchor = anchor });
        var fetched = await store.GetAsync(added.Id);

        Assert.NotNull(fetched);
        Assert.Equal(2, fetched.NewLineStart);
        Assert.Equal(3, fetched.NewLineEnd);
        Assert.Equal(anchor, fetched.Anchor);
    }

    [Fact]
    public async Task Add_validates_its_input()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.AddAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(New("src/a.cs", 1, body: "  ")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(New("", 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AddAsync(New("src/a.cs", 0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AddAsync(New("src/a.cs", 5) with { NewLineEnd = 4 }));
    }

    [Fact]
    public async Task Get_unknown_id_returns_null()
    {
        using var sandbox = new TempDirectory();

        Assert.Null(await new ReviewStore(sandbox.Path).GetAsync(9999));
    }

    [Fact]
    public async Task Query_filters_by_workspace_session_file_status_and_resolution()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var a = await store.AddAsync(New("src/a.cs", 1));
        var b = await store.AddAsync(New("src/b.cs", 1));
        await store.AddAsync(New("src/a.cs", 2, session: "session-2"));
        await store.AddAsync(New("src/a.cs", 3, workspace: "/work/beta"));
        await store.MarkSentAsync([b.Id]);
        await store.SetResolvedAsync(a.Id, true);

        Assert.Equal(3, (await store.QueryAsync(new ReviewQuery { WorkspacePath = Workspace })).Count);
        Assert.Equal(2, (await store.QueryAsync(new ReviewQuery { WorkspacePath = Workspace, SessionId = Session })).Count);
        Assert.Equal([a.Id], (await store.QueryAsync(new ReviewQuery { WorkspacePath = Workspace, SessionId = Session, FilePath = "src/a.cs" })).Select(static c => c.Id));
        Assert.Equal([b.Id], (await store.QueryAsync(new ReviewQuery { WorkspacePath = Workspace, SessionId = Session, Status = ReviewCommentStatus.Sent })).Select(static c => c.Id));
        Assert.Equal([a.Id], (await store.QueryAsync(new ReviewQuery { WorkspacePath = Workspace, SessionId = Session, IsResolved = true })).Select(static c => c.Id));
        Assert.Single(await store.QueryAsync(new ReviewQuery { WorkspacePath = Workspace, SessionId = Session, IsResolved = false }));
    }

    [Fact]
    public async Task Query_returns_newest_first_and_pages_with_limit_and_offset()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var ids = new List<long>();
        for (var i = 1; i <= 5; i++)
        {
            ids.Add((await store.AddAsync(New("src/a.cs", i))).Id);
        }

        var all = await store.QueryAsync(new ReviewQuery { WorkspacePath = Workspace });
        var page = await store.QueryAsync(new ReviewQuery { WorkspacePath = Workspace, Limit = 2, Offset = 1 });
        var clamped = await store.QueryAsync(new ReviewQuery { WorkspacePath = Workspace, Limit = 0 });

        Assert.Equal(ids.AsEnumerable().Reverse(), all.Select(static c => c.Id));
        Assert.Equal(ids.AsEnumerable().Reverse().Skip(1).Take(2), page.Select(static c => c.Id));
        Assert.Single(clamped);
    }

    [Fact]
    public async Task Update_body_changes_text_and_bumps_timestamp()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var added = await store.AddAsync(New("src/a.cs", 1));
        await Task.Delay(20);

        var updated = await store.UpdateBodyAsync(added.Id, "rewritten");
        var fetched = await store.GetAsync(added.Id);

        Assert.True(updated);
        Assert.Equal("rewritten", fetched!.Body);
        Assert.True(fetched.UpdatedAt > added.UpdatedAt);
        Assert.Equal(added.CreatedAt, fetched.CreatedAt);
        Assert.False(await store.UpdateBodyAsync(999, "nothing"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateBodyAsync(added.Id, " "));
    }

    [Fact]
    public async Task Resolve_toggles_and_reports_unknown_ids()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var added = await store.AddAsync(New("src/a.cs", 1));

        Assert.True(await store.SetResolvedAsync(added.Id, true));
        Assert.True((await store.GetAsync(added.Id))!.IsResolved);
        Assert.True(await store.SetResolvedAsync(added.Id, false));
        Assert.False((await store.GetAsync(added.Id))!.IsResolved);
        Assert.False(await store.SetResolvedAsync(999, true));
    }

    [Fact]
    public async Task Delete_removes_the_row_once()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var added = await store.AddAsync(New("src/a.cs", 1));

        Assert.True(await store.DeleteAsync(added.Id));
        Assert.False(await store.DeleteAsync(added.Id));
        Assert.Null(await store.GetAsync(added.Id));
    }

    [Fact]
    public async Task Drafts_are_scoped_to_workspace_and_session()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var keep = await store.AddAsync(New("src/a.cs", 1));
        await store.AddAsync(New("src/a.cs", 2, session: "other"));
        await store.AddAsync(New("src/a.cs", 3, workspace: "/work/beta"));

        var drafts = await store.GetDraftsAsync(Workspace, Session);

        Assert.Equal([keep.Id], drafts.Select(static c => c.Id));
    }

    [Fact]
    public async Task Mark_sent_moves_drafts_to_sent_and_returns_rows_in_requested_order()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var first = await store.AddAsync(New("src/a.cs", 1));
        var second = await store.AddAsync(New("src/a.cs", 2));
        var third = await store.AddAsync(New("src/a.cs", 3));

        var sent = await store.MarkSentAsync([third.Id, first.Id, 424242]);

        Assert.Equal([third.Id, first.Id], sent.Select(static c => c.Id));
        Assert.All(sent, static comment =>
        {
            Assert.Equal(ReviewCommentStatus.Sent, comment.Status);
            Assert.NotNull(comment.SentAt);
        });
        Assert.Equal([second.Id], (await store.GetDraftsAsync(Workspace, Session)).Select(static c => c.Id));
    }

    [Fact]
    public async Task Mark_sent_again_keeps_the_original_sent_at()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var comment = await store.AddAsync(New("src/a.cs", 1));
        var firstSent = Assert.Single(await store.MarkSentAsync([comment.Id]));
        await Task.Delay(20);

        var secondSent = Assert.Single(await store.MarkSentAsync([comment.Id]));

        Assert.NotNull(firstSent.SentAt);
        Assert.Equal(firstSent.SentAt, secondSent.SentAt);
        Assert.Equal(ReviewCommentStatus.Sent, secondSent.Status);
    }

    [Fact]
    public async Task Mark_sent_with_no_ids_is_a_no_op()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);

        Assert.Empty(await store.MarkSentAsync([]));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.MarkSentAsync(null!));
    }

    [Fact]
    public async Task Comments_persist_across_store_instances_and_database_reopen()
    {
        using var sandbox = new TempDirectory();
        var anchor = SampleAnchor();
        long id;
        var first = new ReviewStore(sandbox.Path);
        id = (await first.AddAsync(New("src/a.cs", 2) with { Anchor = anchor, NewLineEnd = 2 })).Id;
        await first.MarkSentAsync([id]);

        var reopened = new ReviewStore(sandbox.Path);
        var comment = await reopened.GetAsync(id);

        Assert.NotNull(comment);
        Assert.Equal(ReviewCommentStatus.Sent, comment.Status);
        Assert.NotNull(comment.SentAt);
        Assert.Equal("abc1234", comment.BaseCommit);
        Assert.Equal(anchor, comment.Anchor);
        Assert.Equal("needs work", comment.Body);
        Assert.Equal(Path.Combine(sandbox.Path, ReviewStore.DatabaseFileName), reopened.DatabasePath);
        Assert.True(File.Exists(reopened.DatabasePath));
    }

    [Fact]
    public async Task Missing_data_directory_is_created_on_first_use()
    {
        using var sandbox = new TempDirectory();
        var nested = Path.Combine(sandbox.Path, "deep", "data");

        var store = new ReviewStore(nested);
        await store.AddAsync(New("src/a.cs", 1));

        Assert.True(File.Exists(Path.Combine(nested, ReviewStore.DatabaseFileName)));
    }

    [Fact]
    public async Task Concurrent_adds_from_many_stores_all_land_with_distinct_ids()
    {
        using var sandbox = new TempDirectory();
        var stores = Enumerable.Range(0, 4).Select(_ => new ReviewStore(sandbox.Path)).ToArray();

        var added = await Task.WhenAll(Enumerable.Range(0, 40).Select(index =>
            stores[index % stores.Length].AddAsync(New("src/a.cs", index + 1, body: $"comment {index}"))));

        Assert.Equal(40, added.Select(static comment => comment.Id).Distinct().Count());
        var stored = await new ReviewStore(sandbox.Path).QueryAsync(new ReviewQuery { WorkspacePath = Workspace, Limit = 500 });
        Assert.Equal(40, stored.Count);
        Assert.Equal(40, stored.Select(static comment => comment.Body).Distinct().Count());
    }

    [Fact]
    public async Task Concurrent_status_changes_from_several_tasks_leave_consistent_rows()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var ids = new List<long>();
        for (var i = 0; i < 10; i++)
        {
            ids.Add((await store.AddAsync(New("src/a.cs", i + 1))).Id);
        }

        await Task.WhenAll(ids.Select(id => store.SetResolvedAsync(id, true)));
        var sent = await store.MarkSentAsync(ids);

        Assert.Equal(10, sent.Count(static comment => comment.IsResolved));
        Assert.Empty(await store.GetDraftsAsync(Workspace, Session));
    }

    [Fact]
    public void Constructor_rejects_blank_directory()
    {
        Assert.Throws<ArgumentException>(() => new ReviewStore(" "));
    }

    [Fact]
    public async Task Query_rejects_null_query()
    {
        using var sandbox = new TempDirectory();

        await Assert.ThrowsAsync<ArgumentNullException>(() => new ReviewStore(sandbox.Path).QueryAsync(null!));
    }
}
