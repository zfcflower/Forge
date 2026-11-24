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
        var stored = (await reviews.GetAsync(comment.Id, TestContext.Current.CancellationToken))!;
        Assert.False(stored.IsResolved);
        Assert.Equal("clarify", stored.ReplyVerdict);
        Assert.Equal("Which caller?", stored.ReplyText);

        var applied = await Resolve(tools, $$"""{"id":{{comment.Id}},"verdict":"apply","note":"renamed"}""");
        Assert.False(applied.IsError, applied.Content);
        stored = (await reviews.GetAsync(comment.Id, TestContext.Current.CancellationToken))!;
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
        await reviews.SetReplyAsync(comment.Id, "push_back", "keeps the contract", resolve: true, ct: TestContext.Current.CancellationToken);

        await reviews.CopySessionAsync(Workspace, "s1", "s2", TestContext.Current.CancellationToken);

        var copy = (await reviews.QueryAsync(new ReviewQuery { WorkspacePath = Workspace, SessionId = "s2" }, TestContext.Current.CancellationToken)).Single();
        Assert.Equal("push_back", copy.ReplyVerdict);
        Assert.Equal("keeps the contract", copy.ReplyText);
    }
}
