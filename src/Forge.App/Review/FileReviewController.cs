using System.ComponentModel;
using AvaloniaEdit.Document;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Workspace;

namespace Forge.App.Review;

internal sealed record ReviewOwner(string WorkspacePath, string SessionId);
internal sealed record ReviewServices(ReviewStore Store, Func<string, string, Action, bool> Send, Action Changed);

/// <summary>Drafts pinned to the file's owning session. Sending saves and validates before queueing.</summary>
internal sealed class FileReviewController : ReviewSurfaceCore, IDisposable
{
    private readonly FileDocumentViewModel model;
    private ReviewServices? services;
    private ReviewOwner? owner;
    private TextDocument? bound;
    private int edits;

    public FileReviewController(FileDocumentViewModel model)
    {
        this.model = model;
        model.PropertyChanged += OnModelChanged;
    }

    public override TextDocument? Document => model.EditorMirror?.Document;

    public ReviewOwner? Owner => owner;

    public override bool CanCompose =>
        !disposed && services is not null && owner is not null && Document is not null
        && !model.IsEditorFaulted && RelativePath() is not null;

    public override LocText Status
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

    protected override ReviewServices? Services => services;

    protected override string FailureNotificationKey => "review-file";

    public void Configure(ReviewServices? newServices, ReviewOwner? newOwner)
    {
        services = newServices;
        owner = newOwner;
        _ = Fire(ReloadAsync);
    }

    public override bool CanCommentOn(int startLine, int endLine) => true;

    public override ReviewDraft? BeginDraft(int startLine, int endLine)
    {
        if (!CanCompose || Document is not { LineCount: > 0 } doc)
        {
            return null;
        }

        startLine = Math.Clamp(startLine, 1, doc.LineCount);
        return ReviewDraft.Create(doc, startLine, Math.Clamp(endLine, startLine, doc.LineCount));
    }

    public override void Discard(ReviewDraft draft)
    {
        if (!IsBusy)
        {
            draft.Release();
        }
    }

    public override bool IsLost(ReviewDraft draft) => Document is not { } doc || draft.IsLostCached(doc, edits);

    public override Task<bool> SendAsync(ReviewDraft draft, string body) => SendCoreAsync([draft], draft, body, single: true);

    public override Task<bool> SendFileAsync()
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

    protected override async Task<bool> PersistAsync(int g, TextDocument doc, ReviewDraft draft, string? body)
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

    protected override async Task LoadAsync(int current, TextDocument? doc)
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

    protected override void OnReloadBegin(TextDocument? doc)
    {
        if (!ReferenceEquals(doc, bound))
        {
            Unbind();
            bound = doc;
            if (doc is not null)
            {
                doc.Changed += OnDocumentChanged;
            }
        }
    }

    protected override bool Stale(int g, TextDocument doc) =>
        disposed || g != generation || !ReferenceEquals(Document, doc) || model.IsEditorFaulted;

    private bool Unchanged(int g, TextDocument doc, int version, IReadOnlyList<ReviewDraft> targets) =>
        !Stale(g, doc) && !model.IsDirty && model.ReviewVersion == version && !targets.Any(target => target.IsLost(doc));

    private void Unbind()
    {
        if (bound is not null)
        {
            bound.Changed -= OnDocumentChanged;
            bound = null;
        }
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
}
