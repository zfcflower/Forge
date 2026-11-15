using Forge.Core.Tools;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>
/// Answers the step completion gate (<see cref="IStepChangeProbe"/>) from the app's git layer: whether the session's own
/// worktree holds uncommitted changes right now. The session-to-worktree resolution is
/// <see cref="StepCommitter.WorktreeOf"/>, the same one step commits use, so the gate and the commit always agree on which
/// directory a step belongs to.
/// <para>Answers. <see cref="StepChangeState.NotApplicable"/>: nothing of this step's own can be committed there - a general
/// <c>scratch/</c> session (no worktree of its own), a sub-agent session (it shares its parent's worktree, so the parent's
/// steps own those changes), a worktree directory that is gone, or a directory that is not a git repository.
/// <see cref="StepChangeState.Clean"/>: <c>git status</c> is empty, untracked files included. <see cref="StepChangeState.Dirty"/>:
/// it is not. <see cref="StepChangeState.Unknown"/>: the worktree could not be resolved or git failed; the gate then refuses
/// the step (fail closed). Expected failures are answers, never exceptions; only cancellation throws, and it propagates.</para>
/// </summary>
internal sealed class GitStepChangeProbe : IStepChangeProbe
{
    private readonly GitCli git;
    private readonly Func<string, CancellationToken, Task<WorktreeResolution>> resolveWorktree;

    /// <param name="git">The git wrapper every command goes through.</param>
    /// <param name="resolveWorktree">Session id to the worktree the session's steps live in; see <see cref="StepCommitter.WorktreeOf"/>.</param>
    public GitStepChangeProbe(GitCli git, Func<string, CancellationToken, Task<WorktreeResolution>> resolveWorktree)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(resolveWorktree);
        this.git = git;
        this.resolveWorktree = resolveWorktree;
    }

    /// <param name="sessionId">The session whose worktree the step belongs to.</param>
    /// <param name="workingDirectory">
    /// The calling session's own directory. Not read here: the worktree comes from the session itself, and a session without one
    /// is not applicable anyway (a general session lives in the shared general repository, which its steps never commit into).
    /// </param>
    /// <param name="ct">Cancels the git call; a cancellation throws instead of answering.</param>
    public async Task<StepChangeProbe> ProbeAsync(string sessionId, string workingDirectory, CancellationToken ct)
    {
        WorktreeResolution worktree;
        try
        {
            worktree = string.IsNullOrWhiteSpace(sessionId)
                ? WorktreeResolution.None
                : await resolveWorktree(sessionId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return new StepChangeProbe(StepChangeState.Unknown, "Could not look up the session's worktree: " + exception.Message);
        }

        if (worktree.IsSubAgent)
        {
            return new StepChangeProbe(StepChangeState.NotApplicable, StepCommitter.SubAgentSkipMessage);
        }

        var directory = worktree.Path;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new StepChangeProbe(StepChangeState.NotApplicable, "The session has no worktree of its own.");
        }

        try
        {
            directory = Path.GetFullPath(directory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new StepChangeProbe(StepChangeState.Unknown, "The session's worktree path is not valid: " + exception.Message);
        }

        if (!Directory.Exists(directory))
        {
            return new StepChangeProbe(StepChangeState.NotApplicable, "The session's worktree no longer exists.");
        }

        try
        {
            // A directory that is not a repository would make `git status` fail with git's own fatal line; it is a session
            // whose steps are never committed, so it answers "not applicable" rather than an unreadable git failure.
            if (!await git.IsRepositoryAsync(directory, ct).ConfigureAwait(false))
            {
                return new StepChangeProbe(StepChangeState.NotApplicable, "The session's worktree is not a git repository.");
            }

            var changes = await git.GetStatusAsync(directory, includeUntracked: true, ct).ConfigureAwait(false);
            return changes.Count == 0
                ? new StepChangeProbe(StepChangeState.Clean)
                : new StepChangeProbe(
                    StepChangeState.Dirty,
                    changes.Count == 1
                        ? "1 uncommitted change in the worktree."
                        : changes.Count + " uncommitted changes in the worktree.");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return new StepChangeProbe(StepChangeState.Unknown, "Could not read the git status of the session's worktree: " + exception.Message);
        }
    }

    /// <summary>A failure the gate turns into "unknown" instead of an exception; only cancellation and OOM are not expected.</summary>
    private static bool IsExpected(Exception exception) => exception is not (OperationCanceledException or OutOfMemoryException);
}