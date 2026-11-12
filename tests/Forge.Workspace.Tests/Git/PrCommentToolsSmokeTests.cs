using Forge.Abstractions;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>Shared helpers of the PR comment tool tests.</summary>
internal static class PrCommentHarness
{
    public static Task<ToolResult> InvokeAsync(ITool tool, string workingDirectory, string json, CancellationToken ct = default) =>
        tool.InvokeAsync(
            new ToolCall(Guid.NewGuid().ToString("n"), tool.Definition.Name, json),
            new ToolContext("session-1", workingDirectory),
            ct).AsTask();

    public static PrReviewComment Comment(string id, string? author, string body, int minute = 0) =>
        new(id, null, author, body, new DateTimeOffset(2026, 5, 1, 10, minute, 0, TimeSpan.Zero), null);

    public static PrReviewThread Thread(
        string id,
        string path = "src/A.cs",
        int? line = 10,
        bool resolved = false,
        bool outdated = false,
        string? resolvedBy = null,
        params PrReviewComment[] comments) =>
        new(id, resolved, outdated, path, line, null, line, "RIGHT", resolvedBy, comments.Length > 0 ? comments : [Comment("c-" + id, "reviewer", "please fix " + id)]);

    public static PullRequestInfo PullRequest(string head = "feature", int number = 5, PrState state = PrState.Open) =>
        new(number, $"https://github.com/acme/forge/pull/{number}", state, "Title", "Body", head, "main", "0000000", false, PrMergeStateStatus.Clean, PrReviewDecision.None);

    /// <summary>The comment tools over a <see cref="FakePrPlatform"/> holding <paramref name="threads"/>, together with
    /// that platform and the ledger that remembers the verdicts.</summary>
    public static (PrCommentTools Tools, FakePrPlatform Platform, PrReplyLedger Ledger) Tools(params PrReviewThread[] threads)
    {
        var platform = new FakePrPlatform();
        platform.Threads.AddRange(threads);
        var ledger = new PrReplyLedger();
        return (new PrCommentTools(new GitCli(), platform, new GitToolSettings(), ledger, null), platform, ledger);
    }

    /// <summary>A resolver that returns a fixed login, or null, and counts its calls.</summary>
    public sealed class FixedUser(string? login)
    {
        public int Calls { get; private set; }

        public PrUserResolver Resolver => (_, _) =>
        {
            Calls++;
            return Task.FromResult(login);
        };
    }
}

/// <summary>An in-memory pull request platform that keeps thread state, so a reply posted by a tool shows up in the next read.</summary>
internal sealed class FakePrPlatform : IPrPlatform
{
    private int nextComment = 1000;

    public string Name => "Fake";

    /// <summary>The login the fake "posts" replies as.</summary>
    public string Login { get; set; } = "octo";

    public PullRequestInfo PullRequest { get; set; } = PrCommentHarness.PullRequest();

    public List<PrReviewThread> Threads { get; } = [];

    public List<(string ThreadId, string Body)> Replies { get; } = [];

    public List<string> Resolves { get; } = [];

    public int ThreadReads { get; private set; }

    public PrResult<PullRequestInfo>? ViewFailure { get; set; }

    public PrResult<IReadOnlyList<PrReviewThread>>? ThreadsFailure { get; set; }

    public PrResult<PrReviewComment>? ReplyFailure { get; set; }

    /// <summary>When set, the reply is created but the call reports a timeout, as a lost response would.</summary>
    public bool LoseReplyResponse { get; set; }

    public PrResult<PrThreadResolution>? ResolveFailure { get; set; }

    /// <summary>When set, a resolve call succeeds but reports the thread as still unresolved.</summary>
    public bool ResolveDoesNotStick { get; set; }

    public PrResult<PrText> Diff { get; set; } = PrResult<PrText>.Ok(new PrText("diff --git a/src/A.cs b/src/A.cs\n+added\n", false));

    public Task<PrPlatformAvailability> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default) =>
        Task.FromResult(new PrPlatformAvailability(PrPlatformState.Ready, "ok"));

    public Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(string workingDirectory, string? selector = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ViewFailure ?? PrResult<PullRequestInfo>.Ok(PullRequest));
    }

    public Task<PrResult<IReadOnlyList<PrReviewThread>>> GetReviewThreadsAsync(string workingDirectory, int number, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThreadReads++;
        return Task.FromResult(ThreadsFailure ?? PrResult<IReadOnlyList<PrReviewThread>>.Ok(Threads.ToArray()));
    }

    public Task<PrResult<PrText>> GetPullRequestDiffAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        Task.FromResult(Diff);

    public Task<PrResult<PrReviewComment>> ReplyToReviewThreadAsync(string workingDirectory, string threadId, string body, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (ReplyFailure is not null)
        {
            return Task.FromResult(ReplyFailure);
        }

        var index = Threads.FindIndex(thread => thread.Id == threadId);
        if (index < 0)
        {
            return Task.FromResult(PrResult<PrReviewComment>.Fail(GitRemoteFailureKind.Other, "no such thread"));
        }

        var comment = new PrReviewComment(
            "PRRC_" + nextComment++,
            null,
            Login,
            body,
            new DateTimeOffset(2026, 5, 2, 10, 0, 0, TimeSpan.Zero).AddMinutes(nextComment),
            "https://github.com/acme/forge/pull/5#discussion_r" + nextComment);
        Replies.Add((threadId, body));
        Threads[index] = Threads[index] with { Comments = [.. Threads[index].Comments, comment] };
        if (LoseReplyResponse)
        {
            // The comment exists on the platform; the answer to the caller was lost on the way back.
            return Task.FromResult(PrResult<PrReviewComment>.Fail(
                GitRemoteFailureKind.TimedOut,
                "gh api timed out after sending the reply"));
        }

        return Task.FromResult(PrResult<PrReviewComment>.Ok(comment));
    }

    public Task<PrResult<PrThreadResolution>> ResolveReviewThreadAsync(string workingDirectory, string threadId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (ResolveFailure is not null)
        {
            return Task.FromResult(ResolveFailure);
        }

        Resolves.Add(threadId);
        var index = Threads.FindIndex(thread => thread.Id == threadId);
        if (index < 0)
        {
            return Task.FromResult(PrResult<PrThreadResolution>.Fail(GitRemoteFailureKind.Other, "no such thread"));
        }

        if (ResolveDoesNotStick)
        {
            return Task.FromResult(PrResult<PrThreadResolution>.Ok(new PrThreadResolution(threadId, false)));
        }

        Threads[index] = Threads[index] with { IsResolved = true, ResolvedBy = Login };
        return Task.FromResult(PrResult<PrThreadResolution>.Ok(new PrThreadResolution(threadId, true)));
    }

    public Task<PrResult<PrReference>> CreatePullRequestAsync(string workingDirectory, string title, string body, string baseBranch, string headBranch, bool draft = false, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrReference>> EditPullRequestAsync(string workingDirectory, int number, string? title = null, string? body = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrChecksSnapshot>> GetChecksAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrMergeState>> GetMergeStateAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrText>> GetFailedJobLogAsync(string workingDirectory, long? runId, long jobId, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

public sealed class PrCommentToolsSmokeTests
{
    [Fact]
    public void Tools_are_split_by_side_effect_and_carry_the_documented_names()
    {
        using var temp = new TempDirectory();
        var tools = new PrCommentTools(
            new GitCli(), new FakePrPlatform(), new GitToolSettings(), new PrReplyLedger(), new ReviewStore(temp.Path));

        Assert.Equal("pr_comments", PrCommentTools.ListName);
        Assert.Equal("pr_comment_reply", PrCommentTools.ReplyName);
        Assert.Equal("pr_comment_resolve", PrCommentTools.ResolveName);
        Assert.Equal("review_comments", PrCommentTools.ReviewListName);
        Assert.Equal("review_comment_resolve", PrCommentTools.ReviewResolveName);

        Assert.Equal(PrCommentTools.ListName, tools.Comments.Definition.Name);
        Assert.Equal(PrCommentTools.ReplyName, tools.Reply.Definition.Name);
        Assert.Equal(PrCommentTools.ResolveName, tools.Resolve.Definition.Name);
        Assert.Equal(PrCommentTools.ReviewListName, tools.ReviewComments!.Definition.Name);
        Assert.Equal(PrCommentTools.ReviewResolveName, tools.ReviewResolve!.Definition.Name);

        Assert.Equal(ToolSideEffect.ReadOnly, tools.Comments.SideEffect);
        Assert.Equal(ToolSideEffect.Write, tools.Reply.SideEffect);
        Assert.Equal(ToolSideEffect.Write, tools.Resolve.SideEffect);
        Assert.Equal(ToolSideEffect.ReadOnly, tools.ReviewComments.SideEffect);
        Assert.Equal(ToolSideEffect.Write, tools.ReviewResolve.SideEffect);

        Assert.Equal(5, tools.All.Count);
        Assert.Equal(5, tools.All.Select(static tool => tool.Definition.Name).Distinct().Count());
        foreach (var tool in tools.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Definition.Description));
            Assert.Contains("review-comments", tool.Definition.Description);
            using var schema = System.Text.Json.JsonDocument.Parse(tool.Definition.InputSchemaJson);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        }
    }

    [Fact]
    public void Without_a_review_store_the_local_tools_are_omitted()
    {
        var tools = new PrCommentTools(new GitCli(), new FakePrPlatform(), new GitToolSettings(), new PrReplyLedger(), reviews: null);

        Assert.Null(tools.ReviewComments);
        Assert.Null(tools.ReviewResolve);
        Assert.Equal(
            new[] { "pr_comment_reply", "pr_comment_resolve", "pr_comments" },
            tools.All.Select(static tool => tool.Definition.Name).OrderBy(static name => name, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("pr_comments")]
    [InlineData("pr_comment_reply")]
    [InlineData("pr_comment_resolve")]
    [InlineData("review_comments")]
    [InlineData("review_comment_resolve")]
    public async Task Invalid_json_and_unknown_properties_are_tool_errors_not_exceptions(string name)
    {
        using var temp = new TempDirectory();
        var platform = new FakePrPlatform();
        var tools = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), new PrReplyLedger(), new ReviewStore(temp.Path));
        var tool = Assert.Single(tools.All, candidate => candidate.Definition.Name == name);

        var broken = await PrCommentHarness.InvokeAsync(tool, temp.Path, "not json{{");
        var unknown = await PrCommentHarness.InvokeAsync(tool, temp.Path, """{"surprise":1}""");

        Assert.True(broken.IsError);
        Assert.True(unknown.IsError);
        Assert.Contains("surprise", unknown.Content);
        Assert.Empty(platform.Replies);
        Assert.Empty(platform.Resolves);
    }

    [Fact]
    public async Task A_cancelled_call_propagates_cancellation()
    {
        using var temp = new TempDirectory();
        var tools = new PrCommentTools(new GitCli(), new CancellingPlatform(), new GitToolSettings(), new PrReplyLedger(), reviews: null);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PrCommentHarness.InvokeAsync(tools.Comments, temp.Path, "{}", source.Token));
    }

    private sealed class CancellingPlatform : IPrPlatform
    {
        public string Name => "Cancelling";

        public Task<PrPlatformAvailability> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default) =>
            throw new OperationCanceledException(ct);

        public Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(string workingDirectory, string? selector = null, CancellationToken ct = default) =>
            throw new OperationCanceledException(ct);

        public Task<PrResult<PrReference>> CreatePullRequestAsync(string workingDirectory, string title, string body, string baseBranch, string headBranch, bool draft = false, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PrResult<PrReference>> EditPullRequestAsync(string workingDirectory, int number, string? title = null, string? body = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PrResult<PrChecksSnapshot>> GetChecksAsync(string workingDirectory, int number, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PrResult<PrMergeState>> GetMergeStateAsync(string workingDirectory, int number, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PrResult<PrText>> GetFailedJobLogAsync(string workingDirectory, long? runId, long jobId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PrResult<IReadOnlyList<PrReviewThread>>> GetReviewThreadsAsync(string workingDirectory, int number, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PrResult<PrText>> GetPullRequestDiffAsync(string workingDirectory, int number, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PrResult<PrReviewComment>> ReplyToReviewThreadAsync(string workingDirectory, string threadId, string body, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PrResult<PrThreadResolution>> ResolveReviewThreadAsync(string workingDirectory, string threadId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
