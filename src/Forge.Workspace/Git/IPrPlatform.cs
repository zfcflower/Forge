namespace Forge.Workspace;

/// <summary>
/// The pull request hosting platform as tools and UI see it. Every method works in <paramref name="workingDirectory"/>
/// (a session worktree) and resolves the repository from it. All calls read the platform fresh – nothing is cached – and
/// report expected failures through <see cref="PrResult{T}"/> instead of throwing; only cancellation throws. There is
/// <para>Merging is the one write that is never automatic: <see cref="MergePullRequestAsync"/> runs only after the user
/// confirmed a strategy in the shell's dialog, and nothing here ever merges, closes or force-pushes on its own.</para>
/// </summary>
public interface IPrPlatform
{
    /// <summary>Display name of the platform, e.g. <c>GitHub</c>.</summary>
    string Name { get; }

    /// <summary>Whether the platform CLI is installed and logged in for this repository's host. Never throws.</summary>
    Task<PrPlatformAvailability> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default);

    /// <summary>
    /// The pull request for <paramref name="selector"/> (number, URL or branch), or for the current branch when null.
    /// "No pull request" is a failed result with <see cref="PrFailureReason.PullRequestNotFound"/>, not an exception.
    /// </summary>
    Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(string workingDirectory, string? selector = null, CancellationToken ct = default);

    /// <summary>
    /// Opens a pull request from <paramref name="headBranch"/> into <paramref name="baseBranch"/>. The body travels through
    /// a temporary file that is always deleted. The head branch must already be pushed; this call never pushes.
    /// </summary>
    Task<PrResult<PrReference>> CreatePullRequestAsync(
        string workingDirectory,
        string title,
        string body,
        string baseBranch,
        string headBranch,
        bool draft = false,
        CancellationToken ct = default);

    /// <summary>Changes the title and/or body (null leaves it alone; an empty body clears it). At least one must be given.</summary>
    Task<PrResult<PrReference>> EditPullRequestAsync(
        string workingDirectory,
        int number,
        string? title = null,
        string? body = null,
        CancellationToken ct = default);

    /// <summary>
    /// Merges the pull request with the strategy the user confirmed, optionally deleting the head branch on the platform
    /// afterwards. The platform enforces its own protection rules; a refusal comes back as a failed result, never as an
    /// exception. The default implementation refuses, so a platform that cannot merge (a test double, a future host) needs
    /// no boilerplate — callers must therefore treat "not supported" as a real answer.
    /// </summary>
    Task<PrResult<PrReference>> MergePullRequestAsync(
        string workingDirectory,
        int number,
        PrMergeMethod method,
        bool deleteBranch = false,
        CancellationToken ct = default) =>
        Task.FromResult(PrResult<PrReference>.Fail(
            GitRemoteFailureKind.Other,
            "This platform cannot merge pull requests."));

    /// <summary>
    /// Marks the pull request ready for review, i.e. takes it out of draft. Nothing else about it changes: the title, the
    /// body, the head branch and its commits stay as they are, and no review is requested. Marking a pull request that is
    /// already ready is a success, not an error. The default implementation refuses, so a platform that cannot do this (a
    /// test double, a future host) needs no boilerplate – callers must therefore treat "not supported" as a real answer.
    /// </summary>
    Task<PrResult<PrReference>> MarkPullRequestReadyAsync(
        string workingDirectory,
        int number,
        CancellationToken ct = default) =>
        Task.FromResult(PrResult<PrReference>.Fail(
            GitRemoteFailureKind.Other,
            "This platform cannot mark a pull request ready."));

    /// <summary>
    /// The checks reported for the pull request. A successful read with no checks means none are reported yet – CI may
    /// not have registered for a fresh head – and is not an error.
    /// </summary>
    Task<PrResult<PrChecksSnapshot>> GetChecksAsync(string workingDirectory, int number, CancellationToken ct = default);

    /// <summary>Draft flag, review decision and merge state, read in one call so the values belong together.</summary>
    Task<PrResult<PrMergeState>> GetMergeStateAsync(string workingDirectory, int number, CancellationToken ct = default);

    /// <summary>
    /// The log of the failed steps of one job. Returned whole up to a generous cap with an explicit
    /// <see cref="PrText.Truncated"/> flag; a job log keeps its tail, because the failure is at the end.
    /// </summary>
    Task<PrResult<PrText>> GetFailedJobLogAsync(string workingDirectory, long? runId, long jobId, CancellationToken ct = default);

    /// <summary>Every review thread of the pull request with all of its comments, paginated to the end.</summary>
    Task<PrResult<IReadOnlyList<PrReviewThread>>> GetReviewThreadsAsync(string workingDirectory, int number, CancellationToken ct = default);

    /// <summary>The full unified diff of the pull request, returned up to a generous cap with a truncated flag.</summary>
    Task<PrResult<PrText>> GetPullRequestDiffAsync(string workingDirectory, int number, CancellationToken ct = default);

    /// <summary>Adds a reply to a review thread (by its platform thread id) and returns the created comment.</summary>
    Task<PrResult<PrReviewComment>> ReplyToReviewThreadAsync(string workingDirectory, string threadId, string body, CancellationToken ct = default);

    /// <summary>Marks a review thread resolved.</summary>
    Task<PrResult<PrThreadResolution>> ResolveReviewThreadAsync(string workingDirectory, string threadId, CancellationToken ct = default);
}
