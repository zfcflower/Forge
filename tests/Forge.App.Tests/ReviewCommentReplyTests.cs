using Forge.Abstractions;
using Forge.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

public sealed class ReviewCommentReplyTests : IDisposable
{
    private const string Workspace = "D:/work/repo";
    private readonly string dir = Path.Combine(Path.GetTempPath(), "forge-reply-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<ReviewComment> AddSent(ReviewStore reviews, string body = "why?")
    {
        var comment = await reviews.AddAsync(new NewReviewComment { WorkspacePath = Workspace, SessionId = "s1", FilePath = "a.cs", NewLineStart = 3, Body = body });
        return (await reviews.MarkSentAsync([comment.Id])).Single();
    }

    private sealed class NoPlatform : IPrPlatform
    {
        public string Name => "none";

        public Task<PrPlatformAvailability> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(string workingDirectory, string? selector = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PrResult<PrChecksSnapshot>> GetChecksAsync(string workingDirectory, int number, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PrResult<PrMergeState>> GetMergeStateAsync(string workingDirectory, int number, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PrResult<PrText>> GetFailedJobLogAsync(string workingDirectory, long? runId, long jobId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PrResult<IReadOnlyList<PrReviewThread>>> GetReviewThreadsAsync(string workingDirectory, int number, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PrResult<PrText>> GetPullRequestDiffAsync(string workingDirectory, int number, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PrResult<PrReviewComment>> ReplyToReviewThreadAsync(string workingDirectory, string threadId, string body, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PrResult<PrReference>> CreatePullRequestAsync(string workingDirectory, string title, string body, string baseBranch, string headBranch, bool draft = false, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PrResult<PrReference>> EditPullRequestAsync(string workingDirectory, int number, string? title = null, string? body = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PrResult<PrThreadResolution>> ResolveReviewThreadAsync(string workingDirectory, string threadId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static Task<ToolResult> Resolve(PrCommentTools tools, string json) =>
        tools.ReviewResolve!.InvokeAsync(new ToolCall("c", "review_comment_resolve", json), new ToolContext("s1", Workspace), CancellationToken.None).AsTask();

    [Fact]
    public async Task Clarify_stores_the_question_keeps_the_comment_open_and_a_later_apply_replaces_it()
    {
        var reviews = new ReviewStore(dir);
        var comment = await AddSent(reviews);
        var tools = new PrCommentTools(new GitCli(), new NoPlatform(), new GitToolSettings(), new PrReplyLedger(), reviews);

        var asked = await Resolve(tools, $$"""{"id":{{comment.Id}},"verdict":"clarify","note":"Which caller?"}""");
        Assert.False(asked.IsError, asked.Content);
        var stored = (await reviews.GetAsync(comment.Id))!;
        Assert.False(stored.IsResolved);
        Assert.Equal("clarify", stored.ReplyVerdict);
        Assert.Equal("Which caller?", stored.ReplyText);

        var applied = await Resolve(tools, $$"""{"id":{{comment.Id}},"verdict":"apply","note":"renamed"}""");
        Assert.False(applied.IsError, applied.Content);
        stored = (await reviews.GetAsync(comment.Id))!;
        Assert.True(stored.IsResolved);
        Assert.Equal("apply", stored.ReplyVerdict);
        Assert.Equal("renamed", stored.ReplyText);
    }

    [Fact]
    public async Task Clarify_without_a_question_and_clarify_on_a_resolved_comment_are_refused()
    {
        var reviews = new ReviewStore(dir);
        var comment = await AddSent(reviews);
        var tools = new PrCommentTools(new GitCli(), new NoPlatform(), new GitToolSettings(), new PrReplyLedger(), reviews);

        Assert.True((await Resolve(tools, $$"""{"id":{{comment.Id}},"verdict":"clarify"}""")).IsError);

        await Resolve(tools, $$"""{"id":{{comment.Id}},"verdict":"apply"}""");
        Assert.True((await Resolve(tools, $$"""{"id":{{comment.Id}},"verdict":"clarify","note":"and?"}""")).IsError);
    }

    [Fact]
    public async Task Reply_survives_a_session_copy()
    {
        var reviews = new ReviewStore(dir);
        var comment = await AddSent(reviews);
        await reviews.SetReplyAsync(comment.Id, "push_back", "keeps the contract", resolve: true);

        await reviews.CopySessionAsync(Workspace, "s1", "s2");

        var copy = (await reviews.QueryAsync(new ReviewQuery { WorkspacePath = Workspace, SessionId = "s2" })).Single();
        Assert.Equal("push_back", copy.ReplyVerdict);
        Assert.Equal("keeps the contract", copy.ReplyText);
    }

    [Fact]
    public async Task A_database_created_before_the_reply_columns_is_upgraded_and_keeps_its_rows()
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ReviewStore.DatabaseFileName);
        await using (var connection = new SqliteConnection("Data Source=" + path))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE review_comments (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, workspace_path TEXT NOT NULL, session_id TEXT NOT NULL,
                    file_path TEXT NOT NULL, new_line_start INTEGER NOT NULL, new_line_end INTEGER NOT NULL,
                    body TEXT NOT NULL, status TEXT NOT NULL, is_resolved INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL, updated_at TEXT NOT NULL, sent_at TEXT NULL, base_commit TEXT NULL, anchor_json TEXT NULL);
                INSERT INTO review_comments (workspace_path, session_id, file_path, new_line_start, new_line_end, body, status, created_at, updated_at)
                VALUES ('D:/work/repo', 's1', 'a.cs', 1, 1, 'old', 'sent', '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var reviews = new ReviewStore(dir);
        var old = (await reviews.QueryAsync(new ReviewQuery { WorkspacePath = Workspace })).Single();
        Assert.Equal("old", old.Body);
        Assert.Equal(ReviewCommentStatus.Sent, old.Status);
        Assert.Null(old.ReplyVerdict);

        Assert.True(await reviews.SetReplyAsync(old.Id, "clarify", "which?", resolve: false));
        Assert.Equal("which?", (await reviews.GetAsync(old.Id))!.ReplyText);
    }
}
