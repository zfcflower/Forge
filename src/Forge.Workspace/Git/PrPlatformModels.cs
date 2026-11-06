namespace Forge.Workspace;

// Platform-neutral models for pull request hosting. Nothing here names GitHub or gh: tools and UI depend on
// IPrPlatform and these shapes only, so another platform can slot in later. Every enum has an Unknown member (or, for
// ReviewDecision, a None/Unknown pair) because platforms add states over time; an unrecognised value is mapped there
// and the raw text is kept next to it where a caller has to report it exactly as observed.

/// <summary>Lifecycle state of a pull request.</summary>
public enum PrState { Unknown, Open, Closed, Merged }

/// <summary>
/// Whether the pull request can be merged right now, as the platform computes it. <see cref="HasHooks"/> is mergeable
/// with passing checks, but pre-receive hooks still run at merge time. <see cref="Unknown"/> covers both "the platform
/// has not computed it yet" and a value this build does not know; callers keep waiting or report it, never treat it as
/// good.
/// </summary>
public enum PrMergeStateStatus { Unknown, Clean, HasHooks, Behind, Dirty, Blocked, Unstable, Draft }

/// <summary>Review gate on the pull request. <see cref="None"/> means no review is required or none was given.</summary>
public enum PrReviewDecision { None, Approved, ChangesRequested, ReviewRequired, Unknown }

/// <summary>Whether the head merges cleanly into the base.</summary>
public enum PrMergeable { Unknown, Mergeable, Conflicting }

/// <summary>Coarse outcome of one check, in the platform's own bucketing.</summary>
public enum PrCheckBucket { Unknown, Pass, Fail, Pending, Skipping, Cancel }

/// <summary>Whether the platform CLI can be used at all. Local git is unaffected by any value.</summary>
public enum PrPlatformState { NotInstalled, NotAuthenticated, Ready }

/// <summary>
/// Extra signal on a failed <see cref="PrResult{T}"/> that <see cref="GitRemoteFailureKind"/> cannot express. Expected
/// conditions such as "no pull request for this branch" are results, not exceptions.
/// </summary>
public enum PrFailureReason
{
    None,

    /// <summary>The platform answered, and the pull request (or the one for the current branch) does not exist.</summary>
    PullRequestNotFound,

    /// <summary>A pull request for the same head and base already exists; the message carries its address.</summary>
    PullRequestAlreadyExists,

    /// <summary>The platform CLI could not be started (not installed, not on PATH, not executable).</summary>
    PlatformNotInstalled,
}

/// <summary>
/// Whether the platform CLI is installed and logged in. <see cref="Failure"/> is <see cref="GitRemoteFailureKind.None"/>
/// except when the login could not be verified because of connectivity (<see cref="GitRemoteFailureKind.Network"/> or
/// <see cref="GitRemoteFailureKind.TimedOut"/>): <see cref="State"/> is then <see cref="PrPlatformState.NotAuthenticated"/>
/// (Ready is only ever reported for a verified login) and the caller can tell "log in" from "check the connection".
/// </summary>
public sealed record PrPlatformAvailability(
    PrPlatformState State,
    string Detail,
    string? Host = null,
    GitRemoteFailureKind Failure = GitRemoteFailureKind.None)
{
    public bool IsReady => State == PrPlatformState.Ready;
}

/// <summary>Where a pull request lives.</summary>
public sealed record PrReference(int Number, string Url);

/// <summary>One pull request as read just now.</summary>
public sealed record PullRequestInfo(
    int Number,
    string Url,
    PrState State,
    string Title,
    string Body,
    string HeadRefName,
    string BaseRefName,
    string HeadRefOid,
    bool IsDraft,
    PrMergeStateStatus MergeStateStatus,
    PrReviewDecision ReviewDecision);

/// <summary>
/// Merge readiness read in one call. The <c>*Text</c> members are the platform's raw strings, kept so a report can show
/// the value exactly as observed even when the enum is <c>Unknown</c>.
/// </summary>
public sealed record PrMergeState(
    PrState State,
    bool IsDraft,
    PrMergeStateStatus MergeStateStatus,
    PrReviewDecision ReviewDecision,
    PrMergeable Mergeable,
    string MergeStateStatusText,
    string ReviewDecisionText,
    string MergeableText);

/// <summary>
/// One check on a pull request. <see cref="State"/> is the platform's raw state string; <see cref="Bucket"/> is the
/// coarse outcome. <see cref="RunId"/> and <see cref="JobId"/> are derived from <see cref="Link"/> when it points at a
/// workflow run or job, and null for checks that do not (external status contexts).
/// </summary>
public sealed record PrCheck(
    string Name,
    string Workflow,
    string State,
    PrCheckBucket Bucket,
    string Description,
    string? Link,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long? RunId,
    long? JobId);

/// <summary>
/// The checks reported for a pull request right now. An empty list is a successful read that means "no checks reported
/// (yet)" – a fresh head may not have registered its CI – and is deliberately distinct from a failed read.
/// </summary>
public sealed record PrChecksSnapshot(IReadOnlyList<PrCheck> Checks)
{
    public bool NoneReported => Checks.Count == 0;

    public int Count(PrCheckBucket bucket)
    {
        var count = 0;
        foreach (var check in Checks)
        {
            if (check.Bucket == bucket)
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>
/// Large text (a job log, a diff) returned whole up to a cap. When <see cref="Truncated"/> is set the text was cut at the
/// cap (a job log keeps its tail, a diff its head) and says so in a marker line; callers spill the text to an artifact.
/// </summary>
public sealed record PrText(string Text, bool Truncated);

/// <summary>One comment inside a review thread.</summary>
public sealed record PrReviewComment(
    string Id,
    long? DatabaseId,
    string? Author,
    string Body,
    DateTimeOffset? CreatedAt,
    string? Url);

/// <summary>
/// A review thread: the anchoring location plus every comment, oldest first. <see cref="Line"/> is the line in the
/// current diff and is null once the thread is outdated; <see cref="OriginalLine"/> is where it was first placed.
/// </summary>
public sealed record PrReviewThread(
    string Id,
    bool IsResolved,
    bool IsOutdated,
    string? Path,
    int? Line,
    int? StartLine,
    int? OriginalLine,
    string? DiffSide,
    string? ResolvedBy,
    IReadOnlyList<PrReviewComment> Comments);

/// <summary>State of a thread after a resolve call.</summary>
public sealed record PrThreadResolution(string ThreadId, bool IsResolved);

/// <summary>
/// Outcome of a platform call. Expected failures (not authenticated, repository or pull request missing, network,
/// timeout) come back in <see cref="Status"/> with the raw stderr attached; only cancellation throws. A branch without
/// a pull request is a failure with <see cref="PrFailureReason.PullRequestNotFound"/>, and a pull request without any
/// checks is not a failure at all (an empty <see cref="PrChecksSnapshot"/>). <see cref="Value"/> is set exactly when
/// <see cref="Success"/> is true.
/// </summary>
public sealed record PrResult<T>(GitRemoteResult Status, T? Value = default, PrFailureReason Reason = PrFailureReason.None)
{
    public bool Success => Status.Success;

    public bool PullRequestNotFound => Reason == PrFailureReason.PullRequestNotFound;

    public static PrResult<T> Ok(T value, string message = "") =>
        new(GitRemoteResult.Ok(message), value);

    public static PrResult<T> Fail(
        GitRemoteFailureKind kind,
        string message,
        string standardError = "",
        PrFailureReason reason = PrFailureReason.None) =>
        new(new GitRemoteResult(kind, message, standardError), default, reason);
}
