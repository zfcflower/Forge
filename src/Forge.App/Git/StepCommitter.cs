using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Forge.Abstractions;
using Forge.App.Automations;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.ViewModels;
using Forge.Workspace;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Git;

/// <summary>
/// Commits one finished TODO step into the session's own worktree (forge-app spec, "步骤提交"). It serves both callers of
/// the contract: the <c>todo</c> tool (through <c>AgentHostOptions.StepCommitter</c>, when the agent marks an item done)
/// and the TODO panel (when the user ticks an item or presses "Retry commit").
/// <para>Outcome rules, in order. <see cref="StepCommitOutcome.Skipped"/>: the <c>gitStepCommits</c> preference is off,
/// the session is a sub-agent (it shares its parent's worktree, so the parent's own steps own those changes), the session
/// has no worktree of its own (general <c>scratch/</c> sessions, which live inside the shared general repository and must
/// never be committed into), the worktree directory is gone, or it is not a git repository.
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
/// <para>Failures: when git refuses or the commit otherwise fails, the index is reset to HEAD (a mixed <c>git reset</c>,
/// working tree files untouched), so the rejected changes show up as unstaged again and neither the next retry nor the
/// status display inherits a half-staged index; a reset that fails is traced only.</para>
/// <para>Late cancellation: the HEAD of the worktree is read before the commit, and a cancellation that kills git after
/// the commit object and ref were written (a slow <c>post-commit</c> hook) re-reads HEAD; when it moved, the new sha is
/// persisted and reported as <see cref="StepCommitOutcome.Committed"/>, so a commit that exists is never lost. A
/// cancellation before any commit could exist is rethrown.</para>
/// <para>Concurrency: commits to the same worktree are serialized by a per-path gate held from the status read to the
/// persisted result, so the agent and the user ticking two items at once cannot interleave <c>add</c> and <c>commit</c>
/// (the second one then sees <c>NoChanges</c>). Expected failures are results, never exceptions; only cancellation
/// throws, except the late cancellation above that is recovered into a result. Every git call is asynchronous and runs on
/// whatever thread the caller is on, so callers off the UI thread keep the UI free.</para>
/// <para>Automatic review (wave W17b): a step that was just committed is handed to the session's own agent as a
/// <c>request_review</c> request when the user switched <c>reviewAfterStepCommit</c> on and the review mode is the sub-agent
/// (<c>reviewMode</c> = <c>subagent</c>). The hand-off happens after the per-worktree gate is released and never changes the
/// commit result: an idle session runs the review as a turn at once, and a step commit inside that turn must not wait for a
/// gate this call had already released.</para>
/// </summary>
internal sealed class StepCommitter : IStepCommitter
{
    /// <summary>Longest failure text kept on a TODO row; git's hook output is cut to its tail.</summary>
    internal const int MaxFailureCharacters = 800;

    /// <summary>Subject used when an item has neither a commit subject nor a title.</summary>
    internal const string FallbackSubject = "Complete step";

    /// <summary>Skipped text of a sub-agent session, which never commits into the worktree it inherits from its parent.</summary>
    internal const string SubAgentSkipMessage = "The session is a sub-agent and shares its parent's worktree, so nothing is committed here.";

    private const int ShortCommitLength = 7;

    private readonly GitCli git;
    private readonly ITodoStore store;
    private readonly Func<string, CancellationToken, Task<WorktreeResolution>> resolveWorktree;
    private readonly Func<bool> isEnabled;
    private readonly INotifications? notifications;
    private readonly Func<StepReviewSettings>? reviewSettings;
    private readonly IStepReviewMessenger? messenger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="git">The git wrapper every command goes through.</param>
    /// <param name="store">Where the outcome is recorded.</param>
    /// <param name="resolveWorktree">Session id to the worktree the session may commit into; see <see cref="WorktreeResolution"/>.</param>
    /// <param name="isEnabled">Reads the <c>gitStepCommits</c> preference at the start of every commit.</param>
    /// <param name="notifications">Where a refused commit and a review hand-off that could not be delivered are announced.</param>
    /// <param name="reviewSettings">
    /// Reads the two review preferences at each commit, so a change in the settings section applies to the next one; null
    /// (or <see cref="StepReviewSettings.WantsReview"/> false) means no automatic review.
    /// </param>
    /// <param name="messenger">Hands the review request to the session's agent; null means no automatic review at all.</param>
    public StepCommitter(
        GitCli git,
        ITodoStore store,
        Func<string, CancellationToken, Task<WorktreeResolution>> resolveWorktree,
        Func<bool> isEnabled,
        INotifications? notifications = null,
        Func<StepReviewSettings>? reviewSettings = null,
        IStepReviewMessenger? messenger = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resolveWorktree);
        ArgumentNullException.ThrowIfNull(isEnabled);
        this.git = git;
        this.store = store;
        this.resolveWorktree = resolveWorktree;
        this.isEnabled = isEnabled;
        this.notifications = notifications;
        this.reviewSettings = reviewSettings;
        this.messenger = messenger;
    }

    /// <summary>
    /// The resolver the composition root uses: the session's <see cref="SessionInfo.WorktreePath"/>. A general session has
    /// none; a sub-agent session inherits its parent's path but is reported as a sub-agent, because committing there would
    /// sweep the parent's work into the child's step.
    /// </summary>
    public static Func<string, CancellationToken, Task<WorktreeResolution>> WorktreeOf(ISessionStore sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return async (sessionId, ct) =>
        {
            var session = await sessions.GetSessionAsync(sessionId, ct).ConfigureAwait(false);
            if (session is null)
            {
                return WorktreeResolution.None;
            }

            return string.IsNullOrEmpty(session.ParentSessionId)
                ? WorktreeResolution.Own(session.WorktreePath)
                : WorktreeResolution.SubAgent(session.WorktreePath);
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

        WorktreeResolution worktree;
        try
        {
            worktree = string.IsNullOrWhiteSpace(item.SessionId)
                ? WorktreeResolution.None
                : await resolveWorktree(item.SessionId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return await FailAsync(item, "Could not look up the session's worktree: " + exception.Message).ConfigureAwait(false);
        }

        if (worktree.IsSubAgent)
        {
            return await NothingToCommitAsync(item, StepCommitOutcome.Skipped, SubAgentSkipMessage, ct).ConfigureAwait(false);
        }

        var directory = worktree.Path;
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

        StepCommitResult result;
        var gate = gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            result = await CommitLockedAsync(item, directory, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        // Deliberately outside the gate: an idle session runs the review as a turn at once, and that turn may commit its own
        // steps, which would wait for a gate this call still held.
        return await WithReviewRequestAsync(item, result).ConfigureAwait(false);
    }

    /// <summary>
    /// The handover commit (lifecycle 6.2 step 4): everything uncommitted in <paramref name="directory"/>, untracked
    /// files included, committed with <paramref name="message"/>. It takes the same per-worktree gate as step commits, so
    /// it never interleaves with a "retry commit", and applies the same guards (not a repository / nothing to commit /
    /// an operation in progress / detached HEAD) and the same unstage-on-failure and late-cancellation recovery. It is
    /// the user's explicit choice, so the <c>gitStepCommits</c> preference does not apply, and nothing is persisted on a
    /// TODO row. Expected failures are results; only cancellation before a commit exists throws.
    /// </summary>
    public async Task<StepCommitResult> CommitHandoverAsync(string directory, string message, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ct.ThrowIfCancellationRequested();

        string key;
        try
        {
            directory = Path.GetFullPath(directory);
            key = Path.TrimEndingDirectorySeparator(directory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new StepCommitResult(StepCommitOutcome.Failed, null, "The workspace path is not valid.");
        }

        if (!Directory.Exists(directory))
        {
            return new StepCommitResult(StepCommitOutcome.Failed, null, "The workspace folder no longer exists.");
        }

        var gate = gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CommitHandoverLockedAsync(directory, message.Trim(), ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<StepCommitResult> CommitHandoverLockedAsync(string directory, string message, CancellationToken ct)
    {
        var commitAttempted = false;
        try
        {
            if (!await git.IsRepositoryAsync(directory, ct).ConfigureAwait(false))
            {
                return new StepCommitResult(StepCommitOutcome.Skipped, null, "The workspace is not a git repository.");
            }

            var changes = await git.GetStatusAsync(directory, includeUntracked: true, ct).ConfigureAwait(false);
            if (changes.Count == 0)
            {
                return new StepCommitResult(StepCommitOutcome.NoChanges, null, "No uncommitted changes to commit.");
            }

            var operation = await git.GetConflictStateAsync(directory, ct).ConfigureAwait(false);
            if (operation.InProgress)
            {
                return new StepCommitResult(
                    StepCommitOutcome.Failed,
                    null,
                    $"A {OperationName(operation.Operation)} is in progress in the workspace; finish or abort it first.");
            }

            var branch = await git.GetBranchStatusAsync(directory, ct).ConfigureAwait(false);
            if (branch.IsDetached)
            {
                return new StepCommitResult(StepCommitOutcome.Failed, null, "HEAD is detached; check out a branch for the handover commit to land on.");
            }

            var previousHead = branch.HeadCommit ?? await git.RevParseAsync(directory, "HEAD", ct).ConfigureAwait(false);
            commitAttempted = true;
            try
            {
                var commit = await git.CommitAsync(directory, [], message, ct).ConfigureAwait(false);
                return new StepCommitResult(StepCommitOutcome.Committed, commit.Id, "Committed " + commit.Id);
            }
            catch (OperationCanceledException) when (!string.IsNullOrWhiteSpace(previousHead))
            {
                var head = await ReadHeadAsync(directory).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(head) || string.Equals(head, previousHead, StringComparison.Ordinal))
                {
                    throw;
                }

                return new StepCommitResult(StepCommitOutcome.Committed, head, "Committed " + head);
            }
        }
        catch (GitException exception)
        {
            if (commitAttempted)
            {
                await UnstageAsync(directory).ConfigureAwait(false);
            }

            return new StepCommitResult(StepCommitOutcome.Failed, null, "git commit failed: " + FailureText(exception));
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            if (commitAttempted)
            {
                await UnstageAsync(directory).ConfigureAwait(false);
            }

            return new StepCommitResult(StepCommitOutcome.Failed, null, "The handover could not be committed: " + exception.Message);
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
        // Only a commit attempt can leave staged content behind (git add -A runs inside CommitAsync); a failure earlier in
        // the rule chain must not unstage changes the user had staged before the step.
        var commitAttempted = false;
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

            // Remember where HEAD was: a cancellation that kills git after the commit object and the ref were written (a
            // slow post-commit hook) is recognized by the new HEAD, so the commit that exists is not lost.
            var previousHead = branch.HeadCommit ?? await git.RevParseAsync(directory, "HEAD", ct).ConfigureAwait(false);
            commitAttempted = true;
            GitCommit commit;
            try
            {
                commit = await git.CommitAsync(directory, [], BuildMessage(item), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!string.IsNullOrWhiteSpace(previousHead))
            {
                var head = await ReadHeadAsync(directory).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(head) || string.Equals(head, previousHead, StringComparison.Ordinal))
                {
                    // No commit of this step landed (for example the cancellation hit git add), so the cancellation stands.
                    throw;
                }

                // The commit exists from here on: record it even though the caller's turn is cancelled.
                await PersistAsync(item.Id, head, null, CancellationToken.None).ConfigureAwait(false);
                return new StepCommitResult(StepCommitOutcome.Committed, head, CommittedMessage(item, head, null));
            }

            // The commit exists from here on: record it even if the caller's turn is cancelled a moment later.
            await PersistAsync(item.Id, commit.Id, null, CancellationToken.None).ConfigureAwait(false);
            return new StepCommitResult(StepCommitOutcome.Committed, commit.Id, CommittedMessage(item, commit.Id, FirstLine(commit.Subject)));
        }
        catch (GitException exception)
        {
            if (commitAttempted)
            {
                await UnstageAsync(directory).ConfigureAwait(false);
            }

            return await FailAsync(item, "git commit failed: " + FailureText(exception)).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            if (commitAttempted)
            {
                await UnstageAsync(directory).ConfigureAwait(false);
            }

            return await FailAsync(item, "The step could not be committed: " + exception.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks the session's own agent to review a step that was just committed, when the user switched
    /// <c>reviewAfterStepCommit</c> on and the review mode is the sub-agent (wave W17b). The message is handed over through
    /// the same W11 contract the checks panel and a failed Mermaid block use: a turn when the session is idle, the composer's
    /// draft when it is busy, never a second turn behind the user's back.
    /// <para>Nothing here can change the commit result. A hand-off that throws is traced and announced once and the commit
    /// still returns as committed; a session that cannot take the message (its tab closed) is silently skipped, because that
    /// is not an error of the commit.</para>
    /// </summary>
    private async Task<StepCommitResult> WithReviewRequestAsync(TodoItem item, StepCommitResult result)
    {
        if (result.Outcome != StepCommitOutcome.Committed
            || messenger is null
            || reviewSettings is null
            || string.IsNullOrWhiteSpace(item.SessionId)
            || !reviewSettings().WantsReview)
        {
            return result;
        }

        try
        {
            // No cancellation token: the commit is already recorded, and a turn that is cancelled a moment later must not be
            // the reason the review it asked for never arrives.
            await messenger
                .HandAsync(item.SessionId, BuildReviewRequest(item), new LocText(GitStrings.HandOffReviewOrigin))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            AppLogs.StepCommit.ZLogWarning($"could not hand the review of step '{item.Id}' to its session: {exception.Message}");
            notifications.Notify(
                NotificationLevel.Warning,
                new LocText(GitStrings.NotificationReviewHandOffFailed),
                LocText.Literal(exception.Message),
                "step-review:" + item.Id);
        }

        return result;
    }

    /// <summary>
    /// The message that asks the session's agent for the step's review. Model input, not UI text, so it is English and lives
    /// here beside the trigger (the same rule as <c>MermaidFixMessage</c> and <c>PrChecksFixMessage</c>): it names the step
    /// id, because that is what makes <c>request_review</c> read exactly that step's own commit, and the one instruction that
    /// must not get lost - a request changes verdict is not a finished step.
    /// </summary>
    internal static string BuildReviewRequest(TodoItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var title = FirstLine(item.CommitSubject) ?? FirstLine(item.Title);
        return new StringBuilder(320)
            .Append("A checklist step was just committed. Call request_review with step_id \"")
            .Append(item.Id)
            .Append('"')
            .Append(title is null ? string.Empty : " (step: " + title + ")")
            .Append(
                " to review that commit, then act on the verdict before you go on: while it is request changes, fix the " +
                "findings, commit the fix, and call request_review again at the new HEAD; do not report the step as done " +
                "while a blocking finding stands.")
            .ToString();
    }

    /// <summary>The result text of a commit: short sha and the subject (the item title when git's subject was not read).</summary>
    private static string CommittedMessage(TodoItem item, string commitId, string? subject)
    {
        var shortId = commitId.Length > ShortCommitLength ? commitId[..ShortCommitLength] : commitId;
        return $"Committed {shortId}: {subject ?? FirstLine(item.Title) ?? FallbackSubject}";
    }

    /// <summary>
    /// HEAD after a cancelled commit, read without the cancelled token; null when it cannot be read (the caller then
    /// treats the cancellation as having landed before the commit).
    /// </summary>
    private async Task<string?> ReadHeadAsync(string directory)
    {
        try
        {
            return await git.RevParseAsync(directory, "HEAD", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            AppLogs.StepCommit.ZLogWarning($"could not re-read HEAD in '{directory}' after a cancelled commit: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// Puts the index back to HEAD after a failed commit (a mixed <c>git reset</c>: working tree files stay as they are),
    /// so the rejected changes are unstaged again. Best effort: a failure is only traced.
    /// </summary>
    private async Task UnstageAsync(string directory)
    {
        try
        {
            var result = await git.RunAsync(directory, ["reset", "-q"], CancellationToken.None).ConfigureAwait(false);
            if (!result.Success)
            {
                AppLogs.StepCommit.ZLogWarning($"could not unstage '{directory}' (git reset -q exited {result.ExitCode}): {result.StandardError.Trim()}");
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            AppLogs.StepCommit.ZLogWarning($"could not unstage '{directory}' after a failed commit: {exception.Message}");
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
        // The TODO row carries the reason, but a refused commit is exactly the kind of thing the user misses while
        // looking at another session, so it is announced too.
        notifications.Notify(
            NotificationLevel.Error,
            NotificationsStrings.StepCommitFailedTitle,
            LocText.Literal(reason),
            "step-commit:" + item.Id);
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
            AppLogs.StepCommit.ZLogWarning($"could not re-read the checklist of '{item.SessionId}': {exception.Message}");
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
            AppLogs.StepCommit.ZLogWarning($"could not record the commit result of '{id}': {exception.Message}");
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

/// <summary>
/// The two review preferences of wave W17b, read at each step commit (the composition root hands in
/// <see cref="Live"/>, so a change in the <c>git</c> settings section applies to the next commit without a restart).
/// </summary>
/// <param name="AfterStepCommit">The <c>reviewAfterStepCommit</c> switch; off by default.</param>
/// <param name="SubAgentMode">
/// The <c>reviewMode</c> is <c>subagent</c> (<paramref name="AfterStepCommit"/> is the only switch that can make a
/// commit ask for a review); the <c>manual</c> mode never triggers one.
/// </param>
internal readonly record struct StepReviewSettings(bool AfterStepCommit, bool SubAgentMode)
{
    /// <summary>The <c>reviewMode</c> token of the default mode: one read-only Reviewer pass per review.</summary>
    internal const string SubAgentModeToken = "subagent";

    /// <summary>The <c>reviewMode</c> token of the mode that only reviews by hand.</summary>
    internal const string ManualModeToken = "manual";

    /// <summary>A committed step should be handed to a review: the switch is on and the mode is the sub-agent.</summary>
    public bool WantsReview => AfterStepCommit && SubAgentMode;

    /// <summary>The live reader the composition root hands to <see cref="StepCommitter"/>.</summary>
    public static Func<StepReviewSettings> Live(Forge.App.Preferences.AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return () => new StepReviewSettings(
            preferences.ReviewAfterStepCommit,
            !string.Equals(preferences.ReviewMode, ManualModeToken, StringComparison.Ordinal));
    }
}

/// <summary>
/// Delivers one prepared message to one session's own agent (the W11 hand-off contract). The implementation is the window's,
/// because it needs the live session view; <see cref="StepCommitter"/> only sees this seam.
/// </summary>
internal interface IStepReviewMessenger
{
    /// <summary>
    /// True when a turn ran, false when the message was queued in the session's composer or the session cannot take it.
    /// </summary>
    Task<bool> HandAsync(string sessionId, string message, LocText origin);
}

/// <summary>
/// The product <see cref="IStepReviewMessenger"/>: the live session view comes from the composition's hook and the hand-off
/// runs on the UI thread, because a session view model touches bound state. A session that is not open (its tab was closed)
/// or that can no longer take messages is skipped with false.
/// </summary>
internal sealed class StepReviewMessenger(Func<string, IAgentMessageHost?> sessions, IUiThread ui) : IStepReviewMessenger
{
    public Task<bool> HandAsync(string sessionId, string message, LocText origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return ui.InvokeAsync(() =>
            sessions(sessionId) is { CanHandOff: true } host
                ? host.HandMessageAsync(message, origin)
                : Task.FromResult(false));
    }
}

/// <summary>
/// What the worktree resolver found for a session. <see cref="Path"/> is the session's own worktree (null or blank when it
/// has none); <see cref="IsSubAgent"/> marks a sub-agent session, which inherits its parent's worktree and never commits
/// there - the parent's own steps own those changes.
/// </summary>
internal readonly record struct WorktreeResolution(string? Path, bool IsSubAgent)
{
    /// <summary>No worktree of its own (a general <c>scratch/</c> session or an unknown session).</summary>
    public static WorktreeResolution None => default;

    /// <summary>A session with its own worktree (or none when <paramref name="path"/> is null or blank).</summary>
    public static WorktreeResolution Own(string? path) => new(path, false);

    /// <summary>A sub-agent session and the parent's worktree path it carries.</summary>
    public static WorktreeResolution SubAgent(string? inheritedPath) => new(inheritedPath, true);
}
