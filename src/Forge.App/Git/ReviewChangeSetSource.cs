using Forge.Core.Tools;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>Builds the git side of a review request: what changed, in which commits, as the raw unified diff.</summary>
internal sealed class ReviewChangeSetSource(GitCli git) : IReviewChangeSetSource
{
    /// <summary>Commits listed for a branch review, newest first; the package shows the newest and says how many more there are.</summary>
    private const int CommitListLimit = 200;

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

            // The revision the diff runs from: the reviewed commit's parent, or the base the branch is compared against.
            string? baseRevision;
            GitDiffOptions options;
            IReadOnlyList<ReviewCommit> commits;

            if (!string.IsNullOrWhiteSpace(request.Commit))
            {
                var matches = await git.GetCommitsAsync(directory, exclude: string.Empty, head: request.Commit, limit: 1, ct)
                    .ConfigureAwait(false);
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

                if (baseName is not null && await git.RevParseAsync(directory, baseName, ct).ConfigureAwait(false) is null)
                {
                    return ReviewChangeSetResult.Failed(
                        $"Base '{baseName}' does not resolve in this repository; fetch first, or pass base explicitly.");
                }

                // No base means nothing committed is in the change set, so it is the uncommitted tree against HEAD
                // (ReviewChangeSet.Base names that case); only an unborn repository has neither, and a plain `git diff`
                // then reads the index.
                baseRevision = baseName ?? head;
                options = new GitDiffOptions
                {
                    From = baseRevision,
                    Paths = request.Paths,
                    // git has no diff text for a file it does not track; GetDiffAsync adds the synthetic add-diffs for them.
                    IncludeUntracked = true,
                };
                commits = head is null || baseRevision is null
                    ? []
                    : await ReadCommitsAsync(directory, baseRevision, head, ct).ConfigureAwait(false);
            }

            // The text call ignores IncludeUntracked: an untracked file has no diff text of its own, so the file list below is
            // what names it. Both calls read the same options, so the files and the text always describe the same change set.
            var text = await git.GetDiffTextAsync(directory, options, ct).ConfigureAwait(false);
            var files = await git.GetDiffAsync(directory, options, ct).ConfigureAwait(false);

            var changed = new List<ReviewChangedFile>(files.Count);
            foreach (var file in files)
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
                    Files = changed,
                    Commits = commits,
                    DiffText = text,
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

    /// <summary>The branch's own commits, newest first, as the package lists them.</summary>
    private async Task<IReadOnlyList<ReviewCommit>> ReadCommitsAsync(string directory, string exclude, string head, CancellationToken ct)
    {
        var history = await git.GetCommitsAsync(directory, exclude, head, CommitListLimit, ct).ConfigureAwait(false);
        var commits = new List<ReviewCommit>(history.Count);
        foreach (var commit in history)
        {
            commits.Add(new ReviewCommit(commit.Sha, commit.Subject));
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
