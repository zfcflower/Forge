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
            var model = await TestDocuments.LoadAsync(path);
            var host = Show(model, ThemeVariant.Dark);
            Assert.Equal(text.Split('\n'), EditorUi.RenderedLines(host.Editor).Take(text.Split('\n').Length).ToArray());
            await WaitUntil(() => EditorUi.TokenColors(host.Editor).Count >= 2);
            host.Window.Close();
        });
    }

    [Fact]
    public async Task Dark_and_light_variants_use_different_token_palettes()
    {
        var path = Write("Sample.cs", "using System;\nclass A { int x = 1; string s = \"a\"; // c\n}\n", new UTF8Encoding(false));
        await Ui(async () =>
        {
            var model = await TestDocuments.LoadAsync(path);
            var host = Show(model, ThemeVariant.Dark);
            await WaitUntil(() => EditorUi.TokenColors(host.Editor).Count >= 2);
            var dark = EditorUi.TokenColors(host.Editor);
            host.Window.RequestedThemeVariant = ThemeVariant.Light;
            WorkbenchUi.Settle(host.Window);
            await WaitUntil(() => EditorUi.TokenColors(host.Editor).Count >= 2 && !EditorUi.TokenColors(host.Editor).SetEquals(dark));
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
            var model = await TestDocuments.LoadAsync(path);
            var host = Show(model, ThemeVariant.Dark);
            host.Editor.SelectAll();
            Assert.Equal(text, host.Editor.SelectedText);
            Assert.Equal(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetBytes(host.Editor.Document.Text));
            host.Window.Close();
        });
    }

    private static EditorHost Show(FileDocumentViewModel model, ThemeVariant variant) => EditorUi.Show(model, variant: variant);

    private static Task Ui(Func<Task> body) => SharedUi.RunAsync(body);

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
        var root = TestTempFiles.NewDirectory("forge-textmate-");
        roots.Add(root);
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(content)]);
        return path;
    }
}
