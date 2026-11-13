using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Git;
using Forge.App.Lifecycle;
using Forge.App.Localization;
using Forge.App.Tests.Git;
using Forge.App.ViewModels;
using Forge.App.Workbench;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// The shell half of the parallel fork and merge back / sync (A3, TASK-session-worktree-lifecycle 6.5 and 6.6), against a
/// real composition root and a real repository: a refused or unavailable fork is said, "fork anyway" and "commit first"
/// open the new session and leave the source going, a busy source only offers "fork anyway" and keeps its queue; a merge
/// holds the target workspace (operation flag, handing-over tab) only while it runs, a conflict leaves the workspace in the
/// conflict state until the abort, a merge back without a target degrades to pushing the fork branch, a busy target is
/// refused with the reasons, the removal after a merge back gets the source branch as its base branch, and a turn in a
/// workspace that is operating is rejected without dropping the draft.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ForkMergeShellTests
{
    private const int PumpIterations = 400;
    private const int PumpDelayMilliseconds = 5;
    private const string SourceTitle = "source";

    [Fact]
    public async Task A_fork_without_dialogs_is_a_status_problem()
    {
        await RunAsync(
            async harness =>
            {
                await harness.Shell.ForkWorkspaceAsync(await harness.SourceForkSourceAsync(), HandoverContextMode.Full, CancellationToken.None);

                Assert.Equal(ForkStrings.Unavailable, harness.Shell.Status.HostDetail);
                Assert.Single(await harness.RootSessionsAsync());
            },
            withDialogs: false);
    }

    [Fact]
    public async Task A_fork_of_a_folder_that_is_not_a_repository_is_refused_with_a_notice()
    {
        await RunAsync(
            async harness =>
            {
                var plain = Path.Combine(harness.Root, "plain");
                Directory.CreateDirectory(plain);
                var source = new ForkSource(harness.Project with { RootPath = plain }, plain, null);

                await harness.Shell.ForkWorkspaceAsync(source, HandoverContextMode.Summary, CancellationToken.None);

                var notice = Assert.Single(harness.Dialogs.Notices);
                Assert.Equal(ForkStrings.Title, notice.Title);
                Assert.Equal(ForkStrings.NotRepository, notice.Message);
                Assert.Empty(harness.Dialogs.Forks);
            });
    }

    [Fact]
    public async Task Fork_anyway_opens_the_new_session_on_its_own_branch_and_leaves_the_source_and_its_changes_alone()
    {
        await RunAsync(
            async harness =>
            {
                var head = harness.Head(harness.Repository);
                File.WriteAllText(Path.Combine(harness.Repository, "scratch.txt"), "draft\n");
                harness.Dialogs.ConfigureFork = model =>
                {
                    model.CommitFirst = false;
                    model.Mode = HandoverContextMode.Full;
                    model.BranchName = "forge/fork-a";
                };

                await harness.Shell.ForkWorkspaceAsync(await harness.SourceForkSourceAsync(), HandoverContextMode.Full, CancellationToken.None);

                var fork = Assert.Single(harness.Dialogs.Forks);
                Assert.True(fork.HasSession);
                Assert.True(fork.OffersCommitFirst);
                Assert.Equal(1, fork.UncommittedCount);
                Assert.Null(fork.Error);

                var live = harness.ActiveView();
                Assert.NotEqual(harness.SourceId, live.SessionId);
                var info = await harness.InfoAsync(live.SessionId);
                Assert.Equal(SessionStatus.Active, info.Status);
                Assert.Equal("forge/fork-a", info.Branch);
                Assert.Equal("main", info.ForkOfBranch);
                Assert.Equal(head, info.ForkBaseSha);
                Assert.Equal(SessionOrigin.Full, info.Origin);
                Assert.Equal(harness.SourceId, info.InheritsFromSessionId);
                Assert.Equal(harness.SourceId, info.LineageRootId);
                Assert.Null(info.ParentSessionId);
                Assert.NotNull(info.WorktreePath);
                Assert.True(Directory.Exists(info.WorktreePath));
                Assert.False(File.Exists(Path.Combine(info.WorktreePath!, "scratch.txt")));
                Assert.Equal("forge/fork-a", harness.Git(info.WorktreePath!, "branch", "--show-current").Trim());

                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(harness.SourceId)).Status);
                Assert.True(File.Exists(Path.Combine(harness.Repository, "scratch.txt")));
                Assert.Equal(head, harness.Head(harness.Repository));
            });
    }

    [Fact]
    public async Task Commit_first_commits_in_the_source_sessions_name_and_forks_from_that_commit()
    {
        await RunAsync(
            async harness =>
            {
                File.WriteAllText(Path.Combine(harness.Repository, "file.txt"), "v2\n");
                harness.Dialogs.ConfigureFork = model => model.Mode = HandoverContextMode.Blank;

                await harness.Shell.ForkWorkspaceAsync(await harness.SourceForkSourceAsync(), HandoverContextMode.Full, CancellationToken.None);

                var fork = Assert.Single(harness.Dialogs.Forks);
                Assert.True(fork.CommitFirst);
                Assert.Null(fork.Error);
                Assert.Equal(HandoverFlow.CommitPrefix + SourceTitle, harness.Git(harness.Repository, "log", "-1", "--format=%s").Trim());
                Assert.Empty(harness.Git(harness.Repository, "status", "--porcelain").Trim());

                var info = await harness.InfoAsync(harness.ActiveView().SessionId);
                Assert.Equal(harness.Head(harness.Repository), info.ForkBaseSha);
                Assert.Equal(SessionOrigin.Blank, info.Origin);
                Assert.Equal("v2\n", File.ReadAllText(Path.Combine(info.WorktreePath!, "file.txt")));
                Assert.False(harness.SourceView.IsHandingOver);
            });
    }

    [Fact]
    public async Task Commit_first_holds_the_source_workspace_and_session_only_while_the_attempt_runs()
    {
        await RunAsync(
            async harness =>
            {
                File.WriteAllText(Path.Combine(harness.Repository, "file.txt"), "v2\n");
                var during = new List<(bool Operating, bool HandingOver)>();
                harness.HookForkCommit(() => during.Add((
                    harness.Composition.WorkspaceStates.IsOperating(harness.Repository),
                    Volatile.Read(ref harness.SourceHandingOverSnapshot))));
                harness.Dialogs.ConfigureFork = model => model.Mode = HandoverContextMode.Blank;

                await harness.Shell.ForkWorkspaceAsync(await harness.SourceForkSourceAsync(), HandoverContextMode.Full, CancellationToken.None);

                Assert.Null(Assert.Single(harness.Dialogs.Forks).Error);
                Assert.Equal([(true, true)], during);
                Assert.False(harness.Composition.WorkspaceStates.IsOperating(harness.Repository));
                Assert.False(harness.SourceView.IsHandingOver);
            });
    }

    [Fact]
    public async Task A_fork_from_a_source_session_taken_over_since_the_tree_refresh_is_refused_with_a_notice()
    {
        await RunAsync(
            async harness =>
            {
                var stale = await harness.SourceForkSourceAsync();
                await Task.Run(() => harness.Composition.Catalog.SetStatusAsync(harness.SourceId, SessionStatus.Archived, CancellationToken.None));

                await harness.Shell.ForkWorkspaceAsync(stale, HandoverContextMode.Full, CancellationToken.None);

                var notice = Assert.Single(harness.Dialogs.Notices);
                Assert.Equal(ForkStrings.SourceChanged(SourceTitle), notice.Message);
                Assert.Empty(harness.Dialogs.Forks);
                Assert.Single(await harness.RootSessionsAsync());
            });
    }

    [Fact]
    public async Task A_busy_source_only_offers_fork_anyway_and_keeps_its_queue()
    {
        await RunAsync(
            async harness =>
            {
                File.WriteAllText(Path.Combine(harness.Repository, "file.txt"), "v2\n");
                harness.SourceView.RestoreComposerState((string.Empty, ["queued one"]));
                harness.Dialogs.ConfigureFork = model =>
                {
                    model.CommitFirst = true;
                    model.Mode = HandoverContextMode.Blank;
                };

                await harness.Shell.ForkWorkspaceAsync(await harness.SourceForkSourceAsync(), HandoverContextMode.Full, CancellationToken.None);

                var fork = Assert.Single(harness.Dialogs.Forks);
                Assert.True(fork.SourceBusy);
                Assert.False(fork.OffersCommitFirst);
                Assert.False(fork.CommitFirst);
                Assert.Null(fork.Error);
                Assert.Equal("initial", harness.Git(harness.Repository, "log", "-1", "--format=%s").Trim());
                Assert.Equal("v2\n", File.ReadAllText(Path.Combine(harness.Repository, "file.txt")));
                Assert.True(harness.SourceView.HasQueued);
                Assert.False(harness.SourceView.IsHandingOver);
                Assert.NotEqual(harness.SourceId, harness.ActiveView().SessionId);
            });
    }

    [Fact]
    public async Task A_merge_back_holds_the_target_only_while_it_runs_then_offers_the_removal_with_the_source_branch()
    {
        await RunAsync(
            async harness =>
            {
                var relation = await harness.CreateForkAsync();
                harness.CommitIn(relation.ForkDirectory, "feature.txt", "feature\n", "feature work");
                var during = new List<(bool Operating, bool HandingOver)>();
                harness.HookMerge(() => during.Add((
                    harness.Composition.WorkspaceStates.IsOperating(harness.Repository),
                    Volatile.Read(ref harness.SourceHandingOverSnapshot))));
                var removals = new List<(ForkRelation Relation, string BaseBranch)>();
                harness.Shell.RemoveForkWorkspace = (value, baseBranch, _) =>
                {
                    removals.Add((value, baseBranch));
                    return Task.FromResult<string?>(null);
                };
                harness.Dialogs.ChooseAnswers.Enqueue(0);
                harness.Dialogs.ChooseAnswers.Enqueue(0);

                await harness.Shell.MergeForkAsync(relation, MergeDirection.MergeBack, CancellationToken.None);

                Assert.Equal([(true, true)], during);
                Assert.False(harness.Composition.WorkspaceStates.IsOperating(harness.Repository));
                Assert.False(harness.SourceView.IsHandingOver);
                var confirm = harness.Dialogs.Choices[0];
                Assert.Equal(ForkStrings.MergeBackTitle, confirm.Title);
                Assert.Contains("feature work", confirm.Message, StringComparison.Ordinal);
                Assert.Equal([ForkStrings.Merge, ShellStrings.Cancel], confirm.Options);
                var offer = harness.Dialogs.Choices[1];
                Assert.Equal(ForkStrings.Merged("fork", "main"), offer.Message);
                Assert.Equal([ForkStrings.OfferRemove, ForkStrings.KeepFork], offer.Options);
                var removal = Assert.Single(removals);
                Assert.Equal("main", removal.BaseBranch);
                Assert.Equal(relation.ForkDirectory, removal.Relation.ForkDirectory);

                // A --no-ff merge commit on main, with the fork branch as its second parent.
                var parents = harness.Git(harness.Repository, "rev-list", "--parents", "-n", "1", "main").Trim().Split(' ');
                Assert.Equal(3, parents.Length);
                Assert.Equal(harness.Head(relation.ForkDirectory), parents[2]);
                Assert.Empty(harness.Dialogs.Notices);
            });
    }

    [Fact]
    public async Task A_merge_conflict_puts_the_target_in_the_conflict_state_until_the_abort()
    {
        await RunAsync(
            async harness =>
            {
                var relation = await harness.CreateForkAsync();
                harness.CommitIn(relation.ForkDirectory, "file.txt", "fork\n", "fork side");
                harness.CommitIn(harness.Repository, "file.txt", "main\n", "main side");
                var mainHead = harness.Head(harness.Repository);
                harness.Dialogs.ChooseAnswers.Enqueue(0);
                harness.Dialogs.ChooseAnswers.Enqueue(1);
                var states = harness.Composition.WorkspaceStates;

                await harness.Shell.MergeForkAsync(relation, MergeDirection.MergeBack, CancellationToken.None);

                var conflict = harness.Dialogs.Choices[1];
                Assert.Equal([ForkStrings.MenuAbort, ForkStrings.Later], conflict.Options);
                Assert.True(states.IsConflicted(harness.Repository));
                Assert.False(states.IsOperating(harness.Repository));
                Assert.True(File.Exists(Path.Combine(harness.Repository, ".git", "MERGE_HEAD")));

                // While conflicted the source cannot start a turn; the draft stays.
                harness.SourceView.Draft = "next";
                harness.SourceView.SendCommand.Execute(null);
                var pending = LifecycleStrings.MergeConflictPending.Resolve();
                await PumpUntilAsync(() => harness.SourceView.Transcript.Items.Any(item => item is NoticeViewModel notice && notice.Text == pending));
                Assert.Equal("next", harness.SourceView.Draft);
                Assert.False(harness.SourceView.IsRunning);

                await harness.Shell.AbortMergeAsync(harness.Repository, CancellationToken.None);

                Assert.False(states.IsConflicted(harness.Repository));
                Assert.False(states.IsOperating(harness.Repository));
                Assert.False(File.Exists(Path.Combine(harness.Repository, ".git", "MERGE_HEAD")));
                Assert.Equal(mainHead, harness.Head(harness.Repository));
                Assert.Equal(ForkStrings.Aborted, harness.Dialogs.Notices[^1].Message);
            });
    }

    [Fact]
    public async Task An_abort_that_git_refuses_says_why_and_the_refresh_reads_the_state_back_from_git()
    {
        await RunAsync(
            async harness =>
            {
                // A stale flag with no merge in progress: git refuses the abort; the refresh after it re-reads MERGE_HEAD.
                var states = harness.Composition.WorkspaceStates;
                states.SetConflicted(harness.Repository, true);

                await harness.Shell.AbortMergeAsync(harness.Repository, CancellationToken.None);

                var notice = Assert.Single(harness.Dialogs.Notices);
                Assert.Equal(ForkStrings.MenuAbort, notice.Title);
                Assert.NotEqual(ForkStrings.Aborted, notice.Message);
                Assert.StartsWith(ForkStrings.AbortFailed(string.Empty), notice.Message, StringComparison.Ordinal);
                Assert.True(notice.Message.Length > ForkStrings.AbortFailed(string.Empty).Length);
                Assert.False(states.IsOperating(harness.Repository));
                Assert.False(states.IsConflicted(harness.Repository));
            });
    }

    [Fact]
    public async Task A_merge_back_without_a_target_offers_to_push_the_fork_branch()
    {
        await RunAsync(
            async harness =>
            {
                var relation = await harness.CreateForkAsync() with { SourceBranch = "release" };
                harness.Dialogs.ChooseAnswers.Enqueue(0);

                await harness.Shell.MergeForkAsync(relation, MergeDirection.MergeBack, CancellationToken.None);

                var offer = Assert.Single(harness.Dialogs.Choices);
                Assert.Equal(ForkStrings.TargetMissing("release", "fork"), offer.Message);
                Assert.Equal([ForkStrings.PushFork("fork"), ShellStrings.Cancel], offer.Options);
                Assert.Equal(ForkStrings.Pushed("fork"), Assert.Single(harness.Dialogs.Notices).Message);
                Assert.Contains("refs/heads/fork", harness.Git(harness.Repository, "ls-remote", "origin", "fork"), StringComparison.Ordinal);
                Assert.False(harness.Composition.WorkspaceStates.IsOperating(harness.Repository));
            });
    }

    [Fact]
    public async Task A_busy_target_is_refused_with_the_reasons_and_nothing_is_merged()
    {
        await RunAsync(
            async harness =>
            {
                var relation = await harness.CreateForkAsync();
                harness.CommitIn(relation.ForkDirectory, "feature.txt", "feature\n", "feature work");
                var mainHead = harness.Head(harness.Repository);
                harness.SourceView.RestoreComposerState((string.Empty, ["queued one"]));
                harness.Dialogs.ChooseAnswers.Enqueue(0);

                await harness.Shell.MergeForkAsync(relation, MergeDirection.MergeBack, CancellationToken.None);

                Assert.Empty(harness.Dialogs.Choices);
                var notice = Assert.Single(harness.Dialogs.Notices);
                Assert.Equal(ForkStrings.Busy(SourceTitle, [HandoverStrings.BusyQueued]), notice.Message);
                Assert.Equal(mainHead, harness.Head(harness.Repository));
                Assert.True(harness.SourceView.HasQueued);
                Assert.False(harness.Composition.WorkspaceStates.IsOperating(harness.Repository));
            });
    }

    [Fact]
    public async Task A_sync_merges_the_source_into_the_fork_workspace()
    {
        await RunAsync(
            async harness =>
            {
                var relation = await harness.CreateForkAsync();
                harness.CommitIn(harness.Repository, "upstream.txt", "upstream\n", "upstream work");
                harness.Dialogs.ChooseAnswers.Enqueue(0);

                await harness.Shell.MergeForkAsync(relation, MergeDirection.Sync, CancellationToken.None);

                var confirm = Assert.Single(harness.Dialogs.Choices);
                Assert.Equal(ForkStrings.SyncTitle, confirm.Title);
                Assert.Equal([ForkStrings.Sync, ShellStrings.Cancel], confirm.Options);
                Assert.Equal(ForkStrings.Merged("main", "fork"), Assert.Single(harness.Dialogs.Notices).Message);
                Assert.True(File.Exists(Path.Combine(relation.ForkDirectory, "upstream.txt")));
            });
    }

    [Fact]
    public async Task A_turn_in_a_workspace_that_is_operating_is_rejected_and_the_draft_stays()
    {
        await RunAsync(
            async harness =>
            {
                var states = harness.Composition.WorkspaceStates;
                Assert.True(states.TryBeginOperation(harness.Repository));
                try
                {
                    harness.SourceView.Draft = "hello";
                    harness.SourceView.SendCommand.Execute(null);
                    var operating = LifecycleStrings.WorkspaceOperating.Resolve();
                    await PumpUntilAsync(() => harness.SourceView.Transcript.Items.Any(item => item is NoticeViewModel notice && notice.Text == operating));
                }
                finally
                {
                    states.EndOperation(harness.Repository);
                }

                Assert.False(harness.SourceView.IsRunning);
                Assert.False(harness.SourceView.IsReadOnlyView);
                Assert.Equal("hello", harness.SourceView.Draft);
                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(harness.SourceId)).Status);
            });
    }

    private static async Task RunAsync(Func<Harness, Task> body, bool withDialogs = true)
    {
        await SharedUi.Session.Dispatch(
            async () =>
            {
                var harness = await Harness.CreateAsync(withDialogs);
                try
                {
                    await body(harness);
                }
                finally
                {
                    await harness.DisposeAsync();
                }

                return 0;
            },
            CancellationToken.None);
    }

    private static async Task PumpUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < PumpIterations && !condition(); i++)
        {
            await Task.Delay(PumpDelayMilliseconds);
        }

        Assert.True(condition(), "the pending action did not finish");
    }

    private sealed record NotifyCall(string Title, string Message);

    private sealed record ChooseCall(string Title, string Message, IReadOnlyList<string> Options);

    private sealed record ForkCall(bool HasSession, bool SourceBusy, bool OffersCommitFirst, bool CommitFirst, int UncommittedCount, string? Error);

    /// <summary>Records every dialog; choices are answered from <see cref="ChooseAnswers"/> (-1 = closed when empty).</summary>
    private sealed class RecordingDialogs : IDialogService
    {
        public Queue<int> ChooseAnswers { get; } = new();

        public List<ChooseCall> Choices { get; } = [];

        public List<NotifyCall> Notices { get; } = [];

        public List<ForkCall> Forks { get; } = [];

        public Action<ForkDialogModel>? ConfigureFork { get; set; }

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options)
        {
            Choices.Add(new ChooseCall(title, message, options));
            return Task.FromResult(ChooseAnswers.Count > 0 ? ChooseAnswers.Dequeue() : -1);
        }

        /// <summary>Runs the fork once, as a user confirming the dialog after <see cref="ConfigureFork"/> would.</summary>
        public async Task<bool?> RunForkAsync(ForkDialogModel model, CancellationToken ct)
        {
            ConfigureFork?.Invoke(model);
            var succeeded = await model.ExecuteAsync();
            Forks.Add(new ForkCall(model.HasSession, model.SourceBusy, model.OffersCommitFirst, model.CommitFirst, model.UncommittedCount, model.Error));
            return succeeded;
        }

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText) => Task.FromResult(false);

        public Task<string?> PromptTextAsync(string title, string initial) => Task.FromResult<string?>(null);

        public Task<NewBranchChoice?> PickNewBranchAsync(NewBranchRequest request, CancellationToken ct) =>
            Task.FromResult<NewBranchChoice?>(null);

        public Task NotifyAsync(string title, string message)
        {
            Notices.Add(new NotifyCall(title, message));
            return Task.CompletedTask;
        }
    }

    /// <summary>The real fork git, with a hook run on the pool thread right before the "commit first" commit.</summary>
    private sealed class HookedForkGit(IForkGit inner, Action beforeCommit) : IForkGit
    {
        public Task<IReadOnlyList<string>?> UncommittedAsync(string directory, CancellationToken ct) => inner.UncommittedAsync(directory, ct);

        public Task<string?> HeadAsync(string directory, CancellationToken ct) => inner.HeadAsync(directory, ct);

        public Task<StepCommitResult> CommitAsync(string directory, string message, CancellationToken ct)
        {
            beforeCommit();
            return inner.CommitAsync(directory, message, ct);
        }

        public Task<GitHead> ReadHeadAsync(string directory, CancellationToken ct) => inner.ReadHeadAsync(directory, ct);

        public Task<SparseCheckout?> SparseAsync(string directory, CancellationToken ct) => inner.SparseAsync(directory, ct);

        public Task<bool> IsValidBranchNameAsync(string repositoryRoot, string name, CancellationToken ct) =>
            inner.IsValidBranchNameAsync(repositoryRoot, name, ct);

        public Task<WorktreeSession> CreateWorktreeAsync(
            string repositoryRoot,
            string title,
            string branchName,
            string startPoint,
            SparseCheckout? sparse,
            CancellationToken ct) =>
            inner.CreateWorktreeAsync(repositoryRoot, title, branchName, startPoint, sparse, ct);

        public Task RemoveCreatedAsync(WorktreeSession worktree, CancellationToken ct) => inner.RemoveCreatedAsync(worktree, ct);
    }

    /// <summary>The real git, with a hook run on the pool thread right before the merge itself.</summary>
    private sealed class HookedMergeGit(IMergeGit inner, Action beforeMerge) : IMergeGit
    {
        public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string repositoryRoot, CancellationToken ct) =>
            inner.ListWorktreesAsync(repositoryRoot, ct);

        public Task<string?> BranchCommitAsync(string repositoryRoot, string branch, CancellationToken ct) =>
            inner.BranchCommitAsync(repositoryRoot, branch, ct);

        public Task<IReadOnlyList<GitCommit>> ListCommitsAsync(string repositoryRoot, string range, CancellationToken ct) =>
            inner.ListCommitsAsync(repositoryRoot, range, ct);

        public Task<int> CountCommitsAsync(string repositoryRoot, string exclude, string revision, CancellationToken ct) =>
            inner.CountCommitsAsync(repositoryRoot, exclude, revision, ct);

        public Task<bool> IsAncestorAsync(string repositoryRoot, string ancestor, string descendant, CancellationToken ct) =>
            inner.IsAncestorAsync(repositoryRoot, ancestor, descendant, ct);

        public Task<GitRemoteResult> MergeAsync(string directory, string revision, CancellationToken ct)
        {
            beforeMerge();
            return inner.MergeAsync(directory, revision, ct);
        }

        public Task<bool> HasMergeInProgressAsync(string directory, CancellationToken ct) => inner.HasMergeInProgressAsync(directory, ct);

        public Task AbortMergeAsync(string directory, CancellationToken ct) => inner.AbortMergeAsync(directory, ct);

        public Task<GitRemoteResult> PushBranchAsync(string repositoryRoot, string branch, CancellationToken ct) =>
            inner.PushBranchAsync(repositoryRoot, branch, ct);
    }

    /// <summary>
    /// A throw-away data root, a real composition root and a real clone of a bare origin registered as a repository
    /// project, with one Active session in its main checkout open as a live tab.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly EnvironmentSnapshot previous;
        private readonly GitTestSandbox sandbox;

        /// <summary>The source tab's handing-over flag, mirrored on the UI thread so the merge hook can read it from the pool.</summary>
        public bool SourceHandingOverSnapshot;

        private Harness(
            string root,
            EnvironmentSnapshot previous,
            GitTestSandbox sandbox,
            AppComposition composition,
            ProjectInfo project,
            string sourceId,
            MainWindowViewModel shell,
            RecordingDialogs dialogs)
        {
            Root = root;
            this.previous = previous;
            this.sandbox = sandbox;
            Composition = composition;
            Project = project;
            SourceId = sourceId;
            Shell = shell;
            Dialogs = dialogs;
        }

        public string Root { get; }

        public string Repository => Project.RootPath;

        public AppComposition Composition { get; }

        public ProjectInfo Project { get; }

        public string SourceId { get; }

        public MainWindowViewModel Shell { get; }

        public RecordingDialogs Dialogs { get; }

        public SessionWorkspaceViewModel SourceView { get; private set; } = null!;

        public static async Task<Harness> CreateAsync(bool withDialogs)
        {
            var (root, previous) = TempDataRoot.Redirect("forge-fork-shell-");

            var sandbox = GitTestSandbox.Create();
            var repository = sandbox.CloneTracked("repo");
            var composition = AppComposition.Create();
            var project = await Task.Run(() => composition.Projects.AddAsync(repository, "repo", ProjectKind.Repository, CancellationToken.None));
            var source = await Task.Run(() => composition.Catalog.CreateAsync(
                new SessionCreate(repository, SourceTitle) { ProjectId = project.Id },
                CancellationToken.None));

            var dialogs = new RecordingDialogs();
            var shell = new MainWindowViewModel(composition);
            if (withDialogs)
            {
                shell.AttachDialogs(dialogs);
            }

            await shell.InitializeAsync();
            var harness = new Harness(root, previous, sandbox, composition, project, source.Id, shell, dialogs);
            await shell.OpenSessionByIdAsync(source.Id, CancellationToken.None);
            harness.SourceView = harness.ActiveView();
            Assert.Equal(source.Id, harness.SourceView.SessionId);
            Assert.False(harness.SourceView.IsReadOnlyView);
            harness.SourceView.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SessionWorkspaceViewModel.IsHandingOver))
                {
                    Volatile.Write(ref harness.SourceHandingOverSnapshot, harness.SourceView.IsHandingOver);
                }
            };
            return harness;
        }

        public async Task<ForkSource> SourceForkSourceAsync() => new(Project, Repository, await InfoAsync(SourceId));

        /// <summary>A fork worktree on branch <c>fork</c> from <c>main</c>, with its Active session, as a fork created it.</summary>
        public async Task<ForkRelation> CreateForkAsync()
        {
            var directory = Path.Combine(sandbox.Root, "fork-wt");
            var baseSha = Head(Repository);
            Git(Repository, "worktree", "add", "-q", "-b", "fork", directory, "main");
            await Task.Run(() => Composition.Catalog.CreateAsync(
                new SessionCreate(directory, "fork")
                {
                    ProjectId = Project.Id,
                    WorktreePath = directory,
                    Branch = "fork",
                    ForkOfBranch = "main",
                    ForkBaseSha = baseSha,
                },
                CancellationToken.None));
            return new ForkRelation(Project, directory, "fork", "main", baseSha);
        }

        public void HookForkCommit(Action beforeCommit) =>
            Composition.Fork = new ForkFlow(
                Composition.Catalog,
                Composition.Sessions,
                new HookedForkGit(
                    new ForkGit(Composition.Git, (StepCommitter)Composition.TodoStepCommitter, Composition.Worktrees),
                    beforeCommit),
                static (_, _, _) => Task.FromResult("summary"));

        public void HookMerge(Action beforeMerge) =>
            Composition.Merge = new MergeFlow(new HookedMergeGit(new MergeGit(Composition.Git), beforeMerge));

        public void CommitIn(string directory, string relativePath, string content, string message) =>
            sandbox.CommitFile(directory, relativePath, content, message);

        public string Git(string directory, params string[] arguments) => GitTestShell.RunChecked(directory, arguments);

        public string Head(string directory) => Git(directory, "rev-parse", "HEAD").Trim();

        public SessionWorkspaceViewModel ActiveView() => Assert.IsType<SessionWorkspaceViewModel>(Shell.ActiveSession);

        public async Task<SessionInfo> InfoAsync(string sessionId) =>
            Assert.IsType<SessionInfo>(await Task.Run(() => Composition.Sessions.GetSessionAsync(sessionId, CancellationToken.None)));

        public async Task<IReadOnlyList<SessionInfo>> RootSessionsAsync() =>
            await Task.Run(() => Composition.Sessions.ListSessionsAsync(null, CancellationToken.None));

        public async ValueTask DisposeAsync()
        {
            Shell.Files.Attach(PanelContext.Empty);
            Shell.Dispose();
            await Composition.DisposeAsync();
            previous.Restore();
            sandbox.Dispose();
            GitTestFiles.DeleteTree(Root);
        }
    }

}
