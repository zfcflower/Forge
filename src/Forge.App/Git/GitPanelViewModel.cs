using System.Collections.ObjectModel;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>Live git settings the panel and the agent tools read on every operation (backed by <c>AppPreferences</c>).</summary>
internal sealed record GitPanelSettings(Func<GitPullStrategy> PullStrategy, Func<TimeSpan> NetworkTimeout);

/// <summary>
/// The inspector's Git tab for the active session's worktree: branch, upstream, ahead/behind (as of the last fetch),
/// remotes, local branches, the in-progress operation with its conflicted files, and the result of the last operation
/// with a hint derived from its <see cref="GitRemoteFailureKind"/>. Follows the active session through
/// <see cref="Attach"/> like the other inspector panels.
/// <para>Threading: every git call runs on the thread pool; results are applied on the UI thread as one immutable
/// snapshot. All fields are touched on the UI thread only.</para>
/// <para>Per-worktree state: the panel shows one worktree at a time, but remembers a small, bounded table of
/// <see cref="WorktreeState"/> keyed by working directory (<see cref="MaxRememberedWorktrees"/> entries, least recently
/// attached idle one evicted first). Each entry carries whether a write is running for that worktree and the outcome of
/// its last operation, so an operation that finishes after a session switch is not lost: switching back shows it.</para>
/// <para>Reads (the state refresh) belong to the attached context: <see cref="Attach"/>, a clear and
/// <see cref="Dispose"/> cancel the read in flight, and a read that was cancelled never applies its result. Writes
/// (fetch, pull, push, branch operations) are never cancelled by <see cref="Attach"/> - killing a push half way is worse
/// than letting it finish - they run to the end under the panel's own lifetime token and record their outcome on their
/// worktree. <see cref="IsBusy"/> describes the attached worktree only (a write of it, or a read of it), so the session
/// that was switched to is not blocked by the one that was left; the left one's write also does not re-read the state
/// afterwards unless it is attached again.</para>
/// <para>Commands: they only start work and are disabled while <see cref="IsBusy"/>. A programmatic <c>Execute</c> while
/// busy is therefore a no-op (the command checks <c>CanExecute</c>), and the work bodies repeat that guard instead of
/// queueing: nothing a command asks for is deferred. The only thing that queues is the panel's own refresh request
/// (<see cref="Attach"/> with the same context, <see cref="NotifyTurnCompleted"/>), which runs once after the current work
/// item ends. There is no automatic fetch; refresh happens on attach, after every operation, after each main-session turn
/// and on demand.</para>
/// <para>The title bar shows <see cref="BranchText"/> next to the session title and the status bar shows
/// <see cref="AheadBehindText"/>; both are empty when the session is not in a repository.</para>
/// </summary>
internal sealed class GitPanelViewModel : ObservableObject, IDisposable
{
    /// <summary>How much of a commit hash the detached-head text keeps (git's own short form).</summary>
    private const int ShortCommitLength = 7;

    /// <summary>How many worktrees keep their busy flag and last result; idle, least recently attached ones go first.</summary>
    internal const int MaxRememberedWorktrees = 16;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly GitCli git;
    private readonly GitPanelSettings settings;
    private readonly Func<string, string, Task<bool>> confirm;
    private readonly RelayCommand[] commands;

    /// <summary>The thread the panel was built on (the UI thread); a language notification from another one is not ours.</summary>
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    /// <summary>Cancels whatever git call is in flight when the panel goes away; never replaced, only cancelled.</summary>
    private readonly CancellationTokenSource lifetime = new();

    private readonly Dictionary<string, WorktreeState> worktrees = new(PathComparer);

    /// <summary>The state of "no session": never stored in <see cref="worktrees"/>, never busy.</summary>
    private readonly WorktreeState emptyState = new(string.Empty);

    private PanelContext context = PanelContext.Empty;
    private WorktreeState current;
    private GitSnapshot snapshot = GitSnapshot.Empty;
    private string? refreshProblem;
    private string newBranchName = string.Empty;
    private string? selectedBranch;
    private bool busy;
    private bool refreshQueued;

    /// <summary>The read of the attached context; cancelled and cleared by every context change and by <see cref="Dispose"/>.</summary>
    private CancellationTokenSource? readSource;

    /// <summary>Bumped whenever the attached context changes; a confirmation dialog that outlives its context is dropped.</summary>
    private int contextEpoch;
    private long useClock;
    private bool disposed;

    /// <param name="confirm">Asks the user (title, message) before a destructive action such as deleting a branch.</param>
    public GitPanelViewModel(GitCli git, GitPanelSettings settings, Func<string, string, Task<bool>> confirm)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(confirm);

        this.git = git;
        this.settings = settings;
        this.confirm = confirm;
        current = emptyState;

        RefreshCommand = new RelayCommand(() => Launch(RefreshAsync), CanRead);
        FetchCommand = new RelayCommand(() => Launch(FetchAsync), CanOperate);
        PullCommand = new RelayCommand(() => Launch(PullAsync), CanOperate);
        PushCommand = new RelayCommand(() => Launch(PushAsync), CanOperate);
        CreateBranchCommand = new RelayCommand(() => Launch(CreateBranchAsync), CanOperate);
        SwitchBranchCommand = new RelayCommand(() => Launch(SwitchBranchAsync), CanOperate);
        SetUpstreamCommand = new RelayCommand(() => Launch(SetUpstreamAsync), CanOperate);
        DeleteBranchCommand = new RelayCommand(() => Launch(DeleteBranchAsync), CanOperate);
        AbortOperationCommand = new RelayCommand(() => Launch(AbortOperationAsync), CanOperate);
        ContinueOperationCommand = new RelayCommand(() => Launch(ContinueOperationAsync), CanOperate);
        commands =
        [
            RefreshCommand,
            FetchCommand,
            PullCommand,
            PushCommand,
            CreateBranchCommand,
            SwitchBranchCommand,
            SetUpstreamCommand,
            DeleteBranchCommand,
            AbortOperationCommand,
            ContinueOperationCommand,
        ];

        // The panel has to re-read its title and every formatted text on a language switch, the same way the settings
        // sections do. The subscription is dropped by Dispose, which the main window view model calls with itself.
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Tab title (localized; refreshed on <c>Loc.LanguageChanged</c>).</summary>
    public string Title => Loc.Instance[GitStrings.TabTitle];

    public bool HasRepository => snapshot.HasRepository;

    /// <summary>Current branch, or "(detached at abc1234)"; empty outside a repository.</summary>
    public string BranchText
    {
        get
        {
            if (snapshot.Status is not { } status)
            {
                return string.Empty;
            }

            if (status.Branch is { Length: > 0 } branch)
            {
                return branch;
            }

            return status is { IsDetached: true, HeadCommit: { Length: > 0 } head }
                ? Loc.Instance.Format(GitStrings.DetachedHead, Shorten(head))
                : string.Empty;
        }
    }

    /// <summary>Upstream name, "no upstream" or "upstream gone"; empty outside a repository.</summary>
    public string UpstreamText
    {
        get
        {
            if (snapshot.Status is not { } status)
            {
                return string.Empty;
            }

            return status.Upstream is { Length: > 0 } upstream
                ? status.UpstreamGone ? Loc.Instance.Format(GitStrings.UpstreamGone, upstream) : upstream
                : Loc.Instance[GitStrings.NoUpstream];
        }
    }

    /// <summary>"↑2 ↓0"; empty when there is no upstream or no repository.</summary>
    public string AheadBehindText
    {
        get
        {
            if (snapshot.Status is not { } status || status.UpstreamGone)
            {
                return string.Empty;
            }

            return status is { Upstream: not null, Ahead: { } ahead, Behind: { } behind }
                ? Loc.Instance.Format(GitStrings.AheadBehind, ahead, behind)
                : string.Empty;
        }
    }

    public ObservableCollection<string> Remotes { get; } = [];

    /// <summary>Local branch names for the switch/delete pickers, current branch first.</summary>
    public ObservableCollection<string> Branches { get; } = [];

    /// <summary>"Rebase in progress" etc.; empty when nothing is in progress.</summary>
    public string OperationText => snapshot.Conflicts.Operation switch
    {
        GitOperationKind.Rebase => Loc.Instance[GitStrings.OperationRebase],
        GitOperationKind.Merge => Loc.Instance[GitStrings.OperationMerge],
        GitOperationKind.CherryPick => Loc.Instance[GitStrings.OperationCherryPick],
        GitOperationKind.Revert => Loc.Instance[GitStrings.OperationRevert],
        _ => string.Empty,
    };

    public bool HasOperation => snapshot.Conflicts.InProgress;

    public ObservableCollection<string> ConflictedFiles { get; } = [];

    /// <summary>
    /// True while the <em>attached</em> worktree has a write running (or the confirmation that precedes one) or its state
    /// is being read. Work of a worktree that was switched away from does not count.
    /// </summary>
    public bool IsBusy => busy;

    /// <summary>Message of the last operation of the attached worktree; failures carry git's stderr in <see cref="LastResultDetail"/>.</summary>
    public string LastResultText
    {
        get
        {
            var result = current.Result;
            return result.LocalizedKey is { } key
                ? result.LocalizedArgument is { } argument ? Loc.Instance.Format(key, argument) : Loc.Instance[key]
                : result.Message;
        }
    }

    public string LastResultDetail => current.Result.Detail;

    public bool HasLastResultDetail => current.Result.Detail.Length > 0;

    public bool LastResultIsError => current.Result.Failure != GitRemoteFailureKind.None;

    public bool HasResult => LastResultText.Length > 0;

    /// <summary>Recovery hint for the last failure kind (sign in, pull first, resolve conflicts, add a remote…).</summary>
    public string HintText => HintFor(current.Result.Failure);

    public bool HasHint => HintText.Length > 0;

    /// <summary>
    /// "Git failed: …" when the last re-read of the state failed. Kept apart from <see cref="LastResultText"/> so a
    /// successful push followed by a failing re-read still shows the push; cleared by the next successful read and on a
    /// context change.
    /// </summary>
    public string RefreshProblemText =>
        refreshProblem is { } problem ? Loc.Instance.Format(GitStrings.ErrorGitFailed, problem) : string.Empty;

    public bool HasRefreshProblem => refreshProblem is not null;

    /// <summary>Text box for create-branch / set-upstream input.</summary>
    public string NewBranchName
    {
        get => newBranchName;
        set => Set(ref newBranchName, value ?? string.Empty);
    }

    public string? SelectedBranch
    {
        get => selectedBranch;
        set => Set(ref selectedBranch, value);
    }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand FetchCommand { get; }

    public RelayCommand PullCommand { get; }

    /// <summary>Push; sets the upstream on the first push. Never forces.</summary>
    public RelayCommand PushCommand { get; }

    public RelayCommand CreateBranchCommand { get; }

    public RelayCommand SwitchBranchCommand { get; }

    /// <summary>Sets the current branch's upstream to <c>origin/&lt;branch&gt;</c> (or the typed name).</summary>
    public RelayCommand SetUpstreamCommand { get; }

    /// <summary>Deletes <see cref="SelectedBranch"/> after confirmation; never the current branch.</summary>
    public RelayCommand DeleteBranchCommand { get; }

    public RelayCommand AbortOperationCommand { get; }

    public RelayCommand ContinueOperationCommand { get; }

    /// <summary>Follows the active session; an empty context clears the panel.</summary>
    public void Attach(PanelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (disposed)
        {
            return;
        }

        var previous = this.context;
        this.context = context;

        var active = HasContext;
        var changed = !string.Equals(previous.SessionId, context.SessionId, StringComparison.Ordinal)
            || !string.Equals(previous.WorkingDirectory, context.WorkingDirectory, StringComparison.Ordinal);
        if (changed)
        {
            // A read belongs to the context it was started for: it is cancelled and can no longer apply anything. A
            // write is left running; its outcome is recorded on its own worktree and shown when that one is attached.
            CancelRead();
            contextEpoch++;
            refreshQueued = false;
            current = active ? StateFor(context.WorkingDirectory) : emptyState;
            if (!active)
            {
                emptyState.Result = PanelResult.None;
            }
        }

        if (changed || !active)
        {
            // Nothing of the previous session may stay on screen while the new read is in flight.
            Clear();
        }

        if (active)
        {
            RequestRefresh();
        }
    }

    /// <summary>Called by the main window after a main-session turn finished, so the panel re-reads local state.</summary>
    public void NotifyTurnCompleted(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (disposed || !string.Equals(sessionId, context.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        RequestRefresh();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        refreshQueued = false;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;

        // Cancel only: the git call in flight may still be registering on a token, and it reports cancellation as an
        // OperationCanceledException that the work item already handles.
        CancelRead();
        lifetime.Cancel();
        RefreshBusy();
    }

    // ---------------------------------------------------------------- work items

    /// <summary>
    /// Starts a work body without tying a command to its duration: busy-ness is the panel's own (per worktree), a command
    /// that stayed "running" until a push finished would keep the same button disabled for the session switched to.
    /// An exception the body did not handle is shown like any other failure instead of faulting an unobserved task.
    /// </summary>
    private void Launch(Func<Task> body) => _ = LaunchAsync(body);

    private async Task LaunchAsync(Func<Task> body)
    {
        try
        {
            await body().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportCommandFailure(exception);
        }
    }

    /// <summary>Starts a read of the current context, or remembers that one is wanted once the work item that is running ends.</summary>
    private void RequestRefresh()
    {
        if (disposed || !HasContext)
        {
            return;
        }

        if (busy)
        {
            refreshQueued = true;
            return;
        }

        Launch(RefreshAsync);
    }

    /// <summary>A read outside any write. A no-op while busy (see the class remarks), so it never queues a second one.</summary>
    private async Task RefreshAsync()
    {
        if (disposed || busy || !HasContext)
        {
            return;
        }

        try
        {
            await ReadCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            EndWork();
        }
    }

    /// <summary>
    /// One read of the whole local state for the attached context, applied on the UI thread as one snapshot. The caller
    /// has checked the gate. The read runs under its own source, which <see cref="CancelRead"/> cancels when the context
    /// changes; a cancelled or superseded read applies nothing. A failing read is reported as the refresh problem and
    /// leaves the last operation result alone.
    /// </summary>
    private async Task ReadCoreAsync()
    {
        var workingDirectory = context.WorkingDirectory;
        var source = new CancellationTokenSource();
        readSource = source;
        refreshQueued = false;
        RefreshBusy();
        try
        {
            var read = await Task.Run(() => ReadSnapshotAsync(workingDirectory, source.Token), source.Token)
                .ConfigureAwait(true);
            if (IsCurrentRead(source))
            {
                Apply(read);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsGitFailure(exception))
        {
            if (IsCurrentRead(source))
            {
                ApplyRefreshProblem(exception.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(readSource, source))
            {
                readSource = null;
            }

            source.Dispose();
        }
    }

    /// <summary>Reads branch, remotes, local branches and the in-progress operation; runs on a pool thread.</summary>
    private async Task<GitSnapshot> ReadSnapshotAsync(string workingDirectory, CancellationToken ct)
    {
        if (!await git.IsRepositoryAsync(workingDirectory, ct).ConfigureAwait(false))
        {
            return GitSnapshot.Empty;
        }

        var status = await git.GetBranchStatusAsync(workingDirectory, ct).ConfigureAwait(false);
        var remotes = await git.GetRemotesAsync(workingDirectory, ct).ConfigureAwait(false);
        var branches = await git.GetBranchesAsync(workingDirectory, ct: ct).ConfigureAwait(false);
        var conflicts = await git.GetConflictStateAsync(workingDirectory, ct).ConfigureAwait(false);
        return new GitSnapshot(status, remotes, branches, conflicts);
    }

    /// <summary>
    /// One write on the attached worktree. The write runs to the end even if another session is attached meanwhile; its
    /// outcome is recorded on its own <see cref="WorktreeState"/>, and only an outcome of the worktree that is still
    /// attached is shown and followed by a re-read.
    /// </summary>
    private async Task RunOperationAsync(
        Func<string, CancellationToken, Task<GitRemoteResult>> operation,
        Action? onSuccess = null)
    {
        if (disposed || busy)
        {
            return;
        }

        if (!HasContext)
        {
            ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorNoRepository, null);
            return;
        }

        var state = current;
        var workingDirectory = context.WorkingDirectory;
        state.Writing = true;
        RefreshBusy();
        try
        {
            GitRemoteResult? outcome = null;
            try
            {
                outcome = await Task.Run(() => operation(workingDirectory, lifetime.Token), lifetime.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception) when (IsGitFailure(exception))
            {
                Report(state, LocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorGitFailed, exception.Message));
            }

            if (disposed)
            {
                return;
            }

            if (outcome is not null)
            {
                Report(state, ResultOf(outcome));
                if (outcome.Success && IsCurrent(state))
                {
                    onSuccess?.Invoke();
                }
            }

            if (IsCurrent(state))
            {
                await ReadCoreAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            state.Writing = false;
            EndWork();
        }
    }

    /// <summary>Releases the busy flag and runs the refresh that a context change or a turn completion queued behind the work item.</summary>
    private void EndWork()
    {
        RefreshBusy();
        if (refreshQueued && !busy && !disposed && HasContext)
        {
            Launch(RefreshAsync);
        }
    }

    /// <summary>Recomputes <see cref="IsBusy"/> for the attached worktree; the one place that writes the flag.</summary>
    private void RefreshBusy()
    {
        var now = !disposed && (current.Writing || readSource is not null);
        if (busy == now)
        {
            return;
        }

        busy = now;
        OnPropertyChanged(nameof(IsBusy));
        NotifyCommands();
    }

    private void CancelRead()
    {
        var running = readSource;
        if (running is null)
        {
            return;
        }

        // The read's owner disposes the source once its task has unwound; here it only stops counting as the current one.
        readSource = null;
        running.Cancel();
    }

    private bool IsCurrentRead(CancellationTokenSource source) => !disposed && ReferenceEquals(readSource, source);

    private bool IsCurrent(WorktreeState state) => !disposed && ReferenceEquals(current, state);

    private bool HasContext => context.HasSession && context.WorkingDirectory.Length > 0;

    /// <summary>The remembered state of a worktree, created on first use; a full table drops its least recently attached idle entry.</summary>
    private WorktreeState StateFor(string workingDirectory)
    {
        if (!worktrees.TryGetValue(workingDirectory, out var state))
        {
            EvictIfFull();
            state = new WorktreeState(workingDirectory);
            worktrees.Add(workingDirectory, state);
        }

        state.LastUsed = ++useClock;
        return state;
    }

    private void EvictIfFull()
    {
        if (worktrees.Count < MaxRememberedWorktrees)
        {
            return;
        }

        WorktreeState? oldest = null;
        foreach (var candidate in worktrees.Values)
        {
            if (candidate.Writing || ReferenceEquals(candidate, current))
            {
                continue;
            }

            if (oldest is null || candidate.LastUsed < oldest.LastUsed)
            {
                oldest = candidate;
            }
        }

        if (oldest is not null)
        {
            worktrees.Remove(oldest.Key);
        }
    }

    // ---------------------------------------------------------------- commands

    private bool CanRead() => !disposed && !busy && HasContext;

    private bool CanOperate() => CanRead() && snapshot.HasRepository;

    private void NotifyCommands()
    {
        foreach (var command in commands)
        {
            command.NotifyCanExecuteChanged();
        }
    }

    private async Task FetchAsync()
    {
        var network = NetworkOptions();
        await RunOperationAsync((directory, token) => git.FetchAsync(directory, remote: null, network, token))
            .ConfigureAwait(true);
    }

    private async Task PullAsync()
    {
        var strategy = settings.PullStrategy();
        var network = NetworkOptions();
        await RunOperationAsync((directory, token) => git.PullAsync(directory, strategy, network, token))
            .ConfigureAwait(true);
    }

    private async Task PushAsync()
    {
        var push = new GitPushOptions { SetUpstream = !HasUpstream };
        var network = NetworkOptions();
        await RunOperationAsync((directory, token) => git.PushAsync(directory, push, network, token))
            .ConfigureAwait(true);
    }

    private async Task CreateBranchAsync()
    {
        var name = newBranchName.Trim();
        if (name.Length == 0)
        {
            ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorBranchNameRequired, null);
            return;
        }

        await RunOperationAsync(
                (directory, token) => git.CreateBranchAsync(directory, name, ct: token),
                () => NewBranchName = string.Empty)
            .ConfigureAwait(true);
    }

    private async Task SwitchBranchAsync()
    {
        if (selectedBranch is not { Length: > 0 } branch)
        {
            ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorSelectBranch, null);
            return;
        }

        await RunOperationAsync((directory, token) => git.SwitchBranchAsync(directory, branch, token))
            .ConfigureAwait(true);
    }

    private async Task SetUpstreamAsync()
    {
        var typed = newBranchName.Trim();
        var upstream = typed.Length > 0 ? typed : DefaultUpstream();
        if (upstream is null)
        {
            ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorUpstreamRequired, null);
            return;
        }

        await RunOperationAsync(
                (directory, token) => git.SetUpstreamAsync(directory, branch: null, upstream, token),
                () => NewBranchName = string.Empty)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Deletes the selected branch after the user confirmed. The worktree and the branch are captured before the dialog:
    /// the dialog can stay open while the active session changes, and the delete must then not reach into the session that
    /// was switched to. A context that changed while the dialog was open drops the request without a trace - the user
    /// confirmed a deletion in a worktree that is no longer on screen.
    /// </summary>
    private async Task DeleteBranchAsync()
    {
        if (disposed || busy)
        {
            return;
        }

        var state = current;
        state.Writing = true;
        RefreshBusy();
        try
        {
            if (!HasContext)
            {
                ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorNoRepository, null);
                return;
            }

            if (selectedBranch is not { Length: > 0 } branch)
            {
                ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorSelectBranch, null);
                return;
            }

            if (string.Equals(branch, snapshot.Status?.Branch, StringComparison.Ordinal))
            {
                ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorCannotDeleteCurrent, branch);
                return;
            }

            var epoch = contextEpoch;
            var workingDirectory = context.WorkingDirectory;
            var confirmed = await confirm(
                    Loc.Instance[GitStrings.ConfirmDeleteTitle],
                    Loc.Instance.Format(GitStrings.ConfirmDeleteMessage, branch))
                .ConfigureAwait(true);
            if (!confirmed || disposed || epoch != contextEpoch)
            {
                return;
            }

            await DeleteBranchCoreAsync(state, workingDirectory, branch).ConfigureAwait(true);
            if (IsCurrent(state))
            {
                await ReadCoreAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            state.Writing = false;
            EndWork();
        }
    }

    /// <summary>
    /// <see cref="GitCli.DeleteBranchAsync"/> reports a refusal (checked out in a worktree, not merged) as a
    /// <see cref="GitException"/>; the panel shows it like any other failure instead of letting it escape.
    /// </summary>
    private async Task DeleteBranchCoreAsync(WorktreeState state, string workingDirectory, string branch)
    {
        try
        {
            await Task.Run(
                    () => git.DeleteBranchAsync(workingDirectory, branch, force: false, lifetime.Token),
                    lifetime.Token)
                .ConfigureAwait(true);
            Report(state, new PanelResult(GitRemoteFailureKind.None, string.Empty, string.Empty, GitStrings.ResultBranchDeleted, branch));
        }
        catch (GitException exception)
        {
            Report(state, ResultOf(new GitRemoteResult(
                GitFailureClassifier.Classify(new GitCommandResult(exception.ExitCode, string.Empty, exception.StandardError)),
                exception.Message,
                exception.StandardError)));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsGitFailure(exception))
        {
            Report(state, LocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorGitFailed, exception.Message));
        }
    }

    private async Task AbortOperationAsync() =>
        await RunOperationAsync((directory, token) => git.AbortOperationAsync(directory, token)).ConfigureAwait(true);

    private async Task ContinueOperationAsync() =>
        await RunOperationAsync((directory, token) => git.ContinueOperationAsync(directory, token)).ConfigureAwait(true);

    /// <summary>The network calls read the timeout per operation, so a settings change needs no restart.</summary>
    private GitNetworkOptions NetworkOptions() => new() { Timeout = settings.NetworkTimeout() };

    /// <summary>True when the current branch has an upstream that still exists on the remote.</summary>
    private bool HasUpstream => snapshot.Status is { Upstream: not null, UpstreamGone: false };

    /// <summary>The upstream a first push would create: <c>origin/&lt;branch&gt;</c> when there is an origin.</summary>
    private string? DefaultUpstream()
    {
        if (snapshot.Status is not { Branch: { Length: > 0 } branch } || !Remotes.Contains("origin"))
        {
            return null;
        }

        return "origin/" + branch;
    }

    private void ReportCommandFailure(Exception exception) =>
        ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorGitFailed, exception.Message);

    // ---------------------------------------------------------------- state application

    /// <summary>Applies one immutable read as a whole, on the UI thread.</summary>
    private void Apply(GitSnapshot read)
    {
        snapshot = read;
        refreshProblem = null;

        Remotes.Clear();
        foreach (var remote in read.Remotes)
        {
            Remotes.Add(remote.Name);
        }

        Branches.Clear();
        var currentBranch = read.Status?.Branch;
        if (currentBranch is { Length: > 0 } && Contains(read.Branches, currentBranch))
        {
            Branches.Add(currentBranch);
        }

        foreach (var branch in read.Branches)
        {
            if (!string.Equals(branch.Name, currentBranch, StringComparison.Ordinal))
            {
                Branches.Add(branch.Name);
            }
        }

        ConflictedFiles.Clear();
        foreach (var path in read.Conflicts.ConflictedPaths)
        {
            ConflictedFiles.Add(path);
        }

        SyncSelection();
        RaiseTexts();
        NotifyCommands();
    }

    /// <summary>Records the outcome on the worktree that ran the operation; it is shown only if that worktree is still attached.</summary>
    private void Report(WorktreeState state, PanelResult result)
    {
        state.Result = result;
        if (IsCurrent(state))
        {
            RaiseTexts();
        }
    }

    /// <summary>A failure the panel decided on itself (no branch selected, nothing typed); the text is a <see cref="Loc"/> key.</summary>
    private void ApplyLocalFailure(GitRemoteFailureKind kind, string key, string? argument) =>
        Report(current, LocalFailure(kind, key, argument));

    /// <summary>The last read failed: shown next to, not instead of, the last operation's outcome.</summary>
    private void ApplyRefreshProblem(string message)
    {
        refreshProblem = message;
        RaiseTexts();
    }

    private static PanelResult LocalFailure(GitRemoteFailureKind kind, string key, string? argument) =>
        new(kind, string.Empty, string.Empty, key, argument);

    private static PanelResult ResultOf(GitRemoteResult outcome) =>
        new(
            outcome.Failure,
            outcome.Message,
            outcome.Success ? string.Empty : outcome.StandardError.Trim(),
            null,
            null);

    /// <summary>Keeps a picker selection the user already made; otherwise selects the current branch, else the first one.</summary>
    private void SyncSelection()
    {
        if (selectedBranch is { Length: > 0 } existing && Branches.Contains(existing))
        {
            return;
        }

        var currentBranch = snapshot.Status?.Branch;
        SelectedBranch = currentBranch is { Length: > 0 } && Branches.Contains(currentBranch)
            ? currentBranch
            : Branches.Count > 0 ? Branches[0] : null;
    }

    /// <summary>
    /// Empties everything the panel shows about a repository. The last operation result is not part of it: it belongs to
    /// the attached worktree's <see cref="WorktreeState"/> and is shown again as soon as the worktree is attached.
    /// </summary>
    private void Clear()
    {
        snapshot = GitSnapshot.Empty;
        refreshProblem = null;
        Remotes.Clear();
        Branches.Clear();
        ConflictedFiles.Clear();
        NewBranchName = string.Empty;
        SelectedBranch = null;
        RaiseTexts();
        RefreshBusy();
        NotifyCommands();
    }

    /// <summary>Re-reads every language-dependent text; called after a state change and on <c>Loc.LanguageChanged</c>.</summary>
    private void RaiseTexts()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(BranchText));
        OnPropertyChanged(nameof(UpstreamText));
        OnPropertyChanged(nameof(AheadBehindText));
        OnPropertyChanged(nameof(OperationText));
        OnPropertyChanged(nameof(HasOperation));
        OnPropertyChanged(nameof(LastResultText));
        OnPropertyChanged(nameof(LastResultDetail));
        OnPropertyChanged(nameof(HasLastResultDetail));
        OnPropertyChanged(nameof(LastResultIsError));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HintText));
        OnPropertyChanged(nameof(HasHint));
        OnPropertyChanged(nameof(RefreshProblemText));
        OnPropertyChanged(nameof(HasRefreshProblem));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A notification from another thread belongs to a panel whose window is gone (a test host): the properties
        // raised here are bound to controls that live on their own thread.
        if (!disposed && Environment.CurrentManagedThreadId == uiThread)
        {
            RaiseTexts();
        }
    }

    private static bool Contains(IReadOnlyList<GitBranch> branches, string name)
    {
        for (var i = 0; i < branches.Count; i++)
        {
            if (string.Equals(branches[i].Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string HintFor(GitRemoteFailureKind failure) => failure switch
    {
        GitRemoteFailureKind.AuthenticationFailed => Loc.Instance[GitStrings.HintAuthentication],
        GitRemoteFailureKind.NonFastForward => Loc.Instance[GitStrings.HintNonFastForward],
        GitRemoteFailureKind.Conflict => Loc.Instance[GitStrings.HintConflict],
        GitRemoteFailureKind.RemoteNotFound => Loc.Instance[GitStrings.HintRemoteNotFound],
        GitRemoteFailureKind.NoUpstream => Loc.Instance[GitStrings.HintNoUpstream],
        GitRemoteFailureKind.DirtyWorkTree => Loc.Instance[GitStrings.HintDirtyWorkTree],
        GitRemoteFailureKind.Network => Loc.Instance[GitStrings.HintNetwork],
        GitRemoteFailureKind.TimedOut => Loc.Instance[GitStrings.HintTimedOut],
        GitRemoteFailureKind.Other => Loc.Instance[GitStrings.HintOther],
        _ => string.Empty,
    };

    private static string Shorten(string commit) =>
        commit.Length <= ShortCommitLength ? commit : commit[..ShortCommitLength];

    /// <summary>The failures a git call can report besides its own <see cref="GitRemoteResult"/> (missing executable, I/O).</summary>
    private static bool IsGitFailure(Exception exception) =>
        exception is GitException or IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or ObjectDisposedException;

    /// <summary>
    /// What the panel remembers about one worktree: whether a write (or the confirmation before one) is running for it, and
    /// the outcome of its last operation. Lives in the bounded table of the panel; read and written on the UI thread only.
    /// </summary>
    private sealed class WorktreeState(string key)
    {
        public string Key { get; } = key;

        public bool Writing { get; set; }

        public PanelResult Result { get; set; } = PanelResult.None;

        /// <summary>Order of attachment, for eviction.</summary>
        public long LastUsed { get; set; }
    }

    /// <summary>One immutable read of the session's repository, built on a pool thread and applied in one go.</summary>
    private readonly record struct GitSnapshot(
        GitBranchStatus? Status,
        IReadOnlyList<GitRemote> Remotes,
        IReadOnlyList<GitBranch> Branches,
        GitConflictState Conflicts)
    {
        /// <summary>No repository, no branch: everything the panel shows is empty.</summary>
        public static GitSnapshot Empty { get; } = new(
            null,
            Array.Empty<GitRemote>(),
            Array.Empty<GitBranch>(),
            new GitConflictState(GitOperationKind.None, Array.Empty<string>()));

        public bool HasRepository => Status is not null;
    }

    /// <summary>The last operation's outcome: either git's own message or a text the panel picked from the string table.</summary>
    private readonly record struct PanelResult(
        GitRemoteFailureKind Failure,
        string Message,
        string Detail,
        string? LocalizedKey,
        string? LocalizedArgument)
    {
        public static PanelResult None { get; } = new(GitRemoteFailureKind.None, string.Empty, string.Empty, null, null);
    }
}
