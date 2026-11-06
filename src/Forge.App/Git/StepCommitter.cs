using System.Collections.Concurrent;
using System.Diagnostics;
using Forge.Abstractions;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>
/// Commits one finished TODO step into the session's own worktree (forge-app spec, "步骤提交"). It serves both callers of
/// the contract: the <c>todo</c> tool (through <c>AgentHostOptions.StepCommitter</c>, when the agent marks an item done)
/// and the TODO panel (when the user ticks an item or presses "Retry commit").
/// <para>Outcome rules, in order. <see cref="StepCommitOutcome.Skipped"/>: the <c>gitStepCommits</c> preference is off,
/// the session has no worktree of its own (general <c>scratch/</c> sessions, which live inside the shared general
/// repository and must never be committed into), the worktree directory is gone, or it is not a git repository.
/// <see cref="StepCommitOutcome.NoChanges"/>: <c>git status</c> (untracked files included) is empty, so no empty commit is
/// made. <see cref="StepCommitOutcome.Failed"/>: a merge, rebase, cherry-pick or revert is in progress, HEAD is detached,
/// or git refused the commit (a hook failed, no identity, ...); the reason is the tail of git's stderr. Otherwise
/// <see cref="StepCommitOutcome.Committed"/>: everything uncommitted (<c>add -A</c>, so untracked files too) is committed
/// with the item's commit subject (title when absent; first non-blank line, trimmed) and, after a blank line, its summary.
/// The verification text is never part of the message.</para>
/// <para>Persistence: the outcome is stored through <see cref="ITodoStore.SetCommitResultAsync"/> before returning -
/// <c>(id, sha, null)</c> on a commit, <c>(id, null, reason)</c> on a failure. <c>NoChanges</c> and <c>Skipped</c> only
/// clear an earlier failure (<c>(id, null, null)</c>), and only when the stored item still carries one, so a recorded
/// commit id is never wiped. A store that cannot persist is traced and does not change the returned result.</para>
/// <para>Concurrency: commits to the same worktree are serialized by a per-path gate held from the status read to the
/// persisted result, so the agent and the user ticking two items at once cannot interleave <c>add</c> and <c>commit</c>
/// (the second one then sees <c>NoChanges</c>). Expected failures are results, never exceptions; only cancellation
/// throws. Every git call is asynchronous and runs on whatever thread the caller is on, so callers off the UI thread
/// keep the UI free.</para>
/// </summary>
internal sealed class StepCommitter : IStepCommitter
{
    /// <summary>Longest failure text kept on a TODO row; git's hook output is cut to its tail.</summary>
    internal const int MaxFailureCharacters = 800;

    /// <summary>Subject used when an item has neither a commit subject nor a title.</summary>
    internal const string FallbackSubject = "Complete step";

    private const int ShortCommitLength = 7;

    private readonly GitCli git;
    private readonly ITodoStore store;
    private readonly Func<string, CancellationToken, Task<string?>> resolveWorktree;
    private readonly Func<bool> isEnabled;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="git">The git wrapper every command goes through.</param>
    /// <param name="store">Where the outcome is recorded.</param>
    /// <param name="resolveWorktree">Session id to the session's own worktree path; null or empty means the session has none.</param>
    /// <param name="isEnabled">Reads the <c>gitStepCommits</c> preference at the start of every commit.</param>
    public StepCommitter(
        GitCli git,
        ITodoStore store,
        Func<string, CancellationToken, Task<string?>> resolveWorktree,
        Func<bool> isEnabled)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resolveWorktree);
        ArgumentNullException.ThrowIfNull(isEnabled);
        this.git = git;
        this.store = store;
        this.resolveWorktree = resolveWorktree;
        this.isEnabled = isEnabled;
    }

    /// <summary>
    /// The resolver the composition root uses: the session's <see cref="SessionInfo.WorktreePath"/>. A sub-agent session
    /// inherits its parent's worktree path, a general session has none.
    /// </summary>
    public static Func<string, CancellationToken, Task<string?>> WorktreeOf(ISessionStore sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return async (sessionId, ct) =>
        {
            var session = await sessions.GetSessionAsync(sessionId, ct).ConfigureAwait(false);
            return session?.WorktreePath;
        };
    }

    public async Task<StepCommitResult> CommitStepAsync(TodoItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();

        if (!isEnabled())
        {
            return await NothingToCommitAsync(item, StepCommitOutcome.Skipped, "Step commits are switched off.", ct).ConfigureAwait(false);
        }

        string? directory;
        try
        {
            directory = string.IsNullOrWhiteSpace(item.SessionId)
                ? null
                : await resolveWorktree(item.SessionId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return await FailAsync(item, "Could not look up the session's worktree: " + exception.Message).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            return await NothingToCommitAsync(
                    item,
                    StepCommitOutcome.Skipped,
                    "The session has no worktree of its own, so nothing was committed.",
                    ct)
                .ConfigureAwait(false);
        }

        string key;
        try
        {
            directory = Path.GetFullPath(directory);
            key = Path.TrimEndingDirectorySeparator(directory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return await NothingToCommitAsync(item, StepCommitOutcome.Skipped, "The session's worktree path is not valid.", ct).ConfigureAwait(false);
        }

        if (!Directory.Exists(directory))
        {
            return await NothingToCommitAsync(item, StepCommitOutcome.Skipped, "The session's worktree no longer exists.", ct).ConfigureAwait(false);
        }

        var gate = gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CommitLockedAsync(item, directory, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The commit message of a step: subject, and after a blank line the summary. Verification is not part of it.</summary>
    internal static string BuildMessage(TodoItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var subject = FirstLine(item.CommitSubject) ?? FirstLine(item.Title) ?? FallbackSubject;
        if (string.IsNullOrWhiteSpace(item.Summary))
        {
            return subject;
        }

        var body = item.Summary.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return subject + "\n\n" + body;
    }

    /// <summary>The first non-blank line of <paramref name="text"/>, trimmed; null when there is none.</summary>
    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }

    private async Task<StepCommitResult> CommitLockedAsync(TodoItem item, string directory, CancellationToken ct)
    {
        try
        {
            if (!await git.IsRepositoryAsync(directory, ct).ConfigureAwait(false))
            {
                return await NothingToCommitAsync(
                        item,
                        StepCommitOutcome.Skipped,
                        "The session's worktree is not a git repository.",
                        ct)
                    .ConfigureAwait(false);
            }

            var changes = await git.GetStatusAsync(directory, includeUntracked: true, ct).ConfigureAwait(false);
            if (changes.Count == 0)
            {
                return await NothingToCommitAsync(item, StepCommitOutcome.NoChanges, "No uncommitted changes to commit.", ct).ConfigureAwait(false);
            }

            var operation = await git.GetConflictStateAsync(directory, ct).ConfigureAwait(false);
            if (operation.InProgress)
            {
                return await FailAsync(
                        item,
                        $"A {OperationName(operation.Operation)} is in progress in the worktree; finish or abort it, then retry the commit.")
                    .ConfigureAwait(false);
            }

            var branch = await git.GetBranchStatusAsync(directory, ct).ConfigureAwait(false);
            if (branch.IsDetached)
            {
                return await FailAsync(item, "HEAD is detached; check out a branch for the step to land on, then retry the commit.").ConfigureAwait(false);
            }

            var commit = await git.CommitAsync(directory, [], BuildMessage(item), ct).ConfigureAwait(false);

            // The commit exists from here on: record it even if the caller's turn is cancelled a moment later.
            await PersistAsync(item.Id, commit.Id, null, CancellationToken.None).ConfigureAwait(false);
            var shortId = commit.Id.Length > ShortCommitLength ? commit.Id[..ShortCommitLength] : commit.Id;
            return new StepCommitResult(StepCommitOutcome.Committed, commit.Id, $"Committed {shortId}: {FirstLine(commit.Subject) ?? FirstLine(item.Title) ?? FallbackSubject}");
        }
        catch (GitException exception)
        {
            return await FailAsync(item, "git commit failed: " + FailureText(exception)).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return await FailAsync(item, "The step could not be committed: " + exception.Message).ConfigureAwait(false);
        }
    }

    /// <summary><see cref="StepCommitOutcome.NoChanges"/> or <see cref="StepCommitOutcome.Skipped"/>: clears a recorded failure, nothing else.</summary>
    private async Task<StepCommitResult> NothingToCommitAsync(TodoItem item, StepCommitOutcome outcome, string message, CancellationToken ct)
    {
        if (await HasRecordedFailureAsync(item, ct).ConfigureAwait(false))
        {
            await PersistAsync(item.Id, null, null, ct).ConfigureAwait(false);
        }

        return new StepCommitResult(outcome, null, message);
    }

    private async Task<StepCommitResult> FailAsync(TodoItem item, string reason)
    {
        await PersistAsync(item.Id, null, reason, CancellationToken.None).ConfigureAwait(false);
        return new StepCommitResult(StepCommitOutcome.Failed, null, reason);
    }

    /// <summary>
    /// Whether the stored item still carries a failure. The given snapshot can be stale (a retry clicked while the agent
    /// committed the same step), so the store is read again; if it cannot be read, the snapshot decides.
    /// </summary>
    private async Task<bool> HasRecordedFailureAsync(TodoItem item, CancellationToken ct)
    {
        try
        {
            var items = await store.ListAsync(item.SessionId, ct).ConfigureAwait(false);
            foreach (var stored in items)
            {
                if (string.Equals(stored.Id, item.Id, StringComparison.Ordinal))
                {
                    return stored.CommitError is not null;
                }
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            Trace.WriteLine($"[step-commit] could not re-read the checklist of '{item.SessionId}': {exception.Message}");
        }

        return item.CommitError is not null;
    }

    private async Task PersistAsync(string id, string? commitId, string? commitError, CancellationToken ct)
    {
        try
        {
            await store.SetCommitResultAsync(id, commitId, commitError, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            Trace.WriteLine($"[step-commit] could not record the commit result of '{id}': {exception.Message}");
        }
    }

    /// <summary>The tail of git's stderr (a hook prints its verdict last), bounded; falls back to the exit code.</summary>
    private static string FailureText(GitException exception)
    {
        var text = exception.StandardError.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (text.Length == 0)
        {
            return exception.Message.EndsWith("timed out.", StringComparison.Ordinal)
                ? "the command timed out."
                : $"git exited with code {exception.ExitCode} and no message.";
        }

        return text.Length <= MaxFailureCharacters ? text : "…" + text[^MaxFailureCharacters..].TrimStart();
    }

    private static string OperationName(GitOperationKind operation) => operation switch
    {
        GitOperationKind.Rebase => "rebase",
        GitOperationKind.Merge => "merge",
        GitOperationKind.CherryPick => "cherry-pick",
        GitOperationKind.Revert => "revert",
        _ => "git operation",
    };

    /// <summary>Everything except cancellation and process-fatal errors is an expected failure of the environment.</summary>
    private static bool IsExpected(Exception exception) =>
        exception is not (OperationCanceledException or OutOfMemoryException);
}
