using Forge.Abstractions;
using Xunit;

namespace Forge.Workspace.Tests.Git;

public sealed class GitToolsTests
{
    private static GitTools CreateTools(out GitReviewLedger ledger)
    {
        ledger = new GitReviewLedger();
        return new GitTools(new GitCli(), new GitToolSettings(), ledger);
    }

    private static string SeedTracked(RemoteSandbox sandbox, string name = "alice")
    {
        var repo = sandbox.Clone(name);
        sandbox.CommitFile(repo, "base.txt", "base\n", "seed");
        IsolatedGit.RunChecked(repo, "push", "-q", "-u", "origin", "main");
        return repo;
    }

    private static Task<ToolResult> InvokeAsync(
        ITool tool, string session, string repo, string json, CancellationToken ct = default) =>
        tool.InvokeAsync(
            new ToolCall(Guid.NewGuid().ToString("n"), tool.Definition.Name, json),
            new ToolContext(session, repo),
            ct).AsTask();

    private static ITool ToolByName(GitTools tools, string name) =>
        Assert.Single(tools.All, tool => tool.Definition.Name == name);

    [Fact]
    public void Definitions_expose_all_six_tools_with_documented_names()
    {
        var tools = CreateTools(out _);

        Assert.Equal(6, tools.All.Count);
        Assert.Equal(
            new[] { "git_branch", "git_diff", "git_fetch", "git_log", "git_status", "git_sync" },
            tools.All.Select(static tool => tool.Definition.Name).OrderBy(static name => name));
        Assert.Equal(GitTools.StatusName, tools.Status.Definition.Name);
        Assert.Equal(GitTools.LogName, tools.Log.Definition.Name);
        Assert.Equal(GitTools.DiffName, tools.Diff.Definition.Name);
        Assert.Equal(GitTools.FetchName, tools.Fetch.Definition.Name);
        Assert.Equal(GitTools.BranchName, tools.Branch.Definition.Name);
        Assert.Equal(GitTools.SyncName, tools.Sync.Definition.Name);
    }

    [Fact]
    public void Side_effects_split_read_only_tools_from_write_tools()
    {
        var tools = CreateTools(out _);

        Assert.Equal(ToolSideEffect.ReadOnly, tools.Status.SideEffect);
        Assert.Equal(ToolSideEffect.ReadOnly, tools.Log.SideEffect);
        Assert.Equal(ToolSideEffect.ReadOnly, tools.Diff.SideEffect);
        Assert.Equal(ToolSideEffect.ReadOnly, tools.Fetch.SideEffect);
        Assert.Equal(ToolSideEffect.Write, tools.Branch.SideEffect);
        Assert.Equal(ToolSideEffect.Write, tools.Sync.SideEffect);
    }

    [Theory]
    [InlineData("git_status")]
    [InlineData("git_log")]
    [InlineData("git_diff")]
    [InlineData("git_fetch")]
    [InlineData("git_branch")]
    [InlineData("git_sync")]
    public async Task Invalid_json_returns_an_error_result_instead_of_throwing(string name)
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        var tools = CreateTools(out _);

        var result = await InvokeAsync(ToolByName(tools, name), "session-1", repo, "not json{{");

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Branch_with_unknown_operation_returns_an_error_result()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        var tools = CreateTools(out _);

        var result = await InvokeAsync(tools.Branch, "session-1", repo, "{\"operation\": \"frobnicate\"}");

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Sync_with_unknown_operation_returns_an_error_result()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        var tools = CreateTools(out _);

        var result = await InvokeAsync(tools.Sync, "session-1", repo, "{\"operation\": \"frobnicate\"}");

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Status_output_contains_branch_and_ahead_behind()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        sandbox.CommitFile(repo, "local.txt", "local\n", "local work");
        var tools = CreateTools(out _);

        var result = await InvokeAsync(tools.Status, "session-1", repo, "{}");

        Assert.False(result.IsError);
        Assert.Contains("main", result.Content, StringComparison.Ordinal);
        Assert.Contains("ahead", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Log_respects_limit()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        sandbox.CommitFile(repo, "f.txt", "2\n", "second-subject");
        sandbox.CommitFile(repo, "f.txt", "3\n", "third-subject");
        sandbox.CommitFile(repo, "f.txt", "4\n", "fourth-subject");
        sandbox.CommitFile(repo, "f.txt", "5\n", "fifth-subject");
        var tools = CreateTools(out _);

        var result = await InvokeAsync(tools.Log, "session-1", repo, "{\"limit\": 2}");

        Assert.False(result.IsError);
        var lines = result.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("fifth-subject", result.Content, StringComparison.Ordinal);
        Assert.Contains("fourth-subject", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Full_diff_read_records_head_in_the_ledger()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        var head = IsolatedGit.Head(repo);
        var tools = CreateTools(out var ledger);

        var result = await InvokeAsync(tools.Diff, "session-1", repo, "{}");

        Assert.False(result.IsError);
        Assert.Equal(head, ledger.LastReviewed("session-1", repo));
    }

    [Fact]
    public async Task Paths_filtered_or_stat_only_diff_does_not_record_head()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        var tools = CreateTools(out var ledger);
        var first = await InvokeAsync(tools.Diff, "session-1", repo, "{}");
        Assert.False(first.IsError);
        var recorded = IsolatedGit.Head(repo);
        Assert.Equal(recorded, ledger.LastReviewed("session-1", repo));

        var head = sandbox.CommitFile(repo, "other.txt", "other\n", "second");
        Assert.NotEqual(recorded, head);

        var filtered = await InvokeAsync(tools.Diff, "session-1", repo, "{\"paths\": [\"base.txt\"]}");
        Assert.False(filtered.IsError);
        Assert.Equal(recorded, ledger.LastReviewed("session-1", repo));

        var statOnly = await InvokeAsync(tools.Diff, "session-1", repo, "{\"stat_only\": true}");
        Assert.False(statOnly.IsError);
        Assert.Equal(recorded, ledger.LastReviewed("session-1", repo));
    }

    [Fact]
    public async Task Diff_includes_untracked_file_content()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        File.WriteAllText(
            Path.Combine(repo, "untracked-note.txt"),
            "untracked-marker-7f3a9c\n",
            new System.Text.UTF8Encoding(false));
        var tools = CreateTools(out _);

        var result = await InvokeAsync(tools.Diff, "session-1", repo, "{}");

        Assert.False(result.IsError);
        Assert.Contains("untracked-note.txt", result.Content, StringComparison.Ordinal);
        Assert.Contains("untracked-marker-7f3a9c", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_reports_the_new_position()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var alice = SeedTracked(sandbox, "alice");
        var bob = sandbox.Clone("bob");
        var tip = sandbox.CommitFile(bob, "remote.txt", "remote\n", "remote work");
        IsolatedGit.RunChecked(bob, "push", "-q", "origin", "main");
        var tools = CreateTools(out _);

        var result = await InvokeAsync(tools.Fetch, "session-1", alice, "{\"remote\": \"origin\"}");

        Assert.False(result.IsError);
        Assert.Equal(tip, IsolatedGit.RunChecked(alice, "rev-parse", "origin/main").Trim());
    }

    [Fact]
    public async Task Branch_tool_creates_and_switches()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        var tools = CreateTools(out _);

        var created = await InvokeAsync(tools.Branch, "session-1", repo, "{\"operation\": \"create\", \"name\": \"tool-feature\"}");
        Assert.False(created.IsError);
        var switched = await InvokeAsync(tools.Branch, "session-1", repo, "{\"operation\": \"switch\", \"name\": \"tool-feature\"}");
        Assert.False(switched.IsError);

        Assert.Equal("tool-feature", IsolatedGit.CurrentBranch(repo));
    }

    [Fact]
    public async Task Branch_tool_sets_upstream()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        IsolatedGit.RunChecked(repo, "checkout", "-q", "-b", "tool-upstream");
        var tools = CreateTools(out _);

        var result = await InvokeAsync(
            tools.Branch, "session-1", repo,
            "{\"operation\": \"set_upstream\", \"upstream\": \"origin/main\"}");

        Assert.False(result.IsError);
        Assert.Equal("origin/main", IsolatedGit.UpstreamOf(repo, "tool-upstream"));
    }

    [Fact]
    public async Task Branch_tool_refuses_to_delete_the_current_branch()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        var tools = CreateTools(out _);

        var result = await InvokeAsync(
            tools.Branch, "session-1", repo,
            "{\"operation\": \"delete\", \"name\": \"main\"}");

        Assert.True(result.IsError);
        Assert.Contains("main", IsolatedGit.BranchList(repo), StringComparer.Ordinal);
    }

    [Fact]
    public async Task Sync_push_sets_upstream_by_default()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        IsolatedGit.RunChecked(repo, "checkout", "-q", "-b", "push-default");
        sandbox.CommitFile(repo, "p.txt", "p\n", "push work");
        var tools = CreateTools(out _);

        var result = await InvokeAsync(tools.Sync, "session-1", repo, "{\"operation\": \"push\"}");

        Assert.False(result.IsError);
        Assert.Equal("origin/push-default", IsolatedGit.UpstreamOf(repo, "push-default"));
    }

    [Fact]
    public async Task Sync_merge_conflict_result_lists_conflicted_paths()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var alice = SeedTracked(sandbox, "alice");
        var bob = sandbox.Clone("bob");
        sandbox.CommitFile(alice, "clash.txt", "alice\n", "alice work");
        sandbox.CommitFile(bob, "clash.txt", "bob\n", "bob work");
        IsolatedGit.RunChecked(bob, "push", "-q", "origin", "main");
        var tools = CreateTools(out _);

        var fetched = await InvokeAsync(tools.Fetch, "session-1", alice, "{\"remote\": \"origin\"}");
        Assert.False(fetched.IsError);
        var result = await InvokeAsync(
            tools.Sync, "session-1", alice,
            "{\"operation\": \"merge\", \"onto\": \"origin/main\"}");

        Assert.True(result.IsError);
        Assert.Contains("Conflict", result.Content, StringComparison.Ordinal);
        Assert.Contains("clash.txt", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_token_propagates_for_a_read_tool()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        var tools = CreateTools(out _);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokeAsync(tools.Status, "session-1", repo, "{}", cancelled.Token));
    }

    [Fact]
    public async Task Cancelled_token_propagates_for_a_write_tool()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        var tools = CreateTools(out _);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokeAsync(
                tools.Branch, "session-1", repo,
                "{\"operation\": \"create\", \"name\": \"cancelled-branch\"}",
                cancelled.Token));
    }
}
