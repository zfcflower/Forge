using System.Data.Common;
using System.Runtime.CompilerServices;
using AvaloniaEdit.Document;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.App.Notifications;
using Forge.Workspace;
using ZLogger;

namespace Forge.App.Review;

/// <summary>
/// The draft lifecycle both review surfaces (the single-file editor and the multi-file diff) share: the
/// busy/loading/reload-pending state machine around operations and reloads, the mark/restore/retry pipeline, and the
/// store-level unmarked, restore-pending and gate tracking that serializes both surfaces on one
/// <see cref="ReviewStore"/>. Anchors, persistence details and sending stay in the subclasses behind the abstract
/// members.
/// </summary>
internal abstract class ReviewSurfaceCore : IReviewSurface
{
    private static readonly ConditionalWeakTable<ReviewStore, HashSet<long>> UnmarkedByStore = new();
    private static readonly ConditionalWeakTable<ReviewStore, HashSet<long>> RestorePendingByStore = new();
    private static readonly ConditionalWeakTable<ReviewStore, SemaphoreSlim> GateByStore = new();

    protected readonly List<ReviewDraft> drafts = [];
    protected LocText message;
    protected int generation;
    protected bool disposed;
    private bool busy;
    private bool loading;
    private bool reloadPending;

    public event EventHandler? Changed;
    public event EventHandler? DocumentReset;

    public IReadOnlyList<ReviewDraft> Drafts => drafts;
    public bool IsBusy => busy || loading;

    public abstract TextDocument? Document { get; }
    public abstract bool CanCompose { get; }
    public abstract LocText Status { get; }
    public abstract bool CanCommentOn(int startLine, int endLine);
    public abstract ReviewDraft? BeginDraft(int startLine, int endLine);
    public abstract void Discard(ReviewDraft draft);
    public abstract bool IsLost(ReviewDraft draft);
    public abstract Task<bool> SendAsync(ReviewDraft draft, string body);
    public abstract Task<bool> SendFileAsync();

    /// <summary>The review services this surface is configured with, or <c>null</c> while it has no owner.</summary>
    protected abstract ReviewServices? Services { get; }

    /// <summary>The key the failure notification is grouped under; each surface keeps its own.</summary>
    protected abstract string FailureNotificationKey { get; }

    public Task<bool> SaveAsync(ReviewDraft draft, string body) =>
        RunAsync(async (g, doc) =>
        {
            if (!await PersistAsync(g, doc, draft, body).ConfigureAwait(true))
            {
                return false;
            }

            SetMessage(new LocText(ReviewStrings.Saved));
            Services?.Changed();
            return true;
        });

    public Task<bool> DeleteAsync(ReviewDraft draft) =>
        RunAsync(async (g, doc) =>
        {
            if (draft.Id != 0 && Services is { } svc)
            {
                if (!await svc.Store.DeleteAsync(draft.Id).ConfigureAwait(true))
                {
                    _ = Fire(ReloadAsync);
                    throw new InvalidOperationException("The comment no longer exists.");
                }

                UnmarkedOf(svc.Store).Remove(draft.Id);
                RestorePendingOf(svc.Store).Remove(draft.Id);
                if (Stale(g, doc))
                {
                    svc.Changed();
                    return true;
                }
            }

            draft.Release();
            RemoveDraft(draft);
            Raise();
            Services?.Changed();
            return true;
        });

    /// <summary>Runs one user operation: refused while another one is in flight, failures shown through <see cref="SetFailure"/>.</summary>
    protected async Task<bool> RunAsync(Func<int, TextDocument, Task<bool>> body)
    {
        if (IsBusy)
        {
            SetMessage(new LocText(ReviewStrings.Busy));
            return false;
        }

        if (!CanCompose || Document is not { } doc)
        {
            Raise();
            return false;
        }

        busy = true;
        Raise();
        try
        {
            return await body(generation, doc).ConfigureAwait(true);
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            AppLogs.Review.ZLogWarning($"operation failed: {exception.Message}");
            SetFailure(new LocText(ReviewStrings.StoreFailed, LocText.Literal(exception.Message)));
            return false;
        }
        finally
        {
            busy = false;
            if (reloadPending && !disposed)
            {
                reloadPending = false;
                _ = Fire(ReloadAsync);
            }

            Raise();
        }
    }

    /// <summary>
    /// Reloads the stored drafts: releases the current ones, then fills them again through <see cref="LoadAsync"/>. A
    /// reload that lands while an operation is in flight is deferred until it ends.
    /// </summary>
    protected async Task ReloadAsync()
    {
        var current = ++generation;
        var doc = Document;
        OnReloadBegin(doc);
        ReleaseAll();
        DocumentReset?.Invoke(this, EventArgs.Empty);
        if (busy)
        {
            reloadPending = true;
            Raise();
            return;
        }

        loading = true;
        Raise();
        try
        {
            await LoadAsync(current, doc).ConfigureAwait(true);
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            AppLogs.Review.ZLogWarning($"loading drafts failed: {exception.Message}");
            SetFailure(new LocText(ReviewStrings.StoreFailed, LocText.Literal(exception.Message)));
        }
        finally
        {
            if (current == generation)
            {
                loading = false;
                Raise();
            }
        }
    }

    /// <summary>Runs before the current drafts are released; the file surface rebinds its document listeners here.</summary>
    protected virtual void OnReloadBegin(TextDocument? doc)
    {
    }

    /// <summary>Reads the stored drafts into <see cref="drafts"/>; <paramref name="doc"/> is the document the reload started with.</summary>
    protected abstract Task LoadAsync(int current, TextDocument? doc);

    /// <summary>Marks this surface's ids sent through <see cref="ReviewMarking" /> and shows what it reported.</summary>
    protected async Task MarkAsync(ReviewServices svc, long[] ids, bool retry = false)
    {
        var reported = await ReviewMarking.MarkAsync(svc, ids, retry).ConfigureAwait(true);
        if (!reported.IsEmpty)
        {
            SetMessage(reported);
        }
    }

    /// <summary>
    /// Retries the drafts a previous send left unmarked: restores what is restore-pending, re-marks what is still
    /// unmarked and releases the drafts that came back fully sent.
    /// </summary>
    protected async Task<bool> RetryUnmarkedAsync(ReviewServices svc)
    {
        var pending = RestorePendingOf(svc.Store);
        if (pending.Count > 0)
        {
            await TryRestoreAsync(svc, pending.ToArray()).ConfigureAwait(true);
        }

        var unmarked = UnmarkedOf(svc.Store);
        if (unmarked.Count == 0)
        {
            return pending.Count == 0;
        }

        foreach (var id in unmarked.ToArray())
        {
            if (await svc.Store.GetAsync(id).ConfigureAwait(true) is null)
            {
                unmarked.Remove(id);
            }
        }

        if (unmarked.Count > 0)
        {
            await MarkAsync(svc, unmarked.ToArray(), retry: true).ConfigureAwait(true);
        }

        foreach (var done in drafts.Where(draft => draft.IsSent && !unmarked.Contains(draft.Id)).ToArray())
        {
            done.Release();
            RemoveDraft(done);
        }

        Raise();
        return unmarked.Count == 0 && pending.Count == 0;
    }

    /// <summary>Drops one sent draft from this surface; the diff surface also forgets its draft info.</summary>
    protected virtual void RemoveDraft(ReviewDraft draft) => drafts.Remove(draft);

    private async Task<bool> TryRestoreAsync(ReviewServices svc, long[] ids)
    {
        var pending = RestorePendingOf(svc.Store);
        foreach (var id in ids)
        {
            pending.Add(id);
        }
        var gate = GateOf(svc.Store);
        await gate.WaitAsync().ConfigureAwait(true);
        try
        {
            return await TryRestoreCoreAsync(svc, ids).ConfigureAwait(true);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> TryRestoreCoreAsync(ReviewServices svc, long[] ids)
    {
        var unmarked = UnmarkedOf(svc.Store);
        var pending = RestorePendingOf(svc.Store);
        foreach (var id in ids)
        {
            unmarked.Remove(id);
            pending.Add(id);
        }
        try
        {
            await svc.Store.RestoreDraftsAsync(ids).ConfigureAwait(true);
            foreach (var id in ids)
            {
                pending.Remove(id);
            }
            return true;
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            AppLogs.Review.ZLogWarning($"restoring drafts failed: {exception.Message}");
            SetMessage(new LocText(ReviewStrings.RestoreFailed, LocText.Literal(exception.Message)));
            return false;
        }
    }

    protected async Task RestoreAsync(ReviewServices svc, long[] ids)
    {
        if (await TryRestoreAsync(svc, ids).ConfigureAwait(true))
        {
            SetMessage(new LocText(ReviewStrings.Restored));
        }
        svc.Changed();
        if (!disposed)
        {
            _ = Fire(ReloadAsync);
        }
    }

    /// <summary>Stores one draft (or updates it); each surface anchors it its own way.</summary>
    protected abstract Task<bool> PersistAsync(int g, TextDocument doc, ReviewDraft draft, string? body);

    /// <summary>Whether the document changed under the running operation (generation, document or faulted state).</summary>
    protected abstract bool Stale(int g, TextDocument doc);

    protected virtual void ReleaseAll()
    {
        foreach (var draft in drafts)
        {
            draft.Release();
        }

        drafts.Clear();
    }

    protected bool FailChanged()
    {
        SetMessage(new LocText(ReviewStrings.ChangedDuringSave));
        return false;
    }

    /// <summary>Shows the failure in the panel and announces it: a review that could not be saved is missed otherwise.</summary>
    protected virtual void SetFailure(LocText value)
    {
        SetMessage(value);
        NotificationCenter.Current?.Notify(
            NotificationLevel.Error,
            NotificationsStrings.EditorFailedTitle,
            value,
            FailureNotificationKey);
    }

    protected void SetMessage(LocText value)
    {
        message = value;
        Raise();
    }

    protected void Raise()
    {
        if (!disposed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    protected async Task Fire(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppLogs.Review.ZLogError(exception, $"background work failed");
            SetFailure(new LocText(ReviewStrings.StoreFailed, LocText.Literal(exception.Message)));
        }
    }

    /// <summary>The ids a store has not recorded as sent; shared by every surface of that store.</summary>
    internal static HashSet<long> UnmarkedOf(ReviewStore store) => UnmarkedByStore.GetOrCreateValue(store);

    /// <summary>The ids whose restore is in flight; shared by every surface of that store.</summary>
    internal static HashSet<long> RestorePendingOf(ReviewStore store) => RestorePendingByStore.GetOrCreateValue(store);

    /// <summary>The gate that serializes marking and restoring against one store.</summary>
    internal static SemaphoreSlim GateOf(ReviewStore store) => GateByStore.GetValue(store, static _ => new SemaphoreSlim(1, 1));

    internal static bool IsBlocked(ReviewStore store, ReviewDraft draft) =>
        draft.IsSent || UnmarkedOf(store).Contains(draft.Id) || RestorePendingOf(store).Contains(draft.Id);

    internal static bool IsStoreFailure(Exception exception) =>
        exception is DbException or IOException or InvalidOperationException or ArgumentException;
}
