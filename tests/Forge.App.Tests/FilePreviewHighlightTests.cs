using Avalonia.Controls;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Opening a file shows its text first and installs the highlighter afterwards, on the dispatcher's idle pass: the install
/// costs 30-90 ms of UI-thread work per document (its own grammar registry, model and coloured layout) and the click must not
/// wait for it. These checks pin both ends of that: the text is there in the same turn the document is bound, and a view that
/// goes away before the idle pass ran does not take the editor with it.
/// </summary>
[Collection("forge-app-ui")]
public sealed class FilePreviewHighlightTests
{
    private const string Source = """
        namespace Fixture;

        internal sealed class Sample
        {
            public int Value(string input) => input.Length;
        }
        """;

    [Fact]
    public Task The_text_is_bound_before_the_highlighter_is_installed() => SharedUi.RunAsync(async () =>
    {
        using var file = new TempFile("sample.cs", Source);
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(file.Path, CancellationToken.None);

        var window = new Window { Width = 800, Height = 600 };
        var view = new FileDocumentView { DataContext = model };
        window.Content = view;
        window.Show();

        // The same turn the document was bound: the file's text is in the editor, the highlighter has not been built yet.
        Assert.Contains("internal sealed class Sample", view.Editor.Document.Text);

        SharedUi.Settle(window);
        window.Content = null;
        window.Close();
        await model.ReleaseDocumentAsync();
    });

    [Fact]
    public Task A_view_that_goes_away_before_the_idle_pass_stays_usable() => SharedUi.RunAsync(async () =>
    {
        using var file = new TempFile("sample.cs", Source);
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(file.Path, CancellationToken.None);

        var window = new Window { Width = 800, Height = 600 };
        var view = new FileDocumentView { DataContext = model };
        window.Content = view;
        window.Show();

        // Detach in the same turn: the pending install belongs to a bridge that is being disposed.
        window.Content = null;
        SharedUi.Settle(window);
        Assert.Null(model.Error);

        // The view and the document survived it: attaching again shows the same text.
        window.Content = view;
        SharedUi.Settle(window);
        Assert.Contains("public int Value", view.Editor.Document.Text);

        window.Content = null;
        window.Close();
        await model.ReleaseDocumentAsync();
    });

    /// <summary>A scratch file under the temp directory, deleted with the test.</summary>
    private sealed class TempFile : IDisposable
    {
        private readonly string directory;

        public TempFile(string name, string content)
        {
            directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-preview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, name);
            File.WriteAllText(Path, content);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
