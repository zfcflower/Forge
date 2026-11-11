using Xunit;

namespace Forge.Workspace.Tests.Review;

/// <summary>Copying a session's review comments to another session (takeover) and deleting a session's review data.</summary>
public sealed class ReviewStoreSessionTests
{
    private const string Workspace = "/work/alpha";

    private static NewReviewComment New(string session, string file, int line, string body, string workspace = Workspace) => new()
    {
        WorkspacePath = workspace,
        SessionId = session,
        FilePath = file,
        NewLineStart = line,
        NewLineEnd = line + 2,
        Body = body,
        BaseCommit = "abc1234",
    };

    private static ReviewQuery Query(string session, string? workspace = Workspace) => new()
    {
        WorkspacePath = workspace,
        SessionId = session,
        Limit = 100,
    };

    [Fact]
    public async Task Copy_duplicates_comments_changing_only_the_session_id()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var draft = await store.AddAsync(New("old", "src/a.cs", 4, "rename this"));
        var sent = await store.AddAsync(New("old", "src/b.cs", 9, "already sent"));
        await store.MarkSentAsync([sent.Id]);
        await store.SetResolvedAsync(draft.Id, true);
        var originals = (await store.QueryAsync(Query("old"))).OrderBy(c => c.Id).ToList();

        var copied = await store.CopySessionAsync(Workspace, "old", "new");

        Assert.Equal(2, copied);
        var copies = (await store.QueryAsync(Query("new"))).OrderBy(c => c.Id).ToList();
        Assert.Equal(2, copies.Count);
        for (var i = 0; i < 2; i++)
        {
            var original = originals[i];
            var copy = copies[i];
            Assert.NotEqual(original.Id, copy.Id);
            Assert.Equal("new", copy.SessionId);
            Assert.Equal(original with { Id = copy.Id, SessionId = "new" }, copy);
        }

        // The source rows stay as they were.
        Assert.Equal(originals, (await store.QueryAsync(Query("old"))).OrderBy(c => c.Id).ToList());
    }

    [Fact]
    public async Task Copy_keeps_the_anchor_and_workspace_path()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var anchor = DiffAnchor.Create(Assert.Single(UnifiedDiffParser.Parse("""
            diff --git a/src/a.cs b/src/a.cs
            --- a/src/a.cs
            +++ b/src/a.cs
            @@ -1,2 +1,2 @@
             one
            -two
            +TWO
            """)), 2, 1);
        await store.AddAsync(New("old", "src/a.cs", 2, "anchored") with { Anchor = anchor });

        await store.CopySessionAsync(Workspace, "old", "new");

        var copy = Assert.Single(await store.QueryAsync(Query("new")));
        Assert.Equal(Workspace, copy.WorkspacePath);
        Assert.Equal(anchor, copy.Anchor);
    }

    [Fact]
    public async Task Copy_only_takes_the_given_workspace()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        await store.AddAsync(New("old", "a.cs", 1, "here"));
        await store.AddAsync(New("old", "a.cs", 1, "elsewhere", "/work/beta"));

        var copied = await store.CopySessionAsync(Workspace, "old", "new");

        Assert.Equal(1, copied);
        Assert.Equal("here", Assert.Single(await store.QueryAsync(Query("new", workspace: null))).Body);
    }

    [Fact]
    public async Task Copy_of_a_session_without_comments_copies_nothing()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);

        Assert.Equal(0, await store.CopySessionAsync(Workspace, "old", "new"));
        Assert.Empty(await store.QueryAsync(Query("new")));
    }

    [Fact]
    public async Task Copy_also_carries_the_review_marks()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        await store.AddAsync(New("old", "a.cs", 1, "x"));
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO review_marks (workspace_path, session_id, kind, file_path, item_hash, created_at) VALUES ($w, 'old', 1, 'a.cs', 'h1', '2026-01-01T00:00:00.0000000+00:00')";
            insert.Parameters.AddWithValue("$w", Workspace);
            await insert.ExecuteNonQueryAsync();
        }

        await store.CopySessionAsync(Workspace, "old", "new");

        Assert.Equal(1, await CountMarksAsync(store, "new"));
        Assert.Equal(1, await CountMarksAsync(store, "old"));
    }

    [Fact]
    public async Task Copy_onto_the_same_session_or_with_blank_arguments_is_rejected()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        await store.AddAsync(New("old", "a.cs", 1, "x"));

        await Assert.ThrowsAsync<ArgumentException>(() => store.CopySessionAsync(Workspace, "old", "old"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CopySessionAsync(Workspace, "", "new"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CopySessionAsync(Workspace, "old", " "));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CopySessionAsync(" ", "old", "new"));
        Assert.Single(await store.QueryAsync(Query("old")));
    }

    [Fact]
    public async Task Copy_twice_doubles_the_comments_rather_than_failing()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        await store.AddAsync(New("old", "a.cs", 1, "x"));

        await store.CopySessionAsync(Workspace, "old", "new");
        await store.CopySessionAsync(Workspace, "old", "new");

        Assert.Equal(2, (await store.QueryAsync(Query("new"))).Count);
    }

    [Fact]
    public async Task Delete_removes_a_sessions_comments_and_marks_and_nothing_else()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        await store.AddAsync(New("gone", "a.cs", 1, "x"));
        await store.AddAsync(New("gone", "b.cs", 2, "y"));
        await store.AddAsync(New("gone", "c.cs", 3, "z", "/work/beta"));
        await store.AddAsync(New("kept", "a.cs", 1, "keep"));

        var removed = await store.DeleteSessionAsync("gone");

        Assert.Equal(3, removed);
        Assert.Empty(await store.QueryAsync(Query("gone", workspace: null)));
        Assert.Equal("keep", Assert.Single(await store.QueryAsync(Query("kept"))).Body);
    }

    [Fact]
    public async Task Delete_also_clears_marks()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        await store.AddAsync(New("gone", "a.cs", 1, "x"));
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO review_marks (workspace_path, session_id, kind, file_path, item_hash, created_at) VALUES ($w, 'gone', 1, 'a.cs', 'h1', '2026-01-01T00:00:00.0000000+00:00')";
            insert.Parameters.AddWithValue("$w", Workspace);
            await insert.ExecuteNonQueryAsync();
        }

        await store.DeleteSessionAsync("gone");

        Assert.Equal(0, await CountMarksAsync(store, "gone"));
    }

    [Fact]
    public async Task Delete_of_an_unknown_session_removes_nothing_and_blank_ids_are_rejected()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        await store.AddAsync(New("kept", "a.cs", 1, "keep"));

        Assert.Equal(0, await store.DeleteSessionAsync("unknown"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteSessionAsync(""));
        Assert.Single(await store.QueryAsync(Query("kept")));
    }

    [Fact]
    public async Task Deleting_the_source_after_a_copy_leaves_the_copy_intact()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        await store.AddAsync(New("old", "a.cs", 1, "x"));
        await store.CopySessionAsync(Workspace, "old", "new");

        await store.DeleteSessionAsync("old");

        Assert.Equal("x", Assert.Single(await store.QueryAsync(Query("new"))).Body);
    }

    private static async Task<long> CountMarksAsync(ReviewStore store, string session)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM review_marks WHERE session_id = $s";
        command.Parameters.AddWithValue("$s", session);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
