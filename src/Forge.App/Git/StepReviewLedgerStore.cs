using Forge.Core.Tools;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>Keeps the step review ledger in <c>reviews.db</c>; maps Core's records onto the Workspace rows.</summary>
internal sealed class StepReviewLedgerStore(ReviewStore reviews) : IStepReviewStore
{
    public async Task<IReadOnlyList<StepReviewEntry>> LoadAsync(string sessionId, CancellationToken ct)
    {
        var rows = await reviews.GetStepVerdictsAsync(sessionId, ct).ConfigureAwait(false);
        var entries = new List<StepReviewEntry>(rows.Count);
        foreach (var row in rows)
        {
            entries.Add(new StepReviewEntry(
                row.StepId,
                new StepReviewRecord((ReviewVerdict)row.Verdict, row.CommitId, row.At, row.Tree),
                row.Rounds));
        }

        return entries;
    }

    public Task SaveAsync(string sessionId, StepReviewEntry entry, CancellationToken ct) =>
        reviews.SaveStepVerdictAsync(
            new StepVerdictRow(sessionId, entry.StepId, (int)entry.Record.Verdict, entry.Record.CommitId, entry.Record.At, entry.Record.Tree, entry.Rounds),
            ct);

    public Task DeleteAsync(string sessionId, string stepId, CancellationToken ct) =>
        reviews.DeleteStepVerdictAsync(sessionId, stepId, ct);
}
