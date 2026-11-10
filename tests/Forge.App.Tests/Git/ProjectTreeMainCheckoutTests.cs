using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins the repository-root checkout row of <see cref="ProjectTreeViewModel"/>: every repository project whose HEAD
/// is readable leads with exactly one main-checkout row (a worktree row in every way except its icon and, when
/// detached, its label); the row is always present even with no sessions, root-path sessions hang under it instead of
/// a second row for the same directory, other worktree groups keep their order behind it, its "+" and its row
/// activation create a session at the repository root without a new worktree, HEAD is re-read on every refresh, an
/// unreadable HEAD degrades to "no main row" without failing the refresh, and the general-chats panel never reads HEAD.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ProjectTreeMainCheckoutTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task Empty_project_still_has_exactly_one_main_checkout_child()
    {
        var root = GitTestFiles.NewTempDir("forge-main-empty-");
        try
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
            var row = await session.Dispatch(
                async () =>
                {
                    using var world = World.Create(root, [], _ => new GitHead(GitHeadKind.Branch, "main"));
                    await world.Tree.RefreshAsync(CancellationToken.None);
                    var project = world.Tree.Sections.Single(static node => node.Kind == TreeNodeKind.Project);
                    var main = Assert.Single(project.Children);
                    return new
                    {
                        IsMainCheckout = main.IsMainCheckout,
                        Kind = main.Kind,
                        MainIcon = ReferenceEquals(TreeIcons.MainCheckout, main.Icon),
                        Title = main.Title,
                        Key = main.Key,
                    };
                },
                CancellationToken.None);

            Assert.True(row.IsMainCheckout);
            Assert.Equal(TreeNodeKind.Worktree, row.Kind);
            Assert.True(row.MainIcon);
            Assert.Equal("main", row.Title);
            Assert.StartsWith("main:", row.Key, StringComparison.Ordinal);
        }
        finally
        {
            GitTestFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task Unborn_branch_shows_the_unborn_name()
    {
        var root = GitTestFiles.NewTempDir("forge-main-unborn-");
        try
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
            var title = await session.Dispatch(
                async () =>
                {
                    using var world = World.Create(root, [], _ => new GitHead(GitHeadKind.UnbornBranch, "main"));
                    await world.Tree.RefreshAsync(CancellationToken.None);
                    var main = world.Tree.Sections
                        .Single(static node => node.Kind == TreeNodeKind.Project)
                        .Children.Single();
                    Assert.True(main.IsMainCheckout);
                    return main.Title;
                },
                CancellationToken.None);

            Assert.Equal("main", title);
        }
        finally
        {
            GitTestFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task Detached_head_shows_the_detached_label()
    {
        var root = GitTestFiles.NewTempDir("forge-main-detached-");
        try
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
            var title = await session.Dispatch(
                async () =>
                {
                    using var world = World.Create(root, [], _ => GitHead.Detached);
                    await world.Tree.RefreshAsync(CancellationToken.None);
                    var main = world.Tree.Sections
                        .Single(static node => node.Kind == TreeNodeKind.Project)
                        .Children.Single();
                    Assert.True(main.IsMainCheckout);
                    Assert.Equal(TreeNodeKind.Worktree, main.Kind);
                    return main.Title;
                },
                CancellationToken.None);

            Assert.Equal(ProjectStrings.MainCheckoutDetached, title);
        }
        finally
        {
            GitTestFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task Not_a_repository_has_no_main_row_and_root_sessions_stay_grouped()
    {
        var root = GitTestFiles.NewTempDir("forge-main-notrepo-");
        try
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
            var state = await session.Dispatch(
                async () =>
                {
                    var seed = new List<SessionInfo>
                    {
                        Session("root-1", root, "main"),
                    };
                    using var world = World.Create(root, seed, _ => GitHead.NotRepository);
                    await world.Tree.RefreshAsync(CancellationToken.None);
                    var project = world.Tree.Sections.Single(static node => node.Kind == TreeNodeKind.Project);
                    var worktree = Assert.Single(project.Children);
                    return new
                    {
                        IsMainCheckout = worktree.IsMainCheckout,
                        Kind = worktree.Kind,
                        SessionTitles = worktree.Children
                            .Where(static node => node.Kind == TreeNodeKind.Session)
                            .Select(static node => node.Title)
                            .ToArray(),
                    };
                },
                CancellationToken.None);

            Assert.False(state.IsMainCheckout);
            Assert.Equal(TreeNodeKind.Worktree, state.Kind);
            Assert.Contains("title-root-1", state.SessionTitles);
        }
        finally
        {
            GitTestFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task Root_sessions_live_under_the_main_row_without_duplication()
    {
        var root = GitTestFiles.NewTempDir("forge-main-root-");
        var linked = Path.Combine(root, "linked");
        Directory.CreateDirectory(linked);
        try
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
            var state = await session.Dispatch(
                async () =>
                {
                    var seed = new List<SessionInfo>
                    {
                        Session("root-1", root, "main"),
                        Session("linked-1", linked, "feature"),
                    };
                    using var world = World.Create(root, seed, _ => new GitHead(GitHeadKind.Branch, "main"));
                    await world.Tree.RefreshAsync(CancellationToken.None);
                    var project = world.Tree.Sections.Single(static node => node.Kind == TreeNodeKind.Project);
                    Assert.Equal(2, project.Children.Count);
                    var main = project.Children[0];
                    var other = project.Children[1];
                    var allSessionIds = project.Children
                        .SelectMany(static node => node.Children)
                        .Where(static node => node.Kind == TreeNodeKind.Session)
                        .Select(static node => node.SessionId)
                        .ToArray();
                    return new
                    {
                        MainIsMain = main.IsMainCheckout,
                        MainSessions = main.Children
                            .Where(static node => node.Kind == TreeNodeKind.Session)
                            .Select(static node => node.SessionId)
                            .ToArray(),
                        OtherIsMain = other.IsMainCheckout,
                        OtherSessions = other.Children
                            .Where(static node => node.Kind == TreeNodeKind.Session)
                            .Select(static node => node.SessionId)
                            .ToArray(),
                        AllSessionIds = allSessionIds,
                    };
                },
                CancellationToken.None);

            Assert.True(state.MainIsMain);
            Assert.Equal(["root-1"], state.MainSessions);
            Assert.False(state.OtherIsMain);
            Assert.Equal(["linked-1"], state.OtherSessions);
            Assert.Equal(2, state.AllSessionIds.Length);
            Assert.Equal(1, state.AllSessionIds.Count(static id => id == "root-1"));
        }
        finally
        {
            GitTestFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task Main_row_comes_first_and_other_groups_keep_their_existing_order()
    {
        var root = GitTestFiles.NewTempDir("forge-main-order-");
        var older = Path.Combine(root, "older");
        var newer = Path.Combine(root, "newer");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newer);
        try
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
            var subtitles = await session.Dispatch(
                async () =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var seed = new List<SessionInfo>
                    {
                        Session("older-1", older, "a", now.AddHours(-2)),
                        Session("newer-1", newer, "b", now),
                    };
                    using var world = World.Create(root, seed, _ => new GitHead(GitHeadKind.Branch, "main"));
                    await world.Tree.RefreshAsync(CancellationToken.None);
                    var project = world.Tree.Sections.Single(static node => node.Kind == TreeNodeKind.Project);
                    Assert.Equal(3, project.Children.Count);
                    Assert.True(project.Children[0].IsMainCheckout);
                    Assert.False(project.Children[1].IsMainCheckout);
                    Assert.False(project.Children[2].IsMainCheckout);
                    return project.Children.Select(static node => node.Subtitle).ToArray();
                },
                CancellationToken.None);

            Assert.Equal(root, subtitles[0]);
            Assert.Equal(newer, subtitles[1]);
            Assert.Equal(older, subtitles[2]);
        }
        finally
        {
            GitTestFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task Main_row_plus_creates_a_session_at_the_repository_root()
    {
        var root = GitTestFiles.NewTempDir("forge-main-plus-");
        try
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
            var outcome = await session.Dispatch(
                async () =>
                {
                    using var world = World.Create(root, [], _ => new GitHead(GitHeadKind.Branch, "main"));
                    await world.Tree.RefreshAsync(CancellationToken.None);
                    var main = world.Tree.Sections
                        .Single(static node => node.Kind == TreeNodeKind.Project)
                        .Children.Single();
                    Assert.True(main.IsMainCheckout);
                    main.NewSessionCommand!.Execute(null);
                    await WaitForCreatedOnUiAsync(world.Catalog, 1);
                    return new
                    {
                        Created = world.Catalog.CreatedSnapshot,
                        Picks = world.PickCount,
                        Worktrees = await world.Worktrees.ListAsync(root, CancellationToken.None),
                    };
                },
                CancellationToken.None);

            var created = Assert.Single(outcome.Created);
            Assert.Equal(root, created.WorktreePath);
            Assert.Equal(0, outcome.Picks);
            Assert.Empty(outcome.Worktrees);
        }
        finally
        {
            GitTestFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task Main_row_activation_creates_a_session_at_the_repository_root()
    {
        var root = GitTestFiles.NewTempDir("forge-main-activate-");
        try
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
            var created = await session.Dispatch(
                async () =>
                {
                    using var world = World.Create(root, [], _ => new GitHead(GitHeadKind.Branch, "main"));
                    await world.Tree.RefreshAsync(CancellationToken.None);
                    var main = world.Tree.Sections
                        .Single(static node => node.Kind == TreeNodeKind.Project)
                        .Children.Single();
                    Assert.True(main.IsMainCheckout);
                    Assert.True(main.Activate());
                    await WaitForCreatedOnUiAsync(world.Catalog, 1);
                    return world.Catalog.CreatedSnapshot;
                },
                CancellationToken.None);

            Assert.Equal(root, Assert.Single(created).WorktreePath);
        }
        finally
        {
            GitTestFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task Refresh_rereads_head_and_the_row_title_follows()
    {
        var root = GitTestFiles.NewTempDir("forge-main-reread-");
        try
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
            var titles = await session.Dispatch(
                async () =>
                {
                    var current = "main";
                    using var world = World.Create(root, [], _ => new GitHead(GitHeadKind.Branch, current));
                    await world.Tree.RefreshAsync(CancellationToken.None);
                    var first = world.Tree.Sections
                        .Single(static node => node.Kind == TreeNodeKind.Project)
                        .Children.Single().Title;
                    current = "dev";
                    await world.Tree.RefreshAsync(CancellationToken.None);
                    var second = world.Tree.Sections
                        .Single(static node => node.Kind == TreeNodeKind.Project)
                        .Children.Single().Title;
                    return (First: first, Second: second, Calls: world.HeadCalls);
                },
                CancellationToken.None);

            Assert.Equal("main", titles.First);
            Assert.Equal("dev", titles.Second);
            Assert.True(titles.Calls >= 2);
        }
        finally
        {
            GitTestFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task Throwing_read_head_keeps_the_tree_without_the_main_row()
    {
        var root = GitTestFiles.NewTempDir("forge-main-throw-");
        try
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
            var state = await session.Dispatch(
                async () =>
                {
                    using var world = World.Create(
                        root,
                        [],
                        _ => throw new InvalidOperationException("no git today"));
                    await world.Tree.RefreshAsync(CancellationToken.None);
                    var project = world.Tree.Sections.Single(static node => node.Kind == TreeNodeKind.Project);
                    return new
                    {
                        Sections = world.Tree.Sections.Count,
                        Children = project.Children.Count,
                        HasMain = project.Children.Any(static node => node.IsMainCheckout),
                    };
                },
                CancellationToken.None);

            Assert.Equal(1, state.Sections);
            Assert.Equal(0, state.Children);
            Assert.False(state.HasMain);
        }
        finally
        {
            GitTestFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task General_scope_never_reads_head()
    {
        var generalRoot = GitTestFiles.NewTempDir("forge-main-general-");
        var repoRoot = GitTestFiles.NewTempDir("forge-main-general-repo-");
        try
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
            var state = await session.Dispatch(
                async () =>
                {
                    var general = new ProjectInfo("general", "general", generalRoot, ProjectKind.General, 0, DateTimeOffset.UtcNow);
                    var repo = new ProjectInfo("proj-1", "repo", repoRoot, ProjectKind.Repository, 1, DateTimeOffset.UtcNow);
                    var seed = new List<SessionInfo>
                    {
                        new SessionInfo("general-1", generalRoot, "title-general-1", DateTimeOffset.UtcNow)
                        {
                            ProjectId = "general",
                            WorktreePath = generalRoot,
                            LastActiveAt = DateTimeOffset.UtcNow,
                        },
                    };
                    using var world = World.CreateScoped(
                        [general, repo],
                        seed,
                        _ => new GitHead(GitHeadKind.Branch, "main"),
                        TreeScope.General);
                    await world.Tree.RefreshAsync(CancellationToken.None);
                    return new
                    {
                        Calls = world.HeadCalls,
                        Kinds = world.Tree.Sections.Select(static node => node.Kind).ToArray(),
                        Titles = world.Tree.Sections.Select(static node => node.Title).ToArray(),
                    };
                },
                CancellationToken.None);

            Assert.Equal(0, state.Calls);
            Assert.Contains(TreeNodeKind.Session, state.Kinds);
            Assert.DoesNotContain(TreeNodeKind.Project, state.Kinds);
            Assert.Contains("title-general-1", state.Titles);
        }
        finally
        {
            GitTestFiles.DeleteTree(generalRoot);
            GitTestFiles.DeleteTree(repoRoot);
        }
    }

    // ------------------------------------------------------------ harness

    private static SessionInfo Session(string id, string workspacePath, string? branch, DateTimeOffset? at = null)
    {
        var stamp = at ?? DateTimeOffset.UtcNow;
        return new SessionInfo(id, workspacePath, "title-" + id, stamp)
        {
            ProjectId = "proj-1",
            WorktreePath = workspacePath,
            Branch = branch,
            LastActiveAt = stamp,
        };
    }

    private static async Task WaitForCreatedOnUiAsync(FakeCatalog catalog, int expected)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (catalog.CreatedCount < expected)
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("the repository-root session was never created.");
            }

            await Task.Delay(PollInterval);
        }
    }

    private sealed class World : IDisposable
    {
        private int headCalls;
        private int pickCalls;

        private World(ProjectTreeViewModel tree, FakeCatalog catalog, WorktreeService worktrees)
        {
            Tree = tree;
            Catalog = catalog;
            Worktrees = worktrees;
        }

        public ProjectTreeViewModel Tree { get; }

        public FakeCatalog Catalog { get; }

        public WorktreeService Worktrees { get; }

        public int HeadCalls => Volatile.Read(ref headCalls);

        public int PickCount => Volatile.Read(ref pickCalls);

        public static World Create(string repositoryRoot, List<SessionInfo> seed, Func<string, GitHead> answer) =>
            CreateScoped(
                [new ProjectInfo("proj-1", "repo", repositoryRoot, ProjectKind.Repository, 0, DateTimeOffset.UtcNow)],
                seed,
                answer,
                TreeScope.Both);

        public static World CreateScoped(
            IReadOnlyList<ProjectInfo> projects,
            List<SessionInfo> seed,
            Func<string, GitHead> answer,
            TreeScope scope)
        {
            var catalog = new FakeCatalog(seed);
            var worktrees = new WorktreeService(new GitCli());
            World? world = null;
            var tree = new ProjectTreeViewModel(
                new FakeRegistry(projects),
                catalog,
                worktrees,
                (path, _) =>
                {
                    Interlocked.Increment(ref world!.headCalls);
                    return Task.FromResult(answer(path));
                },
                (_, _) => Task.CompletedTask,
                async factory =>
                {
                    await factory(CancellationToken.None);
                    return true;
                },
                (_, _) => Task.FromResult<string?>(null),
                (request, _) =>
                {
                    Interlocked.Increment(ref world!.pickCalls);
                    return Task.FromResult<NewBranchChoice?>(null);
                },
                _ => Task.CompletedTask,
                _ => Task.CompletedTask,
                scope);
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

    private sealed class FakeCatalog(List<SessionInfo> seed) : ISessionCatalog
    {
        private readonly object gate = new();
        private readonly List<SessionCreate> created = [];

        public int CreatedCount
        {
            get
            {
                lock (gate)
                {
                    return created.Count;
                }
            }
        }

        public IReadOnlyList<SessionCreate> CreatedSnapshot
        {
            get
            {
                lock (gate)
                {
                    return created.ToArray();
                }
            }
        }

        public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct)
        {
            lock (gate)
            {
                created.Add(request);
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
