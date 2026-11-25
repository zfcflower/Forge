using System.ComponentModel;
using Forge.Abstractions;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.Workspace;
using Forge.App.Logging;

namespace Forge.App.Lifecycle;

/// <summary>The git half of a parallel fork; <see cref="ForkGit"/> is the real one, a host substitutes its own.</summary>
internal interface IForkGit : IHandoverGit
{
    /// <summary>The live HEAD of the source workspace (6.5 step 1: never the stored branch).</summary>
    Task<GitHead> ReadHeadAsync(string directory, CancellationToken ct);

    /// <summary>The source workspace's sparse-checkout configuration; null when it is a full checkout.</summary>
    Task<SparseCheckout?> SparseAsync(string directory, CancellationToken ct);

    /// <summary>git's own ref-name rules, the same check the new-branch dialog uses.</summary>
    Task<bool> IsValidBranchNameAsync(string repositoryRoot, string name, CancellationToken ct);

    /// <summary><c>git worktree add -b</c> under the existing path rule, with the sparse configuration when there is one.</summary>
    Task<WorktreeSession> CreateWorktreeAsync(
        string repositoryRoot,
        string title,
        string branchName,
        string startPoint,
        SparseCheckout? sparse,
        CancellationToken ct);

}

/// <summary>The real <see cref="IForkGit"/>: reads through <see cref="GitCli"/> and creates the worktree through <see cref="WorktreeService"/>.</summary>
internal sealed class ForkGit(GitCli git, StepCommitter committer, WorktreeService worktrees) : IForkGit
{
    private readonly HandoverGit handover = new(git, committer);

    public Task<IReadOnlyList<string>?> UncommittedAsync(string directory, CancellationToken ct) => handover.UncommittedAsync(directory, ct);

    public Task<string?> HeadAsync(string directory, CancellationToken ct) => handover.HeadAsync(directory, ct);

    public Task<GitHead> ReadHeadAsync(string directory, CancellationToken ct) => git.GetHeadAsync(directory, ct);

    public Task<SparseCheckout?> SparseAsync(string directory, CancellationToken ct) => git.GetSparseConfigAsync(directory, ct);

    public Task<bool> IsValidBranchNameAsync(string repositoryRoot, string name, CancellationToken ct) =>
        git.IsValidBranchNameAsync(repositoryRoot, name, ct);

    /// <summary><c>IHandoverGit</c>'s commit: the fork itself never commits, the interface is what the reads come through.</summary>
    public Task<StepCommitResult> CommitAsync(string directory, string message, CancellationToken ct) =>
        handover.CommitAsync(directory, message, ct);

    public Task<WorktreeSession> CreateWorktreeAsync(
        string repositoryRoot,
        string title,
        string branchName,
        string startPoint,
        SparseCheckout? sparse,
        CancellationToken ct) =>
        worktrees.Manager.CreateAsync(repositoryRoot, title, branchName, startPoint, ct, sparse);

}

/// <summary>
/// What is forked: the project, the source workspace directory and its active session, if it has one (a workspace
/// without one can still be forked; the new session then starts blank).
/// </summary>
internal sealed record ForkSource(ProjectInfo Project, string Directory, SessionInfo? Session);

/// <summary>
/// The source workspace as the fork dialog opens on it: the live branch and commit, the uncommitted paths and the sparse
/// configuration; <see cref="Error"/> instead when it cannot be forked (not a repository, detached HEAD, unborn branch).
/// </summary>
internal sealed record ForkPreparation(string? Branch, string? StartCommit, IReadOnlyList<string> Uncommitted, SparseCheckout? Sparse, string? Error)
{
    public bool CanFork => Error is null;

    public static ForkPreparation Refused(string error) => new(null, null, [], null, error);
}

/// <summary>The fork dialog's answers: the new branch name; uncommitted source changes are neither committed nor carried.</summary>
internal sealed record ForkChoice(string BranchName);

/// <summary>One fork execution: the new worktree's path on success, else the reason to show.</summary>
internal sealed record ForkAttempt(string? Workspace, string? Error)
{
    public bool Succeeded => Workspace is not null;

    public static ForkAttempt Failed(string error) => new(null, error);
}

/// <summary>
/// The parallel fork (lifecycle 6.5): a new worktree on a new branch started at the source workspace's live HEAD. The new
/// branch starts with no root session - the user creates one there and decides then what it is told - and uncommitted
/// source changes are neither committed nor carried over: a fork is only the branch and its checkout. Every step that fails
/// stops with a user-facing reason and takes back the worktree and its unmoved branch. Runs off the UI thread.
/// </summary>
internal sealed class ForkFlow
{
    private readonly ISessionStore store;
    private readonly IForkGit git;

    public ForkFlow(ISessionStore store, IForkGit git)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(git);
        this.store = store;
        this.git = git;
    }

    /// <summary>Reads the source workspace for the dialog: live HEAD (detached / unborn refuse), changes, sparse config.</summary>
    public async Task<ForkPreparation> PrepareAsync(ForkSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            if (await SourceChangedAsync(source, ct).ConfigureAwait(false) is { } changed)
            {
                return ForkPreparation.Refused(changed);
            }

            var head = await git.ReadHeadAsync(source.Directory, ct).ConfigureAwait(false);
            if (Refusal(head) is { } refusal)
            {
                return ForkPreparation.Refused(refusal);
            }

            var commit = await git.HeadAsync(source.Directory, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(commit))
            {
                return ForkPreparation.Refused(ForkStrings.Unborn);
            }

            var changes = await git.UncommittedAsync(source.Directory, ct).ConfigureAwait(false) ?? [];
            var sparse = await git.SparseAsync(source.Directory, ct).ConfigureAwait(false);
            return new ForkPreparation(head.Branch, commit, changes, sparse, null);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return ForkPreparation.Refused(ForkStrings.ReadFailed(exception.Message));
        }
    }

    /// <summary>
    /// Runs the fork: the worktree on the new branch at the source's live HEAD, with the source's sparse configuration.
    /// Nothing else is created - the new branch has no root session until the user makes one there.
    /// </summary>
    public async Task<ForkAttempt> ExecuteAsync(ForkSource source, ForkChoice choice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(choice);
        var branchName = choice.BranchName.Trim();
        var repository = source.Project.RootPath;
        try
        {
            if (branchName.Length == 0 || !await git.IsValidBranchNameAsync(repository, branchName, ct).ConfigureAwait(false))
            {
                return ForkAttempt.Failed(ForkStrings.BranchRequired);
            }

            // The dialog may have stayed open while the source session was taken over or archived (5.4 rule 4).
            if (await SourceChangedAsync(source, ct).ConfigureAwait(false) is { } changed)
            {
                return ForkAttempt.Failed(changed);
            }

            var head = await git.ReadHeadAsync(source.Directory, ct).ConfigureAwait(false);
            if (Refusal(head) is { } refusal)
            {
                return ForkAttempt.Failed(refusal);
            }

            var start = await git.HeadAsync(source.Directory, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(start))
            {
                return ForkAttempt.Failed(ForkStrings.Unborn);
            }

            var sparse = await git.SparseAsync(source.Directory, ct).ConfigureAwait(false);

            WorktreeSession worktree;
            try
            {
                worktree = await git.CreateWorktreeAsync(
                        repository,
                        ProjectStrings.WorktreeSessionTitle(),
                        branchName,
                        start,
                        sparse,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                return ForkAttempt.Failed(ForkStrings.CreateFailed(exception.Message));
            }

            return new ForkAttempt(worktree.Path, null);
        }
        catch (OperationCanceledException)
        {
            return ForkAttempt.Failed(ForkStrings.Cancelled);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return ForkAttempt.Failed(ForkStrings.ReadFailed(exception.Message));
        }
    }

    /// <summary>The default fork branch: the worktree branch prefix and a timestamped slug (6.5 step 3).</summary>
    public static string DefaultBranchName(string prefix) =>
        prefix + "fork-" + DateTimeOffset.Now.ToString("MMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Null while the source session is still the workspace's active one; otherwise the reason to refuse the fork. A dialog
    /// may stay open for minutes, and a session taken over or archived meanwhile is no longer what the fork was started from.
    /// </summary>
    private async Task<string?> SourceChangedAsync(ForkSource source, CancellationToken ct)
    {
        if (source.Session is not { } session)
        {
            return null;
        }

        var current = await store.GetSessionAsync(session.Id, ct).ConfigureAwait(false);
        return current is { Status: SessionStatus.Active } ? null : ForkStrings.SourceChanged(session.Title);
    }

    private static string? Refusal(GitHead head) => head.Kind switch
    {
        GitHeadKind.NotRepository => ForkStrings.NotRepository,
        GitHeadKind.Detached => ForkStrings.Detached,
        GitHeadKind.UnbornBranch => ForkStrings.Unborn,
        _ => string.IsNullOrWhiteSpace(head.Branch) ? ForkStrings.Detached : null,
    };

    private static bool IsExpected(Exception exception) =>
        exception is not (OperationCanceledException or OutOfMemoryException);
}

/// <summary>
/// Hosted execution of the fork dialog: the dialog collects the <see cref="ForkChoice"/> and calls
/// <see cref="ExecuteAsync"/>; it stays open with a failed attempt's reason until it succeeds or the user cancels. There is
/// nothing else to choose - uncommitted source changes stay where they are, and the new branch starts without a session.
/// </summary>
internal sealed class ForkDialogModel : INotifyPropertyChanged
{
    private readonly Func<ForkChoice, CancellationToken, Task<string?>> execute;
    private CancellationTokenSource? running;
    private string? error;
    private string branchName;

    /// <param name="uncommittedCount">Uncommitted paths (untracked included): the dialog only warns that they stay behind.</param>
    public ForkDialogModel(
        string sourceBranch,
        string startCommit,
        int uncommittedCount,
        bool isSparse,
        string defaultBranchName,
        Func<ForkChoice, CancellationToken, Task<string?>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        SourceBranch = sourceBranch;
        StartCommit = startCommit;
        UncommittedCount = uncommittedCount;
        IsSparse = isSparse;
        branchName = defaultBranchName;
        this.execute = execute;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SourceBranch { get; }

    public string StartCommit { get; }

    public int UncommittedCount { get; }

    public bool HasUncommitted => UncommittedCount > 0;

    public bool IsSparse { get; }

    public string BranchName
    {
        get => branchName;
        set => Set(ref branchName, value ?? string.Empty, nameof(BranchName));
    }

    public bool IsRunning => running is not null;

    public string? Error
    {
        get => error;
        private set => Set(ref error, value, nameof(Error));
    }

    public bool Succeeded { get; private set; }

    /// <summary>One attempt; true when the fork went through (the dialog then closes).</summary>
    public async Task<bool> ExecuteAsync()
    {
        if (running is not null || Succeeded)
        {
            return Succeeded;
        }

        using var source = new CancellationTokenSource();
        running = source;
        Error = null;
        Raise(nameof(IsRunning));
        try
        {
            var reason = await execute(new ForkChoice(branchName), source.Token).ConfigureAwait(true);
            Succeeded = reason is null;
            Error = reason;
            return Succeeded;
        }
        catch (OperationCanceledException)
        {
            Error = ForkStrings.Cancelled;
            return false;
        }
        finally
        {
            running = null;
            Raise(nameof(IsRunning));
        }
    }

    public void CancelRunning() => running?.Cancel();

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Raise(name);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
