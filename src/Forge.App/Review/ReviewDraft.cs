using AvaloniaEdit.Document;
using Forge.Workspace;

namespace Forge.App.Review;

/// <summary>
/// One draft on one file. Attached drafts follow edits through text anchors on the editor document; a draft whose
/// code could not be located on load is detached for good and can only be deleted.
/// </summary>
internal sealed class ReviewDraft
{
    private TextAnchor? first;
    private TextAnchor? last;
    private TextDocument? lostDocument;
    private int lostStamp = -1;
    private bool lostCached;

    private ReviewDraft(long id, string body, int storedStart, int storedEnd)
    {
        Id = id;
        Body = body;
        StoredStart = storedStart;
        StoredEnd = storedEnd;
    }

    public long Id { get; internal set; }
    public string Body { get; internal set; }
    public string Quote { get; private set; } = string.Empty;
    public int StoredStart { get; private set; }
    public int StoredEnd { get; private set; }
    public bool IsSent { get; internal set; }
    public bool IsReleased { get; private set; }
    public bool IsAttached => first is not null;

    public static ReviewDraft Create(TextDocument document, int startLine, int endLine, long id = 0, string body = "")
    {
        var draft = new ReviewDraft(id, body, startLine, endLine)
        {
            first = document.CreateAnchor(document.GetLineByNumber(startLine).Offset),
            last = document.CreateAnchor(document.GetLineByNumber(endLine).EndOffset),
            Quote = TextOf(document, startLine, endLine),
        };
        draft.first.MovementType = AnchorMovementType.AfterInsertion;
        draft.last.MovementType = AnchorMovementType.BeforeInsertion;
        return draft;
    }

    public static ReviewDraft Detached(ReviewComment comment) =>
        new(comment.Id, comment.Body, comment.NewLineStart, comment.NewLineEnd);

    public bool TryGetLines(out int start, out int end)
    {
        start = 0;
        end = 0;
        if (first is null || last is null || first.IsDeleted || last.IsDeleted)
        {
            return false;
        }

        start = first.Line;
        end = last.Line;
        return end >= start;
    }

    public bool IsLost(TextDocument document) =>
        IsReleased || !TryGetLines(out var start, out var end)
        || end > document.LineCount || !string.Equals(TextOf(document, start, end), Quote, StringComparison.Ordinal);

    public void SetStored(int start, int end)
    {
        StoredStart = start;
        StoredEnd = end;
    }

    public bool IsLostCached(TextDocument document, int stamp)
    {
        if (IsReleased)
        {
            return true;
        }
        if (!ReferenceEquals(lostDocument, document) || lostStamp != stamp)
        {
            lostCached = IsLost(document);
            lostDocument = document;
            lostStamp = stamp;
        }
        return lostCached;
    }

    public void Release()
    {
        IsReleased = true;
        first = null;
        last = null;
        lostDocument = null;
    }

    public static string TextOf(TextDocument document, int startLine, int endLine)
    {
        var a = document.GetLineByNumber(startLine);
        var b = document.GetLineByNumber(endLine);
        var text = document.GetText(a.Offset, b.EndOffset - a.Offset);
        return text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
    }
}
