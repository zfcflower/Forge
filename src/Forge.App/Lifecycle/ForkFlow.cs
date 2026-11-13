using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Forge.Abstractions;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.Core.Agents;
using Forge.Workspace;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Lifecycle;

/// <summary>The git half of a parallel fork; <see cref="ForkGit"/> is the real one, tests substitute their own.</summary>
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

    /// <summary>Takes a worktree this fork just created back again (unforced; the branch only while unmoved, 12.4).</summary>
    Task RemoveCreatedAsync(WorktreeSession worktree, CancellationToken ct);
}

/// <summary>The real <see cref="IForkGit"/>: reads through <see cref="GitCli"/>, the commit through <see cref="StepCommitter"/>, the worktree through <see cref="WorktreeService"/>.</summary>
internal sealed class ForkGit(GitCli git, StepCommitter committer, WorktreeService worktrees) : IForkGit
{
    private readonly HandoverGit handover = new(git, committer);

    public Task<IReadOnlyList<string>?> UncommittedAsync(string directory, CancellationToken ct) => handover.UncommittedAsync(directory, ct);

    public Task<string?> HeadAsync(string directory, CancellationToken ct) => handover.HeadAsync(directory, ct);

    public Task<StepCommitResult> CommitAsync(string directory, string message, CancellationToken ct) =>
        handover.CommitAsync(directory, message, ct);

    public Task<GitHead> ReadHeadAsync(string directory, CancellationToken ct) => git.GetHeadAsync(directory, ct);

    public Task<SparseCheckout?> SparseAsync(string directory, CancellationToken ct) => git.GetSparseConfigAsync(directory, ct);

    public Task<bool> IsValidBranchNameAsync(string repositoryRoot, string name, CancellationToken ct) =>
        git.IsValidBranchNameAsync(repositoryRoot, name, ct);

    public Task<WorktreeSession> CreateWorktreeAsync(
        string repositoryRoot,
        string title,
        string branchName,
        string startPoint,
        SparseCheckout? sparse,
        CancellationToken ct) =>
        worktrees.Manager.CreateAsync(repositoryRoot, title, branchName, startPoint, ct, sparse);

    public Task RemoveCreatedAsync(WorktreeSession worktree, CancellationToken ct) => worktrees.TryRemoveCreatedAsync(worktree, ct);
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

/// <summary>The fork dialog's answers: the branch name, whether to commit the source's changes first, the context mode.</summary>
internal sealed record ForkChoice(string BranchName, bool CommitFirst, HandoverContextMode Mode);

/// <summary>One fork execution: the new session on success, else the reason; <see cref="CommitSha"/> is a landed pre-fork commit.</summary>
internal sealed record ForkAttempt(SessionInfo? Session, string? Error, string? CommitSha)
{
    public bool Succeeded => Session is not null;

    public static ForkAttempt Failed(string error, string? commitSha) => new(null, error, commitSha);
}

/// <summary>
/// The parallel fork (lifecycle 6.5): a new worktree on a new branch started at the source workspace's live HEAD, and a
/// new session in it recording <c>fork_of_branch</c>, <c>fork_base_sha</c> and the source's lineage root (never
/// <c>parent_session_id</c>). Uncommitted source changes are committed first in the source session's name (the handover
/// commit path) or left behind; nothing is stashed. The context is a full inheritance of the source session, a summary
/// of it, or blank; todos, plans and review marks are never copied. Every step that fails stops with a user-facing reason
/// and takes back what this attempt created (the session row, then the worktree and its unmoved branch); a landed commit
/// stays and is named. Runs off the UI thread.
/// </summary>
internal sealed class ForkFlow
{
    private readonly ISessionCatalog catalog;
    private readonly ISessionStore store;
    private readonly IForkGit git;
    private readonly Func<IReadOnlyList<ChatMessage>, string?, CancellationToken, Task<string>> summarize;

    public ForkFlow(
        ISessionCatalog catalog,
        ISessionStore store,
        IForkGit git,
        Func<IReadOnlyList<ChatMessage>, string?, CancellationToken, Task<string>> summarize)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(summarize);
        this.catalog = catalog;
        this.store = store;
        this.git = git;
        this.summarize = summarize;
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
    /// Runs the fork. <paramref name="sourceIdle"/> is asked right before a "commit first" commit (the source may have
    /// started a turn while the dialog was open); a busy source then stops the attempt instead of committing under it.
    /// </summary>
    public async Task<ForkAttempt> ExecuteAsync(ForkSource source, ForkChoice choice, Func<bool> sourceIdle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(sourceIdle);
        var branchName = choice.BranchName.Trim();
        var repository = source.Project.RootPath;
        string? commitSha = null;
        try
        {
            if (branchName.Length == 0 || !await git.IsValidBranchNameAsync(repository, branchName, ct).ConfigureAwait(false))
            {
                return ForkAttempt.Failed(ForkStrings.BranchRequired, null);
            }

            // The dialog may have stayed open while the source session was taken over or archived (5.4 rule 4).
            if (await SourceChangedAsync(source, ct).ConfigureAwait(false) is { } changed)
            {
                return ForkAttempt.Failed(changed, null);
            }

            // Step 2: commit first, in the source session's name, through the handover commit path.
            if (choice.CommitFirst && source.Session is { } owner)
            {
                var changes = await git.UncommittedAsync(source.Directory, ct).ConfigureAwait(false);
                if (changes is { Count: > 0 })
                {
                    if (!sourceIdle())
                    {
                        return ForkAttempt.Failed(ForkStrings.BusyNow(owner.Title), null);
                    }

                    var committed = await git.CommitAsync(source.Directory, HandoverFlow.CommitPrefix + owner.Title, ct).ConfigureAwait(false);
                    switch (committed.Outcome)
                    {
                        case StepCommitOutcome.Committed:
                            commitSha = committed.CommitId;
                            break;
                        case StepCommitOutcome.NoChanges:
                            break;
                        default:
                            return ForkAttempt.Failed(ForkStrings.CommitFailed(committed.Message), null);
                    }
                }
            }

            // Step 1: the start point is the live HEAD, read after the commit.
            var head = await git.ReadHeadAsync(source.Directory, ct).ConfigureAwait(false);
            if (Refusal(head) is { } refusal)
            {
                return ForkAttempt.Failed(WithCommit(refusal, commitSha), commitSha);
            }

            var start = await git.HeadAsync(source.Directory, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(start))
            {
                return ForkAttempt.Failed(WithCommit(ForkStrings.Unborn, commitSha), commitSha);
            }

            var sourceBranch = head.Branch!;
            var sparse = await git.SparseAsync(source.Directory, ct).ConfigureAwait(false);

            // The context comes before the worktree, so a failed summary creates nothing.
            var mode = source.Session is null ? HandoverContextMode.Blank : choice.Mode;
            var title = ProjectStrings.WorktreeSessionTitle();
            var create = new SessionCreate(string.Empty, title)
            {
                ProjectId = source.Project.Id,
                ForkOfBranch = sourceBranch,
                ForkBaseSha = start,
                LineageRootId = source.Session is { } lineage ? lineage.LineageRootId ?? lineage.Id : null,
            };
            ChatMessage? initial;
            (create, initial) = await BuildContextAsync(source.Session, create, mode, sourceBranch, branchName, start, commitSha, ct)
                .ConfigureAwait(false);

            // Steps 3-4: the worktree on the new branch, at the start commit, with the source's sparse configuration.
            WorktreeSession worktree;
            try
            {
                worktree = await git.CreateWorktreeAsync(repository, title, branchName, start, sparse, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                return ForkAttempt.Failed(WithCommit(ForkStrings.CreateFailed(exception.Message), commitSha), commitSha);
            }

            // Step 5: the session row, then its first message. Neither is cancellable once the worktree exists: a
            // cancellation would only leave more to take back.
            SessionInfo? session = null;
            try
            {
                session = await catalog.CreateAsync(
                        create with { WorkspacePath = worktree.Path, WorktreePath = worktree.Path, Branch = worktree.Branch },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (initial is not null)
                {
                    await store.AppendAsync(session.Id, initial, null, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                await TakeBackAsync(session, worktree).ConfigureAwait(false);
                return ForkAttempt.Failed(WithCommit(ForkStrings.SessionFailed(exception.Message), commitSha), commitSha);
            }

            return new ForkAttempt(session, null, commitSha);
        }
        catch (OperationCanceledException)
        {
            return ForkAttempt.Failed(WithCommit(ForkStrings.Cancelled, commitSha), commitSha);
        }
        catch (ForkContextException exception)
        {
            return ForkAttempt.Failed(WithCommit(exception.Message, commitSha), commitSha);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return ForkAttempt.Failed(WithCommit(ForkStrings.ReadFailed(exception.Message), commitSha), commitSha);
        }
    }

    /// <summary>The default fork branch: the worktree branch prefix and a timestamped slug (6.5 step 3).</summary>
    public static string DefaultBranchName(string prefix) =>
        prefix + "fork-" + DateTimeOffset.Now.ToString("MMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<(SessionCreate Create, ChatMessage? Initial)> BuildContextAsync(
        SessionInfo? sourceSession,
        SessionCreate create,
        HandoverContextMode mode,
        string sourceBranch,
        string forkBranch,
        string start,
        string? commitSha,
        CancellationToken ct)
    {
        switch (mode)
        {
            case HandoverContextMode.Full:
            {
                long upTo;
                try
                {
                    upTo = await store.LastSeqAsync(sourceSession!.Id, ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsExpected(exception))
                {
                    throw new ForkContextException(HandoverStrings.ContextFailed(exception.Message), exception);
                }

                return (
                    create with { Origin = SessionOrigin.Full, InheritsFromSessionId = sourceSession.Id, InheritsUpToSeq = upTo },
                    ChatMessage.User(InheritanceNote.BuildFork(sourceBranch, forkBranch, start, commitSha)));
            }

            case HandoverContextMode.Summary:
            {
                string summary;
                try
                {
                    var history = new List<ChatMessage>();
                    await foreach (var message in store.ReadAsync(sourceSession!.Id, 0, null, ct).ConfigureAwait(false))
                    {
                        history.Add(message.Message);
                    }

                    summary = await summarize(history, DescribeFork(sourceBranch, forkBranch, start, commitSha), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw new ForkContextException(HandoverStrings.SummaryCancelled, null);
                }
                catch (Exception exception) when (IsExpected(exception))
                {
                    throw new ForkContextException(HandoverStrings.SummaryFailed(exception.Message), exception);
                }

                return (create with { Origin = SessionOrigin.Summary }, ChatMessage.User(summary));
            }

            default:
                return (create with { Origin = SessionOrigin.Blank }, null);
        }
    }

    /// <summary>The working-tree description the fork summary is generated from (model input, not UI text).</summary>
    internal static string DescribeFork(string sourceBranch, string forkBranch, string start, string? commitSha)
    {
        var builder = new StringBuilder();
        builder.Append("This summary starts a parallel fork: a new worktree on branch ").Append(forkBranch)
            .Append(", forked from ").Append(sourceBranch).Append(" at ").Append(start)
            .Append(". The source session goes on separately.\n");
        if (commitSha is not null)
        {
            builder.Append("The source session's changes were committed as ").Append(commitSha).Append(" before the fork.\n");
        }

        builder.Append("Uncommitted changes of the source were not carried over.\n");
        return builder.ToString();
    }

    /// <summary>
    /// The tree hands over the active session it knew at its last refresh; a handover or an archive since then makes it a
    /// stale source (forking it would inherit a session that is no longer the workspace's). Null while it is still Active.
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

    /// <summary>Best effort: the session row (soft delete, then purge) and the worktree this attempt created. Traced, never thrown.</summary>
    private async Task TakeBackAsync(SessionInfo? session, WorktreeSession worktree)
    {
        if (session is not null)
        {
            try
            {
                await catalog.SoftDeleteAsync([session.Id], CancellationToken.None).ConfigureAwait(false);
                await catalog.PurgeAsync(session.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                AppLogs.Fork.ZLogWarning($"could not take back the session row: {exception.Message}");
            }
        }

        try
        {
            await git.RemoveCreatedAsync(worktree, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            AppLogs.Fork.ZLogWarning($"could not take back the worktree: {exception.Message}");
        }
    }

    private static string? Refusal(GitHead head) => head.Kind switch
    {
        GitHeadKind.NotRepository => ForkStrings.NotRepository,
        GitHeadKind.Detached => ForkStrings.Detached,
        GitHeadKind.UnbornBranch => ForkStrings.Unborn,
        _ => string.IsNullOrWhiteSpace(head.Branch) ? ForkStrings.Detached : null,
    };

    private static string WithCommit(string message, string? commitSha) =>
        commitSha is null ? message : message + " " + HandoverStrings.CommitLanded(commitSha);

    private static bool IsExpected(Exception exception) =>
        exception is not (OperationCanceledException or OutOfMemoryException);

    private sealed class ForkContextException(string message, Exception? inner) : Exception(message, inner);
}

/// <summary>
/// Hosted execution of the fork dialog: the dialog collects the <see cref="ForkChoice"/>, calls <see cref="ExecuteAsync"/>
/// and stays open with the returned error until it succeeds or the user cancels.
/// </summary>
internal sealed class ForkDialogModel : INotifyPropertyChanged
{
    private readonly Func<ForkChoice, CancellationToken, Task<string?>> execute;
    private CancellationTokenSource? running;
    private string? error;
    private string branchName;
    private bool commitFirst;
    private HandoverContextMode mode;

    /// <param name="sourceTitle">The source session's title; null when the workspace has no active session.</param>
    /// <param name="sourceBusy">A busy source only allows "fork anyway" (6.5 step 2).</param>
    /// <param name="defaultMode">Summary from a workspace row, full inheritance from a session row; blank without a session.</param>
    public ForkDialogModel(
        string sourceBranch,
        string startCommit,
        string? sourceTitle,
        int uncommittedCount,
        bool sourceBusy,
        bool isSparse,
        string defaultBranchName,
        HandoverContextMode defaultMode,
        Func<ForkChoice, CancellationToken, Task<string?>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        SourceBranch = sourceBranch;
        StartCommit = startCommit;
        SourceTitle = sourceTitle;
        UncommittedCount = uncommittedCount;
        SourceBusy = sourceBusy;
        IsSparse = isSparse;
        branchName = defaultBranchName;
        mode = sourceTitle is null ? HandoverContextMode.Blank : defaultMode;
        commitFirst = OffersCommitFirst;
        this.execute = execute;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SourceBranch { get; }

    public string StartCommit { get; }

    public string? SourceTitle { get; }

    public bool HasSession => SourceTitle is not null;

    public int UncommittedCount { get; }

    public bool HasUncommitted => UncommittedCount > 0;

    public bool SourceBusy { get; }

    public bool IsSparse { get; }

    /// <summary>"Commit first" needs uncommitted changes, a source session to commit in the name of, and that session idle.</summary>
    public bool OffersCommitFirst => HasUncommitted && HasSession && !SourceBusy;

    /// <summary>Only a source session has a context to inherit or summarize.</summary>
    public bool OffersContextMode => HasSession;

    public string BranchName
    {
        get => branchName;
        set => Set(ref branchName, value ?? string.Empty, nameof(BranchName));
    }

    public bool CommitFirst
    {
        get => commitFirst;
        set => Set(ref commitFirst, value && OffersCommitFirst, nameof(CommitFirst));
    }

    public HandoverContextMode Mode
    {
        get => mode;
        set => Set(ref mode, HasSession ? value : HandoverContextMode.Blank, nameof(Mode));
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
            var reason = await execute(new ForkChoice(branchName, commitFirst, mode), source.Token).ConfigureAwait(true);
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
