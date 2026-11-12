using Xunit;

namespace Forge.Workspace.Tests.Git;

public sealed class GitCliConflictTests
{
    private static GitCli Cli() => new();

    /// <summary>Clones origin and builds one <c>clash.txt</c> conflict between <c>side</c> and <c>main</c>;
    /// returns the clone, still on <c>main</c> with the merge or rebase not yet started.</summary>
    private static string ConflictedRepo(RemoteSandbox sandbox)
    {
        var repo = sandbox.Clone("work");
        sandbox.CommitFile(repo, "clash.txt", "base\n", "seed");
        IsolatedGit.RunChecked(repo, "checkout", "-q", "-b", "side");
        sandbox.CommitFile(repo, "clash.txt", "side\n", "side work");
        IsolatedGit.RunChecked(repo, "checkout", "-q", "main");
        sandbox.CommitFile(repo, "clash.txt", "main\n", "main work");
        return repo;
    }

    [Fact]
    public async Task Merge_conflict_reports_conflict_and_state()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = ConflictedRepo(sandbox);

        var merge = await Cli().MergeAsync(repo, "side");

        Assert.False(merge.Success);
        Assert.Equal(GitRemoteFailureKind.Conflict, merge.Failure);
        var state = await Cli().GetConflictStateAsync(repo);
        Assert.Equal(GitOperationKind.Merge, state.Operation);
        Assert.True(state.InProgress);
        Assert.Contains("clash.txt", state.ConflictedPaths, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Continue_with_unresolved_paths_reports_conflict()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = ConflictedRepo(sandbox);
        var merge = await Cli().MergeAsync(repo, "side");
        Assert.Equal(GitRemoteFailureKind.Conflict, merge.Failure);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var continued = await Cli().ContinueOperationAsync(repo, timeout.Token);

        Assert.False(continued.Success);
        Assert.Equal(GitRemoteFailureKind.Conflict, continued.Failure);
        Assert.True((await Cli().GetConflictStateAsync(repo)).InProgress);
    }

    [Fact]
    public async Task Resolve_stage_and_continue_completes_the_merge()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = ConflictedRepo(sandbox);
        var merge = await Cli().MergeAsync(repo, "side");
        Assert.Equal(GitRemoteFailureKind.Conflict, merge.Failure);

        File.WriteAllText(Path.Combine(repo, "clash.txt"), "resolved\n", new System.Text.UTF8Encoding(false));
        IsolatedGit.RunChecked(repo, "add", "clash.txt");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var continued = await Cli().ContinueOperationAsync(repo, timeout.Token);

        Assert.False(timeout.IsCancellationRequested);
        Assert.True(continued.Success);
        var state = await Cli().GetConflictStateAsync(repo);
        Assert.False(state.InProgress);
        Assert.Equal(GitOperationKind.None, state.Operation);
        Assert.Empty(state.ConflictedPaths);
        Assert.Equal("resolved\n", File.ReadAllText(Path.Combine(repo, "clash.txt")));
    }

    [Fact]
    public async Task Abort_merge_restores_the_original_head()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = ConflictedRepo(sandbox);
        var head = IsolatedGit.Head(repo);
        var merge = await Cli().MergeAsync(repo, "side");
        Assert.Equal(GitRemoteFailureKind.Conflict, merge.Failure);

        var abort = await Cli().AbortOperationAsync(repo);

        Assert.True(abort.Success);
        Assert.Equal(head, IsolatedGit.Head(repo));
        Assert.False((await Cli().GetConflictStateAsync(repo)).InProgress);
    }

    [Fact]
    public async Task Rebase_conflict_reports_rebase_state_and_abort_restores_head()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.Clone("work");
        sandbox.CommitFile(repo, "clash.txt", "base\n", "seed");
        IsolatedGit.RunChecked(repo, "checkout", "-q", "-b", "topic");
        var head = sandbox.CommitFile(repo, "clash.txt", "topic\n", "topic work");
        IsolatedGit.RunChecked(repo, "checkout", "-q", "main");
        sandbox.CommitFile(repo, "clash.txt", "main\n", "main work");
        IsolatedGit.RunChecked(repo, "checkout", "-q", "topic");

        var rebase = await Cli().RebaseAsync(repo, "main");

        Assert.False(rebase.Success);
        Assert.Equal(GitRemoteFailureKind.Conflict, rebase.Failure);
        var state = await Cli().GetConflictStateAsync(repo);
        Assert.Equal(GitOperationKind.Rebase, state.Operation);
        Assert.Contains("clash.txt", state.ConflictedPaths, StringComparer.Ordinal);

        var abort = await Cli().AbortOperationAsync(repo);

        Assert.True(abort.Success);
        Assert.Equal(head, IsolatedGit.Head(repo));
        Assert.Equal("topic", IsolatedGit.CurrentBranch(repo));
    }

    [Fact]
    public async Task Clean_repository_reports_no_operation_in_progress()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.Clone("work");
        sandbox.CommitFile(repo, "file.txt", "content\n", "seed");

        var state = await Cli().GetConflictStateAsync(repo);

        Assert.Equal(GitOperationKind.None, state.Operation);
        Assert.False(state.InProgress);
        Assert.Empty(state.ConflictedPaths);
    }

    [Fact]
    public async Task Abort_with_nothing_in_progress_succeeds()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.Clone("work");
        sandbox.CommitFile(repo, "file.txt", "content\n", "seed");

        var abort = await Cli().AbortOperationAsync(repo);

        Assert.True(abort.Success);
    }

    [Fact]
    public async Task Continue_with_nothing_in_progress_succeeds()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.Clone("work");
        sandbox.CommitFile(repo, "file.txt", "content\n", "seed");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var continued = await Cli().ContinueOperationAsync(repo, timeout.Token);

        Assert.False(timeout.IsCancellationRequested);
        Assert.True(continued.Success);
    }
}
