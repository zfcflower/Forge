using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using EditorDocument = AvaloniaEdit.Document.TextDocument;

namespace Forge.App.Editing;

/// <summary>
/// Draws the language server's inlay hints — parameter names and inferred types — inside the text of the visible lines.
/// <para>Each hint is a zero-document-length <see cref="VisualLineElement"/>: it adds width and a text run at its offset and
/// never becomes part of the document, so the caret, a selection, undo and a copy are unaffected. The library builds a visual
/// line by asking this generator for the next interested offset, so only the lines being drawn consult the index — one array
/// lookup per line, no document scan.</para>
/// </summary>
internal sealed class EditorInlayHints : VisualLineElementGenerator, IDisposable
{
    private readonly TextEditor editor;
    private readonly EditorDocument document;
    private InlayHintLineIndex index = InlayHintLineIndex.Empty;
    private IBrush brush = Brushes.Gray;
    // The line builder asks again at the same offset right after a zero-length element; returning the same offset twice would
    // never advance it, so the second construction at one offset is refused.
    private int constructedOffset = -1;
    private bool enabled = true;
    private bool disposed;

    public EditorInlayHints(TextEditor editor, EditorDocument document)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(document);
        this.editor = editor;
        this.document = document;
    }

    /// <summary>The index being drawn; a test reads it to see which build the view kept.</summary>
    public InlayHintLineIndex Index => index;

    public bool Enabled => enabled;

    /// <summary>Publishes a new index and redraws what is on screen; the caller posts this to the UI thread.</summary>
    public void SetIndex(InlayHintLineIndex next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (ReferenceEquals(index, next))
        {
            return;
        }

        index = next;
        constructedOffset = -1;
        brush = EditorOptionsApplier.Brush(editor, editor.ActualThemeVariant, "TextMutedBrush") ?? Brushes.Gray;
        Redraw();
    }

    /// <summary>Turns the hints off without forgetting the index, so switching them back on is a redraw and not a refetch.</summary>
    public void SetEnabled(bool value)
    {
        if (disposed || enabled == value)
        {
            return;
        }

        enabled = value;
        constructedOffset = -1;
        Redraw();
    }

    /// <summary>Drops the hints, e.g. when the language server goes away or the document is rebound.</summary>
    public void Clear() => SetIndex(InlayHintLineIndex.Empty);

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (!CanDraw(startOffset))
        {
            return -1;
        }

        if (startOffset != constructedOffset)
        {
            // A new construction pass starts at a different offset — forward (next line) or backward (a rebuild after
            // folding/width change/scroll-up). Only the library's same-offset second query keeps the guard, so a hint
            // at the end of the last visible line is not lost on the next rebuild.
            constructedOffset = -1;
        }

        var line = document.GetLineByOffset(Math.Min(startOffset, document.TextLength));
        foreach (var hint in index.GetLine(line.LineNumber - 1))
        {
            if (hint.Offset >= startOffset)
            {
                return hint.Offset;
            }
        }

        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (!CanDraw(offset) || offset == constructedOffset)
        {
            return null;
        }

        var line = document.GetLineByOffset(Math.Min(offset, document.TextLength));
        foreach (var hint in index.GetLine(line.LineNumber - 1))
        {
            if (hint.Offset != offset)
            {
                continue;
            }

            constructedOffset = offset;
            return new InlayHintElement(Format(hint), brush);
        }

        return null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        index = InlayHintLineIndex.Empty;
    }

    private bool CanDraw(int offset) =>
        !disposed && enabled && !index.IsEmpty && offset >= 0 && offset <= document.TextLength;

    private void Redraw()
    {
        if (editor.TextArea.TextView is { } view)
        {
            view.Redraw();
        }
    }

    /// <summary>The label with the padding the server asked for, as the text run that is drawn.</summary>
    private static string Format(EditorInlayHint hint) =>
        (hint.PaddingLeft ? " " : string.Empty) + hint.Label + (hint.PaddingRight ? " " : string.Empty);

    /// <summary>One hint's text, in the muted colour so it reads as an annotation and not as code.</summary>
    private sealed class InlayHintElement(string text, IBrush brush) : VisualLineElement(text.Length, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            TextRunProperties?.SetForegroundBrush(brush);
            return new TextCharacters(text.AsMemory(), TextRunProperties!);
        }
    }
}
