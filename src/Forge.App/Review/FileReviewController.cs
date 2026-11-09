using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using AvaloniaEdit.Document;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Workspace;

namespace Forge.App.Review;

internal sealed record ReviewOwner(string WorkspacePath, string SessionId);
internal sealed record ReviewServices(ReviewStore Store, Func<string, string, Action, bool> Send, Action Changed);

/// <summary>Drafts pinned to the file's owning session. Sending saves and validates before queueing.</summary>
internal sealed class FileReviewController : IReviewSurface, IDisposable
{
    private static readonly ConditionalWeakTable<ReviewStore, HashSet<long>> UnmarkedByStore = new();
    private static readonly ConditionalWeakTable<ReviewStore, HashSet<long>> RestorePendingByStore = new();
    private static readonly ConditionalWeakTable<ReviewStore, SemaphoreSlim> GateByStore = new();
    private readonly FileDocumentViewModel model;
    private readonly List<ReviewDraft> drafts = [];
    private ReviewServices? services;
    private ReviewOwner? owner;
    private TextDocument? bound;
    private LocText message;
    private int generation;
    private int edits;
    private bool busy;
    private bool loading;
    private bool reloadPending;
    private bool disposed;

    public FileReviewController(FileDocumentViewModel model)
    {
        this.model = model;
        model.PropertyChanged += OnModelChanged;
    }

    public event EventHandler? Changed;
    public event EventHandler? DocumentReset;
    public IReadOnlyList<ReviewDraft> Drafts => drafts;
    public ReviewOwner? Owner => owner;
    public bool IsBusy => busy || loading;
    public TextDocument? Document => model.EditorMirror?.Document;
    public bool CanCompose =>
        !disposed && services is not null && owner is not null && Document is not null
        && !model.IsEditorFaulted && RelativePath() is not null;

    public LocText Status
    {
        get
        {
            if (owner is null || services is null || (model.Path.Length > 0 && RelativePath() is null))
            {
                return new LocText(ReviewStrings.NoOwner);
            }

            return model.IsEditorFaulted ? new LocText(ReviewStrings.Faulted) : message;
        }
    }

    public void Configure(ReviewServices? newServices, ReviewOwner? newOwner)
    {
        services = newServices;
        owner = newOwner;
        _ = Fire(ReloadAsync);
    }

    public bool CanCommentOn(int startLine, int endLine) => true;

    public ReviewDraft? BeginDraft(int startLine, int endLine)
    {
        if (!CanCompose || Document is not { LineCount: > 0 } doc)
        {
            return null;
        }

        startLine = Math.Clamp(startLine, 1, doc.LineCount);
        return ReviewDraft.Create(doc, startLine, Math.Clamp(endLine, startLine, doc.LineCount));
    }

    public void Discard(ReviewDraft draft)
    {
        if (!IsBusy)
        {
            draft.Release();
        }
    }

    public bool IsLost(ReviewDraft draft) => Document is not { } doc || draft.IsLostCached(doc, edits);

    public Task<bool> SaveAsync(ReviewDraft draft, string body) =>
        RunAsync(async (g, doc) =>
        {
            if (!await PersistAsync(g, doc, draft, body).ConfigureAwait(true))
            {
                return false;
            }

            SetMessage(new LocText(ReviewStrings.Saved));
            services?.Changed();
            return true;
        });

    public Task<bool> DeleteAsync(ReviewDraft draft) =>
        RunAsync(async (g, doc) =>
        {
            if (draft.Id != 0 && services is { } svc)
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
            drafts.Remove(draft);
            Raise();
            services?.Changed();
            return true;
        });

    public Task<bool> SendAsync(ReviewDraft draft, string body) => SendCoreAsync([draft], draft, body, single: true);

    public Task<bool> SendFileAsync()
    {
        var targets = drafts.Where(static draft => !draft.IsSent).ToArray();
        if (targets.Length == 0 && !drafts.Any(static draft => draft.IsSent))
        {
            SetMessage(new LocText(ReviewStrings.NoDrafts));
            return Task.FromResult(false);
        }

        return SendCoreAsync(targets, null, null, single: false);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        generation++;
        model.PropertyChanged -= OnModelChanged;
        Unbind();
        ReleaseAll();
    }

    private Task<bool> SendCoreAsync(IReadOnlyList<ReviewDraft> targets, ReviewDraft? edited, string? body, bool single) =>
        RunAsync(async (g, doc) =>
        {
            if (services is not { } svc || owner is not { } scope || RelativePath() is not { } path)
            {
                return false;
            }

            if (!single && !await RetryUnmarkedAsync(svc).ConfigureAwait(true))
            {
                return false;
            }

            if (Stale(g, doc))
            {
                return FailChanged();
            }

            var version = model.ReviewVersion;
            if (targets.Any(target => IsBlocked(svc.Store, target)))
            {
                SetMessage(new LocText(ReviewStrings.SentNotMarked, LocText.Literal(string.Empty)));
                return false;
            }

            if (targets.Count == 0)
            {
                SetMessage(new LocText(ReviewStrings.NoDrafts));
                return false;
            }

            if (targets.Any(target => target.IsLost(doc)))
            {
                SetMessage(new LocText(ReviewStrings.Lost));
                return false;
            }

            if (!single && drafts.Any(draft => !draft.IsSent && draft.IsLost(doc)))
            {
                SetMessage(new LocText(ReviewStrings.LostInFile));
                return false;
            }

            foreach (var target in targets)
            {
                if (!await PersistAsync(g, doc, target, ReferenceEquals(target, edited) ? body : null).ConfigureAwait(true))
                {
                    return false;
                }
            }

            if (!await model.ReviewSaveAsync().ConfigureAwait(true))
            {
                SetMessage(new LocText(ReviewStrings.SaveFailed, LocText.Literal(model.SaveError ?? string.Empty)));
                return false;
            }

            if (!Unchanged(g, doc, version, targets))
            {
                return FailChanged();
            }

            var quotes = targets.ToDictionary(static draft => draft.Id);
            string? Quote(ReviewComment comment) => quotes.TryGetValue(comment.Id, out var draft) ? draft.Quote : null;
            var dispatcher = new ReviewDispatcher(svc.Store);
            var dispatch = single
                ? await dispatcher.BuildSingleAsync(targets[0].Id, null, 2, CancellationToken.None, Quote).ConfigureAwait(true)
                : await dispatcher.BuildFileAsync(scope.WorkspacePath, scope.SessionId, path, Quote).ConfigureAwait(true);
            if (dispatch is null)
            {
                SetMessage(new LocText(ReviewStrings.NoDrafts));
                return false;
            }

            var sameSet = dispatch.CommentIds.Count == targets.Count && dispatch.CommentIds.All(quotes.ContainsKey);
            if (!sameSet || !Unchanged(g, doc, version, targets))
            {
                if (!sameSet)
                {
                    _ = Fire(ReloadAsync);
                }

                return FailChanged();
            }

            var ids = targets.Select(static draft => draft.Id).ToArray();
            var withdrawn = false;
            var marked = false;
            void OnWithdrawn()
            {
                withdrawn = true;
                if (marked)
                {
                    _ = Fire(() => RestoreAsync(svc, ids));
                }
            }
            if (!svc.Send(scope.SessionId, dispatch.Text, OnWithdrawn))
            {
                SetMessage(new LocText(ReviewStrings.SessionGone));
                return false;
            }

            await MarkAsync(svc, ids).ConfigureAwait(true);
            marked = true;
            if (withdrawn)
            {
                await RestoreAsync(svc, ids).ConfigureAwait(true);
                return true;
            }
            var unmarked = UnmarkedOf(svc.Store);
            foreach (var target in targets)
            {
                target.IsSent = true;
                if (!unmarked.Contains(target.Id))
                {
                    target.Release();
                    drafts.Remove(target);
                }
            }

            if (!targets.Any(target => unmarked.Contains(target.Id)))
            {
                SetMessage(new LocText(ReviewStrings.Sent));
            }

            Raise();
            svc.Changed();
            return true;
        });

    private async Task MarkAsync(ReviewServices svc, long[] ids, bool retry = false)
    {
        var gate = GateOf(svc.Store);
        await gate.WaitAsync().ConfigureAwait(true);
        try
        {
            var unmarked = UnmarkedOf(svc.Store);
            var targets = retry ? ids.Where(unmarked.Contains).ToArray() : ids;
            if (targets.Length > 0)
            {
                await MarkCoreAsync(svc, targets).ConfigureAwait(true);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task MarkCoreAsync(ReviewServices svc, long[] ids)
    {
        var unmarked = UnmarkedOf(svc.Store);
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
            if (missing > 0)
            {
                SetMessage(new LocText(ReviewStrings.SentNotMarked, LocText.Literal($"{missing}/{ids.Length}")));
            }
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            Trace.WriteLine("[review] marking sent failed: " + exception.Message);
            foreach (var id in ids)
            {
                unmarked.Add(id);
            }

            SetMessage(new LocText(ReviewStrings.SentNotMarked, LocText.Literal(exception.Message)));
        }
    }

    private async Task<bool> RetryUnmarkedAsync(ReviewServices svc)
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
            drafts.Remove(done);
        }

        Raise();
        return unmarked.Count == 0 && pending.Count == 0;
    }

    private async Task<bool> PersistAsync(int g, TextDocument doc, ReviewDraft draft, string? body)
    {
        if (services is not { } svc || owner is not { } scope || RelativePath() is not { } path)
        {
            return false;
        }

        var text = (body ?? draft.Body).Trim();
        if (IsBlocked(svc.Store, draft))
        {
            SetMessage(new LocText(ReviewStrings.SentNotMarked, LocText.Literal(string.Empty)));
            return false;
        }

        if (text.Length == 0)
        {
            SetMessage(new LocText(ReviewStrings.BodyEmpty));
            return false;
        }

        if (!draft.TryGetLines(out var start, out var end) || draft.IsLost(doc))
        {
            SetMessage(new LocText(ReviewStrings.Lost));
            return false;
        }

        var anchor = ReviewTextAnchors.Create(path, ReviewTextAnchors.RangeLines(doc, start, end), start);
        if (draft.Id == 0)
        {
            var added = await svc.Store.AddAsync(new NewReviewComment
            {
                WorkspacePath = scope.WorkspacePath,
                SessionId = scope.SessionId,
                FilePath = path,
                NewLineStart = start,
                NewLineEnd = end,
                Body = text,
                Anchor = anchor,
            }).ConfigureAwait(true);
            draft.Id = added.Id;
            if (Stale(g, doc) || draft.IsReleased)
            {
                return FailChanged();
            }

            drafts.Add(draft);
        }
        else
        {
            if (!string.Equals(text, draft.Body, StringComparison.Ordinal))
            {
                if (!await svc.Store.UpdateDraftBodyAsync(draft.Id, text).ConfigureAwait(true))
                {
                    throw new InvalidOperationException("The comment no longer exists as a draft.");
                }

                if (Stale(g, doc) || draft.IsReleased)
                {
                    return FailChanged();
                }
            }

            if (!await svc.Store.UpdateLocationAsync(draft.Id, start, end, anchor).ConfigureAwait(true))
            {
                throw new InvalidOperationException("The comment no longer exists as a draft.");
            }

            if (Stale(g, doc) || draft.IsReleased)
            {
                return FailChanged();
            }
        }

        draft.Body = text;
        draft.SetStored(start, end);
        return true;
    }

    private async Task<bool> RunAsync(Func<int, TextDocument, Task<bool>> body)
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
            Trace.WriteLine("[review] operation failed: " + exception.Message);
            SetMessage(new LocText(ReviewStrings.StoreFailed, LocText.Literal(exception.Message)));
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

    private async Task ReloadAsync()
    {
        var current = ++generation;
        var doc = Document;
        if (!ReferenceEquals(doc, bound))
        {
            Unbind();
            bound = doc;
            if (doc is not null)
            {
                doc.Changed += OnDocumentChanged;
            }
        }

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
            if (disposed || doc is null || services is not { } svc || owner is not { } scope || RelativePath() is not { } path)
            {
                return;
            }

            await RetryUnmarkedAsync(svc).ConfigureAwait(true);
            var stored = await Task.Run(() => svc.Store.GetDraftsAsync(scope.WorkspacePath, scope.SessionId, path)).ConfigureAwait(true);
            if (stored.Count == 0 || current != generation)
            {
                return;
            }

            var snapshot = ReviewTextAnchors.Snapshot(doc);
            var seen = edits;
            var resolved = await Task.Run(() =>
            {
                var lines = ReviewTextAnchors.SplitLines(snapshot);
                return stored.Select(comment => ReviewTextAnchors.Resolve(comment, lines)).ToArray();
            }).ConfigureAwait(true);
            if (current != generation)
            {
                return;
            }

            if (edits != seen)
            {
                _ = Fire(ReloadAsync);
                return;
            }

            var unmarked = UnmarkedOf(svc.Store);
            for (var i = 0; i < stored.Count; i++)
            {
                var found = resolved[i];
                var draft = found.State == ReviewAnchorState.Lost
                    ? ReviewDraft.Detached(stored[i])
                    : ReviewDraft.Create(doc, found.Start, found.End, stored[i].Id, stored[i].Body);
                draft.IsSent = unmarked.Contains(stored[i].Id);
                drafts.Add(draft);
            }

            drafts.Sort(static (a, b) => a.StoredStart.CompareTo(b.StoredStart));
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            Trace.WriteLine("[review] loading drafts failed: " + exception.Message);
            SetMessage(new LocText(ReviewStrings.StoreFailed, LocText.Literal(exception.Message)));
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

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileDocumentViewModel.EditorDocument))
        {
            _ = Fire(ReloadAsync);
        }
        else if (e.PropertyName == nameof(FileDocumentViewModel.IsEditorFaulted))
        {
            Raise();
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e) => edits++;

    private bool Stale(int g, TextDocument doc) =>
        disposed || g != generation || !ReferenceEquals(Document, doc) || model.IsEditorFaulted;

    private bool Unchanged(int g, TextDocument doc, int version, IReadOnlyList<ReviewDraft> targets) =>
        !Stale(g, doc) && !model.IsDirty && model.ReviewVersion == version && !targets.Any(target => target.IsLost(doc));

    private bool FailChanged()
    {
        SetMessage(new LocText(ReviewStrings.ChangedDuringSave));
        return false;
    }

    private void Unbind()
    {
        if (bound is not null)
        {
            bound.Changed -= OnDocumentChanged;
            bound = null;
        }
    }

    private void ReleaseAll()
    {
        foreach (var draft in drafts)
        {
            draft.Release();
        }

        drafts.Clear();
    }

    private string? RelativePath()
    {
        if (owner is null || model.Path.Length == 0)
        {
            return null;
        }

        var relative = Path.GetRelativePath(owner.WorkspacePath, model.Path);
        if (relative == "." || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)
            || relative.StartsWith("..\\", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return relative.Replace('\\', '/');
    }

    private void SetMessage(LocText value)
    {
        message = value;
        Raise();
    }

    private void Raise()
    {
        if (!disposed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal static HashSet<long> UnmarkedOf(ReviewStore store) => UnmarkedByStore.GetOrCreateValue(store);

    internal static HashSet<long> RestorePendingOf(ReviewStore store) => RestorePendingByStore.GetOrCreateValue(store);

    internal static SemaphoreSlim GateOf(ReviewStore store) => GateByStore.GetValue(store, static _ => new SemaphoreSlim(1, 1));

    internal static bool IsBlocked(ReviewStore store, ReviewDraft draft) =>
        draft.IsSent || UnmarkedOf(store).Contains(draft.Id) || RestorePendingOf(store).Contains(draft.Id);

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
            Trace.WriteLine("[review] restoring drafts failed: " + exception.Message);
            SetMessage(new LocText(ReviewStrings.RestoreFailed, LocText.Literal(exception.Message)));
            return false;
        }
    }

    private async Task RestoreAsync(ReviewServices svc, long[] ids)
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

    private async Task Fire(Func<Task> work)
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
            Trace.WriteLine("[review] background work failed: " + exception);
            SetMessage(new LocText(ReviewStrings.StoreFailed, LocText.Literal(exception.Message)));
        }
    }

    internal static bool IsStoreFailure(Exception exception) =>
        exception is DbException or IOException or InvalidOperationException or ArgumentException;
}
