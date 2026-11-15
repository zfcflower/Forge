using Xunit;

namespace Forge.Workspace.Tests.Review;

/// <summary>
/// The <c>review_conclusions</c> table of <c>reviews.db</c> (wave W17b): one row per finished review, read back in order,
/// carried over by a session copy and removed by a session purge. The outcomes are stored as integers, so their numbering is
/// pinned here: a renumbering would reinterpret rows written by an older version.
/// </summary>
public sealed class ReviewConclusionTests
{
    private const string Ws = "/work/alpha";

    private static NewReviewConclusion New(
        string session = "s1",
        ReviewConclusionOutcome outcome = ReviewConclusionOutcome.Approve,
        string text = "verdict: approve",
        string? stepId = null,
        string? baseCommit = null,
        string? headCommit = "1234567890abcdef",
        string workspace = Ws) => new()
    {
        WorkspacePath = workspace,
        SessionId = session,
        StepId = stepId,
        BaseCommit = baseCommit,
        HeadCommit = headCommit,
        Outcome = outcome,
        Text = text,
    };

    [Theory]
    [InlineData(ReviewConclusionOutcome.Approve, 1)]
    [InlineData(ReviewConclusionOutcome.RequestChanges, 2)]
    [InlineData(ReviewConclusionOutcome.Unstructured, 0)]
    [InlineData(ReviewConclusionOutcome.Stopped, 3)]
    [InlineData(ReviewConclusionOutcome.Failed, 4)]
    public async Task Every_outcome_round_trips_with_its_fields(ReviewConclusionOutcome outcome, int stored)
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);

        var added = await store.AddConclusionAsync(New(
            outcome: outcome,
            text: "the reviewer's own text",
            stepId: "step-7",
            baseCommit: "0987654321fedcba"));

        Assert.True(added.Id > 0);
        Assert.Equal(stored, (int)outcome);

        var read = Assert.Single(await store.GetConclusionsAsync("s1"));
        Assert.Equal(added, read);
        Assert.Equal(outcome, read.Outcome);
        Assert.Equal("the reviewer's own text", read.Text);
        Assert.Equal("step-7", read.StepId);
        Assert.Equal("0987654321fedcba", read.BaseCommit);
        Assert.Equal("1234567890abcdef", read.HeadCommit);
        Assert.Equal(Ws, read.WorkspacePath);
        Assert.Equal("s1", read.SessionId);
        Assert.NotEqual(default, read.CreatedAt);
    }

    [Fact]
    public async Task A_branch_review_stores_null_for_the_step_and_reads_it_back_as_null()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);

        await store.AddConclusionAsync(New(stepId: null, baseCommit: "main"));

        var read = Assert.Single(await store.GetConclusionsAsync("s1"));
        Assert.Null(read.StepId);
        Assert.Equal("main", read.BaseCommit);
    }

    [Fact]
    public async Task Conclusions_are_read_newest_first_and_only_for_the_session_asked_about()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);

        var first = await store.AddConclusionAsync(New(text: "verdict: approve"));
        var second = await store.AddConclusionAsync(New(outcome: ReviewConclusionOutcome.RequestChanges, text: "verdict: request changes"));
        await store.AddConclusionAsync(New(session: "other", text: "someone else's review"));

        var rows = await store.GetConclusionsAsync("s1");
        Assert.Equal([second.Id, first.Id], rows.Select(static row => row.Id));
        Assert.Single(await store.GetConclusionsAsync("other"));
        Assert.Empty(await store.GetConclusionsAsync("nobody"));
    }

    [Fact]
    public async Task A_long_review_text_is_stored_whole_because_the_caller_bounds_it()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var text = new string('x', 10_000) + "\nverdict: request changes";

        await store.AddConclusionAsync(New(outcome: ReviewConclusionOutcome.RequestChanges, text: text));

        Assert.Equal(text, Assert.Single(await store.GetConclusionsAsync("s1")).Text);
    }

    [Fact]
    public async Task A_workspace_and_a_session_are_required()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.AddConclusionAsync(New(workspace: " ")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.AddConclusionAsync(New(session: " ")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetConclusionsAsync(" "));
    }

    [Fact]
    public async Task Copying_a_session_carries_its_conclusions_over_changing_only_the_session_id()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var original = await store.AddConclusionAsync(New(stepId: "step-1", text: "verdict: approve"));

        await store.CopySessionAsync(Ws, "s1", "s2");

        var copy = Assert.Single(await store.GetConclusionsAsync("s2"));
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal(original with { Id = copy.Id, SessionId = "s2" }, copy);

        // The source row stays as it was.
        Assert.Equal(original, Assert.Single(await store.GetConclusionsAsync("s1")));
    }

    [Fact]
    public async Task Purging_a_session_removes_its_conclusions_with_its_comments()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        await store.AddConclusionAsync(New());
        await store.AddAsync(new NewReviewComment
        {
            WorkspacePath = Ws,
            SessionId = "s1",
            FilePath = "src/a.cs",
            NewLineStart = 1,
            Body = "rename this",
        });
        await store.AddConclusionAsync(New(session: "s2"));

        var removed = await store.DeleteSessionAsync("s1");

        Assert.Equal(1, removed);
        Assert.Empty(await store.GetConclusionsAsync("s1"));
        Assert.Empty(await store.QueryAsync(new ReviewQuery { WorkspacePath = Ws, SessionId = "s1", Limit = 10 }));
        Assert.Single(await store.GetConclusionsAsync("s2"));
    }

    [Fact]
    public async Task The_outcome_numbers_are_pinned_because_existing_rows_are_read_by_number()
    {
        Assert.Equal(0, (int)ReviewConclusionOutcome.Unstructured);
        Assert.Equal(1, (int)ReviewConclusionOutcome.Approve);
        Assert.Equal(2, (int)ReviewConclusionOutcome.RequestChanges);
        Assert.Equal(3, (int)ReviewConclusionOutcome.Stopped);
        Assert.Equal(4, (int)ReviewConclusionOutcome.Failed);
    }

    // ------------------------------------------------------------ upgrade and idempotent schema (blind spots)

    [Fact]
    public async Task A_pre_w17b_database_without_review_conclusions_is_upgraded_on_open_and_keeps_its_rows()
    {
        using var sandbox = new TempDirectory();
        var databasePath = Path.Combine(sandbox.Path, ReviewStore.DatabaseFileName);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // The schema exactly as it was before W17b appended review_conclusions: comments, their two indexes and marks.
            command.CommandText = """
                CREATE TABLE review_comments (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    workspace_path TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    file_path TEXT NOT NULL,
                    new_line_start INTEGER NOT NULL,
                    new_line_end INTEGER NOT NULL,
                    body TEXT NOT NULL,
                    status TEXT NOT NULL,
                    is_resolved INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    sent_at TEXT NULL,
                    base_commit TEXT NULL,
                    anchor_json TEXT NULL
                );
                CREATE INDEX ix_review_comments_scope ON review_comments (workspace_path, session_id, status);
                CREATE INDEX ix_review_comments_file ON review_comments (workspace_path, session_id, file_path);
                CREATE TABLE review_marks (
                    workspace_path TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    kind INTEGER NOT NULL,
                    file_path TEXT NOT NULL,
                    item_hash TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY (workspace_path, session_id, kind, file_path, item_hash)
                );
                INSERT INTO review_comments
                    (workspace_path, session_id, file_path, new_line_start, new_line_end, body, status, is_resolved,
                     created_at, updated_at, sent_at, base_commit, anchor_json)
                VALUES ('/work/legacy', 'old-session', 'src/a.cs', 3, 4, 'pre-w17b comment', 'draft', 0,
                        '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00', NULL, NULL, NULL);
                INSERT INTO review_marks (workspace_path, session_id, kind, file_path, item_hash, created_at)
                VALUES ('/work/legacy', 'old-session', 1, 'src/a.cs', 'h1', '2026-01-01T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = new ReviewStore(sandbox.Path);

        // The legacy rows survive the upgrade and stay readable.
        var comments = await store.QueryAsync(new ReviewQuery { WorkspacePath = "/work/legacy", SessionId = "old-session", Limit = 10 });
        Assert.Equal("pre-w17b comment", Assert.Single(comments).Body);

        // The new table appeared and takes a row without any version bump.
        var added = await store.AddConclusionAsync(New(session: "old-session", text: "verdict: approve"));
        Assert.Equal("old-session", added.SessionId);

        // The idempotent script did not double-create the table.
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'review_conclusions'";
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task Opening_the_same_database_twice_runs_the_schema_script_again_without_error_or_loss()
    {
        using var sandbox = new TempDirectory();
        var first = new ReviewStore(sandbox.Path);
        await first.AddConclusionAsync(New(text: "verdict: approve"));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var second = new ReviewStore(sandbox.Path);

        Assert.Equal("verdict: approve", Assert.Single(await second.GetConclusionsAsync("s1")).Text);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = second.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'review_conclusions'";
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_review_conclusions_session'";
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task Concurrent_adds_do_not_lose_rows()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);

        var tasks = Enumerable.Range(0, 25)
            .Select(i => store.AddConclusionAsync(New(text: $"review {i}", outcome: (ReviewConclusionOutcome)(i % 5))))
            .ToArray();
        var added = await Task.WhenAll(tasks);

        var read = await store.GetConclusionsAsync("s1");
        Assert.Equal(25, read.Count);
        Assert.Equal(25, read.Select(static row => row.Id).Distinct().Count());
        Assert.Equal(25, added.Select(static row => row.Id).Distinct().Count());
    }

    [Fact]
    public async Task Conclusions_are_session_scoped_across_workspaces_and_ordered_by_id_when_timestamps_tie()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        await store.AddConclusionAsync(New(workspace: "/work/alpha", text: "alpha"));
        await store.AddConclusionAsync(New(workspace: "/work/beta", text: "beta"));
        await store.AddConclusionAsync(New(session: "other", text: "elsewhere"));

        var rows = await store.GetConclusionsAsync("s1");
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, static row => row.WorkspacePath == "/work/alpha");
        Assert.Contains(rows, static row => row.WorkspacePath == "/work/beta");

        // Two rows written with the same timestamp fall back to id descending, so a re-read is still newest first.
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO review_conclusions (workspace_path, session_id, step_id, base_commit, head_commit, outcome, body, created_at)
                VALUES ('/work/tie', 'tie', NULL, NULL, NULL, 1, 'tie-a', '2026-02-02T00:00:00.0000000+00:00'),
                       ('/work/tie', 'tie', NULL, NULL, NULL, 2, 'tie-b', '2026-02-02T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(["tie-b", "tie-a"], (await store.GetConclusionsAsync("tie")).Select(static row => row.Text).ToArray());
    }

    [Fact]
    public async Task Copy_carries_conclusions_from_every_workspace_of_the_session()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        await store.AddConclusionAsync(New(workspace: "/work/alpha", text: "alpha review"));
        await store.AddConclusionAsync(New(workspace: "/work/beta", text: "beta review"));

        await store.CopySessionAsync("/work/alpha", "s1", "s2");

        var copied = await store.GetConclusionsAsync("s2");
        Assert.Equal(2, copied.Count);
        Assert.Contains(copied, static row => row.WorkspacePath == "/work/alpha");
        Assert.Contains(copied, static row => row.WorkspacePath == "/work/beta");
    }

    [Fact]
    public async Task Delete_removes_a_sessions_conclusions_across_every_workspace()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        await store.AddConclusionAsync(New(workspace: "/work/alpha", text: "alpha"));
        await store.AddConclusionAsync(New(workspace: "/work/beta", text: "beta"));
        await store.AddConclusionAsync(New(session: "kept", text: "kept"));

        await store.DeleteSessionAsync("s1");

        Assert.Empty(await store.GetConclusionsAsync("s1"));
        Assert.Single(await store.GetConclusionsAsync("kept"));
    }
}
