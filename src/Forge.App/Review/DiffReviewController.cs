using AvaloniaEdit.Document;
using Forge.App.Changes;
using Forge.App.Localization;
using Forge.Workspace;

namespace Forge.App.Review;

/// <summary>
/// Review drafts over the multi-file diff document. A draft is displayed on document lines of the diff editor but lives in
/// new-file coordinates (<see cref="DraftInfo"/>): the store, the anchor and the quote never see document lines. Sending
/// does not save any file; the messages are built per file like the single-file editor does.
/// </summary>
internal sealed class DiffReviewController : ReviewSurfaceCore, IDisposable
{
    private readonly Dictionary<ReviewDraft, DraftInfo> infos = [];
    private IReadOnlyList<IDiffFileItem> files = [];
    private DiffRowMap map = DiffRowMap.Empty;
    private TextDocument? document;
    private DiffReviewContext? context;

    public override TextDocument? Document => document;

    public override bool CanCompose => !disposed && context is not null && document is not null && files.Count > 0;

    /// <summary>
    /// Without an owner the diff stays silent: headers and block bars work without comments, and a status line at the top edge
    /// would sit on the first file header's buttons. (The single-file editor keeps its own "no owner" message.)
    /// </summary>
    public override LocText Status => context is null ? default : message;

    protected override ReviewServices? Services => context?.Services;

    protected override string FailureNotificationKey => "review-diff";

    /// <summary>Sets (or clears) the review services; reloads the drafts.</summary>
    public void Configure(DiffReviewContext? value)
    {
        context = value;
        _ = Fire(ReloadAsync);
    }

    /// <summary>The diff document was rebuilt: re-resolve every stored draft of the shown files against it.</summary>
    public void Rebind(TextDocument? newDocument, DiffRowMap newMap, IReadOnlyList<IDiffFileItem> newFiles)
    {
        document = newDocument;
        map = newMap;
        files = newFiles;
        _ = Fire(ReloadAsync);
    }

    public override bool CanCommentOn(int startLine, int endLine) =>
        CanCompose && map.TryMapRange(startLine, endLine, out _);

    public override ReviewDraft? BeginDraft(int startLine, int endLine)
    {
        if (!CanCompose || document is not { LineCount: > 0 } doc)
        {
            return null;
        }

        startLine = Math.Clamp(startLine, 1, doc.LineCount);
        endLine = Math.Clamp(endLine, startLine, doc.LineCount);
        if (!map.TryMapRange(startLine, endLine, out var range))
        {
            return null;
        }

        var draft = ReviewDraft.Create(doc, startLine, endLine);
        infos[draft] = new DraftInfo(range.File, range.NewStart, range.NewEnd, QuoteOf(range.File, range.NewStart, range.NewEnd));
        return draft;
    }

    public override void Discard(ReviewDraft draft)
    {
        if (!IsBusy)
        {
            draft.Release();
            infos.Remove(draft);
        }
    }

    public override bool IsLost(ReviewDraft draft) => document is not { } doc || draft.IsLostCached(doc, generation);

    public override Task<bool> SendAsync(ReviewDraft draft, string body) =>
        SendBatchAsync([draft], draft, body, single: true, PathOf(draft));

    /// <summary>Sends every unsent draft of the shown files, one message per file.</summary>
    public override async Task<bool> SendFileAsync()
    {
        var targets = drafts.Where(static draft => !draft.IsSent).ToArray();
        if (targets.Length == 0)
        {
            if (!drafts.Any(static draft => draft.IsSent))
            {
                SetMessage(new LocText(ReviewStrings.NoDrafts));
                return false;
            }

            await RunAsync(async (_, _) =>
            {
                if (context?.Services is { } svc)
                {
                    await RetryUnmarkedAsync(svc).ConfigureAwait(true);
                }

                return true;
            }).ConfigureAwait(true);
            SetMessage(new LocText(ReviewStrings.NoDrafts));
            return false;
        }

        foreach (var group in targets.GroupBy(PathOf))
        {
            if (!await SendBatchAsync([.. group], null, null, single: false, group.Key).ConfigureAwait(true))
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        generation++;
        ReleaseAll();
    }

    private string PathOf(ReviewDraft draft) =>
        infos.TryGetValue(draft, out var info) && (uint)info.File < (uint)files.Count ? files[info.File].Path : string.Empty;

    /// <summary>New-side text of the new lines <paramref name="start"/>..<paramref name="end"/> of a file, from the real diff.</summary>
    private string QuoteOf(int file, int start, int end)
    {
        if ((uint)file >= (uint)files.Count)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var hunk in files[file].Diff.Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                if (line.Kind != DiffLineKind.Removed && line.NewLineNumber >= start && line.NewLineNumber <= end)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append('\n');
                    }

                    builder.Append(line.Text);
                }
            }
        }

        return builder.ToString();
    }

    private Task<bool> SendBatchAsync(IReadOnlyList<ReviewDraft> targets, ReviewDraft? edited, string? body, bool single, string path) =>
        RunAsync(async (g, doc) =>
        {
            if (context?.Services is not { } svc || context.Owner is not { } scope || path.Length == 0)
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

            if (targets.Any(IsLost))
            {
                SetMessage(new LocText(ReviewStrings.Lost));
                return false;
            }

            if (!single && drafts.Any(draft => !draft.IsSent && IsLost(draft) && string.Equals(PathOf(draft), path, StringComparison.Ordinal)))
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

            if (Stale(g, doc))
            {
                return FailChanged();
            }

            var quotes = targets.ToDictionary(static draft => draft.Id, draft => infos.TryGetValue(draft, out var info) ? info.Quote : null);
            string? Quote(ReviewComment comment) => quotes.TryGetValue(comment.Id, out var quote) ? quote : null;
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
            if (!sameSet || Stale(g, doc))
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
                    RemoveDraft(target);
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
        if (context?.Services is not { } svc || context.Owner is not { } scope || !infos.TryGetValue(draft, out var info)
            || (uint)info.File >= (uint)files.Count)
        {
            return false;
        }

        var item = files[info.File];
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

        if (IsLost(draft))
        {
            SetMessage(new LocText(ReviewStrings.Lost));
            return false;
        }

        DiffAnchor anchor;
        try
        {
            anchor = DiffAnchor.Create(item.Diff, info.NewStart, info.NewEnd - info.NewStart + 1);
        }
        catch (ArgumentOutOfRangeException)
        {
            SetMessage(new LocText(ReviewStrings.Lost));
            return false;
        }

        var start = info.NewStart;
        var end = info.NewEnd;
        if (draft.Id == 0)
        {
            var added = await svc.Store.AddAsync(new NewReviewComment
            {
                WorkspacePath = scope.WorkspacePath,
                SessionId = scope.SessionId,
                FilePath = item.Path,
                NewLineStart = start,
                NewLineEnd = end,
                Body = text,
                BaseCommit = context.BaseCommit,
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
        return true;
    }

    protected override async Task LoadAsync(int current, TextDocument? doc)
    {
        var shown = files;
        var rows = map;
        if (disposed || doc is null || context?.Services is not { } svc || context.Owner is not { } scope)
        {
            return;
        }

        await RetryUnmarkedAsync(svc).ConfigureAwait(true);
        var stored = await Task.Run(() => svc.Store.GetDraftsAsync(scope.WorkspacePath, scope.SessionId)).ConfigureAwait(true);
        if (stored.Count == 0 || current != generation)
        {
            return;
        }

        var unmarked = UnmarkedOf(svc.Store);
        var resolvers = new DiffAnchorResolver?[shown.Count];
        foreach (var comment in stored)
        {
            var file = IndexOf(shown, comment.FilePath);
            if (file < 0 || !rows.Contains(rows.HeaderLine(file)))
            {
                continue;
            }

            var resolver = resolvers[file] ??= new DiffAnchorResolver(shown[file].Diff);
            var match = comment.Anchor is { } anchor ? resolver.Resolve(anchor) : null;
            ReviewDraft draft;
            DraftInfo info;
            if (match is null || match.State == DiffAnchorState.Stale || match.NewCount < 1)
            {
                var header = rows.HeaderLine(file);
                draft = ReviewDraft.Detached(comment with { NewLineStart = header, NewLineEnd = header });
                info = new DraftInfo(file, comment.NewLineStart, comment.NewLineEnd, string.Empty);
            }
            else
            {
                var start = match.NewStart;
                var end = match.NewStart + match.NewCount - 1;
                var (first, last) = DisplayLines(rows, shown, file, start, end);
                draft = ReviewDraft.Create(doc, first, last, comment.Id, comment.Body);
                info = new DraftInfo(file, start, end, QuoteOf(shown, file, start, end));
            }

            draft.IsSent = unmarked.Contains(comment.Id);
            drafts.Add(draft);
            infos[draft] = info;
        }

        drafts.Sort(static (a, b) => a.StoredStart.CompareTo(b.StoredStart));
    }

    /// <summary>
    /// Document lines a draft over new lines start..end is displayed on: the lines themselves when visible, else the fold bar
    /// that hides them, else the file's header (collapsed file).
    /// </summary>
    private static (int First, int Last) DisplayLines(DiffRowMap rows, IReadOnlyList<IDiffFileItem> shown, int file, int start, int end)
    {
        if (rows.TryFindNewLine(file, start, out var first))
        {
            if (!rows.TryFindNewLine(file, end, out var last) || last < first)
            {
                last = first;
            }

            return (first, last);
        }

        var from = rows.HeaderLine(file);
        var to = rows.FileLastLine(file);
        for (var line = from; line <= to; line++)
        {
            var row = rows.ForLine(line);
            if (row.Kind == DiffRowKind.Fold && row.Key >= 0 && start >= row.Key && start < row.Key + row.Count)
            {
                return (line, line);
            }
        }

        return (from, from);
    }

    private static string QuoteOf(IReadOnlyList<IDiffFileItem> shown, int file, int start, int end)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var hunk in shown[file].Diff.Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                if (line.Kind != DiffLineKind.Removed && line.NewLineNumber >= start && line.NewLineNumber <= end)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append('\n');
                    }

                    builder.Append(line.Text);
                }
            }
        }

        return builder.ToString();
    }

    private static int IndexOf(IReadOnlyList<IDiffFileItem> shown, string path)
    {
        for (var i = 0; i < shown.Count; i++)
        {
            if (string.Equals(shown[i].Path, path, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    protected override bool Stale(int g, TextDocument doc) => disposed || g != generation || !ReferenceEquals(document, doc);

    protected override void RemoveDraft(ReviewDraft draft)
    {
        infos.Remove(draft);
        base.RemoveDraft(draft);
    }

    protected override void ReleaseAll()
    {
        base.ReleaseAll();
        infos.Clear();
    }

    /// <summary>A draft in new-file coordinates: file index, new-side range and the new-side text it quotes.</summary>
    private sealed record DraftInfo(int File, int NewStart, int NewEnd, string Quote);
}
