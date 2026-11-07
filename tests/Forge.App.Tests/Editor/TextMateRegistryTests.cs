using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Forge.App.Tests.Workbench;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Editor;

[Collection("forge-app-ui")]
public sealed class TextMateRegistryTests : IDisposable
{
    private readonly List<string> roots = [];

    public void Dispose()
    {
        foreach (var root in roots)
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static TheoryData<string, string> Languages => new()
    {
        { "data.json", "{ \"a\": [1, true, null], \"b\": \"x\" }\n" },
        { "data.xml", "<?xml version=\"1.0\"?>\n<a b=\"1\"><!-- c --><c>t</c></a>\n" },
        { "doc.md", "# Title\n\n*em* `code` [l](http://x)\n\n```\nfenced\n```\n" },
        { "tool.py", "def f(a: int) -> str:\n    return f\"{a}\"  # c\n" },
        { "lib.rs", "fn main() { let x: u32 = 1; println!(\"{}\", x); } // c\n" },
    };

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task Registry_languages_render_exact_text_with_several_colors(string name, string text)
    {
        var path = Write(name, text, new UTF8Encoding(false));
        await Ui(async () =>
        {
            var model = FileDocumentViewModel.Create();
            await model.LoadAsync(path, CancellationToken.None);
            var host = Show(model, ThemeVariant.Dark);
            Assert.Equal(text.Split('\n'), RenderedLines(host.Editor).Take(text.Split('\n').Length).ToArray());
            await WaitUntil(() => Colors(host.Editor).Count >= 2);
            host.Window.Close();
        });
    }

    [Fact]
    public async Task Dark_and_light_variants_use_different_token_palettes()
    {
        var path = Write("Sample.cs", "using System;\nclass A { int x = 1; string s = \"a\"; // c\n}\n", new UTF8Encoding(false));
        await Ui(async () =>
        {
            var model = FileDocumentViewModel.Create();
            await model.LoadAsync(path, CancellationToken.None);
            var host = Show(model, ThemeVariant.Dark);
            await WaitUntil(() => Colors(host.Editor).Count >= 2);
            var dark = Colors(host.Editor);
            host.Window.RequestedThemeVariant = ThemeVariant.Light;
            WorkbenchUi.Settle(host.Window);
            await WaitUntil(() => Colors(host.Editor).Count >= 2 && !Colors(host.Editor).SetEquals(dark));
            host.Window.Close();
        });
    }

    [Fact]
    public async Task Crlf_selection_text_is_byte_exact()
    {
        const string text = "alpha\r\nbeta\r\ngamma\r\n";
        var path = Write("crlf.txt", text, new UTF8Encoding(false));
        await Ui(async () =>
        {
            var model = FileDocumentViewModel.Create();
            await model.LoadAsync(path, CancellationToken.None);
            var host = Show(model, ThemeVariant.Dark);
            host.Editor.SelectAll();
            Assert.Equal(text, host.Editor.SelectedText);
            Assert.Equal(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetBytes(host.Editor.Document.Text));
            host.Window.Close();
        });
    }

    private static string[] RenderedLines(TextEditor editor)
    {
        var textView = editor.TextArea.TextView;
        textView.EnsureVisualLines();
        return [.. textView.VisualLines.Select(static line => string.Concat(
            line.TextLines.SelectMany(static t => t.TextRuns).Where(static r => r is not TextEndOfLine).Select(static r => r.Text.ToString())))];
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

    private sealed record EditorHost(Window Window, TextEditor Editor);

    private static EditorHost Show(FileDocumentViewModel model, ThemeVariant variant)
    {
        var view = new FileDocumentView { DataContext = model };
        var window = new Window { Width = 900, Height = 600, Content = view, RequestedThemeVariant = variant };
        window.Show();
        WorkbenchUi.Settle(window);
        return new EditorHost(window, view.GetVisualDescendants().OfType<TextEditor>().Single());
    }

    private static async Task Ui(Func<Task> body)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await SharedUi.Session.Dispatch(async () => { await body(); return 0; }, budget.Token);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }

    private string Write(string name, string content, Encoding encoding)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-textmate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        roots.Add(root);
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(content)]);
        return path;
    }
}
