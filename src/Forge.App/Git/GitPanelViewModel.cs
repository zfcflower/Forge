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
/// snapshot, and a refresh started for a previous context is discarded (generation guard). One operation at a time per
/// panel: commands are disabled while <see cref="IsBusy"/>. There is no automatic fetch; refresh happens on attach, after
/// every operation, after each main-session turn (<see cref="NotifyTurnCompleted"/>) and on demand.</para>
/// <para>The title bar shows <see cref="BranchText"/> next to the session title and the status bar shows
/// <see cref="AheadBehindText"/>; both are empty when the session is not in a repository.</para>
/// </summary>
internal sealed class GitPanelViewModel : ObservableObject, IDisposable
{
    /// <summary>How much of a commit hash the detached-head text keeps (git's own short form).</summary>
    private const int ShortCommitLength = 7;

    private readonly GitCli git;
    private readonly GitPanelSettings settings;
    private readonly Func<string, string, Task<bool>> confirm;
    private readonly AsyncRelayCommand[] commands;

    /// <summary>Cancels whatever git call is in flight when the panel goes away; never replaced, only cancelled.</summary>
    private readonly CancellationTokenSource lifetime = new();

    private PanelContext context = PanelContext.Empty;
    private GitSnapshot snapshot = GitSnapshot.Empty;
    private PanelResult result = PanelResult.None;
    private string newBranchName = string.Empty;
    private string? selectedBranch;
    private bool busy;
    private bool refreshRequested;

    /// <summary>Bumped by every work item and by <see cref="Attach"/>; only the newest one may apply its result.</summary>
    private int generation;
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

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanRead, ReportCommandFailure);
        FetchCommand = new AsyncRelayCommand(FetchAsync, CanOperate, ReportCommandFailure);
        PullCommand = new AsyncRelayCommand(PullAsync, CanOperate, ReportCommandFailure);
        PushCommand = new AsyncRelayCommand(PushAsync, CanOperate, ReportCommandFailure);
        CreateBranchCommand = new AsyncRelayCommand(CreateBranchAsync, CanOperate, ReportCommandFailure);
        SwitchBranchCommand = new AsyncRelayCommand(SwitchBranchAsync, CanOperate, ReportCommandFailure);
        SetUpstreamCommand = new AsyncRelayCommand(SetUpstreamAsync, CanOperate, ReportCommandFailure);
        DeleteBranchCommand = new AsyncRelayCommand(DeleteBranchAsync, CanOperate, ReportCommandFailure);
        AbortOperationCommand = new AsyncRelayCommand(AbortOperationAsync, CanOperate, ReportCommandFailure);
        ContinueOperationCommand = new AsyncRelayCommand(ContinueOperationAsync, CanOperate, ReportCommandFailure);
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

        // The lifetime of this table entry is the process (one panel per window), so the subscription is never dropped:
        // the panel has to re-read its title and every formatted text, the same way the settings sections do.
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

    public bool IsBusy
    {
        get => busy;
        private set
        {
            if (Set(ref busy, value))
            {
                NotifyCommands();
            }
        }
    }

    /// <summary>Message of the last operation; failures carry git's stderr in <see cref="LastResultDetail"/>.</summary>
    public string LastResultText => result.LocalizedKey is { } key
        ? result.LocalizedArgument is { } argument ? Loc.Instance.Format(key, argument) : Loc.Instance[key]
        : result.Message;

    public string LastResultDetail => result.Detail;

    public bool HasLastResultDetail => result.Detail.Length > 0;

    public bool LastResultIsError => result.Failure != GitRemoteFailureKind.None;

    public bool HasResult => LastResultText.Length > 0;

    /// <summary>Recovery hint for the last failure kind (sign in, pull first, resolve conflicts, add a remote…).</summary>
    public string HintText => HintFor(result.Failure);

    public bool HasHint => HintText.Length > 0;

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

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand FetchCommand { get; }

    public AsyncRelayCommand PullCommand { get; }

    /// <summary>Push; sets the upstream on the first push. Never forces.</summary>
    public AsyncRelayCommand PushCommand { get; }

    public AsyncRelayCommand CreateBranchCommand { get; }

    public AsyncRelayCommand SwitchBranchCommand { get; }

    /// <summary>Sets the current branch's upstream to <c>origin/&lt;branch&gt;</c> (or the typed name).</summary>
    public AsyncRelayCommand SetUpstreamCommand { get; }

    /// <summary>Deletes <see cref="SelectedBranch"/> after confirmation; never the current branch.</summary>
    public AsyncRelayCommand DeleteBranchCommand { get; }

    public AsyncRelayCommand AbortOperationCommand { get; }

    public AsyncRelayCommand ContinueOperationCommand { get; }

    /// <summary>Follows the active session; an empty context clears the panel.</summary>
    public void Attach(PanelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (disposed)
        {
            return;
        }

        var previousSessionId = this.context.SessionId;
        var previousDirectory = this.context.WorkingDirectory;
        this.context = context;

        // A refresh or operation that is still running belongs to the previous context: its result must not land, and
        // anything it asks for afterwards is re-issued for the new one through the queued refresh.
        generation++;
        var changed = !string.Equals(previousSessionId, context.SessionId, StringComparison.Ordinal)
            || !string.Equals(previousDirectory, context.WorkingDirectory, StringComparison.Ordinal);
        if (!context.HasSession || context.WorkingDirectory.Length == 0)
        {
            Clear();
            return;
        }

        if (changed)
        {
            // Nothing of the previous session may stay on screen while the new read is in flight.
            Clear();
        }

        RequestRefresh();
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
        generation++;
        refreshRequested = false;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;

        // Cancel only: the git call in flight may still be registering on this token, and it reports cancellation as an
        // OperationCanceledException that the work item already handles.
        lifetime.Cancel();
    }

    // ---------------------------------------------------------------- work items

    /// <summary>Starts a read of the current context, or queues one behind the work item that is running.</summary>
    private void RequestRefresh()
    {
        if (disposed)
        {
            return;
        }

        if (busy)
        {
            refreshRequested = true;
            return;
        }

        _ = RefreshAsync(CancellationToken.None);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        if (disposed)
        {
            return;
        }

        if (busy)
        {
            refreshRequested = true;
            return;
        }

        IsBusy = true;
        try
        {
            await RefreshCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            EndWork();
        }
    }

    /// <summary>One read of the whole local state; assumes the work gate is held and applies the result on the UI thread.</summary>
    private async Task RefreshCoreAsync()
    {
        var generationNow = ++generation;
        var context = this.context;
        if (!context.HasSession || context.WorkingDirectory.Length == 0)
        {
            Clear();
            return;
        }

        var workingDirectory = context.WorkingDirectory;
        try
        {
            var read = await Task.Run(() => ReadSnapshotAsync(workingDirectory, lifetime.Token), lifetime.Token)
                .ConfigureAwait(true);
            if (IsCurrent(generationNow))
            {
                Apply(read);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsGitFailure(exception))
        {
            if (IsCurrent(generationNow))
            {
                ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorGitFailed, exception.Message);
            }
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

    private async Task RunOperationAsync(
        Func<string, CancellationToken, Task<GitRemoteResult>> operation,
        Action? onSuccess = null)
    {
        if (disposed)
        {
            return;
        }

        if (busy)
        {
            refreshRequested = true;
            return;
        }

        IsBusy = true;
        try
        {
            await RunOperationCoreAsync(operation, onSuccess).ConfigureAwait(true);
        }
        finally
        {
            EndWork();
        }
    }

    /// <summary>The body of one operation; assumes the work gate is held, and re-reads the state when it is done.</summary>
    private async Task RunOperationCoreAsync(
        Func<string, CancellationToken, Task<GitRemoteResult>> operation,
        Action? onSuccess = null)
    {
        var generationNow = ++generation;
        var context = this.context;
        if (!context.HasSession || context.WorkingDirectory.Length == 0)
        {
            ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorNoRepository, null);
            return;
        }

        var workingDirectory = context.WorkingDirectory;
        try
        {
            var outcome = await Task.Run(() => operation(workingDirectory, lifetime.Token), lifetime.Token)
                .ConfigureAwait(true);
            if (!IsCurrent(generationNow))
            {
                return;
            }

            ApplyOutcome(outcome);
            if (outcome.Success)
            {
                onSuccess?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsGitFailure(exception))
        {
            if (!IsCurrent(generationNow))
            {
                return;
            }

            ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorGitFailed, exception.Message);
        }

        await RefreshCoreAsync().ConfigureAwait(true);
    }

    /// <summary>Releases the work gate and runs the refresh that a context change or a turn completion queued behind it.</summary>
    private void EndWork()
    {
        IsBusy = false;
        if (refreshRequested && !disposed)
        {
            refreshRequested = false;
            RequestRefresh();
        }
    }

    private bool IsCurrent(int value) => !disposed && value == generation;

    // ---------------------------------------------------------------- commands

    private bool CanRead() => !disposed && !busy && context.HasSession && context.WorkingDirectory.Length > 0;

    private bool CanOperate() => CanRead() && snapshot.HasRepository;

    private void NotifyCommands()
    {
        foreach (var command in commands)
        {
            command.NotifyCanExecuteChanged();
        }
    }

    private async Task FetchAsync(CancellationToken ct)
    {
        var network = NetworkOptions();
        await RunOperationAsync((directory, token) => git.FetchAsync(directory, remote: null, network, token))
            .ConfigureAwait(true);
    }

    private async Task PullAsync(CancellationToken ct)
    {
        var strategy = settings.PullStrategy();
        var network = NetworkOptions();
        await RunOperationAsync((directory, token) => git.PullAsync(directory, strategy, network, token))
            .ConfigureAwait(true);
    }

    private async Task PushAsync(CancellationToken ct)
    {
        var push = new GitPushOptions { SetUpstream = !HasUpstream };
        var network = NetworkOptions();
        await RunOperationAsync((directory, token) => git.PushAsync(directory, push, network, token))
            .ConfigureAwait(true);
    }

    private async Task CreateBranchAsync(CancellationToken ct)
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

    private async Task SwitchBranchAsync(CancellationToken ct)
    {
        if (selectedBranch is not { Length: > 0 } branch)
        {
            ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorSelectBranch, null);
            return;
        }

        await RunOperationAsync((directory, token) => git.SwitchBranchAsync(directory, branch, token))
            .ConfigureAwait(true);
    }

    private async Task SetUpstreamAsync(CancellationToken ct)
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

    private async Task DeleteBranchAsync(CancellationToken ct)
    {
        if (disposed)
        {
            return;
        }

        if (busy)
        {
            refreshRequested = true;
            return;
        }

        IsBusy = true;
        try
        {
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

            var confirmed = await confirm(
                    Loc.Instance[GitStrings.ConfirmDeleteTitle],
                    Loc.Instance.Format(GitStrings.ConfirmDeleteMessage, branch))
                .ConfigureAwait(true);
            if (!confirmed || disposed)
            {
                return;
            }

            var generationNow = ++generation;
            var context = this.context;
            if (!context.HasSession || context.WorkingDirectory.Length == 0)
            {
                ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorNoRepository, null);
                return;
            }

            await DeleteBranchCoreAsync(generationNow, context.WorkingDirectory, branch).ConfigureAwait(true);
            await RefreshCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            EndWork();
        }
    }

    /// <summary>
    /// <see cref="GitCli.DeleteBranchAsync"/> reports a refusal (checked out in a worktree, not merged) as a
    /// <see cref="GitException"/>; the panel shows it like any other failure instead of letting it escape.
    /// </summary>
    private async Task DeleteBranchCoreAsync(int generationNow, string workingDirectory, string branch)
    {
        try
        {
            await Task.Run(
                    () => git.DeleteBranchAsync(workingDirectory, branch, force: false, lifetime.Token),
                    lifetime.Token)
                .ConfigureAwait(true);
            if (IsCurrent(generationNow))
            {
                ApplyLocalSuccess(GitStrings.ResultBranchDeleted, branch);
            }
        }
        catch (GitException exception)
        {
            if (!IsCurrent(generationNow))
            {
                return;
            }

            ApplyOutcome(new GitRemoteResult(
                GitFailureClassifier.Classify(new GitCommandResult(exception.ExitCode, string.Empty, exception.StandardError)),
                exception.Message,
                exception.StandardError));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsGitFailure(exception))
        {
            if (IsCurrent(generationNow))
            {
                ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorGitFailed, exception.Message);
            }
        }
    }

    private async Task AbortOperationAsync(CancellationToken ct) =>
        await RunOperationAsync((directory, token) => git.AbortOperationAsync(directory, token)).ConfigureAwait(true);

    private async Task ContinueOperationAsync(CancellationToken ct) =>
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

        Remotes.Clear();
        foreach (var remote in read.Remotes)
        {
            Remotes.Add(remote.Name);
        }

        Branches.Clear();
        var current = read.Status?.Branch;
        if (current is { Length: > 0 } && Contains(read.Branches, current))
        {
            Branches.Add(current);
        }

        foreach (var branch in read.Branches)
        {
            if (!string.Equals(branch.Name, current, StringComparison.Ordinal))
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

    private void ApplyOutcome(GitRemoteResult outcome)
    {
        result = new PanelResult(
            outcome.Failure,
            outcome.Message,
            outcome.Success ? string.Empty : outcome.StandardError.Trim(),
            null,
            null);
        RaiseTexts();
    }

    /// <summary>A success the panel describes itself (the branch it just deleted); the text is a <see cref="Loc"/> key.</summary>
    private void ApplyLocalSuccess(string key, string? argument)
    {
        result = new PanelResult(GitRemoteFailureKind.None, string.Empty, string.Empty, key, argument);
        RaiseTexts();
    }

    /// <summary>A failure the panel decided on itself (no branch selected, nothing typed); the text is a <see cref="Loc"/> key.</summary>
    private void ApplyLocalFailure(GitRemoteFailureKind kind, string key, string? argument)
    {
        result = new PanelResult(kind, string.Empty, string.Empty, key, argument);
        RaiseTexts();
    }

    /// <summary>Keeps a picker selection the user already made; otherwise selects the current branch, else the first one.</summary>
    private void SyncSelection()
    {
        if (selectedBranch is { Length: > 0 } existing && Branches.Contains(existing))
        {
            return;
        }

        var current = snapshot.Status?.Branch;
        SelectedBranch = current is { Length: > 0 } && Branches.Contains(current)
            ? current
            : Branches.Count > 0 ? Branches[0] : null;
    }

    private void Clear()
    {
        snapshot = GitSnapshot.Empty;
        result = PanelResult.None;
        refreshRequested = false;
        Remotes.Clear();
        Branches.Clear();
        ConflictedFiles.Clear();
        NewBranchName = string.Empty;
        SelectedBranch = null;
        RaiseTexts();
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
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!disposed)
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
