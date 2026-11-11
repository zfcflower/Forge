using System.Reflection;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins the failure edges of the "new session on a repository project" flow that
/// <see cref="ProjectTreeNewBranchFlowTests"/> does not cover:
/// <list type="bullet">
/// <item>a cancellation between the branch dialog and the creation creates neither a worktree nor a session row, and an
/// otherwise identical call that is not cancelled does create them (so the first assertion cannot pass vacuously);</item>
/// <item>an invalid branch name is reported with the fixed localized message and never with the raw
/// <see cref="ArgumentException"/> text, whose <c>(Parameter 'x')</c> suffix leaks the parameter name to the user;</item>
/// <item>when the session row cannot be written after the worktree exists (an I/O error, a store fault or a cancellation),
/// the fresh worktree directory is removed again and the failure surfaces through the status line (a cancellation stays
/// silent). The branch that <c>git worktree add</c> created is deliberately not asserted either way: removing it is a
/// known gap of the cleanup, not a contract.</item>
/// </list>
/// </summary>
[Collection("forge-app-ui")]
public sealed class ProjectTreeWorktreeSessionTests
{
    private const string ProjectId = "proj-1";

    [Theory]
    [InlineData("bad..name")]
    [InlineData("has space")]
    [InlineData("ends.lock")]
    [InlineData("tilde~x")]
    [InlineData("colon:x")]
    public async Task An_invalid_branch_name_reports_the_localized_reason_without_a_parameter_suffix(string typed)
    {
        using var sandbox = GitTestSandbox.Create();
        var repo = sandbox.CloneTracked("repo");

        var session = SharedUi.Session;
        var outcome = await session.Dispatch(
            async () =>
            {
                using var world = World.Create(repo, new RecordingCatalog(), (_, _) => Task.FromResult<NewBranchChoice?>(new NewBranchChoice("main", typed)));
                await world.Tree.RefreshAsync(CancellationToken.None);
                var baseline = world.Tree.StatusText;

                await world.RunNewSessionAsync();

                return new Outcome(
                    world.Tree.StatusText,
                    baseline,
                    world.Catalog.CreateCalls,
                    await world.Worktrees.ListAsync(repo, CancellationToken.None));
            },
            CancellationToken.None);

        Assert.NotEqual(outcome.Baseline, outcome.Status);
        Assert.Equal(Loc.Instance.Format(GitStrings.WorktreeBranchInvalid, typed), outcome.Status);
        Assert.Contains(typed, outcome.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("(Parameter", outcome.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("Parameter '", outcome.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("check-ref-format", outcome.Status, StringComparison.Ordinal);
        Assert.Equal(0, outcome.CatalogCalls);
        Assert.Empty(outcome.Worktrees);
        Assert.DoesNotContain(typed, GitTestShell.BranchList(repo));
    }

    [Fact]
    public async Task An_existing_branch_name_reports_the_unavailable_message_naming_the_branch()
    {
        using var sandbox = GitTestSandbox.Create();
        var repo = sandbox.CloneTracked("repo");
        GitTestShell.RunChecked(repo, "branch", "taken");

        var session = SharedUi.Session;
        var outcome = await session.Dispatch(
            async () =>
            {
                using var world = World.Create(repo, new RecordingCatalog(), (_, _) => Task.FromResult<NewBranchChoice?>(new NewBranchChoice("main", "taken")));
                await world.Tree.RefreshAsync(CancellationToken.None);
                var baseline = world.Tree.StatusText;

                await world.RunNewSessionAsync();

                return new Outcome(
                    world.Tree.StatusText,
                    baseline,
                    world.Catalog.CreateCalls,
                    await world.Worktrees.ListAsync(repo, CancellationToken.None));
            },
            CancellationToken.None);

        Assert.NotEqual(outcome.Baseline, outcome.Status);
        Assert.Contains("taken", outcome.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("(Parameter", outcome.Status, StringComparison.Ordinal);
        Assert.NotEqual(Loc.Instance.Format(GitStrings.WorktreeBranchInvalid, "taken"), outcome.Status);
        Assert.Equal(0, outcome.CatalogCalls);
        Assert.Empty(outcome.Worktrees);
    }

    [Fact]
    public async Task Cancelling_after_the_prompt_answered_creates_neither_a_worktree_nor_a_session()
    {
        using var sandbox = GitTestSandbox.Create();
        var repo = sandbox.CloneTracked("repo");

        var session = SharedUi.Session;
        var outcome = await session.Dispatch(
            async () =>
            {
                using var cancellation = new CancellationTokenSource();
                using var world = World.Create(
                    repo,
                    new RecordingCatalog(),
                    (_, _) =>
                    {
                        // The flow is cancelled while the dialog is open (the window closed under it); the answer
                        // that arrives afterwards must not be acted upon.
                        cancellation.Cancel();
                        return Task.FromResult<NewBranchChoice?>(new NewBranchChoice("main", "feature/never-created"));
                    });

                var task = world.InvokeCreateWorktreeSession(cancellation.Token);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

                return new Outcome(
                    world.Tree.StatusText,
                    string.Empty,
                    world.Catalog.CreateCalls,
                    await world.Worktrees.ListAsync(repo, CancellationToken.None));
            },
            CancellationToken.None);

        Assert.Equal(0, outcome.CatalogCalls);
        Assert.Empty(outcome.Worktrees);
        Assert.Empty(outcome.Status);
        Assert.DoesNotContain("feature/never-created", GitTestShell.BranchList(repo));
    }

    [Fact]
    public async Task The_same_call_without_cancellation_creates_the_worktree_and_the_session()
    {
        using var sandbox = GitTestSandbox.Create();
        var repo = sandbox.CloneTracked("repo");

        var session = SharedUi.Session;
        var outcome = await session.Dispatch(
            async () =>
            {
                using var world = World.Create(repo, new RecordingCatalog(), (_, _) => Task.FromResult<NewBranchChoice?>(new NewBranchChoice("main", "feature/created")));

                await world.InvokeCreateWorktreeSession(CancellationToken.None);

                return new Outcome(
                    world.Tree.StatusText,
                    string.Empty,
                    world.Catalog.CreateCalls,
                    await world.Worktrees.ListAsync(repo, CancellationToken.None));
            },
            CancellationToken.None);

        Assert.Equal(1, outcome.CatalogCalls);
        var worktree = Assert.Single(outcome.Worktrees);
        Assert.True(Directory.Exists(worktree.Path));
        Assert.Contains("feature/created", GitTestShell.BranchList(repo));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("invalid")]
    [InlineData("canceled")]
    public async Task A_failing_session_write_removes_the_worktree_that_was_just_created(string failure)
    {
        using var sandbox = GitTestSandbox.Create();
        var repo = sandbox.CloneTracked("repo");

        var session = SharedUi.Session;
        var outcome = await session.Dispatch(
            async () =>
            {
                Exception Failure() => failure switch
                {
                    "io" => new IOException("the catalog disk is full"),
                    "invalid" => new InvalidOperationException("the catalog is closed"),
                    _ => new OperationCanceledException("the flow was cancelled right after the worktree"),
                };

                var catalog = new RecordingCatalog { Failure = Failure };
                using var world = World.Create(repo, catalog, (_, _) => Task.FromResult<NewBranchChoice?>(new NewBranchChoice("main", "feature/orphan")));
                await world.Tree.RefreshAsync(CancellationToken.None);
                var baseline = world.Tree.StatusText;

                await world.RunNewSessionAsync();

                return new Outcome(
                    world.Tree.StatusText,
                    baseline,
                    catalog.CreateCalls,
                    await world.Worktrees.ListAsync(repo, CancellationToken.None))
                {
                    Requests = catalog.Requests,
                };
            },
            CancellationToken.None);

        // The failing write was reached with a real, existing worktree behind it ...
        var request = Assert.Single(outcome.Requests);
        Assert.Equal(1, outcome.CatalogCalls);
        Assert.False(string.IsNullOrEmpty(request.WorktreePath));
        Assert.True(request.WorktreeExistedAtCall, "the worktree must exist when the session row is written");
        Assert.Equal("feature/orphan", request.Branch);

        // ... and nothing refers to it any more afterwards: no directory, no registered worktree.
        Assert.False(Directory.Exists(request.WorktreePath), "the orphaned worktree directory was not removed");
        Assert.Empty(outcome.Worktrees);

        if (failure == "canceled")
        {
            // A cancellation is not an error: the command swallows it and the status line stays as it was.
            Assert.Equal(outcome.Baseline, outcome.Status);
        }
        else
        {
            Assert.NotEqual(outcome.Baseline, outcome.Status);
            Assert.Contains(failure == "io" ? "disk is full" : "catalog is closed", outcome.Status, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------ rig

    private sealed record Outcome(
        string Status,
        string Baseline,
        int CatalogCalls,
        IReadOnlyList<GitWorktree> Worktrees)
    {
        public IReadOnlyList<CreateRequest> Requests { get; init; } = [];
    }

    private sealed record CreateRequest(string? WorktreePath, string? Branch, bool WorktreeExistedAtCall);

    /// <summary>Everything one tree needs; disposing it detaches the tree from the process-wide language switch.</summary>
    private sealed class World : IDisposable
    {
        private static readonly MethodInfo CreateWorktreeSession = typeof(ProjectTreeViewModel).GetMethod(
            "CreateWorktreeSessionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(ProjectInfo), typeof(CancellationToken)],
            modifiers: null)
            ?? throw new InvalidOperationException("ProjectTreeViewModel.CreateWorktreeSessionAsync(ProjectInfo, CancellationToken) was not found.");

        private readonly ProjectInfo project;

        private World(ProjectInfo project, ProjectTreeViewModel tree, RecordingCatalog catalog, WorktreeService worktrees)
        {
            this.project = project;
            Tree = tree;
            Catalog = catalog;
            Worktrees = worktrees;
        }

        public ProjectTreeViewModel Tree { get; }

        public RecordingCatalog Catalog { get; }

        public WorktreeService Worktrees { get; }

        public static World Create(string repositoryRoot, RecordingCatalog catalog, Func<NewBranchRequest, CancellationToken, Task<NewBranchChoice?>> answer)
        {
            var project = new ProjectInfo(ProjectId, "repo", repositoryRoot, ProjectKind.Repository, 0, DateTimeOffset.UtcNow);
            var worktrees = new WorktreeService(new GitCli());
            var tree = new ProjectTreeViewModel(
                new SingleProjectRegistry(project),
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
                answer,
                _ => Task.CompletedTask,
                _ => Task.CompletedTask);
            return new World(project, tree, catalog, worktrees);
        }

        /// <summary>Runs the whole "new session" command of the project row and waits until it has fully finished.</summary>
        public async Task RunNewSessionAsync()
        {
            var node = Tree.Sections.Single(static node => node.Kind == TreeNodeKind.Project);
            var command = node.NewSessionCommand!;
            command.Execute(null);

            // The command disables itself for the whole run, cleanup included, so re-enabling marks the end.
            await GitPanelTesting.WaitUntilAsync(() => command.CanExecute(null), "the new-session command to finish");
        }

        /// <summary>
        /// The command always runs with <see cref="CancellationToken.None"/>, so a cancellation between the dialog and the
        /// creation can only be driven by calling the private flow directly.
        /// </summary>
        public Task InvokeCreateWorktreeSession(CancellationToken ct) =>
            (Task)CreateWorktreeSession.Invoke(Tree, [project, ct])!;

        public void Dispose() => Tree.Dispose();
    }

    private sealed class SingleProjectRegistry(ProjectInfo project) : IProjectRegistry
    {
        public Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProjectInfo>>([project]);

        public Task<ProjectInfo> AddAsync(string rootPath, string name, ProjectKind kind, CancellationToken ct) =>
            Task.FromResult(project);

        public Task<bool> RemoveAsync(string projectId, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> RenameAsync(string projectId, string name, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> MoveAsync(string projectId, int sortOrder, CancellationToken ct) => Task.FromResult(false);
    }

    /// <summary>
    /// Records every create request (the flow calls it on a pool thread) and either answers like a working store or
    /// throws <see cref="Failure"/> after noting whether the worktree directory existed at that moment.
    /// </summary>
    private sealed class RecordingCatalog : ISessionCatalog
    {
        private readonly object gate = new();
        private readonly List<CreateRequest> requests = [];

        public Func<Exception>? Failure { get; init; }

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

        public IReadOnlyList<CreateRequest> Requests
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
                requests.Add(new CreateRequest(
                    request.WorktreePath,
                    request.Branch,
                    request.WorktreePath is { Length: > 0 } path && Directory.Exists(path)));
            }

            if (Failure is { } failure)
            {
                return Task.FromException<SessionInfo>(failure());
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

        public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) =>
            Task.FromResult(true);

        public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }
}
