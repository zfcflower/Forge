using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Forge.Editor.Highlighting;

namespace Forge.App.Editing;

/// <summary>
/// Paints the language server's semantic classification over the lexical colours of the visible lines: the ranges the server
/// reported win, everything else keeps the TextMate colour underneath.
/// <para>The library calls <see cref="ColorizeLine"/> only for lines it is about to draw, so the index is read per visible
/// line and no work happens for the rest of the document. Every range is clamped to its line, so an index that arrived
/// before the last keystroke can only mis-colour the lines that changed, and the next answer replaces it — dropping the
/// whole overlay on every edit would blink instead.</para>
/// </summary>
internal sealed class SemanticTokenColorizer : DocumentColorizingTransformer
{
    // The colours are the editor's own syntax tokens, which are the TextMate theme's palette: a semantic token therefore
    // reads as a more precise classification, not as a second theme laid over the first.
    private static readonly string[] s_brushKeys = BuildBrushKeys();
    private readonly TextEditor editor;
    private readonly Func<bool> isDarkTheme;
    private readonly IBrush?[] brushes = new IBrush?[s_brushKeys.Length];
    private SemanticLineIndex index = SemanticLineIndex.Empty;
    private ThemeVariant? brushVariant;

    public SemanticTokenColorizer(TextEditor editor, Func<bool> isDarkTheme)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(isDarkTheme);
        this.editor = editor;
        this.isDarkTheme = isDarkTheme;
    }

    /// <summary>The index currently painted; a test reads it to see which build the view kept.</summary>
    public SemanticLineIndex Index => index;

    /// <summary>Publishes a new index and redraws what is on screen; the caller posts this to the UI thread.</summary>
    public void SetIndex(SemanticLineIndex next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (ReferenceEquals(index, next))
        {
            return;
        }

        index = next;
        InvalidateVisual();
    }

    /// <summary>Drops the overlay, e.g. when the language server goes away or the document is rebound.</summary>
    public void Clear() => SetIndex(SemanticLineIndex.Empty);

    private void InvalidateVisual()
    {
        if (editor.TextArea.TextView is { } view && view.VisualLinesValid)
        {
            view.Redraw();
        }
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var current = index;
        if (current.IsEmpty)
        {
            return;
        }

        var tokens = current.GetLine(line.LineNumber - 1);
        if (tokens.IsEmpty)
        {
            return;
        }

        var length = line.Length;
        if (length == 0)
        {
            return;
        }

        foreach (var token in tokens)
        {
            var from = Math.Min(token.Start, length);
            var to = Math.Min(token.Start + token.Length, length);
            if (to <= from)
            {
                continue;
            }

            var group = TokenCategoryGroups.Group(token.Category);
            if (group == TokenCategoryGroup.None || BrushFor(group) is not { } brush)
            {
                continue;
            }

            ChangeLinePart(line.Offset + from, line.Offset + to, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    private IBrush? BrushFor(TokenCategoryGroup group)
    {
        var slot = (int)group;
        if ((uint)slot >= (uint)brushes.Length)
        {
            return null;
        }

        // One comparison per line: the brushes are per theme variant, and the variant can change while the view lives.
        var variant = isDarkTheme() ? ThemeVariant.Dark : ThemeVariant.Light;
        if (brushVariant != variant)
        {
            Array.Clear(brushes);
            brushVariant = variant;
        }

        if (brushes[slot] is { } cached)
        {
            return cached;
        }

        var brush = EditorOptionsApplier.Brush(editor, variant, s_brushKeys[slot]);
        brushes[slot] = brush;
        return brush;
    }

    private static string[] BuildBrushKeys()
    {
        var keys = new string[Enum.GetValues<TokenCategoryGroup>().Length];
        keys[(int)TokenCategoryGroup.Keyword] = "SyntaxKeywordBrush";
        keys[(int)TokenCategoryGroup.String] = "SyntaxStringBrush";
        keys[(int)TokenCategoryGroup.Comment] = "SyntaxCommentBrush";
        keys[(int)TokenCategoryGroup.Number] = "SyntaxNumberBrush";
        keys[(int)TokenCategoryGroup.Type] = "SyntaxTypeBrush";
        keys[(int)TokenCategoryGroup.Function] = "SyntaxFunctionBrush";
        keys[(int)TokenCategoryGroup.Variable] = "SyntaxVariableBrush";
        keys[(int)TokenCategoryGroup.Preprocessor] = "SyntaxPreprocessorBrush";
        keys[(int)TokenCategoryGroup.Invalid] = "DangerBrush";
        return keys;
    }
}
