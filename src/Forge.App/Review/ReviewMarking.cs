using System.Diagnostics;
using Forge.App.Localization;

namespace Forge.App.Review;

/// <summary>
/// The store side of "these drafts are sent": marking ids sent while holding the store's gate and keeping the in-memory
/// unmarked set in step. Both review surfaces (the single file editor and the multi-file diff) send through it; where the
/// status line lives differs per surface, so the text to show comes back as a value and each surface sets its own message.
/// </summary>
internal static class ReviewMarking
{
    /// <summary>
    /// Marks <paramref name="ids"/> sent under the store's gate. With <paramref name="retry"/> only the drafts that are
    /// still unmarked are sent, so a retry cannot re-send one that was already recorded. Returns the text the caller has to
    /// show, or <c>default</c> when there is nothing to report.
    /// </summary>
    public static async Task<LocText> MarkAsync(ReviewServices svc, long[] ids, bool retry)
    {
        var gate = FileReviewController.GateOf(svc.Store);
        await gate.WaitAsync().ConfigureAwait(true);
        try
        {
            var unmarked = FileReviewController.UnmarkedOf(svc.Store);
            var targets = retry ? ids.Where(unmarked.Contains).ToArray() : ids;
            return targets.Length > 0 ? await MarkCoreAsync(svc, targets).ConfigureAwait(true) : default;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Marks one batch and reports the ids the store did not record, or the store failure itself.</summary>
    private static async Task<LocText> MarkCoreAsync(ReviewServices svc, long[] ids)
    {
        var unmarked = FileReviewController.UnmarkedOf(svc.Store);
        try
        {
            var done = await svc.Store.MarkSentAsync(ids).ConfigureAwait(true);
            var marked = done.Select(static comment => comment.Id).ToHashSet();
            foreach (var id in ids)
            {
                if (marked.Contains(id))
                {
                    unmarked.Remove(id);
                }
                else
                {
                    unmarked.Add(id);
                }
            }

            var missing = ids.Count(id => !marked.Contains(id));
            return missing == 0
                ? default
                : new LocText(ReviewStrings.SentNotMarked, LocText.Literal($"{missing}/{ids.Length}"));
        }
        catch (Exception exception) when (FileReviewController.IsStoreFailure(exception))
        {
            Trace.WriteLine("[review] marking sent failed: " + exception.Message);
            foreach (var id in ids)
            {
                unmarked.Add(id);
            }

            return new LocText(ReviewStrings.SentNotMarked, LocText.Literal(exception.Message));
        }
    }
}
