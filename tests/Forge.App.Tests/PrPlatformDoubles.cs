using Forge.Workspace;

namespace Forge.App.Tests;

/// <summary>
/// A pull request platform double that refuses everything it does not override: every member this base declares throws,
/// so a suite's overrides are exactly the surface it observes and an unexpected call fails the test instead of passing
/// silently. <see cref="IPrPlatform.MergePullRequestAsync"/> and <see cref="IPrPlatform.MarkPullRequestReadyAsync"/>
/// keep the interface's own defaults — a failed result, not an exception — exactly as a double that declared nothing
/// for them did.
/// </summary>
internal abstract class ThrowingPrPlatform : IPrPlatform
{
    public abstract string Name { get; }

    public virtual Task<PrPlatformAvailability> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public virtual Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(string workingDirectory, string? selector = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public virtual Task<PrResult<PrReference>> CreatePullRequestAsync(
        string workingDirectory,
        string title,
        string body,
        string baseBranch,
        string headBranch,
        bool draft = false,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    public virtual Task<PrResult<PrReference>> EditPullRequestAsync(string workingDirectory, int number, string? title = null, string? body = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public virtual Task<PrResult<PrChecksSnapshot>> GetChecksAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public virtual Task<PrResult<PrMergeState>> GetMergeStateAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public virtual Task<PrResult<PrText>> GetFailedJobLogAsync(string workingDirectory, long? runId, long jobId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public virtual Task<PrResult<IReadOnlyList<PrReviewThread>>> GetReviewThreadsAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public virtual Task<PrResult<PrText>> GetPullRequestDiffAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public virtual Task<PrResult<PrReviewComment>> ReplyToReviewThreadAsync(string workingDirectory, string threadId, string body, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public virtual Task<PrResult<PrThreadResolution>> ResolveReviewThreadAsync(string workingDirectory, string threadId, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
