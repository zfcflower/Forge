using Forge.Abstractions;
using Forge.App.Lifecycle;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.Tests.Git;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Lifecycle;

/// <summary>
/// The generated menus on real tree rows (TASK-session-worktree-lifecycle 5.2-5.4): each row maps to its row type, the
/// refresh-time git reads (HEAD, default branch, merge / rebase, sparse) end up in the menu, the trailing "+" runs the one
/// enabled first-group entry or shows the group, and opening any menu - with the in-memory flags changing in between -
/// starts no git process and no registry or catalog read (5.4 rule 4, I3).
/// </summary>
[Collection("forge-app-ui")]
public sealed class TreeMenuRowTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Opening_every_menu_reads_neither_git_nor_the_database()
    {
        await RunAsync(
            Standard,
            async world =>
            {
                Assert.True(world.HeadCalls > 0);
                Assert.True(world.StateCalls > 0);
                Assert.True(world.DefaultCalls > 0);
                var before = world.Counters();
                var busy = false;
                world.Tree.SessionBusyReasons = id => busy && id == "ws-1" ? ["turn 2"] : [];
                world.Tree.IsSessionHandingOver = id => busy && id == "root-1";
                world.Tree.IsWorkspaceOperating = _ => busy;

                foreach (var flip in new[] { false, true, false })
                {
                    busy = flip;
                    foreach (var row in world.AllRows())
                    {
                        _ = row.MenuItems();
                        _ = row.PlusItems();
                        foreach (var item in row.MenuItems())
                        {
                            _ = item.IsEnabled;
                            _ = item.Tip;
                        }
                    }
                }

                await Task.Yield();
                Assert.Equal(before, world.Counters());
            });
    }

    [Fact]
    public async Task Each_row_gets_the_menu_of_its_row_type()
    {
        await RunAsync(
            Standard,
            world =>
            {
                Assert.Equal(
                    "NewWorkspace LinkWorkspace | RenameProject Reveal | CloseProject",
                    TreeMenuTests.Shape(world.ProjectRow("proj-1").MenuItems().Select(static item => item.Entry)));
                Assert.Equal(
                    "*ReplaceSession Fork | Reveal",
                    TreeMenuTests.Shape(world.MainRow().MenuItems().Select(static item => item.Entry)));
                Assert.Equal(
                    "*ReplaceSession Fork | RenameBranch Reveal | RemoveWorkspace",
                    TreeMenuTests.Shape(world.Row(world.Linked).MenuItems().Select(static item => item.Entry)));
                Assert.Equal(
                    "RenameSession ForkInherit Archive | RemoveSession",
                    TreeMenuTests.Shape(world.SessionRow("ws-1").MenuItems().Select(static item => item.Entry)));
                Assert.Equal(
                    "*View Continue RenameSession | RemoveSession",
                    TreeMenuTests.Shape(world.SessionRow("old").MenuItems().Select(static item => item.Entry)));

                // Every generated entry has a command on its row, so nothing enabled is a dead click.
                foreach (var row in world.AllRows())
                {
                    foreach (var item in row.MenuItems().Where(static item => !item.IsSeparator && item.Entry.IsEnabled))
                    {
                        Assert.True(item.IsEnabled, $"{item.Key} has no command on {row.Title}");
                    }
                }

                Assert.Equal(ProjectStrings.NewWorkspace, world.ProjectRow("proj-1").MenuItems()[0].Header);
                Assert.Equal(TreeMenuStrings.ReplaceSession("title-root-1"), world.MainRow().MenuItems()[0].Header);
                Assert.True(world.MainRow().MenuItems()[0].IsDefault);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task The_default_branch_and_a_head_off_a_branch_hide_rename_branch_on_a_workspace()
    {
        await RunAsync(
            Standard,
            world =>
            {
                Assert.DoesNotContain(world.Row(world.Linked).MenuItems(), static item => item.Key == TreeMenuKey.RenameBranch);
                return Task.CompletedTask;
            },
            defaultBranch: "origin/feature");

        await RunAsync(
            Standard,
            world =>
            {
                var menu = world.Row(world.Linked).MenuItems();
                Assert.DoesNotContain(menu, static item => item.Key == TreeMenuKey.RenameBranch);
                Assert.False(menu.Single(static item => item.Key == TreeMenuKey.Fork).IsEnabled);
                Assert.Equal(TreeMenuStrings.HeadWarning, menu.Single(static item => item.Key == TreeMenuKey.ReplaceSession).Tip);
                return Task.CompletedTask;
            },
            state: new WorkspaceGitState(GitHeadKind.Detached, false, false));
    }

    [Fact]
    public async Task A_merge_in_progress_disables_rename_branch_and_a_sparse_checkout_marks_its_row()
    {
        await RunAsync(
            Standard,
            world =>
            {
                var linked = world.Row(world.Linked);
                var rename = linked.MenuItems().Single(static item => item.Key == TreeMenuKey.RenameBranch);
                Assert.False(rename.IsEnabled);
                Assert.Equal(TreeMenuStrings.MergeOrRebase, rename.Tip);
                Assert.True(linked.IsSparse);
                Assert.Equal(TreeMenuStrings.SparseMark, linked.SparseLabel);
                Assert.False(world.MainRow().IsSparse);
                return Task.CompletedTask;
            },
            state: new WorkspaceGitState(GitHeadKind.Branch, true, true));
    }

    [Fact]
    public async Task A_project_whose_root_is_not_a_repository_offers_no_workspace_entries_and_no_plus()
    {
        await RunAsync(
            (root, linked, plain) => [],
            world =>
            {
                var row = world.ProjectRow("plain");
                Assert.Equal("RenameProject Reveal | CloseProject", TreeMenuTests.Shape(row.MenuItems().Select(static item => item.Entry)));
                Assert.False(row.HasNewSession);
                Assert.True(world.ProjectRow("proj-1").HasNewSession);
                return Task.CompletedTask;
            },
            withPlain: true);
    }

    [Fact]
    public async Task A_busy_holder_turns_the_workspace_menu_without_a_refresh_and_plus_runs_the_fork()
    {
        await RunAsync(
            Standard,
            async world =>
            {
                var forked = new TaskCompletionSource<ForkSource>(TaskCreationOptions.RunContinuationsAsynchronously);
                world.Tree.ForkWorkspace = (source, _, _) =>
                {
                    forked.TrySetResult(source);
                    return Task.CompletedTask;
                };
                var linked = world.Row(world.Linked);

                // Idle: replace and fork both run, so "+" shows the group instead of guessing.
                Assert.False(linked.TryRunPlus());
                Assert.Equal(2, linked.PlusItems().Count(static item => item.IsEnabled));

                world.Tree.SessionBusyReasons = id => id == "ws-1" ? ["turn 2"] : [];
                world.Tree.IsSessionBusy = id => id == "ws-1";
                world.Tree.NotifyCommandStates();
                var plus = linked.PlusItems();
                var replace = plus.Single(static item => item.Key == TreeMenuKey.ReplaceSession);
                Assert.False(replace.IsEnabled);
                Assert.Equal(TreeMenuStrings.Busy("title-ws-1", ["turn 2"]), replace.Tip);
                Assert.True(plus.Single(static item => item.Key == TreeMenuKey.Fork).IsDefault);

                Assert.True(linked.TryRunPlus());
                var source = await forked.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(world.Linked, source.Directory);
            });
    }

    [Fact]
    public async Task Plus_on_an_empty_checkout_creates_a_session_and_on_a_blocked_one_runs_nothing()
    {
        await RunAsync(
            (root, linked, plain) => [],
            async world =>
            {
                var main = world.MainRow();
                Assert.True(main.TryRunPlus());
                await world.Created.Task.WaitAsync(TimeSpan.FromSeconds(10));

                world.Tree.IsWorkspaceOperating = _ => true;
                world.Tree.IsWorkspaceBlocked = _ => true;
                world.Tree.NotifyCommandStates();
                var calls = world.CreateCalls;
                Assert.False(main.TryRunPlus());
                Assert.False(Assert.Single(main.PlusItems()).IsEnabled);
                await Task.Yield();
                Assert.Equal(calls, world.CreateCalls);
            });
    }

    [Fact]
    public async Task The_menu_only_entries_reach_their_shell_flows()
    {
        await RunAsync(
            Standard,
            async world =>
            {
                var replaced = new TaskCompletionSource<(SessionInfo, SessionCreate)>(TaskCreationOptions.RunContinuationsAsynchronously);
                var continued = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var renamed = new TaskCompletionSource<WorkspaceTarget>(TaskCreationOptions.RunContinuationsAsynchronously);
                var linkedProject = new TaskCompletionSource<ProjectInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
                world.Tree.ReplaceSession = (holder, create, _) => { replaced.TrySetResult((holder, create)); return Task.CompletedTask; };
                world.Tree.ContinueSession = (id, _) => { continued.TrySetResult(id); return Task.CompletedTask; };
                world.Tree.RenameBranch = (target, _) => { renamed.TrySetResult(target); return Task.CompletedTask; };
                world.Tree.LinkWorkspace = (project, _) => { linkedProject.TrySetResult(project); return Task.CompletedTask; };
                world.Tree.NotifyCommandStates();

                Run(world.Row(world.Linked), TreeMenuKey.ReplaceSession);
                var (holder, create) = await replaced.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("ws-1", holder.Id);
                Assert.Equal(world.Linked, create.WorktreePath);
                Assert.Equal("proj-1", create.ProjectId);
                Assert.Equal("feature", create.Branch);

                Run(world.SessionRow("old"), TreeMenuKey.Continue);
                Assert.Equal("old", await continued.Task.WaitAsync(TimeSpan.FromSeconds(10)));

                Run(world.Row(world.Linked), TreeMenuKey.RenameBranch);
                var target = await renamed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(world.Linked, target.Directory);
                Assert.Equal("feature", target.Branch);

                Run(world.ProjectRow("proj-1"), TreeMenuKey.LinkWorkspace);
                Assert.Equal("proj-1", (await linkedProject.Task.WaitAsync(TimeSpan.FromSeconds(10))).Id);
            });
    }

    [Fact]
    public async Task Continue_is_disabled_on_a_history_row_whose_directory_is_gone()
    {
        await RunAsync(
            (root, linked, plain) =>
            [
                Session("root-1", root, "main"),
                Session("gone", Path.Combine(root, "gone"), "old", Epoch) with { Status = SessionStatus.Archived },
            ],
            world =>
            {
                world.Tree.ContinueSession = (_, _) => Task.CompletedTask;
                world.Tree.NotifyCommandStates();
                var row = world.AllRows().Single(static node => node.SessionId == "gone");
                var item = row.MenuItems().Single(static entry => entry.Key == TreeMenuKey.Continue);
                Assert.False(item.IsEnabled);
                Assert.Equal(LifecycleStrings.DirectoryMissing, item.Tip);
                Assert.False(row.ContinueCommand!.CanExecute(null));
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task New_workspace_on_the_project_row_offers_the_sparse_option()
    {
        await RunAsync(
            (root, linked, plain) => [],
            async world =>
            {
                await new GitCli().RunAsync(world.Root, ["init", "-q"], CancellationToken.None);
                Assert.True(world.ProjectRow("proj-1").TryRunPlus());
                var request = await world.BranchRequest.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(request.OfferSparse);
                Assert.Equal(world.Root, request.RepositoryPath);
            });
    }

    private static void Run(TreeNodeViewModel row, TreeMenuKey key) =>
        Assert.True(row.MenuItems().Single(item => item.Key == key).Run(), key + " did not run");

    private static List<SessionInfo> Standard(string root, string linked, string plain) =>
    [
        Session("root-1", root, "main"),
        Session("ws-1", linked, "feature"),
        Session("old", linked, "feature", Epoch) with { Status = SessionStatus.Archived },
    ];

    private static SessionInfo Session(string id, string directory, string? branch, DateTimeOffset? at = null)
    {
        var stamp = at ?? Epoch.AddDays(1);
        return new SessionInfo(id, directory, "title-" + id, stamp)
        {
            ProjectId = "proj-1",
            WorktreePath = directory,
            Branch = branch,
            LastActiveAt = stamp,
        };
    }

    private static async Task RunAsync(
        Func<string, string, string, List<SessionInfo>> seed,
        Func<World, Task> body,
        WorkspaceGitState? state = null,
        string? defaultBranch = "origin/main",
        bool withPlain = false)
    {
        var root = GitTestFiles.NewTempDir("forge-tree-menu-");
        var linked = Path.Combine(root, "linked");
        var plain = GitTestFiles.NewTempDir("forge-tree-menu-plain-");
        Directory.CreateDirectory(linked);
        try
        {
            await SharedUi.Session.Dispatch(
                async () =>
                {
                    using var world = World.Create(root, linked, plain, seed(root, linked, plain), withPlain);
                    world.Tree.ReadWorkspaceState = (_, _) =>
                    {
                        world.CountState();
                        return Task.FromResult(state ?? new WorkspaceGitState(GitHeadKind.Branch, false, false));
                    };
                    world.Tree.ReadDefaultBranch = (_, _) =>
                    {
                        world.CountDefault();
                        return Task.FromResult(defaultBranch);
                    };
                    await world.Tree.RefreshAsync(CancellationToken.None);
                    await body(world);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            GitTestFiles.DeleteTree(root);
            GitTestFiles.DeleteTree(plain);
        }
    }

    private sealed class World : IDisposable
    {
        private int headCalls;
        private int stateCalls;
        private int defaultCalls;
        private int createCalls;

        private World(string root, string linked, ProjectTreeViewModel tree, FakeCatalog catalog, FakeRegistry registry)
        {
            Root = root;
            Linked = linked;
            Tree = tree;
            Catalog = catalog;
            Registry = registry;
        }

        public string Root { get; }

        public string Linked { get; }

        public ProjectTreeViewModel Tree { get; }

        public FakeCatalog Catalog { get; }

        public FakeRegistry Registry { get; }

        public TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<NewBranchRequest> BranchRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int HeadCalls => Volatile.Read(ref headCalls);

        public int StateCalls => Volatile.Read(ref stateCalls);

        public int DefaultCalls => Volatile.Read(ref defaultCalls);

        public int CreateCalls => Volatile.Read(ref createCalls);

        public void CountState() => Interlocked.Increment(ref stateCalls);

        public void CountDefault() => Interlocked.Increment(ref defaultCalls);

        public (int Head, int State, int Default, int Query, int History, int Registry, int Create) Counters() =>
            (HeadCalls, StateCalls, DefaultCalls, Catalog.QueryCalls, Catalog.HistoryCalls, Registry.ListCalls, CreateCalls);

        public static World Create(string root, string linked, string plain, List<SessionInfo> seed, bool withPlain)
        {
            var projects = new List<ProjectInfo> { new("proj-1", "repo", root, ProjectKind.Repository, 0, Epoch) };
            if (withPlain)
            {
                projects.Add(new ProjectInfo("plain", "plain", plain, ProjectKind.Repository, 1, Epoch));
            }

            var catalog = new FakeCatalog(seed);
            var registry = new FakeRegistry(projects);
            World? world = null;
            var tree = new ProjectTreeViewModel(
                registry,
                catalog,
                new WorktreeService(new GitCli()),
                (path, _) =>
                {
                    Interlocked.Increment(ref world!.headCalls);
                    return Task.FromResult(string.Equals(path, plain, StringComparison.OrdinalIgnoreCase)
                        ? new GitHead(GitHeadKind.NotRepository, null)
                        : new GitHead(GitHeadKind.Branch, "main"));
                },
                (_, _) => Task.CompletedTask,
                _ =>
                {
                    Interlocked.Increment(ref world!.createCalls);
                    world.Created.TrySetResult();
                    return Task.FromResult(false);
                },
                (_, _) => Task.FromResult<string?>(null),
                (request, _) =>
                {
                    world!.BranchRequest.TrySetResult(request);
                    return Task.FromResult<NewBranchChoice?>(null);
                },
                _ => Task.CompletedTask,
                _ => Task.CompletedTask,
                TreeScope.Both)
            {
                // The shell flows, wired the way the shell wires them: an entry whose flow is missing is shown unavailable,
                // so a world without them would not show the menus the shell does. Tests that watch a flow replace it.
                ReplaceSession = static (_, _, _) => Task.CompletedTask,
                ContinueSession = static (_, _) => Task.CompletedTask,
                RenameBranch = static (_, _) => Task.CompletedTask,
                LinkWorkspace = static (_, _) => Task.CompletedTask,
                ForkWorkspace = static (_, _, _) => Task.CompletedTask,
                MergeFork = static (_, _, _) => Task.CompletedTask,
                AbortMerge = static (_, _) => Task.CompletedTask,
                RemoveSession = static (_, _, _) => Task.CompletedTask,
                RemoveWorkspace = static (_, _) => Task.CompletedTask,
                RestoreSession = static (_, _, _) => Task.CompletedTask,
                RestoreWorkspace = static (_, _) => Task.CompletedTask,
                PurgeSession = static (_, _) => Task.CompletedTask,
                PurgeAll = static (_, _) => Task.CompletedTask,
                CloseProject = static (_, _) => Task.CompletedTask,
            };
            world = new World(root, linked, tree, catalog, registry);
            return world;
        }

        public IEnumerable<TreeNodeViewModel> AllRows() => Flatten(Tree.Sections);

        public TreeNodeViewModel ProjectRow(string id) =>
            AllRows().Single(node => node.Kind == TreeNodeKind.Project && node.Key == "project:" + id);

        public TreeNodeViewModel MainRow() => AllRows().Single(static node => node.IsMainCheckout);

        public TreeNodeViewModel Row(string directory) =>
            AllRows().Single(node => node.Kind == TreeNodeKind.Worktree && node.WorkspaceDirectory == directory);

        public TreeNodeViewModel SessionRow(string id) =>
            AllRows().Single(node => node.Kind == TreeNodeKind.Session && node.SessionId == id);

        public void Dispose() => Tree.Dispose();

        private static IEnumerable<TreeNodeViewModel> Flatten(IEnumerable<TreeNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                yield return node;
                foreach (var child in Flatten(node.Children))
                {
                    yield return child;
                }
            }
        }
    }

    private sealed class FakeRegistry(IReadOnlyList<ProjectInfo> projects) : IProjectRegistry
    {
        private int listCalls;

        public int ListCalls => Volatile.Read(ref listCalls);

        public Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref listCalls);
            return Task.FromResult(projects);
        }

        public Task<ProjectInfo> AddAsync(string rootPath, string name, ProjectKind kind, CancellationToken ct) =>
            Task.FromResult(projects[0]);

        public Task<bool> RemoveAsync(string projectId, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> RenameAsync(string projectId, string name, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> MoveAsync(string projectId, int sortOrder, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class FakeCatalog(List<SessionInfo> seed) : SessionCatalogDouble, ISessionCatalog
    {
        private int queryCalls;
        private int historyCalls;

        public int QueryCalls => Volatile.Read(ref queryCalls);

        public int HistoryCalls => Volatile.Read(ref historyCalls);

        public override Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) =>
            throw new Xunit.Sdk.XunitException("no session is created here");

        public override Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct)
        {
            Interlocked.Increment(ref queryCalls);
            return Task.FromResult<IReadOnlyList<SessionInfo>>(query.ParentSessionId is { Length: > 0 } ? [] : seed.ToArray());
        }

        public Task<IReadOnlyList<SessionInfo>> ListHistoryAsync(SessionQuery query, CancellationToken ct)
        {
            Interlocked.Increment(ref historyCalls);
            return Task.FromResult<IReadOnlyList<SessionInfo>>(
                seed.Where(static session => session.Status is SessionStatus.Archived or SessionStatus.Deleted).ToArray());
        }
    }
}
