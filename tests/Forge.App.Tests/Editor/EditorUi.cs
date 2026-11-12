using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Forge.App.Tests.Workbench;
using Forge.App.ViewModels;
using Forge.App.Views;

namespace Forge.App.Tests.Editor;

/// <summary>A file document shown in a real window, with the view and the editor control it rendered.</summary>
internal sealed record EditorHost(Window Window, FileDocumentView View, TextEditor Editor);

/// <summary>The window/editor plumbing every editor suite needs: show a document, read the rendered text and its token colors.</summary>
internal static class EditorUi
{
    /// <summary>Shows <paramref name="model"/> in a window of the given size and settles one layout pass.</summary>
    public static EditorHost Show(
        FileDocumentViewModel model,
        double width = 900,
        double height = 600,
        ThemeVariant? variant = null)
    {
        var view = new FileDocumentView { DataContext = model };
        var window = new Window { Width = width, Height = height, Content = view };
        if (variant is not null)
        {
            window.RequestedThemeVariant = variant;
        }

        window.Show();
        WorkbenchUi.Settle(window);
        return new EditorHost(window, view, view.GetVisualDescendants().OfType<TextEditor>().Single());
    }

    /// <summary>The text of each visual line, with the line terminator left out.</summary>
    public static string[] RenderedLines(TextEditor editor)
    {
        var textView = editor.TextArea.TextView;
        textView.EnsureVisualLines();
        return [.. textView.VisualLines.Select(static line => string.Concat(
            line.TextLines.SelectMany(static t => t.TextRuns).Where(static r => r is not TextEndOfLine).Select(static r => r.Text.ToString())))];
    }

    /// <summary>The foreground colors in use by the non-blank text runs of the rendered lines.</summary>
    public static HashSet<Color> TokenColors(TextEditor editor)
    {
        var textView = editor.TextArea.TextView;
        textView.Redraw();
        textView.EnsureVisualLines();
        return [.. textView.VisualLines
            .SelectMany(static l => l.TextLines).SelectMany(static t => t.TextRuns)
            .Where(static r => r is not TextEndOfLine && !string.IsNullOrWhiteSpace(r.Text.ToString()))
            .Select(static r => r.Properties?.ForegroundBrush as ISolidColorBrush)
            .OfType<ISolidColorBrush>().Select(static b => b.Color)];
    }
}
