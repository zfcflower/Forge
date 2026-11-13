using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Forge.Abstractions;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.Core.Agents;
using Forge.Workspace;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Lifecycle;

/// <summary>What happens to the old session's uncommitted changes (6.2 step 4).</summary>
internal enum UncommittedChoice
{
    /// <summary>A handover commit in the old session's name (the default).</summary>
    CommitAsOld,

    /// <summary>No commit: the next step commit of the session that takes over picks them up.</summary>
    CarryToNew,
}

/// <summary>How a new session's context begins (8.1); only a "new session" handover asks.</summary>
internal enum HandoverContextMode
{
    Full,
    Summary,
    Blank,
}

/// <summary>The two answers of the handover dialog.</summary>
internal sealed record HandoverChoice(UncommittedChoice Uncommitted, HandoverContextMode Mode);

/// <summary>
/// Which session takes over: a new one built from <see cref="Create"/> ("new session (replaces X)"), or the existing
/// <see cref="ResumeSessionId"/> ("continue this session", restore).
/// </summary>
internal sealed record HandoverIntent
{
    private HandoverIntent(SessionCreate? create, string? resumeSessionId)
    {
        Create = create;
        ResumeSessionId = resumeSessionId;
    }

    public SessionCreate? Create { get; }

    public string? ResumeSessionId { get; }

    public bool IsNewSession => Create is not null;

    public static HandoverIntent NewSession(SessionCreate create)
    {
        ArgumentNullException.ThrowIfNull(create);
        return new HandoverIntent(create, null);
    }

    public static HandoverIntent Resume(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return new HandoverIntent(null, sessionId);
    }
}

/// <summary>
/// One execution of the handover: the session that is now Active on success, otherwise the reason it stopped (shown in
/// the dialog). <see cref="CommitSha"/> is the handover commit when one was made, on success and failure alike;
/// <see cref="Warning"/> is a failure after the switch that does not undo it (the review copy).
/// </summary>
internal sealed record HandoverAttempt(SessionInfo? Session, string? Error, string? CommitSha, string? Warning)
{
    public bool Succeeded => Session is not null;

    public static HandoverAttempt Failed(string error, string? commitSha) => new(null, error, commitSha, null);
}

/// <summary>The git half of a handover; <see cref="HandoverGit"/> is the real one, tests substitute their own.</summary>
internal interface IHandoverGit
{
    /// <summary>Uncommitted paths, untracked included; null when the directory is not a git working tree.</summary>
    Task<IReadOnlyList<string>?> UncommittedAsync(string directory, CancellationToken ct);

    /// <summary>The HEAD commit; null on an unborn branch.</summary>
    Task<string?> HeadAsync(string directory, CancellationToken ct);

    /// <summary>The handover commit (through the step committer's per-worktree gate).</summary>
    Task<StepCommitResult> CommitAsync(string directory, string message, CancellationToken ct);
}

/// <summary>The real <see cref="IHandoverGit"/>: status and HEAD through <see cref="GitCli"/>, the commit through <see cref="StepCommitter"/>.</summary>
internal sealed class HandoverGit(GitCli git, StepCommitter committer) : IHandoverGit
{
    public async Task<IReadOnlyList<string>?> UncommittedAsync(string directory, CancellationToken ct)
    {
        if (!Directory.Exists(directory) || !await git.IsRepositoryAsync(directory, ct).ConfigureAwait(false))
        {
            return null;
        }

        var status = await git.GetStatusAsync(directory, includeUntracked: true, ct).ConfigureAwait(false);
        return [.. status.Select(static entry => entry.Path)];
    }

    public Task<string?> HeadAsync(string directory, CancellationToken ct) => git.RevParseAsync(directory, "HEAD", ct);

    public Task<StepCommitResult> CommitAsync(string directory, string message, CancellationToken ct) =>
        committer.CommitHandoverAsync(directory, message, ct);
}

/// <summary>
/// The handover sequence after the dialog's answers (6.2 steps 4-7): handover commit or carry, then the context of a
/// new session (summary / inheritance note), then the single-transaction switch, then the review copy. Each step that
/// fails stops the sequence with a user-facing reason; a commit that landed is never undone and is named in every later
/// failure, so a retry (the tree is clean by then, so step 4 is skipped) picks up where it stopped. Runs off the UI
/// thread; it touches no view model.
/// </summary>
internal sealed class HandoverFlow
{
    /// <summary>Prefix of the handover commit subject; the old session's title follows.</summary>
    public const string CommitPrefix = "handover: ";

    private const int MaxListedChanges = 200;

    /// <summary>The head named in the inheritance note of an unborn branch (model input, not UI text).</summary>
    private const string UnbornHead = "(no commit yet)";

    private readonly ISessionCatalog catalog;
    private readonly ISessionStore store;
    private readonly IHandoverGit git;
    private readonly Func<IReadOnlyList<ChatMessage>, string?, CancellationToken, Task<string>> summarize;
    private readonly Func<string, string, string, CancellationToken, Task>? copyReviews;

    /// <param name="summarize">History and working-tree description to the handover summary message text (<see cref="HandoverSummary.GenerateAsync"/>).</param>
    /// <param name="copyReviews">Workspace, old and new session id: copies the review marks; null when no review store exists.</param>
    public HandoverFlow(
        ISessionCatalog catalog,
        ISessionStore store,
        IHandoverGit git,
        Func<IReadOnlyList<ChatMessage>, string?, CancellationToken, Task<string>> summarize,
        Func<string, string, string, CancellationToken, Task>? copyReviews)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(summarize);
        this.catalog = catalog;
        this.store = store;
        this.git = git;
        this.summarize = summarize;
        this.copyReviews = copyReviews;
    }

    /// <summary>The directory the session works in: its worktree, else its workspace (a root checkout, a plain folder).</summary>
    public static string DirectoryOf(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.WorktreePath ?? session.WorkspacePath;
    }

    /// <summary>
    /// Runs steps 4-7. Cancellation of <paramref name="ct"/> stops the summary generation (reported as a failure, the
    /// commit stays); the switch itself runs to completion once it has started.
    /// </summary>
    public async Task<HandoverAttempt> ExecuteAsync(SessionInfo old, HandoverIntent intent, HandoverChoice choice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(choice);
        var directory = DirectoryOf(old);

        // Step 4: the working tree as it is now (a retry after a landed commit finds it clean and skips the commit).
        IReadOnlyList<string>? changes;
        try
        {
            changes = await git.UncommittedAsync(directory, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return HandoverAttempt.Failed(HandoverStrings.StatusFailed(exception.Message), null);
        }
        catch (OperationCanceledException)
        {
            return HandoverAttempt.Failed(HandoverStrings.Cancelled, null);
        }

        var dirty = changes is { Count: > 0 };
        string? commitSha = null;
        if (dirty && choice.Uncommitted == UncommittedChoice.CommitAsOld)
        {
            StepCommitResult committed;
            try
            {
                committed = await git.CommitAsync(directory, CommitPrefix + old.Title, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return HandoverAttempt.Failed(HandoverStrings.Cancelled, null);
            }

            switch (committed.Outcome)
            {
                case StepCommitOutcome.Committed:
                    commitSha = committed.CommitId;
                    break;
                case StepCommitOutcome.NoChanges:
                    break;
                default:
                    return HandoverAttempt.Failed(HandoverStrings.CommitFailed(committed.Message), null);
            }
        }

        var carried = dirty && choice.Uncommitted == UncommittedChoice.CarryToNew;

        // Step 5: the new session's context, built after the commit so the summary sees the tree as it is now.
        HandoverTarget target;
        if (intent.Create is { } create)
        {
            var built = await BuildNewSessionAsync(old, create, choice.Mode, directory, carried ? changes! : null, commitSha, ct)
                .ConfigureAwait(false);
            if (built.Error is not null)
            {
                return HandoverAttempt.Failed(built.Error, commitSha);
            }

            target = built.Target!;
        }
        else
        {
            target = new HandoverTarget.Resume(intent.ResumeSessionId!);
        }

        // Step 6: the switch, one transaction. Not cancellable once started: a half-observed switch would be worse.
        SessionInfo session;
        try
        {
            session = await catalog.HandoverAsync(old.Id, target, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return HandoverAttempt.Failed(
                commitSha is null
                    ? HandoverStrings.SwitchFailed(exception.Message)
                    : HandoverStrings.SwitchFailedAfterCommit(commitSha, exception.Message),
                commitSha);
        }

        // Step 7: the review marks follow a new session; a failure is reported, the switch stands.
        string? warning = null;
        if (intent.IsNewSession && copyReviews is not null)
        {
            try
            {
                await copyReviews(directory, old.Id, session.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                AppLogs.Handover.ZLogWarning($"review copy failed: {exception.Message}");
                warning = HandoverStrings.ReviewCopyFailed(exception.Message);
            }
        }

        return new HandoverAttempt(session, null, commitSha, warning);
    }

    private async Task<(HandoverTarget? Target, string? Error)> BuildNewSessionAsync(
        SessionInfo old,
        SessionCreate create,
        HandoverContextMode mode,
        string directory,
        IReadOnlyList<string>? carriedChanges,
        string? commitSha,
        CancellationToken ct)
    {
        switch (mode)
        {
            case HandoverContextMode.Blank:
                return (new HandoverTarget.NewSession(create with { Origin = SessionOrigin.Blank }), null);

            case HandoverContextMode.Full:
            {
                string? head;
                long upTo;
                try
                {
                    head = await git.HeadAsync(directory, ct).ConfigureAwait(false);
                    upTo = await store.LastSeqAsync(old.Id, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return (null, WithCommit(HandoverStrings.Cancelled, commitSha));
                }
                catch (Exception exception) when (IsExpected(exception))
                {
                    return (null, WithCommit(HandoverStrings.ContextFailed(exception.Message), commitSha));
                }

                var note = InheritanceNote.Message(
                    string.IsNullOrWhiteSpace(head) ? UnbornHead : head,
                    commitSha,
                    carriedChanges is not null);
                return (
                    new HandoverTarget.NewSession(create with
                    {
                        Origin = SessionOrigin.Full,
                        InheritsFromSessionId = old.Id,
                        InheritsUpToSeq = upTo,
                    })
                    {
                        InitialMessages = [note],
                        CopyTodosAndPlans = true,
                    },
                    null);
            }

            default:
            {
                string summary;
                try
                {
                    var history = new List<ChatMessage>();
                    await foreach (var message in store.ReadAsync(old.Id, 0, null, ct).ConfigureAwait(false))
                    {
                        history.Add(message.Message);
                    }

                    summary = await summarize(history, DescribeTree(carriedChanges, commitSha), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return (null, WithCommit(HandoverStrings.SummaryCancelled, commitSha));
                }
                catch (Exception exception) when (IsExpected(exception))
                {
                    return (null, WithCommit(HandoverStrings.SummaryFailed(exception.Message), commitSha));
                }

                return (
                    new HandoverTarget.NewSession(create with { Origin = SessionOrigin.Summary })
                    {
                        InitialMessages = [ChatMessage.User(summary)],
                        CopyTodosAndPlans = true,
                    },
                    null);
            }
        }
    }

    /// <summary>The working-tree description the summary is generated from (model input, not UI text).</summary>
    internal static string? DescribeTree(IReadOnlyList<string>? carriedChanges, string? commitSha)
    {
        var builder = new StringBuilder();
        if (commitSha is not null)
        {
            builder.Append("The previous session's changes were committed as ").Append(commitSha).Append(" (handover commit).\n");
        }

        if (carriedChanges is { Count: > 0 })
        {
            builder.Append("Uncommitted changes carried over into the new session's working tree:\n");
            foreach (var path in carriedChanges.Take(MaxListedChanges))
            {
                builder.Append("- ").Append(path).Append('\n');
            }

            if (carriedChanges.Count > MaxListedChanges)
            {
                builder.Append("- … ").Append(carriedChanges.Count - MaxListedChanges).Append(" more\n");
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string WithCommit(string message, string? commitSha) =>
        commitSha is null ? message : message + " " + HandoverStrings.CommitLanded(commitSha);

    private static bool IsExpected(Exception exception) =>
        exception is not (OperationCanceledException or OutOfMemoryException);
}

/// <summary>
/// Hosted execution of the handover dialog: the dialog collects the <see cref="HandoverChoice"/>, calls
/// <see cref="ExecuteAsync"/> and stays open with the returned error until it succeeds or the user cancels. While it
/// runs, <see cref="CancelRunning"/> stops what can be stopped (the summary generation).
/// </summary>
internal sealed class HandoverDialogModel : INotifyPropertyChanged
{
    private readonly Func<HandoverChoice, CancellationToken, Task<string?>> execute;
    private CancellationTokenSource? running;
    private string? error;
    private UncommittedChoice uncommitted = UncommittedChoice.CommitAsOld;
    private HandoverContextMode mode = HandoverContextMode.Summary;

    /// <param name="execute">Runs the handover; null = succeeded, otherwise the reason to show.</param>
    public HandoverDialogModel(
        string oldTitle,
        int uncommittedCount,
        bool isRootCheckout,
        bool offersContextMode,
        Func<HandoverChoice, CancellationToken, Task<string?>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        OldTitle = oldTitle;
        UncommittedCount = uncommittedCount;
        IsRootCheckout = isRootCheckout;
        OffersContextMode = offersContextMode;
        this.execute = execute;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string OldTitle { get; }

    /// <summary>Uncommitted paths (untracked included); the commit / carry choice only shows when it is above zero.</summary>
    public int UncommittedCount { get; }

    public bool HasUncommitted => UncommittedCount > 0;

    /// <summary>Carrying changes in the repository's own checkout also sweeps manual edits into the next step commit.</summary>
    public bool IsRootCheckout { get; }

    /// <summary>Only a new session has a context to choose; continue / restore resume the session itself.</summary>
    public bool OffersContextMode { get; }

    public UncommittedChoice Uncommitted
    {
        get => uncommitted;
        set => Set(ref uncommitted, value, nameof(Uncommitted));
    }

    public HandoverContextMode Mode
    {
        get => mode;
        set => Set(ref mode, value, nameof(Mode));
    }

    public bool IsRunning => running is not null;

    public string? Error
    {
        get => error;
        private set => Set(ref error, value, nameof(Error));
    }

    public bool Succeeded { get; private set; }

    /// <summary>One attempt; true when the handover went through (the dialog then closes).</summary>
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
            var reason = await execute(new HandoverChoice(uncommitted, mode), source.Token).ConfigureAwait(true);
            Succeeded = reason is null;
            Error = reason;
            return Succeeded;
        }
        catch (OperationCanceledException)
        {
            Error = HandoverStrings.Cancelled;
            return false;
        }
        finally
        {
            running = null;
            Raise(nameof(IsRunning));
        }
    }

    /// <summary>Stops a running attempt (the summary generation); the dialog stays open with the result.</summary>
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
