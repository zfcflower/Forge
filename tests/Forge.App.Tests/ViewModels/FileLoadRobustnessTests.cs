using System.Globalization;
using System.Text;
using Avalonia.Headless;
using Avalonia.Controls;
using Forge.App.Tests.Workbench;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.ViewModels;

[Collection("forge-app-ui")]
public sealed class FileLoadRobustnessTests : IDisposable
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
    public async Task A_file_with_more_than_20000_lines_is_loaded_completely()
    {
        var path = Write("many.txt", string.Concat(Enumerable.Range(0, 25000).Select(static i => "l" + i.ToString(CultureInfo.InvariantCulture) + "\n")));
        await Ui(async () =>
        {
            var model = FileDocumentViewModel.Create();
            await model.LoadAsync(path, CancellationToken.None);
            Assert.False(model.HasError, model.Error);
            Assert.Equal(25000, model.EditText.Count(static c => c == '\n'));
            Assert.Equal("l24999\n", model.GetLinesText(25000, 25000));
        });
    }

    [Fact]
    public async Task A_file_over_16_MiB_is_rejected_with_a_visible_error_and_no_document()
    {
        var path = Path.Combine(NewRoot(), "huge.txt");
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(FileDocumentViewModel.MaxFileBytes + 1);
        }

        await Ui(async () =>
        {
            var model = FileDocumentViewModel.Create();
            await model.LoadAsync(path, CancellationToken.None);
            Assert.True(model.HasError);
            Assert.False(string.IsNullOrWhiteSpace(model.Error));
            Assert.False(model.HasDocument);
            Assert.Null(model.EditorDocument);
            Assert.False(model.IsLoading);
            var view = new FileDocumentView { DataContext = model };
            var window = new Window { Width = 600, Height = 300, Content = view };
            window.Show();
            WorkbenchUi.Settle(window);
            Assert.True(WorkbenchUi.Find<TextBlock>(window, "file-error").IsVisible);
            window.Close();
        });
    }

    [Fact]
    public async Task Missing_and_locked_files_report_an_error_and_a_later_load_recovers()
    {
        var good = Write("good.txt", "fine\n");
        var locked = Write("locked.txt", "secret\n");
        await Ui(async () =>
        {
            var model = FileDocumentViewModel.Create();
            await model.LoadAsync(Path.Combine(NewRoot(), "missing.txt"), CancellationToken.None);
            Assert.True(model.HasError);
            Assert.False(model.HasDocument);
            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await model.LoadAsync(locked, CancellationToken.None);
                Assert.True(model.HasError);
                Assert.False(model.HasDocument);
                Assert.False(model.IsLoading);
            }

            await model.LoadAsync(good, CancellationToken.None);
            Assert.False(model.HasError, model.Error);
            Assert.True(model.HasDocument);
            Assert.Equal("fine", model.GetLinesText(1, 1).TrimEnd('\r', '\n'));
        });
    }

    [Fact]
    public async Task A_cancelled_load_throws_leaves_no_document_and_does_not_poison_the_next_load()
    {
        var path = Write("cancel.txt", "content\n");
        await Ui(async () =>
        {
            var model = FileDocumentViewModel.Create();
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.LoadAsync(path, cts.Token));
            Assert.False(model.IsLoading);
            Assert.False(model.HasDocument);
            await model.LoadAsync(path, CancellationToken.None);
            Assert.True(model.HasDocument);
            Assert.False(model.HasError, model.Error);
        });
    }

    [Fact]
    public async Task Concurrent_loads_keep_only_the_newest_file()
    {
        var big = Write("big.txt", string.Concat(Enumerable.Repeat("0123456789abcdef\n", 200_000)));
        var small = Write("small.txt", "winner\n");
        await Ui(async () =>
        {
            var model = FileDocumentViewModel.Create();
            var first = model.LoadAsync(big, CancellationToken.None);
            var second = model.LoadAsync(small, CancellationToken.None);
            await Task.WhenAll(first, second);
            Assert.Equal(Path.GetFullPath(small), model.Path);
            Assert.False(model.HasError, model.Error);
            Assert.False(model.IsLoading);
            Assert.Equal("winner", model.GetLinesText(1, 1).TrimEnd('\r', '\n'));
            Assert.Equal("winner\n", model.EditText);
        });
    }

    [Fact]
    public async Task Loading_over_unsaved_edits_is_refused_and_keeps_the_content()
    {
        var first = Write("first.txt", "one\n");
        var second = Write("second.txt", "two\n");
        await Ui(async () =>
        {
            var model = FileDocumentViewModel.Create();
            await model.LoadAsync(first, CancellationToken.None);
            model.IsEditing = true;
            model.EditText = "one edited\n";
            await model.LoadAsync(second, CancellationToken.None);
            Assert.True(model.HasError);
            Assert.Equal(Path.GetFullPath(first), model.Path);
            Assert.Equal("one edited\n", model.EditText);
            Assert.True(model.IsDirty);
        });
    }

    private static async Task Ui(Func<Task> body)
    {
        var session = SharedUi.Session;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await session.Dispatch(async () => { await body(); return 0; }, budget.Token);
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-file-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
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
