using Avalonia;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Forge.App.Editing;

/// <summary>
/// The light bulb next to the caret line: it appears while the language server has code actions for the caret and is the
/// clickable half of the feature (Ctrl+. is the keyboard half). The margin measures zero width while there is nothing to
/// offer, so it neither draws nor takes clicks when the server has nothing.
/// </summary>
internal sealed class EditorCodeActionMargin : AbstractMargin
{
    private const double Strip = 18;
    private readonly TextEditor editor;
    private IBrush brush = Brushes.SlateBlue;
    private bool hasActions;
    private int caretLine = 1;

    public EditorCodeActionMargin(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        this.editor = editor;
    }

    /// <summary>Raised when the bulb is clicked; the view then opens the action list.</summary>
    public event EventHandler? Requested;

    public bool HasActions => hasActions;

    /// <summary>The 1-based document line whose actions the bulb stands for.</summary>
    public int CaretLine => caretLine;

    /// <summary>Shows or hides the bulb; a hidden bulb gives the margin back its zero width.</summary>
    public void SetActions(bool value)
    {
        if (hasActions == value)
        {
            return;
        }

        hasActions = value;
        brush = EditorOptionsApplier.Brush(editor, editor.ActualThemeVariant, "AccentBrush") ?? Brushes.SlateBlue;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Moves the bulb to the caret's line; the caret line is the only line that can show one.</summary>
    public void SetCaretLine(int line)
    {
        if (caretLine == line)
        {
            return;
        }

        caretLine = line;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => hasActions ? new Size(Strip, 0) : new Size(0, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnViewChanged;
            oldTextView.ScrollOffsetChanged -= OnViewChanged;
        }

        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnViewChanged;
            newTextView.ScrollOffsetChanged += OnViewChanged;
        }

        base.OnTextViewChanged(oldTextView, newTextView);
    }

    public override void Render(DrawingContext context)
    {
        var textView = TextView;
        if (!hasActions || textView is null || !textView.VisualLinesValid || textView.VisualLines.Count == 0)
        {
            return;
        }

        var scroll = textView.ScrollOffset.Y;
        var height = textView.DefaultLineHeight;
        foreach (var line in textView.VisualLines)
        {
            if (caretLine < line.FirstDocumentLine.LineNumber || caretLine > line.LastDocumentLine.LineNumber)
            {
                continue;
            }

            var center = new Point(Strip / 2, line.VisualTop - scroll + height / 2);
            // A bulb drawn as geometry rather than a glyph: no font dependency, and it is legible at 7 DIP.
            context.DrawEllipse(brush, null, new Point(center.X, center.Y - 1.5), 3.2, 3.2);
            context.DrawRectangle(brush, null, new Rect(center.X - 2, center.Y + 2.2, 4, 1.6));
            context.DrawRectangle(brush, null, new Rect(center.X - 1.2, center.Y + 4.2, 2.4, 1.2));
            return;
        }
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!hasActions || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        Requested?.Invoke(this, EventArgs.Empty);
    }

    private void OnViewChanged(object? sender, EventArgs e) => InvalidateVisual();
}
