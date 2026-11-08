using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Forge.App.Localization;
using EditorDocument = AvaloniaEdit.Document.TextDocument;
using Forge.Editor.Lsp;

namespace Forge.App.Editing;

/// <summary>
/// The editor's inline (in-column) adornment: one severity-coloured dot after the last character of every line that has
/// diagnostics. It is a <see cref="VisualLineElement"/> with visual length one and document length zero, so it never
/// changes the text, the caret offsets or the clipboard, only what is drawn.
/// <para>Hovering the dot shows the line's messages (see <see cref="LineHasDiagnostics"/>), so a line whose diagnostics
/// are longer than the visible text still explains itself.</para>
/// </summary>
internal sealed class EditorDiagnosticAdornments : VisualLineElementGenerator, IDisposable
{
    /// <summary>Lines the adornment marks at most; a pathological diagnostic set must not stall the view.</summary>
    public const int MaxMarkedLines = 4_000;

    private const string Glyph = "●";

    private readonly TextEditor editor;
    private readonly EditorDocument document;
    private int[] offsets = [];
    private int[] severities = [];
    private bool disposed;

    public EditorDiagnosticAdornments(TextEditor editor, EditorDocument document)
    {
        this.editor = editor;
        this.document = document;
    }

    /// <summary>Replaces the marked lines; the view redraws so a removed diagnostic loses its dot immediately.</summary>
    public void SetDiagnostics(IReadOnlyList<EditorLspDiagnostic> items)
    {
        if (disposed)
        {
            return;
        }

        var lines = new Dictionary<int, int>();
        foreach (var item in items)
        {
            if (item.Start < 0 || item.Start > document.TextLength || lines.Count > MaxMarkedLines)
            {
                continue;
            }

            var start = document.GetLineByOffset(Math.Min(item.Start, document.TextLength)).LineNumber;
            var end = document.GetLineByOffset(Math.Min(Math.Max(item.End, item.Start), document.TextLength)).LineNumber;
            for (var line = start; line <= end && line <= document.LineCount && lines.Count <= MaxMarkedLines; line++)
            {
                var severity = item.Severity switch
                {
                    DiagnosticSeverity.Error => 1,
                    DiagnosticSeverity.Warning => 2,
                    DiagnosticSeverity.Information => 3,
                    _ => 4,
                };

                if (!lines.TryGetValue(line, out var existing) || severity < existing)
                {
                    lines[line] = severity;
                }
            }
        }

        var sorted = lines.OrderBy(pair => pair.Key).ToArray();
        var newOffsets = new int[sorted.Length];
        var newSeverities = new int[sorted.Length];
        for (var i = 0; i < sorted.Length; i++)
        {
            newOffsets[i] = document.GetLineByNumber(sorted[i].Key).EndOffset;
            newSeverities[i] = sorted[i].Value;
        }

        offsets = newOffsets;
        severities = newSeverities;
        editor.TextArea.TextView.Redraw();
    }

    /// <summary>Whether the line at <paramref name="offset"/> (its end included) carries a marked dot.</summary>
    public bool LineHasDiagnostics(int offset) =>
        offsets.Length > 0 && Array.BinarySearch(offsets, offset) >= 0;

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (offsets.Length == 0 || disposed)
        {
            return -1;
        }

        var index = Array.BinarySearch(offsets, startOffset);
        if (index < 0)
        {
            index = ~index;
        }

        return index < offsets.Length ? offsets[index] : -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (disposed)
        {
            return null;
        }

        var index = Array.BinarySearch(offsets, offset);
        if (index < 0)
        {
            return null;
        }

        return new BadgeElement(EditorOptionsApplier.SeverityBrush(editor, severities[index]));
    }

    public void Dispose()
    {
        disposed = true;
        offsets = [];
        severities = [];
    }

    /// <summary>A single fixed glyph drawn in the severity colour; it occupies one visual column and no document text.</summary>
    private sealed class BadgeElement(IBrush brush) : VisualLineElement(1, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            TextRunProperties?.SetForegroundBrush(brush);
            return new TextCharacters(Glyph.AsMemory(), TextRunProperties!);
        }
    }
}
