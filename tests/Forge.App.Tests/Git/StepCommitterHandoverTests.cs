using Forge.Abstractions;
using Forge.App.Git;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// <see cref="StepCommitter.CommitHandoverAsync"/> (TASK-session-worktree-lifecycle 6.2 step 4) against real git
/// repositories: the handover commit uses the given message, ignores the step-commit preference, writes nothing to the
/// todo store, and keeps the step path's guards (clean tree, not a repository, operation in progress, detached HEAD).
/// </summary>
public sealed class StepCommitterHandoverTests
{
    private const string Message = "handover: old session";

    private static CancellationTokenSource Bound() => new(TimeSpan.FromSeconds(90));

    [Fact]
    public async Task Commits_everything_with_the_message_even_with_step_commits_off()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        harness.Enabled = false;
        harness.Repo.WriteFile("new.txt", "x\n");
        using var cts = Bound();

        var result = await harness.Committer.CommitHandoverAsync(harness.Repo.Path, Message, cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, result.Outcome);
        Assert.Equal(await harness.Repo.HeadAsync(), result.CommitId);
        Assert.Equal(Message, await harness.Repo.LastMessageAsync());
        Assert.Equal(string.Empty, await harness.Repo.StatusAsync());
        Assert.Empty(await harness.Store.ListAsync(StepCommitHarness.SessionId, default));
    }

    [Fact]
    public async Task A_clean_tree_reports_no_changes_and_commits_nothing()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var head = await harness.Repo.HeadAsync();
        using var cts = Bound();

        var result = await harness.Committer.CommitHandoverAsync(harness.Repo.Path, Message, cts.Token);

        Assert.Equal(StepCommitOutcome.NoChanges, result.Outcome);
        Assert.Equal(head, await harness.Repo.HeadAsync());
    }

    [Fact]
    public async Task A_folder_that_is_not_a_repository_is_skipped()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var plain = Path.Combine(harness.Repo.Root, "plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "a.txt"), "a");
        using var cts = Bound();

        var result = await harness.Committer.CommitHandoverAsync(plain, Message, cts.Token);

        Assert.Equal(StepCommitOutcome.Skipped, result.Outcome);
    }

    [Fact]
    public async Task A_merge_in_progress_fails_and_commits_nothing()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        await harness.Repo.StartMergeInProgressAsync();
        var head = await harness.Repo.HeadAsync();
        using var cts = Bound();

        var result = await harness.Committer.CommitHandoverAsync(harness.Repo.Path, Message, cts.Token);

        Assert.Equal(StepCommitOutcome.Failed, result.Outcome);
        Assert.Equal(head, await harness.Repo.HeadAsync());
    }

    [Fact]
    public async Task A_detached_head_fails_and_commits_nothing()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        await harness.Repo.GitAsync("checkout", "-q", "--detach");
        harness.Repo.WriteFile("new.txt", "x\n");
        var head = await harness.Repo.HeadAsync();
        using var cts = Bound();

        var result = await harness.Committer.CommitHandoverAsync(harness.Repo.Path, Message, cts.Token);

        Assert.Equal(StepCommitOutcome.Failed, result.Outcome);
        Assert.Contains("detached", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(head, await harness.Repo.HeadAsync());
    }

    [Fact]
    public async Task A_rejecting_hook_fails_and_leaves_the_changes_unstaged()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        harness.Repo.InstallHook("pre-commit", "exit 1");
        harness.Repo.WriteFile("new.txt", "x\n");
        var head = await harness.Repo.HeadAsync();
        using var cts = Bound();

        var result = await harness.Committer.CommitHandoverAsync(harness.Repo.Path, Message, cts.Token);

        Assert.Equal(StepCommitOutcome.Failed, result.Outcome);
        Assert.Equal(head, await harness.Repo.HeadAsync());
        Assert.Equal("?? new.txt", await harness.Repo.StatusAsync());
    }
}
