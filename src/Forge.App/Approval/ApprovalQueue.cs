using System.Diagnostics;
using Forge.App.ViewModels;

namespace Forge.App.Approval;

/// <summary>
/// The non-modal approval queue. It belongs to the UI thread: <see cref="Items"/> is only ever touched there, and every
/// entry point marshals through <see cref="IApprovalDispatcher"/> so an agent loop (a pool thread) can enqueue a card and
/// the session release path can withdraw one. Each card carries its own decision task, so several sessions' cards can wait
/// side by side without blocking anything else.
/// </summary>
internal sealed class ApprovalQueue
{
    private readonly RangeObservableCollection<ApprovalCardViewModel> items = [];

    public ApprovalQueue(IApprovalDispatcher? dispatcher = null) => Dispatcher = dispatcher ?? UiThreadDispatcher.Instance;

    /// <summary>Raised on the UI thread right after a card was added; the overlay expands itself on it.</summary>
    public event Action<ApprovalCardViewModel>? CardAdded;

    /// <summary>Raised on the UI thread after a card was added or removed.</summary>
    public event Action? Changed;

    public IApprovalDispatcher Dispatcher { get; }

    /// <summary>The queued cards; UI thread only.</summary>
    public RangeObservableCollection<ApprovalCardViewModel> Items => items;

    /// <summary>
    /// Adds a card on the UI thread and reports whether it was added; a card whose id is already queued is not added
    /// twice, so the caller can withdraw the older one instead of leaving two cards for one decision.
    /// </summary>
    public async Task<bool> EnqueueAsync(ApprovalCardViewModel card, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(card);
        ct.ThrowIfCancellationRequested();
        return await Dispatcher.InvokeAsync(
                () =>
                {
                    if (IndexOf(card.Id) >= 0)
                    {
                        return false;
                    }

                    items.Add(card);
                    RaiseCardAdded(card);
                    RaiseChanged();
                    return true;
                })
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a card and completes it, by default as a denial (a cancelled or released waiter never leaves a live card
    /// behind). A superseded card completes with <see cref="ApprovalCardDecision.Superseded"/> so its waiter knows it must
    /// not act on the decision at all.
    /// </summary>
    public Task WithdrawAsync(ApprovalCardViewModel card, ApprovalCardOutcome? outcome = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        return Dispatcher.InvokeAsync(
            () =>
            {
                var index = IndexOf(card.Id);
                if (index < 0 || !ReferenceEquals(items[index], card))
                {
                    card.Dispose();
                    return;
                }

                items.RemoveAt(index);
                card.TryComplete(outcome ?? card.WithdrawnOutcome);
                card.Dispose();
                RaiseChanged();
            });
    }

    /// <summary>Withdraws every card of one session, as a denial; used when the session runtime is released.</summary>
    public Task<int> WithdrawSessionAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Dispatcher.InvokeAsync(
            () =>
            {
                var removed = 0;
                for (var index = items.Count - 1; index >= 0; index--)
                {
                    if (!string.Equals(items[index].SessionId, sessionId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var card = items[index];
                    items.RemoveAt(index);
                    card.TryComplete(card.WithdrawnOutcome);
                    card.Dispose();
                    removed++;
                }

                if (removed > 0)
                {
                    RaiseChanged();
                }

                return removed;
            });
    }

    /// <summary>Withdraws every card (shutdown); each waiter sees a denial instead of hanging.</summary>
    public Task WithdrawAllAsync() =>
        Dispatcher.InvokeAsync(
            () =>
            {
                if (items.Count == 0)
                {
                    return;
                }

                var queued = items.ToArray();
                items.Clear();
                foreach (var card in queued)
                {
                    card.TryComplete(card.WithdrawnOutcome);
                    card.Dispose();
                }

                RaiseChanged();
            });

    /// <summary>Whether a card with this id is queued; marshals to the UI thread because the list belongs to it.</summary>
    public Task<bool> ContainsAsync(string id) =>
        Dispatcher.InvokeAsync(() => IndexOf(id) >= 0);

    /// <summary>Number of queued cards, read on the UI thread.</summary>
    public Task<int> CountAsync() => Dispatcher.InvokeAsync(() => items.Count);

    /// <summary>Finds a queued card by id on the UI thread; used by the settings section to open an existing card.</summary>
    public Task<ApprovalCardViewModel?> FindAsync(string id) =>
        Dispatcher.InvokeAsync(
            () =>
            {
                var index = IndexOf(id);
                return index < 0 ? null : items[index];
            });

    private int IndexOf(string id)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index].Id, id, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private void RaiseCardAdded(ApprovalCardViewModel card)
    {
        try
        {
            CardAdded?.Invoke(card);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.WriteLine("[approval] a card-added subscriber failed: " + exception.Message);
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.WriteLine("[approval] a queue subscriber failed: " + exception.Message);
        }
    }
}
