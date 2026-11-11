using Forge.Abstractions;
using Forge.App.Localization;
using Forge.Core.Storage;
using Forge.Workspace;

namespace Forge.App.Lifecycle;

/// <summary>The two directions of the one merge primitive (6.6).</summary>
internal enum MergeDirection
{
    /// <summary>The fork branch into the source branch, run in the workspace that has the source branch checked out.</summary>
    MergeBack,

    /// <summary>The source branch into the fork branch, run in the fork workspace.</summary>
    Sync,
}

/// <summary>
/// A workspace's fork relation as the tree knows it (5.4 rule 6: the most recent session of the workspace that records
/// <c>fork_of_branch</c>, history included): the fork workspace, its stored branch, the source branch and the fork's start.
/// </summary>
internal sealed record ForkRelation(ProjectInfo Project, string ForkDirectory, string? ForkBranch, string SourceBranch, string? ForkBaseSha);

/// <summary>The git half of a merge-back / sync; <see cref="MergeGit"/> is the real one, tests substitute their own.</summary>
internal interface IMergeGit
{
    Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string repositoryRoot, CancellationToken ct);

    /// <summary>The commit a local branch points at; null when the branch does not exist.</summary>
    Task<string?> BranchCommitAsync(string repositoryRoot, string branch, CancellationToken ct);

    /// <summary>The newest commits of a range such as <c>base..branch</c>, at most <see cref="MergeFlow.MaxPreviewCommits"/>.</summary>
    Task<IReadOnlyList<GitCommit>> ListCommitsAsync(string repositoryRoot, string range, CancellationToken ct);

    /// <summary>How many commits <paramref name="revision"/> has that <paramref name="exclude"/> does not.</summary>
    Task<int> CountCommitsAsync(string repositoryRoot, string exclude, string revision, CancellationToken ct);

    Task<bool> IsAncestorAsync(string repositoryRoot, string ancestor, string descendant, CancellationToken ct);

    /// <summary><c>git merge --no-ff --no-edit</c> in <paramref name="directory"/>.</summary>
    Task<GitRemoteResult> MergeAsync(string directory, string revision, CancellationToken ct);

    Task<bool> HasMergeInProgressAsync(string directory, CancellationToken ct);

    Task AbortMergeAsync(string directory, CancellationToken ct);

    /// <summary>Pushes the branch to its push remote (else <c>origin</c>) and makes it the upstream.</summary>
    Task<GitRemoteResult> PushBranchAsync(string repositoryRoot, string branch, CancellationToken ct);
}

/// <summary>The real <see cref="IMergeGit"/>, through <see cref="GitCli"/>.</summary>
internal sealed class MergeGit(GitCli git) : IMergeGit
{
    public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string repositoryRoot, CancellationToken ct) =>
        git.ListWorktreesAsync(repositoryRoot, ct);

    public Task<string?> BranchCommitAsync(string repositoryRoot, string branch, CancellationToken ct) =>
        git.RevParseAsync(repositoryRoot, "refs/heads/" + branch + "^{commit}", ct);

    public Task<IReadOnlyList<GitCommit>> ListCommitsAsync(string repositoryRoot, string range, CancellationToken ct) =>
        git.GetLogAsync(repositoryRoot, new GitLogOptions { Revision = range, Limit = MergeFlow.MaxPreviewCommits }, ct);

    public Task<int> CountCommitsAsync(string repositoryRoot, string exclude, string revision, CancellationToken ct) =>
        git.CountCommitsAheadAsync(repositoryRoot, revision, exclude, ct);

    public Task<bool> IsAncestorAsync(string repositoryRoot, string ancestor, string descendant, CancellationToken ct) =>
        git.IsAncestorAsync(repositoryRoot, ancestor, descendant, ct);

    public Task<GitRemoteResult> MergeAsync(string directory, string revision, CancellationToken ct) =>
        git.MergeAsync(directory, revision, noFastForward: true, ct);

    public Task<bool> HasMergeInProgressAsync(string directory, CancellationToken ct) => git.HasMergeInProgressAsync(directory, ct);

    public Task AbortMergeAsync(string directory, CancellationToken ct) => git.AbortMergeAsync(directory, ct);

    public Task<GitRemoteResult> PushBranchAsync(string repositoryRoot, string branch, CancellationToken ct) =>
        git.PushAsync(repositoryRoot, new GitPushOptions { Branch = branch, SetUpstream = true }, null, ct);
}

/// <summary>
/// What a merge-back / sync would do, read when its dialog opens (6.6 steps 1-2): the workspace it runs in (null when no
/// workspace has the source branch checked out: the dialog then degrades to pushing the fork branch), the commits it brings
/// in (the newest <see cref="MergeFlow.MaxPreviewCommits"/>, and how many there are in all), and whether there is nothing
/// to do. <see cref="Error"/> instead when it cannot be planned at all.
/// </summary>
internal sealed record MergePreview(
    MergeDirection Direction,
    string ForkBranch,
    string SourceBranch,
    string? TargetDirectory,
    IReadOnlyList<GitCommit> Commits,
    int CommitCount,
    bool AlreadyDone,
    string? Error)
{
    /// <summary>A merge back whose target workspace does not exist: only "push the fork branch" is offered.</summary>
    public bool TargetMissing => Error is null && TargetDirectory is null;

    public bool CanMerge => Error is null && TargetDirectory is not null && !AlreadyDone;

    /// <summary>The revision merged into the target: the fork branch when merging back, the source branch when syncing.</summary>
    public string Revision => Direction == MergeDirection.MergeBack ? ForkBranch : SourceBranch;

    /// <summary>The branch the merge lands on.</summary>
    public string TargetBranch => Direction == MergeDirection.MergeBack ? SourceBranch : ForkBranch;

    public static MergePreview Failed(MergeDirection direction, ForkRelation relation, string error) =>
        new(direction, relation.ForkBranch ?? string.Empty, relation.SourceBranch, null, [], 0, false, error);
}

internal enum MergeOutcomeKind
{
    Merged,

    /// <summary>The merge stopped on conflicts; the target workspace is left in the merge (<c>MERGE_HEAD</c>).</summary>
    Conflict,

    Failed,
}

internal sealed record MergeOutcome(MergeOutcomeKind Kind, string Message);

/// <summary>
/// Merge back to / sync from the source branch (lifecycle 6.6): the same <c>merge --no-ff --no-edit</c> in opposite
/// directions. The target is located through <c>git worktree list</c> (never guessed from <c>fork_base_sha</c>); the
/// target tree need not be clean - git refuses to overwrite local changes and that refusal is relayed with its file list.
/// A conflict leaves the target in the merge; the caller marks the workspace conflicted. The merge commit is not a step
/// commit and is recorded nowhere else. Runs off the UI thread; it touches no view model.
/// </summary>
internal sealed class MergeFlow(IMergeGit git)
{
    /// <summary>Commits listed in the preview; the rest is only counted.</summary>
    public const int MaxPreviewCommits = 50;

    public async Task<MergePreview> PreviewAsync(ForkRelation relation, MergeDirection direction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(relation);
        var repository = relation.Project.RootPath;
        try
        {
            var worktrees = await git.ListWorktreesAsync(repository, ct).ConfigureAwait(false);
            var forkKey = OccupancyKey.Compute(null, relation.ForkDirectory);
            var forkEntry = worktrees.FirstOrDefault(w => !w.IsPrunable && OccupancyKey.Compute(null, w.Path) == forkKey);

            // The fork branch is the one its workspace has checked out now (a renamed branch follows); the stored name
            // only when the workspace is gone.
            var forkBranch = forkEntry?.Branch ?? relation.ForkBranch;
            if (string.IsNullOrWhiteSpace(forkBranch)
                || await git.BranchCommitAsync(repository, forkBranch, ct).ConfigureAwait(false) is null)
            {
                return MergePreview.Failed(direction, relation, ForkStrings.ForkMissing(forkBranch ?? relation.ForkDirectory));
            }

            var source = relation.SourceBranch;
            var sourceExists = await git.BranchCommitAsync(repository, source, ct).ConfigureAwait(false) is not null;
            if (direction == MergeDirection.MergeBack)
            {
                var target = worktrees.FirstOrDefault(w => !w.IsPrunable && !w.IsBare && string.Equals(w.Branch, source, StringComparison.Ordinal));
                if (target is null || !sourceExists)
                {
                    // Not checked out, renamed or deleted: degrade to pushing the fork branch, no ancestor guessing (9).
                    return new MergePreview(direction, forkBranch, source, null, [], 0, false, null);
                }

                if (await git.IsAncestorAsync(repository, forkBranch, source, ct).ConfigureAwait(false))
                {
                    return new MergePreview(direction, forkBranch, source, target.Path, [], 0, true, null);
                }

                var from = string.IsNullOrWhiteSpace(relation.ForkBaseSha) ? source : relation.ForkBaseSha;
                var commits = await git.ListCommitsAsync(repository, from + ".." + forkBranch, ct).ConfigureAwait(false);
                var count = await git.CountCommitsAsync(repository, from, forkBranch, ct).ConfigureAwait(false);
                return new MergePreview(direction, forkBranch, source, target.Path, commits, Math.Max(count, commits.Count), false, null);
            }

            if (!sourceExists)
            {
                return MergePreview.Failed(direction, relation, ForkStrings.SourceMissing(source));
            }

            if (forkEntry is null)
            {
                return MergePreview.Failed(direction, relation, ForkStrings.ForkMissing(forkBranch));
            }

            if (await git.IsAncestorAsync(repository, source, forkBranch, ct).ConfigureAwait(false))
            {
                return new MergePreview(direction, forkBranch, source, forkEntry.Path, [], 0, true, null);
            }

            var incoming = await git.ListCommitsAsync(repository, forkBranch + ".." + source, ct).ConfigureAwait(false);
            var total = await git.CountCommitsAsync(repository, forkBranch, source, ct).ConfigureAwait(false);
            return new MergePreview(direction, forkBranch, source, forkEntry.Path, incoming, Math.Max(total, incoming.Count), false, null);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return MergePreview.Failed(direction, relation, ForkStrings.PreviewFailed(exception.Message));
        }
    }

    /// <summary>
    /// Runs the merge in the preview's target. The target is re-read first (5.4 rule 4: the confirmation may have stayed
    /// open while someone switched that workspace to another branch); a target that no longer has the target branch
    /// checked out merges nothing. Not cancellable once started: a half-run merge is git's to finish.
    /// </summary>
    public async Task<MergeOutcome> ExecuteAsync(MergePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.CanMerge)
        {
            throw new InvalidOperationException("The preview has nothing to merge.");
        }

        var target = preview.TargetDirectory!;
        try
        {
            var targetKey = OccupancyKey.Compute(null, target);
            var worktrees = await git.ListWorktreesAsync(target, CancellationToken.None).ConfigureAwait(false);
            var entry = worktrees.FirstOrDefault(w => !w.IsPrunable && OccupancyKey.Compute(null, w.Path) == targetKey);
            if (entry is null || !string.Equals(entry.Branch, preview.TargetBranch, StringComparison.Ordinal))
            {
                return new MergeOutcome(MergeOutcomeKind.Failed, ForkStrings.TargetMoved(target, preview.TargetBranch));
            }

            var result = await git.MergeAsync(target, preview.Revision, CancellationToken.None).ConfigureAwait(false);
            if (result.Success)
            {
                return new MergeOutcome(MergeOutcomeKind.Merged, ForkStrings.Merged(preview.Revision, preview.TargetBranch));
            }

            // A conflict is what MERGE_HEAD says, whatever git's wording was.
            if (result.Failure == GitRemoteFailureKind.Conflict
                || await git.HasMergeInProgressAsync(target, CancellationToken.None).ConfigureAwait(false))
            {
                return new MergeOutcome(MergeOutcomeKind.Conflict, ForkStrings.Conflict(target));
            }

            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.Message : result.StandardError.Trim();
            return new MergeOutcome(
                MergeOutcomeKind.Failed,
                result.Failure == GitRemoteFailureKind.DirtyWorkTree ? ForkStrings.DirtyRefused(detail) : ForkStrings.Failed(detail));
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return new MergeOutcome(MergeOutcomeKind.Failed, ForkStrings.Failed(exception.Message));
        }
    }

    /// <summary><c>merge --abort</c>; null on success, else the reason.</summary>
    public async Task<string?> AbortAsync(string directory)
    {
        try
        {
            await git.AbortMergeAsync(directory, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return ForkStrings.AbortFailed(exception.Message);
        }
    }

    /// <summary>The degraded merge back: push the fork branch so a PR can be opened for it. Null on success, else the reason.</summary>
    public async Task<string?> PushForkAsync(ForkRelation relation, string forkBranch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(relation);
        try
        {
            var result = await git.PushBranchAsync(relation.Project.RootPath, forkBranch, ct).ConfigureAwait(false);
            return result.Success
                ? null
                : ForkStrings.PushFailed(string.IsNullOrWhiteSpace(result.StandardError) ? result.Message : result.StandardError.Trim());
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return ForkStrings.PushFailed(exception.Message);
        }
    }

    private static bool IsExpected(Exception exception) =>
        exception is not (OperationCanceledException or OutOfMemoryException);
}
