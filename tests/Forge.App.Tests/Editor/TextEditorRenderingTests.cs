using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Forge.App.Tests.Workbench;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Editor;

[Collection("forge-app-ui")]
public sealed class TextEditorRenderingTests : IDisposable
{
    private readonly List<string> roots = [];

    public void Dispose()
    {
        foreach (var root in roots)
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static TheoryData<string, string> Samples => new()
    {
        { "Sample.cs", "using System;\nclass A<T> where T : new() { int[] x = { 1, 2 }; string s = $\"{x[0]}\"; // c\n    public static T? M(Func<int, T> f) => f(1) ?? default;\n}\n\n" },
        { "page.html", "<!DOCTYPE html>\n<div class=\"a\" data-x='1'><a href=\"#\">&amp; &lt;b&gt;</a><!-- c --></div>\n\n" },
        { "app.js", "const f = (a, b) => ({ [a]: b?.c ?? `t${a}` });\nfor (let i = 0; i < 3; i++) { /* x */ f(i, /re+gex/g); }\n" },
        { "app.ts", "interface I<T extends object = {}> { readonly [k: string]: T | undefined; }\nexport const g = <T,>(v: T): v is T => v as unknown as T;\n" },
        { "style.css", "a:hover > .b::before, #c[data-x=\"1\"] { content: \"\\201C\"; margin: -1px calc(1% + 2px); } /* c */\n@media (min-width: 1px) { .d { color: #fff !important; } }\n" },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Rendered_lines_equal_the_file_exactly_and_use_several_token_colors(string name, string text)
    {
        var path = Write(name, text, new UTF8Encoding(false));
        await Ui(async () =>
        {
            // The HTML sample is opened as source: the file panel shows a placeholder card for it, which this test is not about.
            var model = await TestDocuments.LoadAsync(path);
            var host = Show(model, width: 900, height: 600);
            Assert.Equal(text.Split('\n'), EditorUi.RenderedLines(host.Editor).Take(text.Split('\n').Length).ToArray());
            await WaitUntil(() => EditorUi.TokenColors(host.Editor).Count >= 2);
            host.Window.Close();
        });
    }

    [Fact]
    public async Task Typing_is_rendered_and_saved_byte_for_byte_in_the_original_encoding()
    {
        var cases = new (string Name, Encoding Encoding, string Text)[]
        {
            ("utf8-bom-crlf", new UTF8Encoding(true), "alpha\tone\r\nbeta\r\n"),
            ("utf8-lf-no-tail", new UTF8Encoding(false), "alpha\n\tbeta\ngamma"),
            ("utf16le-bom-lf", new UnicodeEncoding(false, true), "alpha\n\u4e2d\u6587 beta\n"),
            ("utf16be-bom-crlf", new UnicodeEncoding(true, true), "alpha\r\nbeta\r\n"),
        };
        await Ui(async () =>
        {
            foreach (var (name, encoding, text) in cases)
            {
                var path = Write(name + ".txt", text, encoding);
                // The HTML sample is opened as source: the file panel shows a placeholder card for it, which this test is not about.
            var model = await TestDocuments.LoadAsync(path);
                var host = Show(model, width: 600);
                model.IsEditing = true;
                WorkbenchUi.Settle(host.Window);
                host.Editor.TextArea.Focus();
                host.Editor.CaretOffset = 0;
                host.Window.KeyTextInput("X");
                WorkbenchUi.Settle(host.Window);
                Assert.StartsWith("X", EditorUi.RenderedLines(host.Editor)[0], StringComparison.Ordinal);
                host.Window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
                await WaitUntil(() => !model.IsDirty && !model.IsSaving);
                byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes("X" + text)];
                Assert.Equal(expected, await File.ReadAllBytesAsync(path));
                host.Window.Close();
            }
        });
    }

    [Fact]
    public async Task Undo_and_redo_restore_the_document_and_the_rendered_text()
    {
        var path = Write("undo.txt", "alpha\nbeta\n", new UTF8Encoding(false));
        await Ui(async () =>
        {
            var model = await TestDocuments.LoadAsync(path);
            var host = Show(model, width: 600);
            model.IsEditing = true;
            WorkbenchUi.Settle(host.Window);
            host.Editor.TextArea.Focus();
            host.Editor.CaretOffset = 0;
            host.Window.KeyTextInput("X");
            WorkbenchUi.Settle(host.Window);
            Assert.Equal("Xalpha\nbeta\n", host.Editor.Document.Text);
            host.Window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            WorkbenchUi.Settle(host.Window);
            Assert.Equal("alpha\nbeta\n", host.Editor.Document.Text);
            Assert.Equal(new[] { "alpha", "beta", "" }, EditorUi.RenderedLines(host.Editor).Take(3).ToArray());
            host.Window.KeyPressQwerty(PhysicalKey.Y, RawInputModifiers.Control);
            WorkbenchUi.Settle(host.Window);
            Assert.Equal("Xalpha\nbeta\n", host.Editor.Document.Text);
            Assert.Equal("Xalpha", EditorUi.RenderedLines(host.Editor)[0]);
            host.Window.Close();
        });
    }

    private static EditorHost Show(FileDocumentViewModel model, double width = 240, double height = 400) =>
        EditorUi.Show(model, width, height);

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
        var root = TestTempFiles.NewDirectory("forge-text-editor-");
        roots.Add(root);
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(content)]);
        return path;
    }
}
