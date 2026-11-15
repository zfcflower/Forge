using Forge.Core.Tools;
using Forge.Workspace;
using CoreConclusion = Forge.Core.Tools.ReviewConclusion;
using CoreNewConclusion = Forge.Core.Tools.NewReviewConclusion;
using CoreOutcome = Forge.Core.Tools.ReviewConclusionOutcome;
using StoredConclusion = Forge.Workspace.ReviewConclusion;
using StoredNewConclusion = Forge.Workspace.NewReviewConclusion;
using StoredOutcome = Forge.Workspace.ReviewConclusionOutcome;

namespace Forge.App.Git;

/// <summary>
/// The product side of <see cref="IReviewConclusionStore"/> (wave W17b): <c>request_review</c> hands each finished review to
/// Core's injected interface, and this writes it into <c>reviews.db</c> through the same <see cref="ReviewStore"/> the review
/// panels already use. The app keeps no SQL of its own (forge-app spec, boundary), and Workspace sits below Core, so the two
/// record shapes are mapped here instead of being shared: the pair is deliberate duplication across a dependency edge, not a
/// second contract.
/// </summary>
internal sealed class ReviewConclusionStore(ReviewStore reviews) : IReviewConclusionStore
{
    public async Task<CoreConclusion> AddAsync(CoreNewConclusion conclusion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conclusion);

        var stored = await reviews.AddConclusionAsync(
            new StoredNewConclusion
            {
                WorkspacePath = conclusion.WorkspacePath,
                SessionId = conclusion.SessionId,
                StepId = conclusion.StepId,
                BaseCommit = conclusion.Base,
                HeadCommit = conclusion.HeadCommit,
                Outcome = OutcomeToStore(conclusion.Outcome),
                Text = conclusion.Text,
            },
            ct).ConfigureAwait(false);

        return ToCore(stored);
    }

    public async Task<IReadOnlyList<CoreConclusion>> ListAsync(string sessionId, CancellationToken ct = default)
    {
        var stored = await reviews.GetConclusionsAsync(sessionId, ct).ConfigureAwait(false);
        var conclusions = new List<CoreConclusion>(stored.Count);
        foreach (var conclusion in stored)
        {
            conclusions.Add(ToCore(conclusion));
        }

        return conclusions;
    }

    private static CoreConclusion ToCore(StoredConclusion stored) => new(
        stored.Id,
        stored.WorkspacePath,
        stored.SessionId,
        stored.StepId,
        stored.BaseCommit,
        stored.HeadCommit,
        OutcomeToCore(stored.Outcome),
        stored.Text,
        stored.CreatedAt);

    /// <summary>
    /// One case per value, not a cast: the two enums are numbered alike today, and a silent cast would keep compiling if one
    /// side ever gained a state in the middle.
    /// </summary>
    private static StoredOutcome OutcomeToStore(CoreOutcome outcome) => outcome switch
    {
        CoreOutcome.Approve => StoredOutcome.Approve,
        CoreOutcome.RequestChanges => StoredOutcome.RequestChanges,
        CoreOutcome.Stopped => StoredOutcome.Stopped,
        CoreOutcome.Failed => StoredOutcome.Failed,
        _ => StoredOutcome.Unstructured,
    };

    private static CoreOutcome OutcomeToCore(StoredOutcome outcome) => outcome switch
    {
        StoredOutcome.Approve => CoreOutcome.Approve,
        StoredOutcome.RequestChanges => CoreOutcome.RequestChanges,
        StoredOutcome.Stopped => CoreOutcome.Stopped,
        StoredOutcome.Failed => CoreOutcome.Failed,
        _ => CoreOutcome.Unstructured,
    };
}
