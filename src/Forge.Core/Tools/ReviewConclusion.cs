namespace Forge.Core.Tools;

/// <summary>
/// How one <c>request_review</c> ended, as it is recorded (wave W17b). Only a review that actually reached the reviewer is
/// recorded; the argument, change-set and "nothing to review" outcomes are answers to the caller and leave no row, because
/// no review happened. <see cref="Stopped"/> and <see cref="Failed"/> exist so a stored history never reads as if a review
/// had approved: neither of them is an approval, exactly like <see cref="ReviewVerdict.Unstructured"/>.
/// </summary>
public enum ReviewConclusionOutcome
{
    /// <summary>The reviewer answered without a verdict line: never an approval.</summary>
    Unstructured = 0,

    /// <summary>The reviewer's last verdict line approved.</summary>
    Approve = 1,

    /// <summary>The reviewer's last verdict line asked for changes.</summary>
    RequestChanges = 2,

    /// <summary>The reviewer was stopped before it finished; its partial text carries no verdict.</summary>
    Stopped = 3,

    /// <summary>The review could not be started, or the reviewer returned no text at all.</summary>
    Failed = 4,
}

/// <summary>
/// One review to record: who asked (<see cref="SessionId"/>), what was reviewed (<see cref="StepId"/> for a step's own
/// commit, otherwise <see cref="Base"/> plus <see cref="HeadCommit"/>) and what came back. <see cref="Text"/> is the
/// reviewer's own text (already clamped by the caller) or, for a failed review, the reason it did not run.
/// </summary>
public sealed record NewReviewConclusion
{
    /// <summary>The session's working directory the review read its change set from.</summary>
    public required string WorkspacePath { get; init; }

    public required string SessionId { get; init; }

    /// <summary>The checklist step whose own commit was reviewed; null for a branch review.</summary>
    public string? StepId { get; init; }

    /// <summary>The From-side revision of the reviewed diff: a step review records the commit's first parent
    /// (null for a root commit), a branch review records the merge-base with the target branch; null when unknown.</summary>
    public string? Base { get; init; }

    /// <summary>The commit the review read, when the change set source could name it.</summary>
    public string? HeadCommit { get; init; }

    public required ReviewConclusionOutcome Outcome { get; init; }

    public required string Text { get; init; }
}

/// <summary>A recorded review, as a store read it back.</summary>
public sealed record ReviewConclusion(
    long Id,
    string WorkspacePath,
    string SessionId,
    string? StepId,
    string? Base,
    string? HeadCommit,
    ReviewConclusionOutcome Outcome,
    string Text,
    DateTimeOffset CreatedAt);

/// <summary>
/// Where <c>request_review</c> records its conclusions (wave W17b): Core owns the contract, the host application writes the
/// rows (the same split as <see cref="IReviewChangeSetSource"/>, which reads git for it). Null means the tool records
/// nothing and behaves exactly as it did before conclusions were stored.
/// <para>Both members are expected to throw only on cancellation: the tool treats a failing store as "not recorded" and
/// still returns the review it got, because losing the record must never lose the review.</para>
/// </summary>
public interface IReviewConclusionStore
{
    /// <summary>Records one finished review; the returned record carries the id and timestamp the store assigned.</summary>
    Task<ReviewConclusion> AddAsync(NewReviewConclusion conclusion, CancellationToken ct = default);

    /// <summary>The session's conclusions, newest first.</summary>
    Task<IReadOnlyList<ReviewConclusion>> ListAsync(string sessionId, CancellationToken ct = default);
}
