using Forge.Core.Tools;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>Builds the git side of a review request: what changed, in which commits, as the raw unified diff.</summary>
internal sealed class ReviewChangeSetSource(GitCli git, int commitListLimit = ReviewChangeSetSource.DefaultCommitListLimit)
    : IReviewChangeSetSource
{
    /// <summary>
    /// Commits listed for a branch review, newest first; one more than the cap is fetched so a truncated list is known to be
    /// truncated instead of being presented as the branch's commit count. The constructor parameter exists for tests, which
    /// cannot build a thousand-commit history cheaply.
    /// </summary>
    internal const int DefaultCommitListLimit = 1_000;

    /// <summary>
    /// Bytes of each untracked file read to build its file entry. The package only lists untracked files (path, status,
    /// binary or not); the reviewer opens them itself. 8000 bytes is git's own binary heuristic window, so the binary flag
    /// is what git would say, without reading up to a megabyte of every untracked file only to throw the text away.
    /// </summary>
    internal const int UntrackedProbeBytes = 8_000;

    private readonly int commitCap = Math.Max(1, commitListLimit);

    /// <summary>
    /// Reads one change set: one commit against its first parent, or the branch plus the uncommitted tree against a base
    /// (the repository's default branch unless one is given), narrowed to the requested paths. Runs once per review on a
    /// background thread, so it keeps one pass and no state.
    /// </summary>
    public async Task<ReviewChangeSetResult> BuildAsync(ReviewChangeSetRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var directory = request.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return ReviewChangeSetResult.Failed("The session has no working directory, so there is nothing to review.");
        }

        try
        {
            // Inside the try with everything else: a git that cannot start (missing binary, a broken install) throws from
            // here too, and the caller must get that as reviewable text rather than as a failed agent turn.
            if (await git.GetRepositoryRootAsync(directory, ct).ConfigureAwait(false) is null)
            {
                return ReviewChangeSetResult.Failed($"'{directory}' is not inside a git repository, so there is no change set to review.");
            }

            var status = await git.GetBranchStatusAsync(directory, ct).ConfigureAwait(false);

            // Null only in an unborn repository: there is no HEAD to review against yet.
            var head = status.HeadCommit;

            // The revision the diff runs from: the reviewed commit's parent, or the merge-base the branch is compared against.
            string? baseRevision;
            GitDiffOptions options;
            IReadOnlyList<ReviewCommit> commits;
            bool commitsTruncated;
            var singleCommit = !string.IsNullOrWhiteSpace(request.Commit);

            if (singleCommit)
            {
                // Resolve first: `git log <missing sha>` exits 128 with git's own fatal line, and a step whose commit was
                // rebased away or collected must come back as something the model can act on.
                var commitSha = await git.RevParseAsync(directory, request.Commit + "^{commit}", ct).ConfigureAwait(false);
                var matches = commitSha is null
                    ? []
                    : await git.GetCommitsAsync(directory, exclude: string.Empty, head: commitSha, limit: 1, ct).ConfigureAwait(false);
                if (matches.Count == 0)
                {
                    return ReviewChangeSetResult.Failed($"Commit '{request.Commit}' does not exist in this repository.");
                }

                var commit = matches[0];
                baseRevision = commit.ParentShas.FirstOrDefault();
                options = new GitDiffOptions
                {
                    // A root commit has no parent; the empty tree is what it is reviewed against, and git resolves that object
                    // even when the repository does not contain it.
                    From = baseRevision ?? GitCli.EmptyTreeSha,
                    To = commit.Sha,
                    Paths = request.Paths,
                };
                commits = [new ReviewCommit(commit.Sha, commit.Subject)];
                head = commit.Sha;
                commitsTruncated = false;
            }
            else
            {
                var baseName = string.IsNullOrWhiteSpace(request.Base)
                    ? await git.GetDefaultBranchAsync(directory, ct).ConfigureAwait(false)
                    : request.Base.Trim();

                // Threaded into `git diff` after --end-of-options, but git would still read a leading '-' as an option of its own.
                if (baseName is { Length: > 0 } && baseName[0] == '-')
                {
                    return ReviewChangeSetResult.Failed(
                        $"Base '{baseName}' cannot be used: a leading '-' would be read as a git option rather than a revision.");
                }

                var baseSha = baseName is null
                    ? null
                    : await git.RevParseAsync(directory, baseName, ct).ConfigureAwait(false);
                if (baseName is not null && baseSha is null)
                {
                    return ReviewChangeSetResult.Failed(
                        $"Base '{baseName}' does not resolve in this repository; fetch first, or pass base explicitly.");
                }

                // Compare from the merge-base, not from the base itself: `git diff <base>` is two-dot and would show every
                // commit the base gained since the fork as a deletion in this change set, while the rest of the app (git_diff,
                // the pull request, pr_create's gate) reads `base...HEAD`. The merge-base is also a sha, so the package can
                // shorten it - a ref name shortened to seven characters reads as `origin/`.
                if (baseSha is not null && head is not null)
                {
                    baseSha = await git.GetMergeBaseAsync(directory, baseSha, head, ct).ConfigureAwait(false);
                    if (baseSha is null)
                    {
                        return ReviewChangeSetResult.Failed(
                            $"Base '{baseName}' shares no history with HEAD, so there is no change set to compare.");
                    }
                }

                // No base at all means nothing committed is in the change set, so it is the uncommitted tree against HEAD
                // (ReviewChangeSet.Base names that case); only an unborn repository has neither.
                baseRevision = baseSha ?? head;
                options = new GitDiffOptions
                {
                    // From the merge-base to the working tree in one read: that is the branch's commits plus everything
                    // uncommitted, without a second diff of committed-then-uncommitted.
                    From = baseRevision,
                    Paths = request.Paths,
                    // git has no diff text for a file it does not track; the file list adds synthetic add-diffs for them.
                    IncludeUntracked = true,
                    MaxUntrackedBytes = UntrackedProbeBytes,
                };

                var history = head is null || baseRevision is null
                    ? []
                    : await ReadCommitsAsync(directory, baseRevision, head, request.Paths, ct).ConfigureAwait(false);
                commitsTruncated = history.Count > commitCap;
                commits = commitsTruncated ? [.. history.Take(commitCap)] : history;
            }

            var snapshot = await git.GetDiffSnapshotAsync(directory, options, ct).ConfigureAwait(false);
            var changed = new List<ReviewChangedFile>(snapshot.Files.Count);
            foreach (var file in snapshot.Files)
            {
                changed.Add(new ReviewChangedFile(file.Path, StatusWord(file.Status), file.IsBinary));
            }

            return ReviewChangeSetResult.Ok(
                new ReviewChangeSet
                {
                    WorkingDirectory = directory,
                    Branch = status.Branch,
                    HeadCommit = head,
                    Base = baseRevision,
                    SingleCommit = singleCommit,
                    Paths = request.Paths,
                    Files = changed,
                    Commits = commits,
                    CommitsTruncated = commitsTruncated,
                    DiffText = snapshot.Text,
                    // This source returns git's text whole or fails above; it never cuts the diff itself.
                    DiffTruncated = false,
                });
        }
        catch (GitOutputTruncatedException)
        {
            return ReviewChangeSetResult.Failed(
                "The diff is larger than git's capture limit; narrow it with paths or read git_diff stat_only first.");
        }
        catch (GitException exception)
        {
            return ReviewChangeSetResult.Failed(exception.Message);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            return ReviewChangeSetResult.Failed("Reading the change set failed: " + exception.Message);
        }
    }

    /// <summary>
    /// The commits the package lists: the range's own commits, newest first, narrowed to the requested paths like the file
    /// list and the diff are. A commit whose changes are all outside those paths is not part of this change set, and listing
    /// it would make the reviewer read a change it was not given. One commit beyond the cap is fetched so the caller can say
    /// the list is a prefix instead of presenting the cap as a total.
    /// </summary>
    private async Task<IReadOnlyList<ReviewCommit>> ReadCommitsAsync(
        string directory,
        string exclude,
        string head,
        IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        var history = await git.GetLogAsync(
                directory,
                new GitLogOptions { Revision = exclude + ".." + head, Paths = paths, Limit = commitCap + 1 },
                ct)
            .ConfigureAwait(false);
        var commits = new List<ReviewCommit>(history.Count);
        foreach (var commit in history)
        {
            commits.Add(new ReviewCommit(commit.Id, commit.Subject));
        }

        return commits;
    }

    /// <summary>The lower-case status word a review package lists a file under.</summary>
    private static string StatusWord(FileDiffStatus status) => status switch
    {
        FileDiffStatus.Added => "added",
        FileDiffStatus.Deleted => "deleted",
        FileDiffStatus.Renamed => "renamed",
        FileDiffStatus.Copied => "copied",
        _ => "modified",
    };
}
