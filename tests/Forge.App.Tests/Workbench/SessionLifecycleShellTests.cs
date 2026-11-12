using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Lifecycle;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// The shell half of the session lifecycle (A1, sections 6.3 and 7.6): opening a history session never changes its
/// status and shows it read-only, the turn-start check rejects a turn on a session taken over elsewhere or a workspace in
/// a merge conflict, an occupied workspace ends in the occupied dialog instead of the generic error, "continue" makes a
/// session live again, and the startup storage notices are shown once. Each test runs against a real composition root
/// over a throw-away data root.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionLifecycleShellTests
{
    private const int PumpIterations = 400;
    private const int PumpDelayMilliseconds = 5;
    private const string Message = "hello";

    [Fact]
    public async Task Opening_an_archived_session_keeps_its_status_and_opens_it_read_only()
    {
        await RunAsync(
            sessionCount: 2,
            async harness =>
            {
                var archived = harness.SessionIds[1];

                await harness.ViewModel.OpenSessionByIdAsync(archived, CancellationToken.None);

                Assert.Equal(SessionStatus.Archived, (await harness.InfoAsync(archived)).Status);
                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(harness.SessionIds[0])).Status);
                var view = Assert.IsType<SessionWorkspaceViewModel>(harness.ViewModel.ActiveSession);
                Assert.Equal(archived, view.SessionId);
                Assert.True(view.IsReadOnlyView);
                Assert.True(view.HasContinue);
                Assert.False(view.SendCommand.CanExecute(null));
            });
    }

    [Fact]
    public async Task A_turn_on_a_session_archived_elsewhere_is_rejected_and_the_tab_turns_read_only()
    {
        await RunAsync(
            sessionCount: 1,
            async harness =>
            {
                var view = harness.ActiveView();
                Assert.False(view.IsReadOnlyView);
                await Task.Run(() => harness.Composition.Catalog.SetStatusAsync(view.SessionId, SessionStatus.Archived, CancellationToken.None));

                view.Draft = Message;
                view.SendCommand.Execute(null);
                await PumpUntilAsync(() => view.IsReadOnlyView);

                Assert.False(view.IsRunning);
                Assert.Equal(Message, view.Draft);
                Assert.Contains(view.Transcript.Items, item => item is NoticeViewModel notice && notice.Text == LifecycleStrings.TakenOver.Resolve());
                Assert.True(view.HasContinue);
            });
    }

    [Fact]
    public async Task A_turn_in_a_workspace_with_a_merge_conflict_is_rejected_but_the_tab_stays_live()
    {
        await RunAsync(
            sessionCount: 1,
            async harness =>
            {
                var view = harness.ActiveView();
                harness.Composition.WorkspaceStates.SetConflicted(harness.WorkDirectory, value: true);

                view.Draft = Message;
                view.SendCommand.Execute(null);
                var conflict = LifecycleStrings.MergeConflictPending.Resolve();
                await PumpUntilAsync(() => view.Transcript.Items.Any(item => item is NoticeViewModel notice && notice.Text == conflict));

                Assert.False(view.IsRunning);
                Assert.False(view.IsReadOnlyView);
                Assert.Equal(Message, view.Draft);
                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(view.SessionId)).Status);
            });
    }

    [Fact]
    public async Task Continuing_a_session_in_an_occupied_workspace_shows_the_occupied_dialog_and_changes_nothing()
    {
        var dialogs = new RecordingDialogs { ChooseResult = 2 };
        await RunAsync(
            sessionCount: 2,
            async harness =>
            {
                var holder = harness.SessionIds[0];
                var archived = harness.SessionIds[1];

                await harness.ViewModel.ContinueSessionAsync(archived, CancellationToken.None);

                var choice = Assert.Single(dialogs.Choices);
                Assert.Equal(LifecycleStrings.OccupiedTitle, choice.Title);
                Assert.Equal(HandoverStrings.OccupiedQuestion(Harness.TitleOf(0)), choice.Message);
                Assert.Equal([HandoverStrings.TakeOver, LifecycleStrings.ViewHolder(Harness.TitleOf(0)), ShellStrings.Cancel], choice.Options);
                Assert.Empty(dialogs.Handovers);
                Assert.DoesNotContain(Harness.TitleOf(0), harness.ViewModel.Status.HostDetail, StringComparison.Ordinal);
                Assert.Equal(SessionStatus.Archived, (await harness.InfoAsync(archived)).Status);
                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(holder)).Status);
            },
            dialogs);
    }

    [Fact]
    public async Task Viewing_the_holder_from_the_occupied_dialog_opens_the_active_session()
    {
        var dialogs = new RecordingDialogs { ChooseResult = 1 };
        await RunAsync(
            sessionCount: 2,
            async harness =>
            {
                await harness.ViewModel.OpenSessionByIdAsync(harness.SessionIds[1], CancellationToken.None);

                await harness.ViewModel.ContinueSessionAsync(harness.SessionIds[1], CancellationToken.None);

                Assert.Single(dialogs.Choices);
                Assert.Empty(dialogs.Handovers);
                var view = harness.ActiveView();
                Assert.Equal(harness.SessionIds[0], view.SessionId);
                Assert.False(view.IsReadOnlyView);
            },
            dialogs);
    }

    [Fact]
    public async Task Continuing_a_session_in_a_free_workspace_makes_it_active_and_live()
    {
        await RunAsync(
            sessionCount: 2,
            async harness =>
            {
                var archived = harness.SessionIds[1];
                await Task.Run(() => harness.Composition.Catalog.SetStatusAsync(harness.SessionIds[0], SessionStatus.Archived, CancellationToken.None));
                await harness.ViewModel.OpenSessionByIdAsync(archived, CancellationToken.None);
                var readOnly = harness.ActiveView();
                Assert.True(readOnly.IsReadOnlyView);

                Assert.True(readOnly.ContinueCommand!.CanExecute(null));
                await harness.ViewModel.ContinueSessionAsync(archived, CancellationToken.None);

                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(archived)).Status);
                var live = harness.ActiveView();
                Assert.Equal(archived, live.SessionId);
                Assert.False(live.IsReadOnlyView);
                Assert.NotSame(readOnly, live);
            });
    }

    [Fact]
    public async Task Taking_over_from_continue_swaps_the_active_session_and_turns_the_old_tab_read_only()
    {
        var dialogs = new RecordingDialogs { ChooseResult = 0, ConfirmResult = true };
        await RunAsync(
            sessionCount: 2,
            async harness =>
            {
                var holder = harness.SessionIds[0];
                var archived = harness.SessionIds[1];
                var holderView = harness.ActiveView();

                await harness.ViewModel.ContinueSessionAsync(archived, CancellationToken.None);

                var handover = Assert.Single(dialogs.Handovers);
                Assert.False(handover.OffersContextMode);
                Assert.Null(handover.Error);
                Assert.Equal(SessionStatus.Archived, (await harness.InfoAsync(holder)).Status);
                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(archived)).Status);
                Assert.True(holderView.IsReadOnlyView);
                Assert.False(holderView.IsHandingOver);
                var live = harness.ActiveView();
                Assert.Equal(archived, live.SessionId);
                Assert.False(live.IsReadOnlyView);
            },
            dialogs);
    }

    [Fact]
    public async Task A_busy_holder_lists_the_reasons_and_is_not_taken_over_and_its_queue_is_kept()
    {
        var dialogs = new RecordingDialogs { ChooseResult = 0, ConfirmResult = true };
        await RunAsync(
            sessionCount: 2,
            async harness =>
            {
                var holder = harness.SessionIds[0];
                var archived = harness.SessionIds[1];
                var holderView = harness.ActiveView();
                holderView.RestoreComposerState((string.Empty, ["queued one"]));

                await harness.ViewModel.ContinueSessionAsync(archived, CancellationToken.None);

                var busy = Assert.Single(dialogs.Confirms);
                Assert.Equal(HandoverStrings.BusyMessage(Harness.TitleOf(0), [HandoverStrings.BusyQueued]), busy.Message);
                Assert.Equal(HandoverStrings.Wait, busy.ConfirmText);
                Assert.Empty(dialogs.Handovers);
                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(holder)).Status);
                Assert.Equal(SessionStatus.Archived, (await harness.InfoAsync(archived)).Status);
                Assert.True(holderView.HasQueued);
                Assert.False(holderView.IsReadOnlyView);
            },
            dialogs);
    }

    [Fact]
    public async Task A_workspace_operation_in_progress_blocks_the_takeover()
    {
        var dialogs = new RecordingDialogs { ChooseResult = 0, ConfirmResult = true };
        await RunAsync(
            sessionCount: 2,
            async harness =>
            {
                Assert.True(harness.Composition.WorkspaceStates.TryBeginOperation(harness.WorkDirectory));
                try
                {
                    await harness.ViewModel.ContinueSessionAsync(harness.SessionIds[1], CancellationToken.None);
                }
                finally
                {
                    harness.Composition.WorkspaceStates.EndOperation(harness.WorkDirectory);
                }

                Assert.Contains(HandoverStrings.BusyWorkspaceOperating, Assert.Single(dialogs.Confirms).Message, StringComparison.Ordinal);
                Assert.Empty(dialogs.Handovers);
                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(harness.SessionIds[0])).Status);
            },
            dialogs);
    }

    [Fact]
    public async Task A_new_session_taking_over_in_full_mode_inherits_the_holder()
    {
        var dialogs = new RecordingDialogs { ChooseResult = 0, ConfirmResult = true, HandoverMode = HandoverContextMode.Full };
        await RunAsync(
            sessionCount: 1,
            async harness =>
            {
                var holder = harness.SessionIds[0];
                var occupied = new WorktreeOccupiedException(holder);

                await harness.ViewModel.ShowOccupiedAsync(
                    occupied,
                    HandoverIntent.NewSession(new SessionCreate(harness.WorkDirectory, "taker")),
                    CancellationToken.None);

                var handover = Assert.Single(dialogs.Handovers);
                Assert.True(handover.OffersContextMode);
                Assert.Null(handover.Error);
                Assert.Equal(SessionStatus.Archived, (await harness.InfoAsync(holder)).Status);
                var live = harness.ActiveView();
                Assert.NotEqual(holder, live.SessionId);
                var taker = await harness.InfoAsync(live.SessionId);
                Assert.Equal(SessionStatus.Active, taker.Status);
                Assert.Equal(SessionOrigin.Full, taker.Origin);
                Assert.Equal(holder, taker.InheritsFromSessionId);
                Assert.Equal(1, await Task.Run(() => harness.Composition.Sessions.LastSeqAsync(taker.Id, CancellationToken.None)));
            },
            dialogs);
    }

    [Fact]
    public async Task A_turn_on_a_session_deleted_elsewhere_turns_the_tab_into_a_deleted_view()
    {
        await RunAsync(
            sessionCount: 1,
            async harness =>
            {
                var view = harness.ActiveView();
                await Task.Run(() => harness.Composition.Catalog.SetStatusAsync(view.SessionId, SessionStatus.Deleted, CancellationToken.None));

                view.Draft = Message;
                view.SendCommand.Execute(null);
                await PumpUntilAsync(() => view.IsReadOnlyView);

                Assert.Equal(SessionStatus.Deleted, view.Info.Status);
                Assert.False(view.HasContinue);
                Assert.True(view.HasPendingComposer);
                Assert.Equal(LifecycleStrings.PendingComposer(1), view.PendingComposerNote);
            });
    }

    [Fact]
    public async Task Continue_carries_the_draft_of_a_rejected_turn_into_the_live_view()
    {
        await RunAsync(
            sessionCount: 1,
            async harness =>
            {
                var view = harness.ActiveView();
                await Task.Run(() => harness.Composition.Catalog.SetStatusAsync(view.SessionId, SessionStatus.Archived, CancellationToken.None));
                view.Draft = Message;
                view.SendCommand.Execute(null);
                await PumpUntilAsync(() => view.IsReadOnlyView);
                Assert.True(view.HasPendingComposer);

                await harness.ViewModel.ContinueSessionAsync(view.SessionId, CancellationToken.None);

                var live = harness.ActiveView();
                Assert.NotSame(view, live);
                Assert.False(live.IsReadOnlyView);
                Assert.Equal(Message, live.Draft);
                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(live.SessionId)).Status);
            });
    }

    [Fact]
    public async Task Startup_heals_a_session_whose_folder_is_gone_and_shows_the_notice_once()
    {
        var dialogs = new RecordingDialogs();
        await RunAsync(
            sessionCount: 0,
            async harness =>
            {
                var projectRoot = Path.Combine(harness.Root, "repo");
                var worktree = Path.Combine(harness.Root, "repo-wt");
                Directory.CreateDirectory(projectRoot);
                Directory.CreateDirectory(worktree);
                var project = await Task.Run(() => harness.Composition.Projects.AddAsync(projectRoot, "repo", ProjectKind.Repository, CancellationToken.None));
                var session = await Task.Run(() => harness.Composition.Catalog.CreateAsync(
                    new SessionCreate(projectRoot, "gone") { ProjectId = project.Id, WorktreePath = worktree, Branch = "forge/gone" },
                    CancellationToken.None));
                Directory.Delete(worktree);

                using (var first = await harness.StartShellAsync(dialogs))
                {
                    var notice = Assert.Single(dialogs.Notices);
                    Assert.Equal(LifecycleStrings.NoticeTitle, notice.Title);
                    Assert.Contains(LifecycleStrings.HealDeleted(1), notice.Message, StringComparison.Ordinal);
                }

                Assert.Equal(SessionStatus.Deleted, (await harness.InfoAsync(session.Id)).Status);

                using (await harness.StartShellAsync(dialogs))
                {
                    Assert.Single(dialogs.Notices);
                }
            },
            dialogs);
    }

    [Fact]
    public async Task A_shell_without_dialogs_keeps_the_startup_notice_for_the_next_window()
    {
        var dialogs = new RecordingDialogs();
        await RunAsync(
            sessionCount: 0,
            async harness =>
            {
                var projectRoot = Path.Combine(harness.Root, "repo");
                var worktree = Path.Combine(harness.Root, "repo-wt");
                Directory.CreateDirectory(projectRoot);
                Directory.CreateDirectory(worktree);
                var project = await Task.Run(() => harness.Composition.Projects.AddAsync(projectRoot, "repo", ProjectKind.Repository, CancellationToken.None));
                await Task.Run(() => harness.Composition.Catalog.CreateAsync(
                    new SessionCreate(projectRoot, "gone") { ProjectId = project.Id, WorktreePath = worktree, Branch = "forge/gone" },
                    CancellationToken.None));
                Directory.Delete(worktree);

                using (await harness.StartShellAsync(dialogs: null))
                {
                }

                using (await harness.StartShellAsync(dialogs))
                {
                    Assert.Single(dialogs.Notices);
                }
            },
            dialogs: null);
    }

    private static async Task RunAsync(int sessionCount, Func<Harness, Task> body, RecordingDialogs? dialogs = null)
    {
        await SharedUi.Session.Dispatch(
            async () =>
            {
                var harness = await Harness.CreateAsync(sessionCount, dialogs);
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

    private sealed record ConfirmCall(string Title, string Message, string ConfirmText, string CancelText);

    private sealed record NotifyCall(string Title, string Message);

    private sealed record ChooseCall(string Title, string Message, IReadOnlyList<string> Options);

    private sealed record HandoverCall(bool OffersContextMode, string? Error);

    /// <summary>Records every dialog the shell asks for; confirms answer <see cref="ConfirmResult"/>.</summary>
    private sealed class RecordingDialogs : IDialogService
    {
        public bool ConfirmResult { get; init; }

        public List<ConfirmCall> Confirms { get; } = [];

        public List<NotifyCall> Notices { get; } = [];

        /// <summary>The option <see cref="ChooseAsync"/> answers (-1 = closed).</summary>
        public int ChooseResult { get; init; } = -1;

        public List<ChooseCall> Choices { get; } = [];

        public HandoverContextMode HandoverMode { get; init; } = HandoverContextMode.Blank;

        public List<HandoverCall> Handovers { get; } = [];

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options)
        {
            Choices.Add(new ChooseCall(title, message, options));
            return Task.FromResult(ChooseResult);
        }

        /// <summary>Runs the handover once with <see cref="HandoverMode"/>, as a user confirming the dialog would.</summary>
        public async Task<bool?> RunHandoverAsync(HandoverDialogModel model, CancellationToken ct)
        {
            model.Mode = HandoverMode;
            var succeeded = await model.ExecuteAsync();
            Handovers.Add(new HandoverCall(model.OffersContextMode, model.Error));
            return succeeded;
        }

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
        {
            Confirms.Add(new ConfirmCall(title, message, confirmText, cancelText));
            return Task.FromResult(ConfirmResult);
        }

        public Task<string?> PromptTextAsync(string title, string initial) => Task.FromResult<string?>(null);

        public Task<NewBranchChoice?> PickNewBranchAsync(NewBranchRequest request, CancellationToken ct) =>
            Task.FromResult<NewBranchChoice?>(null);

        public Task NotifyAsync(string title, string message)
        {
            Notices.Add(new NotifyCall(title, message));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// One throw-away data root and a real composition root. With sessions, the first is Active and the others Archived
    /// (one Active root per workspace), and the shell is started over the first; with none, the test starts shells itself.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private const string OpenTabsKey = "open-tabs";

        private readonly EnvironmentSnapshot previous;
        private MainWindowViewModel? viewModel;

        private Harness(string root, string workDirectory, EnvironmentSnapshot previous, AppComposition composition, IReadOnlyList<string> sessionIds)
        {
            Root = root;
            WorkDirectory = workDirectory;
            this.previous = previous;
            Composition = composition;
            SessionIds = sessionIds;
        }

        public string Root { get; }

        public string WorkDirectory { get; }

        public AppComposition Composition { get; }

        public IReadOnlyList<string> SessionIds { get; }

        public MainWindowViewModel ViewModel => viewModel ?? throw new InvalidOperationException("no shell was started");

        public static string TitleOf(int index) => "session " + index;

        public static async Task<Harness> CreateAsync(int sessionCount, RecordingDialogs? dialogs)
        {
            var (root, previous) = TempDataRoot.Redirect("forge-lifecycle-", "work");
            var work = Path.Combine(root, "work");

            var composition = AppComposition.Create();
            var catalog = composition.Catalog;
            var sessionIds = new List<string>(sessionCount);
            for (var i = 0; i < sessionCount; i++)
            {
                var info = await Task.Run(() => composition.Sessions.CreateSessionAsync(work, TitleOf(i), CancellationToken.None));
                await Task.Run(() => catalog.SetStatusAsync(info.Id, SessionStatus.Archived, CancellationToken.None));
                sessionIds.Add(info.Id);
            }

            var harness = new Harness(root, work, previous, composition, sessionIds);
            if (sessionCount > 0)
            {
                var firstId = sessionIds[0];
                Assert.True(await Task.Run(() => catalog.ActivateAsync(firstId, CancellationToken.None)));
                var tabs = new OpenTabsSnapshot { Sessions = [firstId], ActiveSession = firstId };
                var json = JsonSerializer.Serialize(tabs, WorkbenchJsonContext.Default.OpenTabsSnapshot);
                await Task.Run(() => composition.UiState.PutAsync(OpenTabsKey, OpenTabsSnapshot.CurrentVersion, json, CancellationToken.None));
                harness.viewModel = await harness.CreateShellAsync(dialogs);
            }

            return harness;
        }

        /// <summary>A shell the test owns (startup scenarios): disposing it leaves the composition running.</summary>
        public async Task<MainWindowViewModel> StartShellAsync(RecordingDialogs? dialogs) => await CreateShellAsync(dialogs);

        public SessionWorkspaceViewModel ActiveView() =>
            Assert.IsType<SessionWorkspaceViewModel>(ViewModel.ActiveSession);

        public async Task<SessionInfo> InfoAsync(string sessionId) =>
            Assert.IsType<SessionInfo>(await Task.Run(() => Composition.Sessions.GetSessionAsync(sessionId, CancellationToken.None)));

        public async ValueTask DisposeAsync()
        {
            if (viewModel is not null)
            {
                viewModel.Files.Attach(PanelContext.Empty);
                viewModel.Dispose();
            }

            await Composition.DisposeAsync();
            previous.Restore();

            TestTempFiles.DeleteTree(Root);
        }

        private async Task<MainWindowViewModel> CreateShellAsync(RecordingDialogs? dialogs)
        {
            var shell = new MainWindowViewModel(Composition);
            if (dialogs is not null)
            {
                shell.AttachDialogs(dialogs);
            }

            await shell.InitializeAsync();
            return shell;
        }
    }

}
