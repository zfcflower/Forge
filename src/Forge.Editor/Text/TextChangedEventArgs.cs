namespace Forge.Editor.Text;

/// <summary>Raised after a document version has been committed.</summary>
public sealed class TextChangedEventArgs : EventArgs
{
    public TextChangedEventArgs(TextSnapshot old, TextSnapshot @new, IReadOnlyList<TextChange> changes, TextChange minimalChange)
    {
        Old = old;
        New = @new;
        Changes = changes;
        MinimalChange = minimalChange;
    }

    public TextSnapshot Old { get; }

    public TextSnapshot New { get; }

    /// <summary>The changes as they were applied, in ascending offset order.</summary>
    public IReadOnlyList<TextChange> Changes { get; }

    /// <summary>Single equivalent change against <see cref="Old"/>; what incremental sync and incremental highlighting consume.</summary>
    public TextChange MinimalChange { get; }

    public LinePosition OldStartPosition => Old.OffsetToPosition(MinimalChange.Offset);

    public LinePosition OldEndPosition => Old.OffsetToPosition(MinimalChange.End);

    public int OldStartLine => Old.GetLineAtOffset(MinimalChange.Offset);

    public int OldEndLine => Old.GetLineAtOffset(MinimalChange.End);

    public int NewStartLine => New.GetLineAtOffset(MinimalChange.Offset);

    public int NewEndLine => New.GetLineAtOffset(MinimalChange.NewEnd);
}
