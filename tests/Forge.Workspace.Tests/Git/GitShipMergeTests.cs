using Forge.Workspace;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// The two git-level capabilities the files panel's 提交目标 row added: merging a branch into another checkout's branch, and
/// deleting a branch on the remote. Both run against real repositories on disk (a bare "remote" plus two clones), because the
/// refusals this code exists for are git's own (dirty tree, wrong checkout, diverged history, a branch in use).
/// </summary>
public sealed class GitShipMergeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-ship-" + Guid.NewGuid().ToString("n"));

    public GitShipMergeTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Merge_fast_forwards_a_session_branch_into_the_checkout_on_main()
    {
        var (git, _, main, work) = await FixtureAsync();
        CommitInWorktree(work, "feature.txt", "f\n", "feature");

        var merged = await git.MergeAsync(main, "session-x", noFastForward: false);

        Assert.True(merged.Success, merged.Message + merged.StandardError);
        Assert.Equal("feature", Run(main, "log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public async Task Merge_with_noFastForward_records_a_merge_commit_after_the_branches_diverged()
    {
        var (git, _, main, work) = await FixtureAsync();
        CommitInWorktree(work, "feature.txt", "f\n", "feature");
        CommitInWorktree(main, "later.txt", "l\n", "later");

        // This is the write the row runs after the user confirmed a merge commit: two parents, not a fast-forward.
        var merged = await git.MergeAsync(main, "session-x", noFastForward: true);

        Assert.True(merged.Success, merged.Message + merged.StandardError);
        Assert.Equal(2, Run(main, "rev-list", "--parents", "-1", "HEAD").Split(' ').Length - 1);
    }

    [Fact]
    public async Task Delete_remote_branch_removes_it_from_the_remote_and_reports_it()
    {
        var (git, remote, main, _) = await FixtureAsync();
        Run(main, "push", "-q", "origin", "HEAD:refs/heads/throwaway");
        Assert.NotEmpty(Run(remote, "branch", "--list", "throwaway"));

        var deleted = await git.DeleteRemoteBranchAsync(main, "origin", "throwaway");

        Assert.True(deleted.Success, deleted.Message + deleted.StandardError);
        Assert.Empty(Run(remote, "branch", "--list", "throwaway"));
    }

    [Fact]
    public async Task Delete_remote_branch_rejects_option_like_names()
    {
        var (git, remote, main, _) = await FixtureAsync();

        var deleted = await git.DeleteRemoteBranchAsync(main, "origin", "--mirror");

        Assert.False(deleted.Success);
        Assert.NotEmpty(Run(remote, "branch", "--list"));
    }

    [Theory]
    [InlineData(PrMergeMethod.Squash, "--squash")]
    [InlineData(PrMergeMethod.Merge, "--merge")]
    [InlineData(PrMergeMethod.Rebase, "--rebase")]
    public async Task Merge_pull_request_asks_gh_for_the_confirmed_strategy(PrMergeMethod method, string flag)
    {
        using var fake = new SmokeFakeGh(stdout: "merged");

        var merged = await new GitHubPlatform(fake.Cli)
            .MergePullRequestAsync(fake.WorkingDirectory, 7, method);

        Assert.True(merged.Success);
        var call = Assert.Single(fake.Calls);
        Assert.Contains("pr merge 7", call, StringComparison.Ordinal);
        Assert.Contains(flag, call, StringComparison.Ordinal);
        Assert.DoesNotContain("--delete-branch", call, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_pull_request_adds_the_delete_flag_only_when_asked()
    {
        using var fake = new SmokeFakeGh(stdout: "merged");

        var merged = await new GitHubPlatform(fake.Cli)
            .MergePullRequestAsync(fake.WorkingDirectory, 7, PrMergeMethod.Squash, deleteBranch: true);

        Assert.True(merged.Success);
        Assert.Contains("--delete-branch", Assert.Single(fake.Calls), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_pull_request_rejects_a_non_positive_number_and_reports_a_failure()
    {
        using var fake = new SmokeFakeGh(exitCode: 1, stderr: "boom");
        var platform = new GitHubPlatform(fake.Cli);

        var invalid = await platform.MergePullRequestAsync(fake.WorkingDirectory, 0, PrMergeMethod.Squash);
        var failed = await platform.MergePullRequestAsync(fake.WorkingDirectory, 7, PrMergeMethod.Squash);

        Assert.False(invalid.Success);
        Assert.Empty(fake.Calls.Take(fake.Calls.Count - 1));
        Assert.False(failed.Success);
        Assert.False(string.IsNullOrWhiteSpace(failed.Status.Message));
    }

    /// <summary>A bare "remote" plus a clone on <c>main</c> and a second clone used as the session worktree.</summary>
    private async Task<(GitCli Git, string Remote, string Main, string Work)> FixtureAsync()
    {
        var remote = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(remote);
        Run(remote, "init", "--bare", "-q", "-b", "main");

        var seed = Path.Combine(root, "seed");
        Run(root, "clone", "-q", remote, seed);
        Run(seed, "checkout", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(seed, "readme.txt"), "seed\n");
        Run(seed, "add", "-A");
        Run(seed, "commit", "-q", "-m", "seed");
        Run(seed, "push", "-q", "-u", "origin", "main");
        Run(remote, "symbolic-ref", "HEAD", "refs/heads/main");

        var main = Path.Combine(root, "main");
        Run(root, "clone", "-q", remote, main);

        // A Forge session lives in a linked worktree of the same repository (refs are shared), not in a second clone.
        var work = Path.Combine(root, "work");
        Run(main, "worktree", "add", "-q", "-b", "session-x", work);

        // A real repository has an identity; the merge commit path needs one, and IsolatedGit only pins it for its own runs.
        Run(main, "config", "user.name", "Forge Test");
        Run(main, "config", "user.email", "forge@example.test");

        var git = new GitCli();
        Assert.True(await git.IsRepositoryAsync(main));
        return (git, remote, main, work);
    }

    private static void CommitInWorktree(string directory, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(directory, file), content);
        Run(directory, "add", "-A");
        Run(directory, "commit", "-q", "-m", message);
    }

    private static string Run(string directory, params string[] arguments)
    {
        var (exitCode, output, error) = IsolatedGit.Run(directory, arguments);
        Assert.True(exitCode == 0, $"git {string.Join(' ', arguments)} failed ({exitCode}): {error}");
        return output;
    }
}
