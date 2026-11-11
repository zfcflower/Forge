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
/// The fork / merge entries of the project tree (TASK-session-worktree-lifecycle 5.4 rule 6, 6.5, 6.6, I3): a fork
/// workspace row carries "forked from &lt;source&gt;" from its most recent session that records one (history included)
/// and offers merge back / sync; every repository workspace row and active session row offers the fork; whether an entry
/// can run is answered from in-memory state alone - no HEAD read and no catalog query - so a merge back whose target is
/// unavailable stays clickable; an operation or a merge conflict in the workspace disables its entries, and the abort
/// entry shows only in the conflict state.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ForkTreeTests
{
    private const string BaseSha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_fork_workspace_row_names_its_source_and_offers_merge_back_and_sync()
    {
        await RunAsync(
            (root, linked) =>
            [
                Session("root-1", root, "main"),
                Session("fork-1", linked, "feature") with { ForkOfBranch = "main", ForkBaseSha = BaseSha },
            ],
            world =>
            {
                var main = world.MainRow();
                var fork = world.Row(world.Linked);

                Assert.Equal("main", fork.ForkOfBranch);
                Assert.True(fork.HasForkLabel);
                Assert.Equal(ForkStrings.ForkedFrom("main"), fork.ForkLabel);
                Assert.True(fork.HasFork);
                Assert.Equal(ForkStrings.MenuFork, fork.ForkHeader);
                Assert.True(fork.HasMergeBack);
                Assert.Equal(ForkStrings.MenuMergeBack("main"), fork.MergeBackHeader);
                Assert.True(fork.HasSync);
                Assert.Equal(ForkStrings.MenuSync("main"), fork.SyncHeader);
                Assert.False(fork.HasAbortMerge);

                Assert.Null(main.ForkOfBranch);
                Assert.False(main.HasForkLabel);
                Assert.True(main.HasFork);
                Assert.False(main.HasMergeBack);
                Assert.False(main.HasSync);

                var session = world.SessionRow("fork-1");
                Assert.True(session.HasFork);
                Assert.Equal(ForkStrings.MenuForkInherit, session.ForkHeader);
                Assert.False(session.HasMergeBack);
                Assert.False(session.HasForkLabel);
            });
    }

    [Fact]
    public async Task The_fork_relation_comes_from_the_most_recent_session_that_records_one_history_included()
    {
        await RunAsync(
            (root, linked) =>
            [
                Session("old", linked, "feature", Epoch) with { Status = SessionStatus.Archived, ForkOfBranch = "release", ForkBaseSha = "1111111" },
                Session("newer", linked, "feature", Epoch.AddHours(1)) with { Status = SessionStatus.Archived, ForkOfBranch = "main", ForkBaseSha = BaseSha },
                Session("live", linked, "feature", Epoch.AddHours(2)),
            ],
            world =>
            {
                ForkRelation? seen = null;
                world.Tree.MergeFork = (relation, _, _) =>
                {
                    seen = relation;
                    return Task.CompletedTask;
                };
                var fork = world.Row(world.Linked);

                Assert.Equal("main", fork.ForkOfBranch);
                fork.MergeBackCommand!.Execute(null);

                Assert.Equal(BaseSha, seen!.ForkBaseSha);
                Assert.Equal("main", seen.SourceBranch);
            });
    }

    [Fact]
    public async Task A_history_only_fork_workspace_still_offers_merge_back()
    {
        await RunAsync(
            (root, linked) =>
            [
                Session("gone", linked, "feature") with { Status = SessionStatus.Archived, ForkOfBranch = "main", ForkBaseSha = BaseSha },
            ],
            world =>
            {
                ForkSource? source = null;
                world.Tree.ForkWorkspace = (value, _, _) =>
                {
                    source = value;
                    return Task.CompletedTask;
                };
                var fork = world.Row(world.Linked);

                Assert.True(fork.HasMergeBack);
                Assert.True(fork.MergeBackCommand!.CanExecute(null));
                fork.ForkCommand!.Execute(null);

                // No active session in the workspace: the fork has no session to inherit from.
                Assert.Null(source!.Session);
            });
    }

    [Fact]
    public async Task The_entries_hand_the_shell_the_workspace_the_relation_and_the_default_context()
    {
        await RunAsync(
            (root, linked) =>
            [
                Session("root-1", root, "main"),
                Session("fork-1", linked, "feature") with { ForkOfBranch = "main", ForkBaseSha = BaseSha },
            ],
            world =>
            {
                var forks = new List<(ForkSource Source, HandoverContextMode Mode)>();
                var merges = new List<(ForkRelation Relation, MergeDirection Direction)>();
                world.Tree.ForkWorkspace = (source, mode, _) =>
                {
                    forks.Add((source, mode));
                    return Task.CompletedTask;
                };
                world.Tree.MergeFork = (relation, direction, _) =>
                {
                    merges.Add((relation, direction));
                    return Task.CompletedTask;
                };
                var fork = world.Row(world.Linked);

                fork.ForkCommand!.Execute(null);
                world.SessionRow("root-1").ForkCommand!.Execute(null);
                fork.MergeBackCommand!.Execute(null);
                fork.SyncCommand!.Execute(null);

                Assert.Equal(2, forks.Count);
                Assert.Equal(world.Linked, forks[0].Source.Directory);
                Assert.Equal("fork-1", forks[0].Source.Session!.Id);
                Assert.Equal("proj-1", forks[0].Source.Project.Id);
                Assert.Equal(HandoverContextMode.Summary, forks[0].Mode);
                Assert.Equal(world.Root, forks[1].Source.Directory);
                Assert.Equal("root-1", forks[1].Source.Session!.Id);
                Assert.Equal(HandoverContextMode.Full, forks[1].Mode);

                Assert.Equal([MergeDirection.MergeBack, MergeDirection.Sync], merges.Select(static merge => merge.Direction));
                var relation = merges[0].Relation;
                Assert.Equal(world.Linked, relation.ForkDirectory);
                Assert.Equal("feature", relation.ForkBranch);
                Assert.Equal("main", relation.SourceBranch);
                Assert.Equal(BaseSha, relation.ForkBaseSha);
                Assert.Equal(world.Root, relation.Project.RootPath);
            });
    }

    [Fact]
    public async Task Asking_whether_the_entries_can_run_reads_neither_git_nor_the_catalog_and_merge_back_stays_clickable()
    {
        await RunAsync(
            (root, linked) =>
            [
                Session("root-1", root, "main"),
                Session("fork-1", linked, "feature") with { ForkOfBranch = "gone-branch", ForkBaseSha = BaseSha },
            ],
            world =>
            {
                var heads = world.HeadCalls;
                var queries = world.Catalog.QueryCalls;

                // What opening a context menu evaluates: every entry's visibility, label and CanExecute.
                foreach (var node in world.AllRows())
                {
                    _ = node.HasFork && node.ForkCommand!.CanExecute(null);
                    _ = node.HasMergeBack && node.MergeBackCommand!.CanExecute(null);
                    _ = node.HasSync && node.SyncCommand!.CanExecute(null);
                    _ = node.HasAbortMerge && node.AbortMergeCommand!.CanExecute(null);
                    _ = node.ForkHeader + node.MergeBackHeader + node.SyncHeader + node.AbortMergeHeader + node.ForkLabel;
                }

                Assert.Equal(heads, world.HeadCalls);
                Assert.Equal(queries, world.Catalog.QueryCalls);

                // Nobody has "gone-branch" checked out; the menu cannot know and does not disable the entry (6.6 step 1).
                Assert.True(world.Row(world.Linked).MergeBackCommand!.CanExecute(null));
            });
    }

    [Fact]
    public async Task A_blocked_workspace_disables_its_entries_and_leaves_the_others_alone()
    {
        await RunAsync(
            (root, linked) =>
            [
                Session("root-1", root, "main"),
                Session("fork-1", linked, "feature") with { ForkOfBranch = "main", ForkBaseSha = BaseSha },
            ],
            world =>
            {
                var blocked = false;
                world.Tree.IsWorkspaceBlocked = directory => blocked && directory == world.Linked;
                var fork = world.Row(world.Linked);
                var forkSession = world.SessionRow("fork-1");
                var main = world.MainRow();
                var changed = 0;
                ((AsyncRelayCommand)fork.MergeBackCommand!).CanExecuteChanged += (_, _) => changed++;
                Assert.True(fork.ForkCommand!.CanExecute(null));
                Assert.True(fork.MergeBackCommand.CanExecute(null));

                blocked = true;
                world.Tree.NotifyCommandStates();

                Assert.True(changed > 0);
                Assert.False(fork.ForkCommand.CanExecute(null));
                Assert.False(fork.MergeBackCommand.CanExecute(null));
                Assert.False(fork.SyncCommand!.CanExecute(null));
                Assert.False(fork.NewSessionCommand!.CanExecute(null));
                Assert.False(forkSession.ForkCommand!.CanExecute(null));
                Assert.False(forkSession.ArchiveCommand!.CanExecute(null));
                Assert.False(forkSession.RenameCommand!.CanExecute(null));

                Assert.True(main.ForkCommand!.CanExecute(null));
                Assert.True(world.SessionRow("root-1").ArchiveCommand!.CanExecute(null));

                blocked = false;
                world.Tree.NotifyCommandStates();

                Assert.True(fork.MergeBackCommand.CanExecute(null));
                Assert.True(forkSession.ArchiveCommand.CanExecute(null));
            });
    }

    [Fact]
    public async Task The_abort_entry_shows_only_while_the_workspace_is_in_a_merge_conflict()
    {
        await RunAsync(
            (root, linked) =>
            [
                Session("root-1", root, "main"),
                Session("fork-1", linked, "feature") with { ForkOfBranch = "main", ForkBaseSha = BaseSha },
            ],
            world =>
            {
                var conflicted = false;
                var aborted = new List<string>();
                world.Tree.IsWorkspaceConflicted = directory => conflicted && directory == world.Root;
                world.Tree.AbortMerge = (directory, _) =>
                {
                    aborted.Add(directory);
                    return Task.CompletedTask;
                };
                var main = world.MainRow();
                var fork = world.Row(world.Linked);
                Assert.False(main.HasAbortMerge);

                conflicted = true;
                world.Tree.NotifyCommandStates();

                Assert.True(main.HasAbortMerge);
                Assert.Equal(ForkStrings.MenuAbort, main.AbortMergeHeader);
                Assert.False(fork.HasAbortMerge);
                main.AbortMergeCommand!.Execute(null);
                Assert.Equal([world.Root], aborted);

                conflicted = false;
                world.Tree.NotifyCommandStates();

                Assert.False(main.HasAbortMerge);
            });
    }

    [Fact]
    public async Task A_conflict_found_before_the_refresh_shows_the_abort_entry_on_the_rebuilt_row()
    {
        await RunAsync(
            (root, linked) => [Session("root-1", root, "main")],
            world =>
            {
                Assert.True(world.MainRow().HasAbortMerge);
                Assert.Contains(world.Root, world.Tree.WorkspaceDirectories());
            },
            configure: (tree, root) => tree.IsWorkspaceConflicted = directory => directory == root);
    }

    [Fact]
    public async Task A_busy_session_disables_merge_and_its_own_fork_but_the_workspace_can_still_be_forked()
    {
        await RunAsync(
            (root, linked) =>
            [
                Session("root-1", root, "main"),
                Session("fork-1", linked, "feature") with { ForkOfBranch = "main", ForkBaseSha = BaseSha },
            ],
            world =>
            {
                world.Tree.IsSessionBusy = id => id == "fork-1";
                world.Tree.NotifyCommandStates();
                var fork = world.Row(world.Linked);

                Assert.False(fork.MergeBackCommand!.CanExecute(null));
                Assert.False(fork.SyncCommand!.CanExecute(null));
                Assert.False(world.SessionRow("fork-1").ForkCommand!.CanExecute(null));

                // 6.5 step 2: a busy source can still be forked from its last commit ("fork anyway").
                Assert.True(fork.ForkCommand!.CanExecute(null));
                Assert.True(world.SessionRow("root-1").ForkCommand!.CanExecute(null));
            });
    }

    [Theory]
    [InlineData(GitHeadKind.Detached)]
    [InlineData(GitHeadKind.UnbornBranch)]
    public async Task A_main_checkout_without_a_branch_with_commits_cannot_be_forked(GitHeadKind kind)
    {
        await RunAsync(
            (root, linked) => [],
            world => Assert.False(world.MainRow().ForkCommand!.CanExecute(null)),
            head: new GitHead(kind, kind == GitHeadKind.UnbornBranch ? "main" : null));
    }

    [Fact]
    public async Task Archived_sessions_and_the_general_workspace_offer_no_lifecycle_entries()
    {
        await RunAsync(
            (root, linked) =>
            [
                Session("root-1", root, "main"),
                Session("done", root, "main", Epoch) with { Status = SessionStatus.Archived },
                new SessionInfo("chat", linked, "chat", Epoch) { ProjectId = "general", LastActiveAt = Epoch },
            ],
            world =>
            {
                Assert.All(
                    world.AllRows().Where(static node => node.SessionId is "done" or "chat"),
                    static node =>
                    {
                        Assert.False(node.HasFork);
                        Assert.False(node.HasMergeBack);
                        Assert.False(node.HasSync);
                        Assert.False(node.HasAbortMerge);
                    });
                Assert.All(
                    world.AllRows().Where(static node => node.Kind == TreeNodeKind.Project),
                    static node => Assert.False(node.HasFork));
            },
            withGeneral: true);
    }

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
        Func<string, string, List<SessionInfo>> seed,
        Action<World> body,
        GitHead? head = null,
        Action<ProjectTreeViewModel, string>? configure = null,
        bool withGeneral = false)
    {
        var root = GitTestFiles.NewTempDir("forge-fork-tree-");
        var linked = Path.Combine(root, "linked");
        Directory.CreateDirectory(linked);
        try
        {
            await SharedUi.Session.Dispatch(
                async () =>
                {
                    using var world = World.Create(root, linked, seed(root, linked), head ?? new GitHead(GitHeadKind.Branch, "main"), withGeneral);
                    configure?.Invoke(world.Tree, root);
                    await world.Tree.RefreshAsync(CancellationToken.None);
                    body(world);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            GitTestFiles.DeleteTree(root);
        }
    }

    private sealed class World : IDisposable
    {
        private int headCalls;

        private World(string root, string linked, ProjectTreeViewModel tree, FakeCatalog catalog)
        {
            Root = root;
            Linked = linked;
            Tree = tree;
            Catalog = catalog;
        }

        public string Root { get; }

        public string Linked { get; }

        public ProjectTreeViewModel Tree { get; }

        public FakeCatalog Catalog { get; }

        public int HeadCalls => Volatile.Read(ref headCalls);

        public static World Create(string root, string linked, List<SessionInfo> seed, GitHead head, bool withGeneral)
        {
            var projects = new List<ProjectInfo> { new("proj-1", "repo", root, ProjectKind.Repository, 0, Epoch) };
            if (withGeneral)
            {
                projects.Add(new ProjectInfo("general", "general", linked, ProjectKind.General, 1, Epoch));
            }

            var catalog = new FakeCatalog(seed);
            World? world = null;
            var tree = new ProjectTreeViewModel(
                new FakeRegistry(projects),
                catalog,
                new WorktreeService(new GitCli()),
                (_, _) =>
                {
                    Interlocked.Increment(ref world!.headCalls);
                    return Task.FromResult(head);
                },
                (_, _) => Task.CompletedTask,
                async factory =>
                {
                    await factory(CancellationToken.None);
                    return true;
                },
                (_, _) => Task.FromResult<string?>(null),
                (_, _) => Task.FromResult<NewBranchChoice?>(null),
                _ => Task.CompletedTask,
                _ => Task.CompletedTask,
                TreeScope.Both);
            world = new World(root, linked, tree, catalog);
            return world;
        }

        public IEnumerable<TreeNodeViewModel> AllRows() => Flatten(Tree.Sections);

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
        public Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct) => Task.FromResult(projects);

        public Task<ProjectInfo> AddAsync(string rootPath, string name, ProjectKind kind, CancellationToken ct) =>
            Task.FromResult(projects[0]);

        public Task<bool> RemoveAsync(string projectId, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> RenameAsync(string projectId, string name, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> MoveAsync(string projectId, int sortOrder, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class FakeCatalog(List<SessionInfo> seed) : ISessionCatalog
    {
        private int queryCalls;

        public int QueryCalls => Volatile.Read(ref queryCalls);

        public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) =>
            throw new Xunit.Sdk.XunitException("no session is created here");

        public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct)
        {
            Interlocked.Increment(ref queryCalls);
            return Task.FromResult<IReadOnlyList<SessionInfo>>(query.ParentSessionId is { Length: > 0 } ? [] : seed.ToArray());
        }

        public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => Task.FromResult(true);

        public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }
}
