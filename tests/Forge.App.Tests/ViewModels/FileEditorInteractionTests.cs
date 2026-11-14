using System.Text;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Forge.App.Tests.Workbench;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests.ViewModels;

[Collection("forge-app-ui")]
public sealed class FileEditorInteractionTests : IDisposable
{
    private readonly List<string> roots = [];

    public void Dispose()
    {
        foreach (var root in roots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Save_writes_exactly_the_original_encoding_bom_line_endings_tabs_and_tail()
    {
        var cases = new (string Name, Encoding Encoding, string Text)[]
        {
            ("utf8-bom-crlf", new UTF8Encoding(true), "alpha\tone\r\nbeta\r\n"),
            ("utf8-lf-no-tail", new UTF8Encoding(false), "alpha\n\tbeta\ngamma"),
            ("utf8-empty-tail", new UTF8Encoding(false), "alpha\n\n\n"),
            ("utf16le-bom-lf", new UnicodeEncoding(false, true), "alpha\n\u4e2d\u6587 beta\n"),
            ("utf16be-bom-crlf", new UnicodeEncoding(true, true), "alpha\r\nbeta\r\n"),
        };
        await Ui(async () =>
        {
            foreach (var (name, encoding, text) in cases)
            {
                var path = Path.Combine(NewRoot(), name + ".txt");
                await File.WriteAllBytesAsync(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(text)]);
                // The HTML half is about the editor, so it opens the source; the placeholder routing is pinned elsewhere.
                var model = await TestDocuments.LoadAsync(path);
                Assert.False(model.HasError, name + ": " + model.Error);
                Assert.Equal(text, model.EditText);
                var edited = text.Replace("alpha", "ALPHA", StringComparison.Ordinal);
                model.IsEditing = true;
                model.EditText = edited;
                await Save(model);
                byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes(edited)];
                Assert.Equal(expected, await File.ReadAllBytesAsync(path));
                Assert.False(model.IsDirty, name);
            }
        });
    }

    [Fact]
    public async Task A_failed_save_on_a_read_only_file_keeps_dirty_text_and_a_later_save_succeeds()
    {
        var path = Write("readonly.txt", "keep\n");
        await Ui(async () =>
        {
            var model = await TestDocuments.LoadAsync(path);
            model.IsEditing = true;
            model.EditText = "changed\n";
            File.SetAttributes(path, FileAttributes.ReadOnly);
            await Save(model);
            Assert.True(model.HasSaveError);
            Assert.True(model.IsDirty);
            Assert.False(model.IsSaving);
            Assert.Equal("changed\n", model.EditText);
            Assert.Equal("keep\n", File.ReadAllText(path));
            File.SetAttributes(path, FileAttributes.Normal);
            await Save(model);
            Assert.False(model.HasSaveError);
            Assert.False(model.IsDirty);
            Assert.Equal("changed\n", File.ReadAllText(path));
        });
    }

    [Fact]
    public async Task Long_html_and_unbroken_plain_lines_scroll_to_their_end_in_a_240px_view()
    {
        var html = Write("long.html", "<div " + string.Join(' ', Enumerable.Range(0, 300).Select(i => $"data-attr{i}=\"value{i}\"")) + ">\n</div>\n");
        var plain = Write("long.txt", new string('x', 6000) + "\nshort\n");
        await Ui(async () =>
        {
            foreach (var path in new[] { html, plain })
            {
                // The HTML half is about the editor, so it opens the source; the placeholder routing is pinned elsewhere.
                var model = await TestDocuments.LoadAsync(path);
                var host = Show(model);
                var scroller = host.Editor.GetVisualDescendants().OfType<ScrollViewer>().First();
                Assert.True(scroller.Viewport.Width <= 240.5, path + " viewport " + scroller.Viewport.Width);
                Assert.True(scroller.Extent.Width > 1000, path + " extent " + scroller.Extent.Width);
                var end = scroller.Extent.Width - scroller.Viewport.Width;
                scroller.Offset = new Avalonia.Vector(end, scroller.Offset.Y);
                WorkbenchUi.Settle(host.Window);
                Assert.True(scroller.Offset.X >= end - 1, path + " offset " + scroller.Offset.X + " end " + end);
                host.Window.Close();
            }
        });
    }

    [Fact]
    public async Task Wrapping_grows_the_height_without_horizontal_scroll_or_leaving_the_view()
    {
        var path = Write("wrap.txt", new string('x', 3000) + "\nshort\n");
        await Ui(async () =>
        {
            var model = await TestDocuments.LoadAsync(path);
            var host = Show(model);
            var scroller = host.Editor.GetVisualDescendants().OfType<ScrollViewer>().First();
            var heightBefore = scroller.Extent.Height;
            WorkbenchUi.Click(host.Window, WorkbenchUi.CentreOf(WorkbenchUi.Find<Control>(host.Window, "file-wrap"), host.Window));
            Assert.True(model.WrapLines);
            WorkbenchUi.Settle(host.Window);
            Assert.True(scroller.Extent.Height > heightBefore * 3,
                $"height {heightBefore} -> {scroller.Extent.Height}; viewport={scroller.Viewport}; extent={scroller.Extent}; hbar={scroller.HorizontalScrollBarVisibility}");
            Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 1, "extent " + scroller.Extent.Width + " viewport " + scroller.Viewport.Width);
            Assert.True(host.View.Bounds.Height <= host.Window.Height + 0.5);
            host.Window.Close();
        });
    }

    [Fact]
    public async Task Editing_in_the_real_editor_saves_with_ctrl_s_and_copy_fills_the_clipboard()
    {
        var path = Write("edit.txt", "alpha\nbeta\n");
        await Ui(async () =>
        {
            var model = await TestDocuments.LoadAsync(path);
            var host = Show(model, width: 600);
            var editor = host.Editor;
            editor.TextArea.Focus();
            editor.CaretOffset = 0;
            WorkbenchUi.Settle(host.Window);
            host.Window.KeyTextInput("X");
            WorkbenchUi.Settle(host.Window);
            Assert.Equal("Xalpha\nbeta\n", model.EditText);
            Assert.True(model.IsDirty);
            Assert.Equal("alpha\nbeta\n", File.ReadAllText(path));
            host.Window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
            await WaitUntil(() => !model.IsDirty && !model.IsSaving);
            Assert.Equal("Xalpha\nbeta\n", File.ReadAllText(path));
            WorkbenchUi.Click(host.Window, WorkbenchUi.CentreOf(WorkbenchUi.Find<Control>(host.Window, "file-copy"), host.Window));
            string? copied = null;
            await WaitUntil(() =>
            {
                copied = host.Window.Clipboard?.TryGetTextAsync().GetAwaiter().GetResult();
                return copied == "Xalpha\nbeta\n";
            });
            Assert.Equal("Xalpha\nbeta\n", copied);
            host.Window.Close();
        });
    }

    [Fact]
    public async Task Declining_one_of_two_dirty_files_closes_nothing_and_marks_neither_clean()
    {
        var first = Write("a.txt", "a\n");
        var second = Write("b.txt", "b\n");
        var session = SharedUi.Session;
        await using var shell = await ShellFixture.OpenAsync(session);
        await session.Dispatch(async () =>
        {
            var model = (MainWindowViewModel)shell.Window.DataContext!;
            var dialogs = new Dialogs(call => call == 1);
            model.AttachDialogs(dialogs);
            var documents = new List<FileDocumentViewModel>();
            foreach (var path in new[] { first, second })
            {
                var document = await TestDocuments.LoadAsync(path);
                model.Workbench.OpenTab(model.Workbench.PreviewTabs, WorkbenchIds.PinnedFileTab(path), TabKind.FilePreview, document, document.TabTitle);
                document.IsEditing = true;
                document.EditText = "edited " + Path.GetFileName(path) + "\n";
                documents.Add(document);
            }

            Assert.False(await model.ConfirmCloseWindowAsync());
            Assert.Equal(2, dialogs.Calls);
            Assert.All(documents, static d => Assert.True(d.IsDirty));
            Assert.Equal("edited a.txt\n", documents[0].EditText);
            Assert.Equal("edited b.txt\n", documents[1].EditText);
            Assert.True(model.HasUnsavedFiles);
            dialogs.Answer = static _ => true;
            Assert.True(await model.ConfirmCloseWindowAsync());
            Assert.All(documents, static d => Assert.True(d.IsDirty));
            return 0;
        }, WorkbenchUi.Budget());
    }

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

    private static async Task Save(FileDocumentViewModel model)
    {
        ((ICommand)model.SaveCommand).Execute(null);
        await WaitUntil(() => !model.IsSaving && (!model.IsDirty || model.HasSaveError));
    }

    private static ScrollViewer Scroller(ListBox list) => list.GetVisualDescendants().OfType<ScrollViewer>().First();
    private sealed record FileHost(Window Window, FileDocumentView View, AvaloniaEdit.TextEditor Editor);

    private static FileHost Show(FileDocumentViewModel model, double width = 240, double height = 400)
    {
        var view = new FileDocumentView { DataContext = model };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        WorkbenchUi.Settle(window);
        return new FileHost(window, view, view.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single());
    }

    private sealed class Dialogs(Func<int, bool> answer) : IDialogService
    {
        public Func<int, bool> Answer { get; set; } = answer;
        public int Calls { get; private set; }
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText) => Task.FromResult(Answer(++Calls));
        public Task<string?> PromptTextAsync(string title, string initial) => Task.FromResult<string?>(null);
        public Task<NewBranchChoice?> PickNewBranchAsync(NewBranchRequest request, CancellationToken ct) => Task.FromResult<NewBranchChoice?>(null);
    }

    private string NewRoot()
    {
        var root = TestTempFiles.NewDirectory("forge-file-editor-");
        roots.Add(root);
        return root;
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(NewRoot(), name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }
}
