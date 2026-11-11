using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins the new-branch dialog flow of <see cref="ProjectTreeViewModel"/>: the project row's "+" asks through
/// <c>pickNewBranch</c> with the repository path and a <c>forge/session-</c> suggestion; a cancelled dialog (null)
/// creates neither a worktree nor a session; a choice creates the worktree on a branch starting at the chosen start
/// point with the session's branch equal to it (asserted through real git, not the code under test); a taken name
/// surfaces the localized failure and creates nothing; general sessions and sessions in an existing worktree never ask.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ProjectTreeNewBranchFlowTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task Plus_asks_through_the_dialog_with_the_repository_path_and_a_forge_prefixed_suggestion()
    {
        using var sandbox = GitTestSandbox.Create();
        var repo = sandbox.CloneTracked("repo");

        var session = SharedUi.Session;
        var request = await session.Dispatch(
            async () =>
            {
                using var world = World.Create(repo, new RecordingCatalog(), (_, _) => Task.FromResult<NewBranchChoice?>(null));
                await world.Tree.RefreshAsync(CancellationToken.None);
                ProjectNode(world).NewSessionCommand!.Execute(null);
                await WaitForPicksOnUiAsync(world, 1);
                return world.Requests[0];
            },
            CancellationToken.None);

        Assert.Equal(repo, request.RepositoryPath);
        Assert.StartsWith("forge/session-", request.SuggestedName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_from_the_dialog_creates_neither_a_worktree_nor_a_session()
    {
        using var sandbox = GitTestSandbox.Create();
        var repo = sandbox.CloneTracked("repo");

        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                using var world = World.Create(repo, new RecordingCatalog(), (_, _) => Task.FromResult<NewBranchChoice?>(null));
                await world.Tree.RefreshAsync(CancellationToken.None);
                var command = ProjectNode(world).NewSessionCommand!;
                command.Execute(null);
                await WaitForPicksOnUiAsync(world, 1);
                await GitPanelTesting.WaitUntilAsync(() => command.CanExecute(null), "the new-session command to finish");
                await GitPanelTesting.QuietWindowAsync();
                return new
                {
                    Created = world.Catalog.CreateCalls,
                    Worktrees = await world.Worktrees.ListAsync(repo, CancellationToken.None),
                };
            },
            CancellationToken.None);

        Assert.Equal(0, state.Created);
        Assert.Empty(state.Worktrees);
    }

    [Fact]
    public async Task A_choice_with_a_dev_start_point_creates_the_worktree_on_devs_commit()
    {
        using var sandbox = GitTestSandbox.Create();
        var repo = sandbox.CloneTracked("repo");
        GitTestShell.RunChecked(repo, "switch", "-c", "dev");
        sandbox.CommitFile(repo, "dev.txt", "dev\n", "dev commit");
        GitTestShell.RunChecked(repo, "switch", "main");
        var devCommit = GitTestShell.RunChecked(repo, "rev-parse", "dev").Trim();

        var session = SharedUi.Session;
        var created = await session.Dispatch(
            async () =>
            {
                using var world = World.Create(
                    repo,
                    new RecordingCatalog(),
                    (_, _) => Task.FromResult<NewBranchChoice?>(new NewBranchChoice("dev", "forge/pick-dev")));
                await world.Tree.RefreshAsync(CancellationToken.None);
                ProjectNode(world).NewSessionCommand!.Execute(null);
                await WaitForCreatedOnUiAsync(world);
                return world.Catalog.Requests[0];
            },
            CancellationToken.None);

        Assert.Equal("forge/pick-dev", created.Branch);
        Assert.NotNull(created.WorktreePath);
        Assert.True(Directory.Exists(created.WorktreePath));
        Assert.Equal(devCommit, GitTestShell.RunChecked(repo, "rev-parse", "forge/pick-dev").Trim());
        Assert.Equal(devCommit, GitTestShell.RunChecked(created.WorktreePath!, "rev-parse", "HEAD").Trim());
        Assert.Equal("forge/pick-dev", GitTestShell.RunChecked(created.WorktreePath!, "branch", "--show-current").Trim());
    }

    [Fact]
    public async Task A_taken_choice_reports_the_localized_failure_and_creates_nothing()
    {
        using var sandbox = GitTestSandbox.Create();
        var repo = sandbox.CloneTracked("repo");
        GitTestShell.RunChecked(repo, "branch", "taken");

        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                using var world = World.Create(
                    repo,
                    new RecordingCatalog(),
                    (_, _) => Task.FromResult<NewBranchChoice?>(new NewBranchChoice("main", "taken")));
                await world.Tree.RefreshAsync(CancellationToken.None);
                var baseline = world.Tree.StatusText;
                ProjectNode(world).NewSessionCommand!.Execute(null);
                var status = await WaitForChangedStatusOnUiAsync(world, baseline);
                return new
                {
                    Status = status,
                    Created = world.Catalog.CreateCalls,
                    Worktrees = await world.Worktrees.ListAsync(repo, CancellationToken.None),
                    SessionRows = world.Tree.Sections
                        .Single(static node => node.Kind == TreeNodeKind.Project)
                        .Children.SelectMany(static node => node.Children)
                        .Count(static node => node.Kind == TreeNodeKind.Session),
                };
            },
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(state.Status));
        Assert.Equal(0, state.Created);
        Assert.Empty(state.Worktrees);
        Assert.Equal(0, state.SessionRows);
    }

    [Fact]
    public async Task General_session_never_asks()
    {
        var generalRoot = GitTestFiles.NewTempDir("forge-new-branch-general-");
        var session = SharedUi.Session;
        try
        {
            var state = await session.Dispatch(
                async () =>
                {
                    using var world = World.CreateGeneral(generalRoot, (_, _) => Task.FromResult<NewBranchChoice?>(null));
                    await world.Tree.RefreshAsync(CancellationToken.None);
                    var general = world.Tree.Sections.Single(static node => node.Kind == TreeNodeKind.General);
                    general.NewSessionCommand!.Execute(null);
                    await WaitForCreatedOnUiAsync(world);
                    return new { Picks = world.Requests.Count, Created = world.Catalog.CreateCalls };
                },
                CancellationToken.None);

            Assert.Equal(0, state.Picks);
            Assert.Equal(1, state.Created);
        }
        finally
        {
            GitTestFiles.DeleteTree(generalRoot);
        }
    }

    [Fact]
    public async Task Session_in_an_existing_worktree_never_asks()
    {
        using var sandbox = GitTestSandbox.Create();
        var repo = sandbox.CloneTracked("repo");
        var worktreeDir = Directory.CreateDirectory(Path.Combine(repo, "linked")).FullName;
        var session = SharedUi.Session;
        var outcome = await session.Dispatch(
            async () =>
            {
                using var world = World.CreateWithSession(
                    repo,
                    worktreeDir,
                    "main",
                    (_, _) => Task.FromResult<NewBranchChoice?>(null));
                await world.Tree.RefreshAsync(CancellationToken.None);
                var worktree = world.Tree.Sections
                    .Single(static node => node.Kind == TreeNodeKind.Project)
                    .Children.Single(static node => node.Kind == TreeNodeKind.Worktree && !node.IsMainCheckout);
                worktree.NewSessionCommand!.Execute(null);
                await WaitForCreatedOnUiAsync(world);
                return new { Picks = world.Requests.Count, Created = world.Catalog.Requests[^1] };
            },
            CancellationToken.None);

        Assert.Equal(0, outcome.Picks);
        Assert.Equal(worktreeDir, outcome.Created.WorktreePath);
    }

    // ------------------------------------------------------------ harness

    private static TreeNodeViewModel ProjectNode(World world) =>
        world.Tree.Sections.Single(static node => node.Kind == TreeNodeKind.Project);

    private static async Task WaitForPicksOnUiAsync(World world, int expected)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (world.Requests.Count < expected)
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("the new-branch dialog was never shown.");
            }

            await Task.Delay(PollInterval);
        }
    }

    private static async Task WaitForCreatedOnUiAsync(World world)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (world.Catalog.CreateCalls < 1)
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail(
                    $"the expected session was never created; status text: '{world.Tree.StatusText}', "
                    + $"dialog calls: {world.Requests.Count}");
            }

            await Task.Delay(PollInterval);
        }
    }

    private static async Task<string> WaitForChangedStatusOnUiAsync(World world, string baseline)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (world.Tree.StatusText == baseline || string.IsNullOrWhiteSpace(world.Tree.StatusText))
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("no failure message surfaced through StatusText.");
            }

            await Task.Delay(PollInterval);
        }

        return world.Tree.StatusText;
    }

    private sealed class World : IDisposable
    {
        private readonly object gate = new();
        private readonly List<NewBranchRequest> requests = [];

        private World(ProjectTreeViewModel tree, RecordingCatalog catalog, WorktreeService worktrees)
        {
            Tree = tree;
            Catalog = catalog;
            Worktrees = worktrees;
        }

        public ProjectTreeViewModel Tree { get; }

        public RecordingCatalog Catalog { get; }

        public WorktreeService Worktrees { get; }

        public IReadOnlyList<NewBranchRequest> Requests
        {
            get
            {
                lock (gate)
                {
                    return requests.ToArray();
                }
            }
        }

        public static World Create(
            string repositoryRoot,
            RecordingCatalog catalog,
            Func<NewBranchRequest, CancellationToken, Task<NewBranchChoice?>> answer)
        {
            var project = new ProjectInfo("proj-1", "repo", repositoryRoot, ProjectKind.Repository, 0, DateTimeOffset.UtcNow);
            return CreateFor([project], catalog, answer);
        }

        public static World CreateGeneral(
            string generalRoot,
            Func<NewBranchRequest, CancellationToken, Task<NewBranchChoice?>> answer)
        {
            var project = new ProjectInfo("general", "general", generalRoot, ProjectKind.General, 0, DateTimeOffset.UtcNow);
            return CreateFor([project], new RecordingCatalog(), answer);
        }

        public static World CreateWithSession(
            string repositoryRoot,
            string worktreePath,
            string branch,
            Func<NewBranchRequest, CancellationToken, Task<NewBranchChoice?>> answer)
        {
            var seed = new SessionInfo("seed-1", worktreePath, "seed", DateTimeOffset.UtcNow)
            {
                ProjectId = "proj-1",
                WorktreePath = worktreePath,
                Branch = branch,
                LastActiveAt = DateTimeOffset.UtcNow,
            };
            var project = new ProjectInfo("proj-1", "repo", repositoryRoot, ProjectKind.Repository, 0, DateTimeOffset.UtcNow);
            return CreateFor([project], new RecordingCatalog(seed), answer);
        }

        private static World CreateFor(
            IReadOnlyList<ProjectInfo> projects,
            RecordingCatalog catalog,
            Func<NewBranchRequest, CancellationToken, Task<NewBranchChoice?>> answer)
        {
            var worktrees = new WorktreeService(new GitCli());
            World? world = null;
            var tree = new ProjectTreeViewModel(
                new FakeRegistry(projects),
                catalog,
                worktrees,
                (_, _) => Task.FromResult(new GitHead(GitHeadKind.Branch, "main")),
                (_, _) => Task.CompletedTask,
                async factory =>
                {
                    await factory(CancellationToken.None);
                    return true;
                },
                (_, _) => Task.FromResult<string?>(null),
                (request, ct) =>
                {
                    lock (world!.gate)
                    {
                        world.requests.Add(request);
                    }

                    return answer(request, ct);
                },
                _ => Task.CompletedTask,
                _ => Task.CompletedTask);
            world = new World(tree, catalog, worktrees);
            return world;
        }

        public void Dispose() => Tree.Dispose();
    }

    private sealed class FakeRegistry(IReadOnlyList<ProjectInfo> projects) : IProjectRegistry
    {
        public Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult(projects);

        public Task<ProjectInfo> AddAsync(string rootPath, string name, ProjectKind kind, CancellationToken ct) =>
            Task.FromResult(projects[0]);

        public Task<bool> RemoveAsync(string projectId, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> RenameAsync(string projectId, string name, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> MoveAsync(string projectId, int sortOrder, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class RecordingCatalog : ISessionCatalog
    {
        private readonly object gate = new();
        private readonly List<SessionInfo> seed;
        private readonly List<SessionCreate> requests = [];

        public RecordingCatalog() => seed = [];

        public RecordingCatalog(SessionInfo single) => seed = [single];

        public int CreateCalls
        {
            get
            {
                lock (gate)
                {
                    return requests.Count;
                }
            }
        }

        public IReadOnlyList<SessionCreate> Requests
        {
            get
            {
                lock (gate)
                {
                    return requests.ToArray();
                }
            }
        }

        public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct)
        {
            lock (gate)
            {
                requests.Add(request);
            }

            return Task.FromResult(
                new SessionInfo(Guid.NewGuid().ToString("N"), request.WorkspacePath, request.Title, DateTimeOffset.UtcNow)
                {
                    ProjectId = request.ProjectId,
                    WorktreePath = request.WorktreePath,
                    Branch = request.Branch,
                    ParentSessionId = request.ParentSessionId,
                    LastActiveAt = DateTimeOffset.UtcNow,
                });
        }

        public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct)
        {
            if (query.ParentSessionId is { Length: > 0 })
            {
                return Task.FromResult<IReadOnlyList<SessionInfo>>([]);
            }

            return Task.FromResult<IReadOnlyList<SessionInfo>>(seed.ToArray());
        }

        public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) =>
            Task.FromResult(true);

        public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }
}
