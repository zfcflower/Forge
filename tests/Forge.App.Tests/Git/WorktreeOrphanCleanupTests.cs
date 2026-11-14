using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins the cleanup of a worktree whose session row could not be written (<see cref="WorktreeService.TryRemoveCreatedAsync"/>
/// and the new-session flow that calls it): a fresh worktree and the branch created with it are both taken away; a
/// worktree that holds anything (uncommitted files, commits beyond its base) stays together with its branch; a branch git
/// does not consider merged stays after its worktree is gone; and no failure escapes, because the caller is already
/// reporting the original error.
/// </summary>
[Collection("forge-app-ui")]
public sealed class WorktreeOrphanCleanupTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(30);

    private static CancellationTokenSource Bound() => new(TimeSpan.FromSeconds(90));

    // ------------------------------------------------------------ the service

    [Fact]
    public async Task A_fresh_worktree_is_removed_together_with_its_branch()
    {
        using var repo = await StepCommitRepo.CreateAsync();
        var service = new WorktreeService(StepCommitRepo.Cli);
        using var cts = Bound();
        var worktree = await service.CreateAsync(repo.Path, "Orphan check", cts.Token);
        Assert.True(Directory.Exists(worktree.Path));
        Assert.Contains(worktree.Branch, await BranchesAsync(repo));

        await service.TryRemoveCreatedAsync(worktree, cts.Token);

        Assert.False(Directory.Exists(worktree.Path));
        Assert.DoesNotContain(worktree.Branch, await BranchesAsync(repo));
        Assert.Contains("main", await BranchesAsync(repo));
        Assert.Equal([Normalize(repo.Path)], await WorktreePathsAsync(repo));
        Assert.Equal(string.Empty, await repo.StatusAsync());
    }

    [Fact]
    public async Task A_second_call_for_the_same_worktree_is_harmless()
    {
        using var repo = await StepCommitRepo.CreateAsync();
        var service = new WorktreeService(StepCommitRepo.Cli);
        using var cts = Bound();
        var worktree = await service.CreateAsync(repo.Path, "Twice", cts.Token);
        await service.TryRemoveCreatedAsync(worktree, cts.Token);

        await service.TryRemoveCreatedAsync(worktree, cts.Token);

        Assert.False(Directory.Exists(worktree.Path));
        Assert.DoesNotContain(worktree.Branch, await BranchesAsync(repo));
        Assert.Contains("main", await BranchesAsync(repo));
    }

    [Theory]
    [InlineData("untracked")]
    [InlineData("modified")]
    [InlineData("staged")]
    public async Task A_worktree_with_uncommitted_work_stays_with_its_files_and_its_branch(string kind)
    {
        using var repo = await StepCommitRepo.CreateAsync();
        var service = new WorktreeService(StepCommitRepo.Cli);
        using var cts = Bound();
        var worktree = await service.CreateAsync(repo.Path, "Dirty " + kind, cts.Token);
        var file = Path.Combine(worktree.Path, kind == "modified" ? "seed.txt" : "work.txt");
        await File.WriteAllTextAsync(file, "precious\n", cts.Token);
        if (kind == "staged")
        {
            await StepCommitRepo.GitInAsync(worktree.Path, "add", "work.txt");
        }

        await service.TryRemoveCreatedAsync(worktree, cts.Token);

        Assert.True(Directory.Exists(worktree.Path));
        Assert.Equal("precious\n", await File.ReadAllTextAsync(file, cts.Token));
        Assert.Contains(worktree.Branch, await BranchesAsync(repo));
        Assert.Contains(Normalize(worktree.Path), await WorktreePathsAsync(repo));
    }

    [Fact]
    public async Task A_worktree_with_commits_beyond_its_base_stays_with_its_branch()
    {
        using var repo = await StepCommitRepo.CreateAsync();
        var service = new WorktreeService(StepCommitRepo.Cli);
        using var cts = Bound();
        var worktree = await service.CreateAsync(repo.Path, "Has commits", cts.Token);
        await CommitInAsync(worktree, "work.txt");

        await service.TryRemoveCreatedAsync(worktree, cts.Token);

        Assert.True(Directory.Exists(worktree.Path));
        Assert.Contains(worktree.Branch, await BranchesAsync(repo));
        Assert.Equal("wip", await StepCommitRepo.GitInAsync(worktree.Path, "log", "-1", "--format=%s"));
    }

    [Fact]
    public async Task A_branch_that_git_does_not_consider_merged_stays_after_its_worktree_is_removed_and_nothing_throws()
    {
        using var repo = await StepCommitRepo.CreateAsync();
        var service = new WorktreeService(StepCommitRepo.Cli);
        using var cts = Bound();
        var worktree = await service.CreateAsync(repo.Path, "Unmerged branch", cts.Token);
        var tip = await CommitInAsync(worktree, "work.txt");

        // The worktree is clean and nothing is ahead of its recorded base, so the worktree goes; the commit on its
        // branch is not merged into the repository's HEAD, so the non-forced branch delete refuses.
        var asRecorded = worktree with { BaseCommit = tip };

        await service.TryRemoveCreatedAsync(asRecorded, cts.Token);

        Assert.False(Directory.Exists(worktree.Path));
        Assert.Contains(worktree.Branch, await BranchesAsync(repo));
        Assert.Equal(tip, await repo.GitAsync("rev-parse", worktree.Branch));
        Assert.Contains("main", await BranchesAsync(repo));
    }

    [Fact]
    public async Task The_repositorys_own_branch_and_work_are_never_touched()
    {
        using var repo = await StepCommitRepo.CreateAsync();
        var service = new WorktreeService(StepCommitRepo.Cli);
        using var cts = Bound();
        repo.WriteFile("main-work.txt", "main work\n");
        var head = await repo.HeadAsync();
        var worktree = await service.CreateAsync(repo.Path, "Beside main", cts.Token);

        await service.TryRemoveCreatedAsync(worktree, cts.Token);

        Assert.Equal(head, await repo.HeadAsync());
        Assert.Equal("?? main-work.txt", await repo.StatusAsync());
        Assert.Equal("main work\n", await File.ReadAllTextAsync(repo.FullPath("main-work.txt"), cts.Token));
        Assert.Contains("main", await BranchesAsync(repo));
    }

    [Fact]
    public async Task A_worktree_that_is_already_gone_does_not_throw()
    {
        using var repo = await StepCommitRepo.CreateAsync();
        var service = new WorktreeService(StepCommitRepo.Cli);
        using var cts = Bound();
        var head = await repo.HeadAsync();
        var phantom = new WorktreeSession(
            repo.Path,
            Path.Combine(repo.Root, "repo.worktrees", "never-existed"),
            "forge/never-existed",
            "never-existed",
            head);

        await service.TryRemoveCreatedAsync(phantom, cts.Token);

        Assert.Equal(head, await repo.HeadAsync());
        Assert.Contains("main", await BranchesAsync(repo));
    }

    [Fact]
    public async Task A_repository_that_is_gone_does_not_throw()
    {
        var service = new WorktreeService(StepCommitRepo.Cli);
        var root = StepCommitRepo.NewRoot();
        try
        {
            using var cts = Bound();
            var phantom = new WorktreeSession(
                Path.Combine(root, "no-repo"),
                Path.Combine(root, "no-repo.worktrees", "x"),
                "forge/x",
                "x",
                null);

            await service.TryRemoveCreatedAsync(phantom, cts.Token);
        }
        finally
        {
            StepCommitRepo.DeleteTree(root);
        }
    }

    [Fact]
    public async Task A_cancelled_token_does_not_throw_and_removes_nothing()
    {
        using var repo = await StepCommitRepo.CreateAsync();
        var service = new WorktreeService(StepCommitRepo.Cli);
        using var cts = Bound();
        var worktree = await service.CreateAsync(repo.Path, "Cancelled", cts.Token);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await service.TryRemoveCreatedAsync(worktree, cancelled.Token);

        Assert.True(Directory.Exists(worktree.Path));
        Assert.Contains(worktree.Branch, await BranchesAsync(repo));
    }

    [Fact]
    public async Task A_null_worktree_is_rejected()
    {
        var service = new WorktreeService(StepCommitRepo.Cli);

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.TryRemoveCreatedAsync(null!));
    }

    // ------------------------------------------------------------ the new-session flow

    [Fact]
    public async Task A_session_row_that_cannot_be_written_takes_the_new_worktree_and_its_branch_away()
    {
        using var repo = await StepCommitRepo.CreateAsync();
        const string Branch = "forge/orphan-check";
        var session = SharedUi.Session;

        var outcome = await session.Dispatch(
            async () =>
            {
                var world = NewTree(repo.Path, Branch, new FailingCatalog(new InvalidOperationException("the session database is locked")));
                await world.Tree.RefreshAsync(CancellationToken.None);
                var baseline = world.Tree.StatusText;
                ProjectNode(world).NewSessionCommand!.Execute(null);
                var status = await WaitForChangedStatusAsync(world.Tree, baseline);
                return (Status: status, Attempt: world.Catalog.Attempts.SingleOrDefault());
            },
            CancellationToken.None);

        Assert.Contains("the session database is locked", outcome.Status, StringComparison.Ordinal);
        Assert.NotNull(outcome.Attempt);
        Assert.Equal(Branch, outcome.Attempt!.Branch);
        Assert.NotNull(outcome.Attempt.WorktreePath);
        Assert.False(Directory.Exists(outcome.Attempt.WorktreePath), "the orphaned worktree folder must be removed");
        Assert.DoesNotContain(Branch, await BranchesAsync(repo));
        Assert.Equal([Normalize(repo.Path)], await WorktreePathsAsync(repo));
        Assert.Contains("main", await BranchesAsync(repo));
    }

    [Fact]
    public async Task A_session_row_that_was_written_keeps_its_worktree_and_branch()
    {
        using var repo = await StepCommitRepo.CreateAsync();
        const string Branch = "forge/kept";
        var session = SharedUi.Session;

        var attempt = await session.Dispatch(
            async () =>
            {
                var world = NewTree(repo.Path, Branch, new FailingCatalog(null));
                await world.Tree.RefreshAsync(CancellationToken.None);
                ProjectNode(world).NewSessionCommand!.Execute(null);
                var deadline = DateTime.UtcNow + WaitBudget;
                while (world.Catalog.Attempts.Count == 0)
                {
                    Assert.True(DateTime.UtcNow < deadline, "the session was never created");
                    await Task.Delay(20);
                }

                return world.Catalog.Attempts[0];
            },
            CancellationToken.None);

        Assert.True(Directory.Exists(attempt.WorktreePath));
        Assert.Contains(Branch, await BranchesAsync(repo));
        Assert.Contains(Normalize(attempt.WorktreePath!), await WorktreePathsAsync(repo));
    }

    // ------------------------------------------------------------ helpers

    private static async Task<IReadOnlyList<string>> BranchesAsync(StepCommitRepo repo) =>
        (await repo.GitAsync("branch", "--list", "--format=%(refname:short)"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The paths of all worktrees git lists for the repository (the repository itself first), normalized.</summary>
    private static async Task<IReadOnlyList<string>> WorktreePathsAsync(StepCommitRepo repo) =>
        (await repo.GitAsync("worktree", "list", "--porcelain"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(static line => Normalize(line["worktree ".Length..]))
            .ToList();

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Replace('\\', '/').ToLowerInvariant();

    /// <summary>Commits <paramref name="file"/> in the worktree and returns the new HEAD.</summary>
    private static async Task<string> CommitInAsync(WorktreeSession worktree, string file)
    {
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, file), "work\n");
        await StepCommitRepo.GitInAsync(worktree.Path, "add", "-A");
        await StepCommitRepo.GitInAsync(worktree.Path, "commit", "-q", "-m", "wip");
        return await StepCommitRepo.GitInAsync(worktree.Path, "rev-parse", "HEAD");
    }

    private static TreeNodeViewModel ProjectNode(World world) =>
        world.Tree.Sections.Single(static node => node.Kind == TreeNodeKind.Project);

    private static World NewTree(string repositoryRoot, string typedBranch, FailingCatalog catalog)
    {
        var tree = new ProjectTreeViewModel(
            new SingleProjectRegistry(new ProjectInfo("proj-1", "repo", repositoryRoot, ProjectKind.Repository, 0, DateTimeOffset.UtcNow)),
            catalog,
            new WorktreeService(StepCommitRepo.Cli),
            (_, _) => Task.FromResult(new GitHead(GitHeadKind.Branch, "main")),
            (_, _) => Task.CompletedTask,
            async factory =>
            {
                await factory(CancellationToken.None);
                return true;
            },
            (_, _) => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult<NewBranchChoice?>(new NewBranchChoice("main", typedBranch)),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
        return new World(tree, catalog);
    }

    private static async Task<string> WaitForChangedStatusAsync(ProjectTreeViewModel tree, string baseline)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (tree.StatusText == baseline || string.IsNullOrWhiteSpace(tree.StatusText))
        {
            Assert.True(DateTime.UtcNow < deadline, "no failure message surfaced through StatusText");
            await Task.Delay(20);
        }

        return tree.StatusText;
    }

    private sealed record World(ProjectTreeViewModel Tree, FailingCatalog Catalog);

    private sealed class SingleProjectRegistry(ProjectInfo project) : IProjectRegistry
    {
        public Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProjectInfo>>([project]);

        public Task<ProjectInfo> AddAsync(string rootPath, string name, ProjectKind kind, CancellationToken ct) => Task.FromResult(project);

        public Task<bool> RemoveAsync(string projectId, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> RenameAsync(string projectId, string name, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> MoveAsync(string projectId, int sortOrder, CancellationToken ct) => Task.FromResult(false);
    }

    /// <summary>A session catalog whose <c>CreateAsync</c> records the attempt and then fails with the given error (or succeeds when null).</summary>
    private sealed class FailingCatalog(Exception? failure) : SessionCatalogDouble
    {
        private readonly List<SessionCreate> attempts = [];

        public IReadOnlyList<SessionCreate> Attempts
        {
            get
            {
                lock (attempts)
                {
                    return attempts.ToList();
                }
            }
        }

        public override Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct)
        {
            lock (attempts)
            {
                attempts.Add(request);
            }

            if (failure is not null)
            {
                return Task.FromException<SessionInfo>(failure);
            }

            return Task.FromResult(
                new SessionInfo(Guid.NewGuid().ToString("N"), request.WorkspacePath, request.Title, DateTimeOffset.UtcNow)
                {
                    ProjectId = request.ProjectId,
                    WorktreePath = request.WorktreePath,
                    Branch = request.Branch,
                    LastActiveAt = DateTimeOffset.UtcNow,
                });
        }

        public override Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);
    }
}
