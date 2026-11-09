using AvaloniaEdit.Document;
using Forge.App.Localization;

namespace Forge.App.Review;

/// <summary>
/// What <see cref="ReviewOverlay"/> needs from the thing that owns the drafts of the text it is drawn over: the single-file
/// editor controller and the multi-file diff controller both implement it.
/// </summary>
internal interface IReviewSurface
{
    event EventHandler? Changed;

    event EventHandler? DocumentReset;

    IReadOnlyList<ReviewDraft> Drafts { get; }

    bool IsBusy { get; }

    TextDocument? Document { get; }

    bool CanCompose { get; }

    LocText Status { get; }

    /// <summary>
    /// Whether the document lines <paramref name="startLine"/>..<paramref name="endLine"/> may carry a comment. A file editor
    /// accepts any range; the diff view rejects ranges that cross files or hold no new-side line.
    /// </summary>
    bool CanCommentOn(int startLine, int endLine);

    ReviewDraft? BeginDraft(int startLine, int endLine);

    void Discard(ReviewDraft draft);

    bool IsLost(ReviewDraft draft);

    Task<bool> SaveAsync(ReviewDraft draft, string body);

    Task<bool> DeleteAsync(ReviewDraft draft);

    Task<bool> SendAsync(ReviewDraft draft, string body);

    Task<bool> SendFileAsync();
}

/// <summary>Marks a left margin of a text area that behaves like a line-number gutter for the review overlay (click = comment on that line).</summary>
internal interface IReviewGutterMargin;
