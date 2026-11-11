using System.Collections.ObjectModel;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.ViewModels;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>Live git settings the row and the agent tools read on every operation (backed by <c>AppPreferences</c>).</summary>
internal sealed record GitPanelSettings(Func<GitPullStrategy> PullStrategy, Func<TimeSpan> NetworkTimeout);

/// <summary>
/// The files panel's git row: the active session's worktree, reduced to what a 24 px status line can carry — branch,
/// upstream, ahead/behind as of the last fetch, the three sync verbs, the in-progress operation with its conflicted files,
/// and the outcome of the last operation with a recovery hint.
/// <para>Why it lives on the files panel: the 已改动 tree compares against a local ref, and nothing fetches on its own, so
/// "look at the changes" and "make the baseline current" belong on one screen. It is not a panel of its own any more — the
/// old Git panel carried branch CRUD, remotes and the pull-request surface, all of which moved out (the session/project tree
/// and the agent's tools), leaving less than one row of content.</para>
/// <para>Threading: every git call runs on the thread pool; results are applied on the UI thread as one immutable
/// snapshot. All fields are touched on the UI thread only.</para>
/// <para>Per-worktree state: the row shows one worktree at a time but remembers a small, bounded table of
/// <see cref="WorktreeState"/> keyed by working directory (<see cref="MaxRememberedWorktrees"/> entries, least recently
/// attached idle one evicted first), so an operation that finishes after a session switch is not lost.</para>
/// <para>Reads belong to the attached context: <see cref="Attach"/>, a clear and <see cref="Dispose"/> cancel the read in
/// flight, and a cancelled read never applies its result. Writes are never cancelled by <see cref="Attach"/> — killing a
/// push half way is worse than letting it finish. <see cref="IsBusy"/> describes the attached worktree only.</para>
/// <para>There is no automatic fetch: refresh happens on attach, after every operation, after each main-session turn and on
/// demand. A write never forces and never leases; a rejected non-fast-forward is reported, not worked around.</para>
/// </summary>
internal sealed class GitSyncViewModel : ObservableObject, IDisposable
{
    /// <summary>How much of a commit hash the detached-head label keeps (git's own short form).</summary>
    private const int ShortCommitLength = 7;

    /// <summary>How many worktrees keep their busy flag and last result; idle, least recently attached ones go first.</summary>
    internal const int MaxRememberedWorktrees = 16;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly GitCli git;
    private readonly GitPanelSettings settings;
    private readonly Action<string>? openFile;
    private readonly RelayCommand[] commands;

    /// <summary>The thread the row was built on (the UI thread); a language notification from another one is not ours.</summary>
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    /// <summary>Cancels whatever git call is in flight when the row goes away; never replaced, only cancelled.</summary>
    private readonly CancellationTokenSource lifetime = new();

    private readonly Dictionary<string, WorktreeState> worktrees = new(PathComparer);

    /// <summary>The state of "no session": never stored in <see cref="worktrees"/>, never busy.</summary>
    private readonly WorktreeState emptyState = new(string.Empty);

    private PanelContext context = PanelContext.Empty;
    private WorktreeState current;
    private GitSnapshot snapshot = GitSnapshot.Empty;
    /// <summary>The shell's notification entry; null (a row built on its own) means nothing is announced.</summary>
    private readonly INotifications? notifications;

    private string? refreshProblem;
    private bool busy;
    private bool refreshQueued;

    /// <summary>The read of the attached context; cancelled and cleared by every context change and by <see cref="Dispose"/>.</summary>
    private CancellationTokenSource? readSource;

    private bool disposed;

    /// <param name="openFile">Opens a conflicted file in the editor; null leaves the banner's rows inert (tests).</param>
    public GitSyncViewModel(
        GitCli git,
        GitPanelSettings settings,
        Action<string>? openFile = null,
        INotifications? notifications = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(settings);

        this.git = git;
        this.settings = settings;
        this.openFile = openFile;
        this.notifications = notifications;
        current = emptyState;

        RefreshCommand = new RelayCommand(() => Launch(ReadOnlyAsync), CanRead);
        FetchCommand = new RelayCommand(() => Launch(FetchAsync, GitAction.Fetch), CanFetchRow);
        PullCommand = new RelayCommand(() => Launch(PullAsync, GitAction.Pull), CanPullRow);
        PushCommand = new RelayCommand(() => Launch(PushAsync, GitAction.Push), CanPushRow);
        AbortOperationCommand = new RelayCommand(() => Launch(AbortOperationAsync, GitAction.Abort), CanOperate);
        ContinueOperationCommand = new RelayCommand(() => Launch(ContinueOperationAsync, GitAction.Continue), CanOperate);
        commands =
        [
            RefreshCommand,
            FetchCommand,
            PullCommand,
            PushCommand,
            AbortOperationCommand,
            ContinueOperationCommand,
        ];

        // Every formatted text follows a language switch, the same way the settings sections do. The subscription is
        // dropped by Dispose, which the main window view model calls with itself.
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>True when the attached session is in a repository; the row is hidden otherwise.</summary>
    public bool HasRepository => snapshot.Status is not null;

    /// <summary>The branch name, or <c>HEAD@3f2a1c9</c> for a detached head; empty outside a repository.</summary>
    public string BranchLabel
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
                ? "HEAD@" + Shorten(head)
                : string.Empty;
        }
    }

    public bool HasBranch => BranchLabel.Length > 0;

    /// <summary>How many commits the branch is ahead of its upstream; 0 when unknown (no upstream, gone, never fetched).</summary>
    public int Ahead => Counts is { } counts ? counts.Ahead : 0;

    /// <summary>How many commits the upstream is ahead of the branch; 0 when unknown.</summary>
    public int Behind => Counts is { } counts ? counts.Behind : 0;

    /// <summary>Non-zero for the legend text: "↑2" / "↓2" are drawn by the view from the numbers, not from a formatted string.</summary>
    public string AheadText => Ahead > 99 ? "99+" : Ahead.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string BehindText => Behind > 99 ? "99+" : Behind.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool HasAhead => Ahead > 0;

    public bool HasBehind => Behind > 0;

    /// <summary>
    /// "↑2 ↓0" for the session composer's strip, which is a readout rather than a control. Empty when there is nothing to
    /// compare against, so the strip does not grow a pair of zeroes.
    /// </summary>
    public string AheadBehindText => Counts is { } counts
        ? Loc.Instance.Format(GitStrings.AheadBehind, counts.Ahead, counts.Behind)
        : string.Empty;

    /// <summary>The upstream as configured ("origin/main"), "no upstream", or the gone marker; empty outside a repository.</summary>
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

    /// <summary>True when the branch tracks an upstream that still exists — the only case where "ahead/behind" means anything.</summary>
    public bool HasUpstream => snapshot.Status is { Upstream: not null, UpstreamGone: false };

    /// <summary>True when the branch has a remote to push to (any remote at all; the push lands on the name-matched branch).</summary>
    public bool HasRemote => snapshot.Remotes.Count > 0;

    public bool IsDetached => snapshot.Status is { IsDetached: true };

    /// <summary>
    /// The <em>branch name</em> a push lands on: the CLI's refspec is always
    /// <c>refs/heads/&lt;branch&gt;:refs/heads/&lt;branch&gt;</c>, so the name is knowable without asking git. Which remote
    /// that goes to is not: <c>GitCli.ResolvePushRemoteAsync</c> walks <c>branch.&lt;b&gt;.pushRemote</c> →
    /// <c>branch.&lt;b&gt;.remote</c> → <c>origin</c>, so naming a remote here would be a guess presented as a fact.
    /// </summary>
    public string PushTarget => snapshot.Status is { Branch: { Length: > 0 } branch } ? branch : string.Empty;

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

    /// <summary>The files a stopped rebase/merge left unmerged; clicking one opens it in the editor.</summary>
    public ObservableCollection<ConflictedFileItem> ConflictedFiles { get; } = [];

    /// <summary>
    /// True while the <em>attached</em> worktree has a write running or its state is being read. Work of a worktree that
    /// was switched away from does not count.
    /// </summary>
    public bool IsBusy => busy;

    /// <summary>
    /// Which verb is running <em>for the attached worktree</em>, so the view puts the pulse on that button. Like the busy
    /// flag it belongs to a worktree: a write of a session that was switched away from must not light up this row.
    /// </summary>
    public GitAction Running => current.Running;

    /// <summary>True while the fetch that this row started is in flight (the view shows a pulse on that button).</summary>
    public bool IsFetchRunning => Running == GitAction.Fetch;

    public bool IsPullRunning => Running == GitAction.Pull;

    public bool IsPushRunning => Running == GitAction.Push;

    /// <summary>
    /// Message of the last operation of the attached worktree, or the last re-read's failure; one line only. A failed
    /// re-read wins, because it means the state behind the row may be stale: showing the earlier success over it would
    /// report a state nobody has verified.
    /// </summary>
    public string MessageText =>
        refreshProblem is { } problem
            ? Loc.Instance.Format(GitStrings.ErrorGitFailed, problem)
            : LastResultText;

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

    public string MessageDetail => current.Result.Detail;

    public bool HasMessageDetail => current.Result.Detail.Length > 0;

    public bool HasResult => LastResultText.Length > 0;

    public bool MessageIsError => current.Result.Failure != GitRemoteFailureKind.None || HasRefreshProblem;

    public bool HasMessage => MessageText.Length > 0;

    /// <summary>Recovery hint for the last failure kind (sign in, pull first, resolve conflicts, add a remote…).</summary>
    public string HintText => HintFor(current.Result.Failure);

    public bool HasHint => HintText.Length > 0;

    /// <summary>"Git failed: …" when the last re-read of the state failed, kept apart from the operation's own result.</summary>
    public string RefreshProblemText =>
        refreshProblem is { } problem ? Loc.Instance.Format(GitStrings.ErrorGitFailed, problem) : string.Empty;

    public bool HasRefreshProblem => refreshProblem is not null;

    // ---------------------------------------------------------------- the row's tooltips and names

    /// <summary>"抓取：只看远端，不动本地分支" — the read-only verb, never tinted.</summary>
    public string FetchTooltip => Loc.Instance[GitStrings.RowFetchHint];

    /// <summary>"拉取：按 ff-only 并入 origin/main（落后 2 个提交）"; the strategy comes from the settings page.</summary>
    public string PullTooltip => Loc.Instance.Format(
        HasBehind ? GitStrings.RowPullHintBehind : GitStrings.RowPullHint,
        StrategyText,
        HasUpstream ? UpstreamText : Loc.Instance[GitStrings.NoUpstream],
        Behind);

    /// <summary>"推送：把 2 个提交推到 origin/x", or the first-push variant when there is no upstream yet.</summary>
    public string PushTooltip
    {
        get
        {
            if (IsDetached)
            {
                return Loc.Instance[GitStrings.PushDetached];
            }

            if (!HasRemote)
            {
                return Loc.Instance[GitStrings.RowNoRemote];
            }

            return HasUpstream
                ? Loc.Instance.Format(GitStrings.RowPushHint, Ahead, PushTarget)
                : Loc.Instance.Format(GitStrings.RowPushHintFirst, PushTarget);
        }
    }

    /// <summary>
    /// The branch label's tooltip: what the branch tracks, where a push lands (the refspec is name-matched, which the
    /// upstream need not be) and the head commit. The single place both facts are visible at once.
    /// </summary>
    public string BranchTooltip => Loc.Instance.Format(
        GitStrings.RowBranchHint,
        BranchLabel,
        PushTarget.Length > 0 ? PushTarget : "-",
        HasUpstream ? UpstreamText : Loc.Instance[GitStrings.NoUpstream],
        HeadShort is { Length: > 0 } head ? head : "-");

    /// <summary>The row's accessible name; the numbers are in it because a chip's colour and arrow are not read out.</summary>
    public string RowAutomationName => Loc.Instance.Format(
        GitStrings.RowAutomationName,
        BranchLabel,
        UpstreamText,
        Ahead,
        Behind);

    /// <summary>"ff-only" / "rebase" / "merge" — the strategy a pull would use right now.</summary>
    public string StrategyText => settings.PullStrategy() switch
    {
        GitPullStrategy.Rebase => Loc.Instance[GitStrings.SettingsPullStrategyRebase],
        GitPullStrategy.Merge => Loc.Instance[GitStrings.SettingsPullStrategyMerge],
        _ => Loc.Instance[GitStrings.SettingsPullStrategyFastForward],
    };

    public RelayCommand RefreshCommand { get; }

    public RelayCommand FetchCommand { get; }

    public RelayCommand PullCommand { get; }

    /// <summary>Push; sets the upstream on the first push. Never forces.</summary>
    public RelayCommand PushCommand { get; }

    public RelayCommand AbortOperationCommand { get; }

    public RelayCommand ContinueOperationCommand { get; }

    /// <summary>Follows the active session; an empty context clears the row.</summary>
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

    /// <summary>Called by the main window after a main-session turn finished, so the row re-reads local state.</summary>
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
    /// Starts a work body without tying a command to its duration: busy-ness is the row's own (per worktree), a command
    /// that stayed "running" until a push finished would keep the same button disabled for the session switched to.
    /// </summary>
    private void Launch(Func<Task> body, GitAction action = GitAction.None) => _ = LaunchAsync(body, action);

    private async Task LaunchAsync(Func<Task> body, GitAction action)
    {
        var state = current;
        if (action != GitAction.None)
        {
            state.Running = action;
            if (IsCurrent(state))
            {
                RaiseRunning();
            }
        }

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
        finally
        {
            if (action != GitAction.None)
            {
                state.Running = GitAction.None;
                if (!disposed && IsCurrent(state))
                {
                    RaiseRunning();
                }
            }
        }
    }

    private void RaiseRunning()
    {
        OnPropertyChanged(nameof(Running));
        OnPropertyChanged(nameof(IsFetchRunning));
        OnPropertyChanged(nameof(IsPullRunning));
        OnPropertyChanged(nameof(IsPushRunning));
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

        Launch(ReadOnlyAsync);
    }

    /// <summary>A read outside any write. A no-op while busy, so it never queues a second one.</summary>
    private async Task ReadOnlyAsync()
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
    /// One read of the whole local state for the attached context, applied on the UI thread as one snapshot. The read runs
    /// under its own source, which <see cref="CancelRead"/> cancels when the context changes; a cancelled or superseded
    /// read applies nothing. A failing read is reported as the refresh problem and leaves the last operation result alone.
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

    /// <summary>Reads branch state, remotes and the in-progress operation; runs on a pool thread.</summary>
    private async Task<GitSnapshot> ReadSnapshotAsync(string workingDirectory, CancellationToken ct)
    {
        if (!await git.IsRepositoryAsync(workingDirectory, ct).ConfigureAwait(false))
        {
            return GitSnapshot.Empty;
        }

        var status = await git.GetBranchStatusAsync(workingDirectory, ct).ConfigureAwait(false);
        var remotes = await git.GetRemotesAsync(workingDirectory, ct).ConfigureAwait(false);
        var conflicts = await git.GetConflictStateAsync(workingDirectory, ct).ConfigureAwait(false);
        return new GitSnapshot(status, remotes, conflicts);
    }

    /// <summary>
    /// One write on the attached worktree. The write runs to the end even if another session is attached meanwhile; its
    /// outcome is recorded on its own <see cref="WorktreeState"/> and only shown when that worktree is still attached.
    /// </summary>
    private async Task RunOperationAsync(Func<string, CancellationToken, Task<GitRemoteResult>> operation)
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
                if (!outcome.Success)
                {
                    // The row's own line is the record; a push or pull that failed is what the user misses while the
                    // workbench is showing something else.
                    notifications.Notify(
                        NotificationLevel.Error,
                        NotificationsStrings.GitOperationFailedTitle,
                        LocText.Literal(
                            outcome.StandardError.Trim() is { Length: > 0 } error ? error : outcome.Message),
                        "git-op:" + workingDirectory);
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
            Launch(ReadOnlyAsync);
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
            if (candidate.Writing || candidate.Running != GitAction.None || ReferenceEquals(candidate, current))
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

    /// <summary>Fetch is read-only and only needs a repository and a remote.</summary>
    private bool CanFetchRow() => CanOperate() && HasRemote;

    /// <summary>
    /// Pulling needs an upstream, not a behind count: <see cref="Behind"/> only becomes true after a fetch, so gating the
    /// verb on it would leave the one action that brings the commits in unreachable until something else fetched. The
    /// number on the button is information; the command is the user's call.
    /// </summary>
    private bool CanPullRow() => CanOperate() && HasUpstream;

    /// <summary>
    /// Push needs a remote and a branch — same reasoning as pull: the ahead count is as stale as the last fetch, so it
    /// never gates the verb. With nothing to send git answers "Everything up-to-date", which is a truthful result.
    /// </summary>
    private bool CanPushRow() => CanOperate() && HasRemote && !IsDetached;

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

    private async Task AbortOperationAsync() =>
        await RunOperationAsync((directory, token) => git.AbortOperationAsync(directory, token)).ConfigureAwait(true);

    private async Task ContinueOperationAsync() =>
        await RunOperationAsync((directory, token) => git.ContinueOperationAsync(directory, token)).ConfigureAwait(true);

    /// <summary>The network calls read the timeout per operation, so a settings change needs no restart.</summary>
    private GitNetworkOptions NetworkOptions() => new() { Timeout = settings.NetworkTimeout() };

    private void ReportCommandFailure(Exception exception) =>
        ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorGitFailed, exception.Message);

    // ---------------------------------------------------------------- state application

    /// <summary>Applies one immutable read as a whole, on the UI thread.</summary>
    private void Apply(GitSnapshot read)
    {
        snapshot = read;
        refreshProblem = null;

        ConflictedFiles.Clear();
        var workingDirectory = context.WorkingDirectory;
        foreach (var path in read.Conflicts.ConflictedPaths)
        {
            ConflictedFiles.Add(new ConflictedFileItem(path, openFile, workingDirectory));
        }

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

    private void ApplyLocalFailure(GitRemoteFailureKind kind, string key, string? argument) =>
        Report(current, LocalFailure(kind, key, argument));

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

    /// <summary>
    /// Empties everything the row shows about a repository. The last operation result is not part of it: it belongs to the
    /// attached worktree's <see cref="WorktreeState"/> and is shown again as soon as the worktree is attached.
    /// </summary>
    private void Clear()
    {
        snapshot = GitSnapshot.Empty;
        refreshProblem = null;
        ConflictedFiles.Clear();
        RaiseTexts();
        RefreshBusy();
        NotifyCommands();
    }

    /// <summary>Re-reads every language-dependent text; called after a state change and on <c>Loc.LanguageChanged</c>.</summary>
    private void RaiseTexts()
    {
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(BranchLabel));
        OnPropertyChanged(nameof(HasBranch));
        OnPropertyChanged(nameof(Ahead));
        OnPropertyChanged(nameof(Behind));
        OnPropertyChanged(nameof(AheadText));
        OnPropertyChanged(nameof(BehindText));
        OnPropertyChanged(nameof(HasAhead));
        OnPropertyChanged(nameof(HasBehind));
        OnPropertyChanged(nameof(AheadBehindText));
        OnPropertyChanged(nameof(UpstreamText));
        OnPropertyChanged(nameof(HasUpstream));
        OnPropertyChanged(nameof(HasRemote));
        OnPropertyChanged(nameof(IsDetached));
        OnPropertyChanged(nameof(PushTarget));
        OnPropertyChanged(nameof(OperationText));
        OnPropertyChanged(nameof(HasOperation));
        OnPropertyChanged(nameof(MessageText));
        OnPropertyChanged(nameof(LastResultText));
        OnPropertyChanged(nameof(MessageDetail));
        OnPropertyChanged(nameof(HasMessageDetail));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(MessageIsError));
        OnPropertyChanged(nameof(HasMessage));
        OnPropertyChanged(nameof(HintText));
        OnPropertyChanged(nameof(HasHint));
        OnPropertyChanged(nameof(RefreshProblemText));
        OnPropertyChanged(nameof(HasRefreshProblem));
        OnPropertyChanged(nameof(FetchTooltip));
        OnPropertyChanged(nameof(PullTooltip));
        OnPropertyChanged(nameof(PushTooltip));
        OnPropertyChanged(nameof(BranchTooltip));
        OnPropertyChanged(nameof(RowAutomationName));
        OnPropertyChanged(nameof(StrategyText));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A notification from another thread belongs to a row whose window is gone (a test host): the properties raised
        // here are bound to controls that live on their own thread.
        if (!disposed && Environment.CurrentManagedThreadId == uiThread)
        {
            RaiseTexts();
        }
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

    private long useClock;

    /// <summary>
    /// What the row remembers about one worktree: whether a write is running for it, and the outcome of its last operation.
    /// Lives in the bounded table; read and written on the UI thread only.
    /// </summary>
    private sealed class WorktreeState(string key)
    {
        public string Key { get; } = key;

        public bool Writing { get; set; }

        /// <summary>The verb this worktree has in flight, for the row's pulse; <see cref="GitAction.None"/> when idle.</summary>
        public GitAction Running { get; set; }

        public PanelResult Result { get; set; } = PanelResult.None;

        /// <summary>Order of attachment, for eviction.</summary>
        public long LastUsed { get; set; }
    }

    /// <summary>One immutable read of the session's repository, built on a pool thread and applied in one go.</summary>
    private readonly record struct GitSnapshot(
        GitBranchStatus? Status,
        IReadOnlyList<GitRemote> Remotes,
        GitConflictState Conflicts)
    {
        /// <summary>No repository, no branch: everything the row shows is empty.</summary>
        public static GitSnapshot Empty { get; } = new(
            null,
            Array.Empty<GitRemote>(),
            new GitConflictState(GitOperationKind.None, Array.Empty<string>()));

        public bool HasRepository => Status is not null;
    }

    /// <summary>The last operation's outcome: either git's own message or a text the row picked from the string table.</summary>
    private readonly record struct PanelResult(
        GitRemoteFailureKind Failure,
        string Message,
        string Detail,
        string? LocalizedKey,
        string? LocalizedArgument)
    {
        public static PanelResult None { get; } = new(GitRemoteFailureKind.None, string.Empty, string.Empty, null, null);
    }

    /// <summary>The ahead/behind pair, or null when there is nothing to compare against.</summary>
    private (int Ahead, int Behind)? Counts =>
        snapshot.Status is { Upstream: not null, UpstreamGone: false } status
            && status.Ahead is { } ahead
            && status.Behind is { } behind
            ? (ahead, behind)
            : null;

    /// <summary>The short head commit, for the branch tooltip; empty outside a repository.</summary>
    private string HeadShort => snapshot.Status?.HeadCommit is { Length: > 0 } head ? Shorten(head) : string.Empty;
}

/// <summary>Which verb a running work item belongs to, so the row can show where the work is.</summary>
internal enum GitAction
{
    None,
    Fetch,
    Pull,
    Push,
    Continue,
    Abort,
}

/// <summary>One unmerged file of a stopped rebase/merge; a row that opens it in the editor when one can be opened.</summary>
internal sealed class ConflictedFileItem
{
    public ConflictedFileItem(string path, Action<string>? openFile, string workingDirectory)
    {
        Path = path;
        FullPath = System.IO.Path.IsPathRooted(path) || workingDirectory.Length == 0
            ? path
            : System.IO.Path.Combine(workingDirectory, path);
        OpenCommand = new RelayCommand(
            () =>
            {
                if (openFile is not null)
                {
                    openFile(FullPath);
                }
            },
            () => openFile is not null);
    }

    /// <summary>The repository-relative path, as git reports it (what the reviewer recognises).</summary>
    public string Path { get; }

    /// <summary>The absolute path handed to the editor.</summary>
    public string FullPath { get; }

    public RelayCommand OpenCommand { get; }
}
