using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
public sealed class TextEditorLifecycleTests : IDisposable
{
    private readonly List<string> roots = [];

    public void Dispose()
    {
        foreach (var root in roots)
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Undo_survives_detaching_and_reattaching_the_view()
    {
        var path = Write("undo.txt", "alpha\nbeta\n");
        await Ui(async () =>
        {
            var model = await TestDocuments.LoadAsync(path);
            var view = new FileDocumentView { DataContext = model };
            var window = SharedUi.Show(view, 600, 400);
            WorkbenchUi.Settle(window);
            var editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            editor.TextArea.Focus();
            editor.CaretOffset = 0;
            window.KeyTextInput("X");
            WorkbenchUi.Settle(window);
            Assert.Equal("Xalpha\nbeta\n", editor.Document.Text);

            window.Content = null;
            WorkbenchUi.Settle(window);
            window.Content = view;
            WorkbenchUi.Settle(window);

            editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            Assert.Equal("Xalpha\nbeta\n", editor.Document.Text);
            editor.TextArea.Focus();
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            WorkbenchUi.Settle(window);
            Assert.Equal("alpha\nbeta\n", editor.Document.Text);
            window.Close();
        });
    }

    [Fact]
    public async Task Typing_on_one_line_keeps_the_tokens_of_untouched_lines_and_ctrl_c_copies_the_exact_text()
    {
        const string text = "using System;\nclass A { int x = 1; string s = \"a\"; // c\n}\n";
        var path = Write("Sample.cs", text);
        await Ui(async () =>
        {
            var model = await TestDocuments.LoadAsync(path);
            var host = Show(model);
            await WaitUntil(() => LineColors(host.Editor, 1).Distinct().Count() >= 2);
            var before = new[] { LineColors(host.Editor, 1), LineColors(host.Editor, 2) };
            host.Editor.TextArea.Focus();
            host.Editor.CaretOffset = 0;
            host.Window.KeyTextInput("X");
            WorkbenchUi.Settle(host.Window);
            Assert.True(model.IsDirty);
            await WaitUntil(() => LineColors(host.Editor, 0).Count > 0);
            Assert.Equal(before[0], LineColors(host.Editor, 1));
            Assert.Equal(before[1], LineColors(host.Editor, 2));

            host.Editor.SelectAll();
            host.Window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
            string? copied = null;
            await WaitUntil(() =>
            {
                copied = host.Window.Clipboard?.TryGetTextAsync().GetAwaiter().GetResult();
                return copied is not null;
            });
            Assert.Equal("X" + text, copied);
            host.Window.Close();
        });
    }

    [Fact]
    public async Task A_large_file_materializes_only_the_visible_lines()
    {
        var text = string.Concat(Enumerable.Range(0, 200_000).Select(static i => "line " + i + "\n"));
        var path = Write("large.txt", text);
        await Ui(async () =>
        {
            var model = await TestDocuments.LoadAsync(path);
            var host = Show(model);
            host.Editor.TextArea.TextView.EnsureVisualLines();
            Assert.Equal(200_001, host.Editor.Document.LineCount);
            Assert.InRange(host.Editor.TextArea.TextView.VisualLines.Count, 1, 100);
            host.Editor.ScrollToLine(150_000);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (true)
            {
                WorkbenchUi.Settle(host.Window);
                host.Editor.TextArea.TextView.EnsureVisualLines();
                if (host.Editor.TextArea.TextView.VisualLines.Any(static l => l.FirstDocumentLine.LineNumber >= 149_990))
                {
                    break;
                }
                Assert.True(DateTime.UtcNow < deadline, "The scrolled lines never materialized.");
                await Task.Delay(10);
            }
            Assert.InRange(host.Editor.TextArea.TextView.VisualLines.Count, 1, 100);
            Assert.Contains(host.Editor.TextArea.TextView.VisualLines, static l => l.FirstDocumentLine.LineNumber >= 149_990);
            host.Window.Close();
        });
    }

    [Fact]
    public async Task A_background_write_faults_the_editor_keeps_the_local_document_and_blocks_saving()
    {
        var path = Write("fault.txt", "alpha\nbeta\n");
        await Ui(async () =>
        {
            var model = await TestDocuments.LoadAsync(path);
            var host = Show(model);
            Assert.False(model.IsEditorFaulted);
            var shared = model.EditorDocument!;

            await Task.Run(() => shared.ApplyChange(new Forge.Editor.Text.TextChange(0, 0, "Z")));
            await WaitUntil(() => model.IsEditorFaulted);
            WorkbenchUi.Settle(host.Window);

            Assert.True(model.IsDirty);
            Assert.False(model.CanSave);
            Assert.False(((System.Windows.Input.ICommand)model.SaveCommand).CanExecute(null));
            Assert.True(model.HasEditorNotice);
            Assert.False(model.HasLspStatus);
            Assert.Equal("alpha\nbeta\n", host.Editor.Document.Text);
            Assert.True(host.Editor.IsReadOnly);

            ((System.Windows.Input.ICommand)model.SaveCommand).Execute(null);
            WorkbenchUi.Settle(host.Window);
            Assert.Equal("alpha\nbeta\n", await File.ReadAllTextAsync(path));
            Assert.True(model.IsEditorFaulted);

            await model.LoadAsync(path, CancellationToken.None);
            Assert.True(model.HasError);
            Assert.True(model.IsEditorFaulted);
            Assert.Same(shared, model.EditorDocument);
            Assert.StartsWith("Z", shared.Snapshot.GetText(), StringComparison.Ordinal);
            Assert.Equal("alpha\nbeta\n", host.Editor.Document.Text);

            await model.LoadAsync(path, CancellationToken.None, discardEdits: true);
            WorkbenchUi.Settle(host.Window);
            Assert.False(model.IsEditorFaulted);
            Assert.False(model.IsDirty);
            Assert.False(model.HasError);
            Assert.False(model.HasEditorNotice);
            Assert.False(model.HasSaveError);
            Assert.False(host.Editor.IsReadOnly);
            Assert.Equal("alpha\nbeta\n", host.Editor.Document.Text);
            host.Window.Close();
        });
    }

    private static List<Color> LineColors(TextEditor editor, int lineIndex)
    {
        var textView = editor.TextArea.TextView;
        textView.EnsureVisualLines();
        var line = textView.VisualLines.FirstOrDefault(l => l.FirstDocumentLine.LineNumber == lineIndex + 1);
        if (line is null)
        {
            return [];
        }

        return [.. line.TextLines.SelectMany(static t => t.TextRuns)
            .Where(static r => r is not TextEndOfLine && !string.IsNullOrWhiteSpace(r.Text.ToString()))
            .Select(static r => (r.Properties?.ForegroundBrush as ISolidColorBrush)?.Color ?? default)];
    }

    private static EditorHost Show(FileDocumentViewModel model) => EditorUi.Show(model);

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

    private string Write(string name, string content)
    {
        var root = TestTempFiles.NewDirectory("forge-lifecycle-");
        roots.Add(root);
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));
        return path;
    }
}
