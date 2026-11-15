using Forge.App.Git;
using Forge.Core.Tools;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// The app's git evidence for the step completion gate (<see cref="GitStepChangeProbe"/>): which sessions have nothing of
/// their own to judge, which worktrees are clean or dirty, and which failures answer "unknown" instead of throwing. The
/// probe reads a real repository through the real <see cref="GitCli"/>, the way the composition root wires it.
/// </summary>
public sealed class StepChangeProbeTests
{
    private static Task<StepChangeProbe> ProbeAsync(
        StepCommitRepo repo,
        string sessionId = StepCommitHarness.SessionId,
        Func<string, CancellationToken, Task<WorktreeResolution>>? resolver = null) =>
        new GitStepChangeProbe(StepCommitRepo.Cli, resolver ?? Resolver(repo)).ProbeAsync(sessionId, repo.Path, CancellationToken.None);

    private static Func<string, CancellationToken, Task<WorktreeResolution>> Resolver(StepCommitRepo repo) =>
        (_, _) => Task.FromResult(WorktreeResolution.Own(repo.Path));

    [Fact]
    public async Task A_committed_worktree_answers_clean()
    {
        await using var harness = await StepCommitHarness.CreateAsync();

        var answer = await ProbeAsync(harness.Repo);

        Assert.Equal(StepChangeState.Clean, answer.State);
    }

    [Theory]
    [InlineData("untracked")]   // a new file git does not know yet
    [InlineData("modified")]    // an edit to a file the seed commit tracks
    public async Task A_worktree_with_changes_answers_dirty(string kind)
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        if (kind == "untracked")
        {
            harness.Repo.WriteFile("new.txt", "new\n");
        }
        else
        {
            File.WriteAllText(harness.Repo.FullPath("seed.txt"), "changed\n");
        }

        var answer = await ProbeAsync(harness.Repo);

        Assert.Equal(StepChangeState.Dirty, answer.State);
        Assert.Contains("1 uncommitted change", answer.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sub-agent")]
    [InlineData("no-worktree")]
    [InlineData("directory-gone")]
    [InlineData("not-a-repository")]
    public async Task Sessions_with_nothing_of_their_own_to_commit_answer_not_applicable(string kind)
    {
        var root = StepCommitRepo.NewRoot();
        try
        {
            var repo = await StepCommitRepo.CreateAsync(root);
            Func<string, CancellationToken, Task<WorktreeResolution>> resolver = kind switch
            {
                "sub-agent" => (_, _) => Task.FromResult(WorktreeResolution.SubAgent(repo.Path)),
                "no-worktree" => (_, _) => Task.FromResult(WorktreeResolution.None),
                "directory-gone" => (_, _) => Task.FromResult(WorktreeResolution.Own(Path.Combine(root, "vanished"))),
                _ => (_, _) => Task.FromResult(WorktreeResolution.Own(Path.Combine(root, "plain-folder"))),
            };
            Directory.CreateDirectory(Path.Combine(root, "plain-folder"));

            var answer = await new GitStepChangeProbe(StepCommitRepo.Cli, resolver)
                .ProbeAsync(StepCommitHarness.SessionId, repo.Path, CancellationToken.None);

            Assert.Equal(StepChangeState.NotApplicable, answer.State);
        }
        finally
        {
            StepCommitRepo.DeleteTree(root);
        }
    }

    [Fact]
    public async Task A_blank_session_id_answers_not_applicable_without_resolving()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var resolved = 0;

        var answer = await new GitStepChangeProbe(StepCommitRepo.Cli, (sessionId, _) =>
        {
            Interlocked.Increment(ref resolved);
            return Task.FromResult(WorktreeResolution.Own(harness.Repo.Path));
        }).ProbeAsync("", harness.Repo.Path, CancellationToken.None);

        Assert.Equal(StepChangeState.NotApplicable, answer.State);
        Assert.Equal(0, resolved);
    }

    [Fact]
    public async Task A_worktree_git_cannot_read_answers_unknown_with_the_reason()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        // A corrupt index leaves `git rev-parse --git-dir` working but `git status` failing, which is the split the
        // probe has to tell apart from a directory that is simply no repository.
        File.WriteAllText(Path.Combine(harness.Repo.Path, ".git", "index"), "not an index at all");

        var answer = await ProbeAsync(harness.Repo);

        Assert.Equal(StepChangeState.Unknown, answer.State);
        Assert.NotNull(answer.Detail);
        Assert.Contains("Could not read the git status", answer.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_resolver_that_fails_answers_unknown_with_the_reason()
    {
        await using var harness = await StepCommitHarness.CreateAsync();

        var answer = await ProbeAsync(
            harness.Repo,
            resolver: (_, _) => Task.FromException<WorktreeResolution>(new InvalidOperationException("the session store is closed")));

        Assert.Equal(StepChangeState.Unknown, answer.State);
        Assert.Contains("the session store is closed", answer.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_resolver_call_propagates_instead_of_answering()
    {
        await using var harness = await StepCommitHarness.CreateAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ProbeAsync(
                harness.Repo,
                resolver: (_, _) => Task.FromException<WorktreeResolution>(new OperationCanceledException())));
    }

    [Fact]
    public async Task A_cancelled_git_call_propagates_instead_of_answering_unknown()
    {
        await using var harness = await StepCommitHarness.CreateAsync();

        // The token is already cancelled, so the first git call throws: the probe must let that through instead of
        // turning the cancellation into an unknown refusal. Had the git catch swallowed OperationCanceledException,
        // the answer would come back Unknown and this would fail without throwing.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new GitStepChangeProbe(StepCommitRepo.Cli, Resolver(harness.Repo))
                .ProbeAsync(StepCommitHarness.SessionId, harness.Repo.Path, new CancellationToken(canceled: true)));
    }
}
