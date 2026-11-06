using Forge.Abstractions;
using Xunit;

namespace Forge.Workspace.Tests.Git;

public sealed class ReviewCommentSmokeTests : IDisposable
{
    private readonly TempDirectory temp = new();
    private readonly ReviewStore store;
    private readonly PrCommentTools tools;

    public ReviewCommentSmokeTests()
    {
        store = new ReviewStore(temp.Combine("data"));
        tools = new PrCommentTools(new GitCli(), new FakePrPlatform(), new GitToolSettings(), new PrReplyLedger(), store);
        WorkspacePath = temp.Combine("work");
        OtherPath = temp.Combine("elsewhere");
    }

    private string WorkspacePath { get; }

    private string OtherPath { get; }

    public void Dispose() => temp.Dispose();

    private async Task<long> AddAsync(
        string file,
        int line,
        string body,
        int? endLine = null,
        string? workspace = null,
        string? baseCommit = null,
        bool sent = false,
        bool resolved = false)
    {
        var comment = await store.AddAsync(new NewReviewComment
        {
            WorkspacePath = workspace ?? WorkspacePath,
            SessionId = "session-1",
            FilePath = file,
            NewLineStart = line,
            NewLineEnd = endLine,
            Body = body,
            BaseCommit = baseCommit,
        });
        if (sent)
        {
            await store.MarkSentAsync([comment.Id]);
        }

        if (resolved)
        {
            await store.SetResolvedAsync(comment.Id, true);
        }

        return comment.Id;
    }

    private Task<ToolResult> ListAsync(string json = "{}") =>
        PrCommentHarness.InvokeAsync(tools.ReviewComments!, WorkspacePath, json);

    private Task<ToolResult> ResolveAsync(string json, CancellationToken ct = default) =>
        PrCommentHarness.InvokeAsync(tools.ReviewResolve!, WorkspacePath, json, ct);

    private static string ResolveJson(long id, string verdict, string? note = null) =>
        note is null
            ? $$"""{"id":{{id}},"verdict":"{{verdict}}"}"""
            : $$"""{"id":{{id}},"verdict":"{{verdict}}","note":"{{note}}"}""";

    [Fact]
    public async Task Unresolved_comments_are_listed_in_file_and_line_order_with_state_and_base_commit()
    {
        var second = await AddAsync("src/B.cs", 5, "rename this\nand that", baseCommit: "0123456789abcdef");
        var first = await AddAsync("src/A.cs", 20, "extract a method", endLine: 24, sent: true);
        var third = await AddAsync("src/A.cs", 3, "typo");
        await AddAsync("src/Done.cs", 1, "already handled", resolved: true);

        var result = await ListAsync();

        Assert.False(result.IsError, result.Content);
        var text = result.Content;
        Assert.Contains("Local review comments: 3 unresolved, 1 resolved.", text);
        Assert.Contains("Showing unresolved comments; pass all=true", text);
        Assert.Contains($"Comment {first} [unresolved, sent] src/A.cs:20-24", text);
        Assert.Contains($"Comment {third} [unresolved, draft] src/A.cs:3 ", text);
        Assert.Contains($"Comment {second} [unresolved, draft] src/B.cs:5 ", text);
        Assert.Contains("base 0123456", text);
        Assert.Contains("     rename this\n     and that\n", text);
        Assert.DoesNotContain("already handled", text);

        var order = new[] { $"Comment {third} ", $"Comment {first} ", $"Comment {second} " }
            .Select(marker => text.IndexOf(marker, StringComparison.Ordinal))
            .ToArray();
        Assert.True(order[0] >= 0 && order[0] < order[1] && order[1] < order[2], text);
        Assert.Contains("review_comment_resolve", text);
    }

    [Fact]
    public async Task All_includes_resolved_comments()
    {
        await AddAsync("src/A.cs", 1, "open one");
        var done = await AddAsync("src/Done.cs", 1, "already handled", resolved: true);

        var result = await ListAsync("""{"all":true}""");

        Assert.Contains("Showing all comments.", result.Content);
        Assert.Contains($"Comment {done} [resolved, draft] src/Done.cs:1", result.Content);
        Assert.Contains("already handled", result.Content);
    }

    [Fact]
    public async Task Only_the_comments_of_this_worktree_are_listed()
    {
        await AddAsync("src/Mine.cs", 1, "mine");
        await AddAsync("src/Theirs.cs", 1, "theirs", workspace: OtherPath);

        var result = await ListAsync();

        Assert.Contains("1 unresolved, 0 resolved", result.Content);
        Assert.Contains("mine", result.Content);
        Assert.DoesNotContain("theirs", result.Content);
    }

    [Fact]
    public async Task Empty_and_fully_resolved_stores_are_told_apart()
    {
        var empty = await ListAsync();
        await AddAsync("src/Done.cs", 1, "done", resolved: true);
        var allDone = await ListAsync();

        Assert.Contains("There are no local review comments.", empty.Content);
        Assert.Contains("There are no unresolved local review comments.", allDone.Content);
        Assert.DoesNotContain("Next:", allDone.Content);
    }

    [Fact]
    public async Task Listing_rejects_unknown_properties_and_changes_nothing()
    {
        var id = await AddAsync("src/A.cs", 1, "open");

        var result = await ListAsync("""{"resolve":true}""");

        Assert.True(result.IsError);
        Assert.False((await store.GetAsync(id))!.IsResolved);
    }

    [Fact]
    public async Task Apply_marks_the_comment_resolved_in_the_store()
    {
        var id = await AddAsync("src/A.cs", 12, "please fix");
        var untouched = await AddAsync("src/A.cs", 13, "leave me open");

        var result = await ResolveAsync(ResolveJson(id, "apply"));

        Assert.False(result.IsError, result.Content);
        Assert.Contains($"Resolved review comment {id} (src/A.cs:12), verdict apply.", result.Content);
        Assert.True((await store.GetAsync(id))!.IsResolved);
        Assert.False((await store.GetAsync(untouched))!.IsResolved);
    }

    [Fact]
    public async Task Push_back_with_a_note_resolves_and_echoes_the_note()
    {
        var id = await AddAsync("src/A.cs", 12, "please fix");

        var result = await ResolveAsync(ResolveJson(id, "push_back", "  Out of scope for this change.  "));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("verdict push_back.", result.Content);
        Assert.EndsWith("Note: Out of scope for this change.", result.Content);
        Assert.True((await store.GetAsync(id))!.IsResolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Push_back_without_reasons_is_refused_and_leaves_the_comment_open(string? note)
    {
        var id = await AddAsync("src/A.cs", 12, "please fix");

        var result = await ResolveAsync(ResolveJson(id, "push_back", note));

        Assert.True(result.IsError);
        Assert.Contains("needs 'note'", result.Content);
        Assert.False((await store.GetAsync(id))!.IsResolved);
    }

    [Fact]
    public async Task Clarify_cannot_resolve_a_local_comment()
    {
        var id = await AddAsync("src/A.cs", 12, "please fix");

        var result = await ResolveAsync(ResolveJson(id, "clarify", "which one?"));

        Assert.True(result.IsError);
        Assert.Contains("ask the user", result.Content);
        Assert.False((await store.GetAsync(id))!.IsResolved);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("Apply")]
    [InlineData("")]
    public async Task An_unknown_verdict_is_refused(string verdict)
    {
        var id = await AddAsync("src/A.cs", 12, "please fix");

        var result = await ResolveAsync(ResolveJson(id, verdict));

        Assert.True(result.IsError);
        Assert.False((await store.GetAsync(id))!.IsResolved);
    }

    [Fact]
    public async Task An_unknown_id_is_a_tool_error_that_points_at_the_listing()
    {
        var result = await ResolveAsync(ResolveJson(9999, "apply"));

        Assert.True(result.IsError);
        Assert.Contains("There is no local review comment 9999", result.Content);
        Assert.Contains("review_comments", result.Content);
    }

    [Fact]
    public async Task A_comment_of_another_workspace_cannot_be_resolved_from_this_session()
    {
        var foreign = await AddAsync("src/Theirs.cs", 1, "theirs", workspace: OtherPath);

        var result = await ResolveAsync(ResolveJson(foreign, "apply"));

        Assert.True(result.IsError);
        Assert.Contains("belongs to another workspace", result.Content);
        Assert.False((await store.GetAsync(foreign))!.IsResolved);
    }

    [Fact]
    public async Task An_already_resolved_comment_is_left_alone_without_an_error()
    {
        var id = await AddAsync("src/A.cs", 12, "please fix", resolved: true);
        var before = await store.GetAsync(id);

        var result = await ResolveAsync(ResolveJson(id, "apply"));

        Assert.False(result.IsError, result.Content);
        Assert.Contains($"Review comment {id} is already resolved. Nothing to do.", result.Content);
        Assert.Equal(before!.UpdatedAt, (await store.GetAsync(id))!.UpdatedAt);
    }

    [Theory]
    [InlineData("""{"verdict":"apply"}""")]
    [InlineData("""{"id":1}""")]
    [InlineData("""{"id":0,"verdict":"apply"}""")]
    [InlineData("""{"id":-3,"verdict":"apply"}""")]
    [InlineData("""{"id":"1","verdict":"apply"}""")]
    [InlineData("""{"id":1.5,"verdict":"apply"}""")]
    [InlineData("""{"id":1,"verdict":"apply","force":true}""")]
    [InlineData("not json{{")]
    public async Task Bad_arguments_are_tool_errors_that_resolve_nothing(string json)
    {
        var id = await AddAsync("src/A.cs", 12, "please fix");
        Assert.Equal(1, id);

        var result = await ResolveAsync(json);

        Assert.True(result.IsError);
        Assert.False((await store.GetAsync(id))!.IsResolved);
    }

    [Fact]
    public async Task A_cancelled_resolve_throws_and_leaves_the_comment_open()
    {
        var id = await AddAsync("src/A.cs", 12, "please fix");
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResolveAsync(ResolveJson(id, "apply"), source.Token));

        Assert.False((await store.GetAsync(id))!.IsResolved);
    }

    [Fact]
    public async Task Resolving_removes_the_comment_from_the_default_listing()
    {
        var id = await AddAsync("src/A.cs", 12, "please fix");
        await AddAsync("src/B.cs", 1, "still open");

        await ResolveAsync(ResolveJson(id, "apply"));
        var result = await ListAsync();

        Assert.Contains("1 unresolved, 1 resolved", result.Content);
        Assert.DoesNotContain("please fix", result.Content);
        Assert.Contains("still open", result.Content);
    }
}
