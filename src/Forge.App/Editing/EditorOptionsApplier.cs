using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Forge.App.Preferences;

namespace Forge.App.Editing;

/// <summary>
/// Pushes <see cref="EditorSettings"/> into one <see cref="TextEditor"/>. Everything here is a property the library
/// already exposes; the editor's own colours come from Forge's token table so the text area never drifts from the
/// workbench palette when the TextMate theme or the UI theme changes.
/// </summary>
internal static class EditorOptionsApplier
{
    /// <summary>Applies the display and typing options. Safe to call repeatedly; only changed values raise anything.</summary>
    public static void ApplyOptions(TextEditor editor, EditorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(settings);

        var options = editor.Options;
        options.ShowSpaces = settings.ShowSpaces;
        options.ShowTabs = settings.ShowTabs;
        options.ShowEndOfLine = settings.ShowEndOfLine;
        options.ShowBoxForControlCharacters = settings.ShowControlCharacters;
        options.AllowScrollBelowDocument = settings.ScrollBelowDocument;
        options.HighlightCurrentLine = settings.HighlightCurrentLine;
        options.EnableHyperlinks = settings.Hyperlinks;
        options.EnableEmailHyperlinks = settings.Hyperlinks;
        options.RequireControlModifierForHyperlinkClick = true;
        options.IndentationSize = settings.IndentationSize;
        options.ConvertTabsToSpaces = settings.TabsToSpaces;
        options.EnableRectangularSelection = true;
        options.ShowColumnRulers = settings.ColumnRulerColumns.Count > 0;
        options.ColumnRulerPositions = settings.ColumnRulerColumns;
        options.CutCopyWholeLine = true;
        editor.ShowLineNumbers = settings.ShowLineNumbers;
    }

    /// <summary>Colours that belong to the text view rather than to the control template.</summary>
    public static void ApplyColors(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var view = editor.TextArea.TextView;
        var variant = editor.ActualThemeVariant;

        // A selection must tint the text, not cover it: the library's theme paints a dark blue with white text.
        if (Brush(editor, variant, "EditorSelectionBrush") is { } selection)
        {
            editor.TextArea.SelectionBrush = selection;
        }

        editor.TextArea.SelectionForeground = null;
        editor.TextArea.SelectionBorder = null;

        if (Brush(editor, variant, "EditorSearchResultBrush") is { } searchResult)
        {
            editor.SearchResultsBrush = searchResult;
        }

        if (Brush(editor, variant, "TextMutedBrush") is { } muted)
        {
            view.NonPrintableCharacterBrush = muted;
        }

        if (Brush(editor, variant, "BorderStrongBrush") is { } border)
        {
            view.ColumnRulerPen = new Pen(border, 1);
        }

        if (Brush(editor, variant, "AccentBrush") is { } accent)
        {
            view.LinkTextForegroundBrush = accent;
            view.LinkTextUnderline = true;
        }

        if (Brush(editor, variant, "SurfaceMutedBrush") is { } currentLine)
        {
            view.CurrentLineBackground = currentLine;
        }

        if (Brush(editor, variant, "TextPrimaryBrush") is { } primary)
        {
            editor.Foreground = primary;
        }

        if (Brush(editor, variant, "SurfaceBrush") is { } surface)
        {
            editor.Background = surface;
        }

        if (Brush(editor, variant, "TextMutedBrush") is { } lineNumbers)
        {
            editor.LineNumbersForeground = lineNumbers;
        }
    }

    /// <summary>Severity colours for the inline diagnostic badges and the squiggle renderer.</summary>
    public static IBrush SeverityBrush(TextEditor editor, int severity)
    {
        var key = severity switch
        {
            1 => "DangerBrush",
            2 => "WarningBrush",
            _ => "TextSecondaryBrush",
        };

        return Brush(editor, editor.ActualThemeVariant, key) ?? Brushes.Gray;
    }

    /// <summary>Reads a Forge brush token, or null when the token is not a brush in this theme.</summary>
    public static IBrush? Brush(StyledElement target, ThemeVariant? variant, string key) =>
        target.TryFindResource(key, variant, out var value) && value is IBrush brush ? brush : null;

    /// <summary>The TextMate theme this editor should use: the fixed choice from the settings, else the UI variant.</summary>
    public static bool IsDarkTheme(TextEditor editor, EditorSettings settings) =>
        settings.IsDarkTheme(editor.ActualThemeVariant == ThemeVariant.Dark);
}
