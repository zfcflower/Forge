using System.Text;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Forge.App.ViewModels.Preview;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// The document view model's preview dispatch: classification before the read, one preview view model per kind, the
/// editor document staying untouched during previews, and the preview released on every switch and release. No UI thread
/// is involved; bitmap decoding is stubbed through the injectable decoder.
/// </summary>
public sealed class FilePreviewDocumentTests : IDisposable
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
    public async Task Loading_a_csv_path_sets_a_table_preview_and_no_editor_document()
    {
        var path = Write("data.csv", "name,age\nalice,30\n");
        var model = FileDocumentViewModel.Create();

        await model.LoadAsync(path, CancellationToken.None);

        Assert.False(model.HasError, model.Error);
        Assert.False(model.HasDocument);
        Assert.Null(model.EditorDocument);
        Assert.True(model.HasPreview);
        var table = Assert.IsType<TableFilePreviewViewModel>(model.Preview);
        Assert.True(table.HasHeader);
        Assert.Equal(1, table.ShownRows);
        Assert.Equal("alice", table.Rows[0].Cells[0].Text);
        Assert.False(model.IsDirty);
        Assert.False(model.CanSave);
    }

    [Fact]
    public async Task Loading_a_text_path_still_creates_the_editor_document()
    {
        var path = Write("notes.txt", "plain text\n");
        var model = FileDocumentViewModel.Create();

        await model.LoadAsync(path, CancellationToken.None);

        Assert.False(model.HasError, model.Error);
        Assert.True(model.HasDocument);
        Assert.False(model.HasPreview);
        Assert.Null(model.Preview);
        Assert.Equal("plain text\n", model.EditText);
    }

    [Fact]
    public async Task Loading_a_binary_file_shows_the_binary_placeholder_not_an_error()
    {
        var path = WriteBytes("blob.bin", [0x00, 0x01, 0x02, 0x00, 0xFF]);
        var model = FileDocumentViewModel.Create();

        await model.LoadAsync(path, CancellationToken.None);

        Assert.False(model.HasError, model.Error);
        Assert.False(model.HasDocument);
        var card = Assert.IsType<PlaceholderFilePreviewViewModel>(model.Preview);
        Assert.Null(card.ShowSourceCommand);
        Assert.Null(card.LfsOid);
    }

    [Fact]
    public async Task Switching_files_replaces_the_preview_with_the_new_document()
    {
        var csv = Write("a.csv", "x\ny\n");
        var text = Write("b.txt", "text\n");
        var model = FileDocumentViewModel.Create();

        await model.LoadAsync(csv, CancellationToken.None);
        Assert.IsType<TableFilePreviewViewModel>(model.Preview);

        await model.LoadAsync(text, CancellationToken.None);

        Assert.Null(model.Preview);
        Assert.False(model.HasPreview);
        Assert.True(model.HasDocument);
        Assert.Equal("text\n", model.EditText);

        // And back to a preview: the editor document is released first, like any switch.
        await model.LoadAsync(csv, CancellationToken.None);
        Assert.IsType<TableFilePreviewViewModel>(model.Preview);
        Assert.False(model.HasDocument);
    }

    [Fact]
    public async Task Releasing_the_document_clears_the_preview()
    {
        var csv = Write("a.csv", "x\ny\n");
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(csv, CancellationToken.None);
        Assert.NotNull(model.Preview);

        await model.ReleaseDocumentAsync();

        Assert.Null(model.Preview);
        Assert.False(model.HasPreview);
        Assert.False(model.HasDocument);
        Assert.False(model.HasError, model.Error);
    }

    [Fact]
    public async Task An_image_over_the_preview_limit_shows_the_placeholder_card_and_is_not_decoded()
    {
        var path = NewPath("big.png");
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(3 * FilePreviewSettings.BytesPerMiB);
        }

        var settings = new FilePreviewSettings(new AppPreferences { PreviewImageBytes = 1 * FilePreviewSettings.BytesPerMiB });
        var model = FileDocumentViewModel.Create(previewSettings: settings, imageDecoder: _ => throw new InvalidOperationException("must not decode"));

        await model.LoadAsync(path, CancellationToken.None);

        Assert.False(model.HasError, model.Error);
        var card = Assert.IsType<PlaceholderFilePreviewViewModel>(model.Preview);
        Assert.Null(card.LfsOid);
        Assert.Equal(3 * FilePreviewSettings.BytesPerMiB, card.ByteSize);
    }

    [Fact]
    public async Task A_png_loads_an_image_preview_and_a_stubbed_decoder_is_a_notice()
    {
        var path = WriteBytes("photo.png", [0x89, 0x50, 0x4E, 0x47, 0x00]);
        var model = FileDocumentViewModel.Create(imageDecoder: _ => null);

        await model.LoadAsync(path, CancellationToken.None);

        Assert.False(model.HasError, model.Error);
        Assert.False(model.HasDocument);
        var image = Assert.IsType<ImageFilePreviewViewModel>(model.Preview);
        Assert.True(image.HasNotice);
        Assert.Null(image.Image);
    }

    [Fact]
    public async Task An_lfs_pointer_png_shows_the_pointer_card_with_its_fields()
    {
        const string pointer = "version https://git-lfs.github.com/spec/v1\noid sha256:abcd\nsize 42\n";
        var path = Write("photo.png", pointer);
        var model = FileDocumentViewModel.Create();

        await model.LoadAsync(path, CancellationToken.None);

        Assert.False(model.HasError, model.Error);
        var card = Assert.IsType<PlaceholderFilePreviewViewModel>(model.Preview);
        Assert.Equal("sha256:abcd", card.LfsOid);
        Assert.Equal(42, card.LfsSize);
        Assert.Equal("https://git-lfs.github.com/spec/v1", card.LfsVersion);
    }

    [Fact]
    public async Task An_html_preview_switches_to_the_text_path_when_show_source_runs()
    {
        var path = Write("page.html", "<html>hello</html>\n");
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);

        var card = Assert.IsType<PlaceholderFilePreviewViewModel>(model.Preview);
        Assert.NotNull(card.ShowSourceCommand);
        Assert.False(model.HasDocument);

        await model.ShowPreviewSourceAsync(CancellationToken.None);

        Assert.False(model.HasError, model.Error);
        Assert.Null(model.Preview);
        Assert.True(model.HasDocument);
        Assert.Equal("<html>hello</html>\n", model.EditText);
    }

    [Fact]
    public async Task A_malformed_notebook_produces_a_notice_never_an_exception()
    {
        var path = Write("broken.ipynb", "{not json");
        var model = FileDocumentViewModel.Create();

        await model.LoadAsync(path, CancellationToken.None);

        Assert.False(model.HasError, model.Error);
        var notebook = Assert.IsType<NotebookFilePreviewViewModel>(model.Preview);
        Assert.True(notebook.HasNotice);
        Assert.Empty(notebook.Cells);
        Assert.False(model.HasDocument);
    }

    private string NewRoot()
    {
        var root = TestTempFiles.NewDirectory("forge-preview-");
        roots.Add(root);
        return root;
    }

    private string NewPath(string name) => Path.Combine(NewRoot(), name);

    private string Write(string name, string content) => WriteBytes(name, new UTF8Encoding(false).GetBytes(content));

    private string WriteBytes(string name, byte[] content)
    {
        var path = NewPath(name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
