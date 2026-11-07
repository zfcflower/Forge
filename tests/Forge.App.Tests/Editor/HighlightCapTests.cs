using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Forge.App.Localization;
using Forge.App.Tests.Workbench;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Editor;

[Collection("forge-app-ui")]
public sealed class HighlightCapTests
{
    private const int Limit = 1 << 20;
    private const string Source = "using System;\nclass A { int x = 1; string s = \"a\"; // c\n}\n";
    private static readonly string TooLarge = new LocText(InspectorStrings.FilesEditorHighlightTooLarge).Resolve();

    [Fact]
    public Task Growing_past_the_cap_drops_highlighting_and_shows_the_notice() => Run(async h =>
    {
        await WaitUntil(() => Colors(h.Editor).Count >= 2);
        Grow(h.Editor, Limit + 1);
        await WaitUntil(() => h.Model.EditorNotice == TooLarge);
        await WaitUntil(() => Colors(h.Editor).Count < 2);
    });

    [Fact]
    public Task Shrinking_below_the_cap_restores_highlighting_and_clears_the_notice() => Run(async h =>
    {
        await WaitUntil(() => Colors(h.Editor).Count >= 2);
        Grow(h.Editor, Limit + 1);
        await WaitUntil(() => h.Model.EditorNotice == TooLarge);
        h.Editor.Document.Remove(Source.Length, h.Editor.Document.TextLength - Source.Length);
        await WaitUntil(() => string.IsNullOrEmpty(h.Model.EditorNotice));
        await WaitUntil(() => Colors(h.Editor).Count >= 2);
    });

    [Fact]
    public Task The_cap_is_exclusive_exactly_at_the_limit_stays_highlighted() => Run(async h =>
    {
        await WaitUntil(() => Colors(h.Editor).Count >= 2);
        Grow(h.Editor, Limit);
        Assert.Equal(Limit, h.Editor.Document.TextLength);
        WorkbenchUi.Settle(h.Window);
        Assert.True(string.IsNullOrEmpty(h.Model.EditorNotice));
        Assert.True(Colors(h.Editor).Count >= 2);
        h.Editor.Document.Insert(h.Editor.Document.TextLength, "x");
        await WaitUntil(() => h.Model.EditorNotice == TooLarge);
    });

    [Fact]
    public Task A_theme_change_while_too_large_applies_to_the_fresh_registry_after_shrinking() => Run(async h =>
    {
        await WaitUntil(() => Colors(h.Editor).Count >= 2);
        var dark = Colors(h.Editor);
        Grow(h.Editor, Limit + 1);
        await WaitUntil(() => h.Model.EditorNotice == TooLarge);
        h.Window.RequestedThemeVariant = ThemeVariant.Light;
        WorkbenchUi.Settle(h.Window);
        Assert.Equal(TooLarge, h.Model.EditorNotice);
        h.Editor.Document.Remove(Source.Length, h.Editor.Document.TextLength - Source.Length);
        await WaitUntil(() => Colors(h.Editor).Count >= 2 && !Colors(h.Editor).SetEquals(dark));
    });

    [Fact]
    public Task Closing_the_view_with_a_recheck_pending_neither_throws_nor_sets_the_notice() => Run(async h =>
    {
        await WaitUntil(() => Colors(h.Editor).Count >= 2);
        Grow(h.Editor, Limit + 1);
        h.Window.Close();
        WorkbenchUi.Settle(h.Window);
        Assert.True(string.IsNullOrEmpty(h.Model.EditorNotice));
    });

    private static void Grow(TextEditor editor, int length)
    {
        var missing = length - editor.Document.TextLength;
        var text = new StringBuilder(missing);
        for (; missing >= 10; missing -= 10)
        {
            text.Append("xxxxxxxxx\n");
        }
        text.Append('x', missing);
        editor.Document.Insert(editor.Document.TextLength, text.ToString());
    }

    private static HashSet<Color> Colors(TextEditor editor)
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

    private sealed record Host(Window Window, TextEditor Editor, FileDocumentViewModel Model);

    private static async Task Run(Func<Host, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "Sample.cs");
            await File.WriteAllTextAsync(path, Source, new UTF8Encoding(false));
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await SharedUi.Session.Dispatch(async () =>
            {
                var model = FileDocumentViewModel.Create();
                try
                {
                    await model.LoadAsync(path, CancellationToken.None);
                    Assert.False(model.HasError, model.Error);
                    var view = new FileDocumentView { DataContext = model };
                    var window = new Window { Width = 900, Height = 600, Content = view, RequestedThemeVariant = ThemeVariant.Dark };
                    window.Show();
                    WorkbenchUi.Settle(window);
                    try
                    {
                        await body(new Host(window, view.GetVisualDescendants().OfType<TextEditor>().Single(), model));
                    }
                    finally
                    {
                        window.Close();
                    }
                }
                finally
                {
                    await model.ReleaseDocumentAsync();
                }
                return 0;
            }, budget.Token);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }
}
