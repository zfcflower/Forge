using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using EditorDocument = AvaloniaEdit.Document.TextDocument;

namespace Forge.App.Editing;

/// <summary>
/// Draws the end-of-line and tab glyphs. AvaloniaEdit 12 renders spaces and control-character boxes itself
/// (<see cref="TextEditorOptions.ShowSpaces"/> / <c>ShowBoxForControlCharacters</c>), but its tab and end-of-line
/// generators are not wired in this version — the options exist and nothing reads them. Both glyphs here are zero-length
/// elements, so they add a column of drawing and never change the text, the caret offsets or a copy.
/// </summary>
internal sealed class EditorWhitespaceAdornments : VisualLineElementGenerator, IDisposable
{
    /// <summary>The pilcrow drawn at the end of a line (the same glyph the library's option names).</summary>
    public const string EndOfLineGlyph = "\u00b6";

    /// <summary>The arrow drawn at a tab stop.</summary>
    public const string TabGlyph = "\u2192";

    private readonly TextEditor editor;
    private readonly EditorDocument document;
    private IBrush brush = Brushes.Gray;
    private bool showTabs;
    private bool showEndOfLine;
    private bool disposed;

    public EditorWhitespaceAdornments(TextEditor editor, EditorDocument document)
    {
        this.editor = editor;
        this.document = document;
    }

    public bool ShowTabs => showTabs;

    public bool ShowEndOfLine => showEndOfLine;

    /// <summary>Switches the glyphs and redraws; the colour is Forge's muted text token.</summary>
    public void Configure(bool tabs, bool endOfLine)
    {
        if (disposed || (showTabs == tabs && showEndOfLine == endOfLine))
        {
            return;
        }

        showTabs = tabs;
        showEndOfLine = endOfLine;
        brush = EditorOptionsApplier.Brush(editor, editor.ActualThemeVariant, "TextMutedBrush") ?? Brushes.Gray;
        editor.TextArea.TextView.Redraw();
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (disposed || (startOffset < 0 || startOffset > document.TextLength))
        {
            return -1;
        }

        // Only the line being constructed matters: a document-wide scan for the next tab would cost the whole file for
        // every visual line and a file without tabs would never answer -1 cheaply.
        var line = document.GetLineByOffset(Math.Min(startOffset, document.TextLength));
        var end = line.EndOffset;
        if (showEndOfLine && startOffset <= end)
        {
            var tab = showTabs ? NextTab(startOffset, end) : -1;
            return tab >= 0 ? Math.Min(tab, end) : end;
        }

        return showTabs ? NextTab(startOffset, end) : -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (disposed)
        {
            return null;
        }

        var line = document.GetLineByOffset(Math.Min(offset, document.TextLength));
        if (offset == line.EndOffset)
        {
            return showEndOfLine ? new GlyphElement(EndOfLineGlyph, brush) : null;
        }

        return showTabs && offset < document.TextLength && document.GetCharAt(offset) == '\t'
            ? new GlyphElement(TabGlyph, brush)
            : null;
    }

    public void Dispose() => disposed = true;

    private int NextTab(int startOffset, int endOffset)
    {
        for (var offset = startOffset; offset < endOffset && offset < document.TextLength; offset++)
        {
            if (document.GetCharAt(offset) == '\t')
            {
                return offset;
            }
        }

        return -1;
    }

    /// <summary>One glyph, one visual column, no document text.</summary>
    private sealed class GlyphElement(string glyph, IBrush brush) : VisualLineElement(1, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            TextRunProperties?.SetForegroundBrush(brush);
            return new TextCharacters(glyph.AsMemory(), TextRunProperties!);
        }

        public override bool IsWhitespace(int visualColumn) => true;
    }
}
