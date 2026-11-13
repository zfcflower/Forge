using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Palette;

/// <summary>
/// Pins the palette's file preview: the head of the file for a row without a position, the lines around a 1-based line
/// otherwise (clamped to the file), every line numbered and padded, an empty file previewing as an empty string, a
/// missing file as null, the decoded file surviving the deletion of the file it came from, and a cancelled read
/// surfacing as <see cref="OperationCanceledException"/> instead of a preview.
/// </summary>
public sealed class PalettePreviewTests : IDisposable
{
    private readonly List<string> roots = [];

    public void Dispose()
    {
        foreach (var root in roots)
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task A_row_without_a_position_shows_the_head_of_the_file()
    {
        var path = NewFile(100);
        var preview = new PalettePreview();

        var atZero = await preview.ReadAsync(path, 0, CancellationToken.None);
        var atOne = await preview.ReadAsync(path, 1, CancellationToken.None);

        Assert.NotNull(atZero);
        var rows = atZero.Split('\n');
        Assert.Equal(PalettePreview.HeadLines, rows.Length);
        Assert.Equal("    1  line1", rows[0]);
        Assert.Equal("   40  line40", rows[^1]);
        Assert.Equal(atZero, atOne);
    }

    [Fact]
    public async Task A_position_shows_the_lines_around_it()
    {
        var preview = new PalettePreview();

        var text = await preview.ReadAsync(NewFile(100), 50, CancellationToken.None);

        Assert.NotNull(text);
        var rows = text.Split('\n');
        Assert.Equal(25, rows.Length);
        Assert.Equal("   38  line38", rows[0]);
        Assert.Equal("   50  line50", rows[12]);
        Assert.Equal("   62  line62", rows[^1]);
    }

    [Fact]
    public async Task A_window_near_the_start_still_starts_at_the_first_line()
    {
        var preview = new PalettePreview();

        var text = await preview.ReadAsync(NewFile(100), 5, CancellationToken.None);

        Assert.NotNull(text);
        var rows = text.Split('\n');
        Assert.Equal(17, rows.Length);
        Assert.Equal("    1  line1", rows[0]);
        Assert.Equal("   17  line17", rows[^1]);
    }

    [Fact]
    public async Task A_position_past_the_file_shows_its_tail()
    {
        var preview = new PalettePreview();

        var text = await preview.ReadAsync(NewFile(100), 101, CancellationToken.None);

        Assert.NotNull(text);
        var rows = text.Split('\n');
        Assert.Equal(25, rows.Length);
        Assert.Equal("   76  line76", rows[0]);
        Assert.Equal("  100  line100", rows[^1]);
    }

    [Fact]
    public async Task A_file_shorter_than_the_window_shows_all_of_it()
    {
        var preview = new PalettePreview();

        var text = await preview.ReadAsync(NewFile(3), 8, CancellationToken.None);

        Assert.Equal("    1  line1\n    2  line2\n    3  line3", text);
    }

    [Fact]
    public async Task An_empty_file_previews_as_an_empty_string()
    {
        var root = NewRoot();
        var path = TestTempFiles.WriteFile(root, "empty.txt", string.Empty);

        var text = await new PalettePreview().ReadAsync(path, 1, CancellationToken.None);

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public async Task A_missing_file_previews_as_null()
    {
        var path = Path.Combine(NewRoot(), "missing.txt");

        Assert.Null(await new PalettePreview().ReadAsync(path, 1, CancellationToken.None));
    }

    [Fact]
    public async Task The_decoded_file_survives_its_deletion()
    {
        var root = NewRoot();
        var path = TestTempFiles.WriteFile(root, "cached.txt", "alpha\nbeta\n");
        var preview = new PalettePreview();
        var first = await preview.ReadAsync(path, 1, CancellationToken.None);

        File.Delete(path);
        var second = await preview.ReadAsync(path, 1, CancellationToken.None);

        Assert.Equal("    1  alpha\n    2  beta", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task A_line_longer_than_the_budget_is_kept_truncated()
    {
        // A minified bundle is one line of megabytes; the preview has to stop at its character budget instead of
        // materializing the line, so what comes back is the numbered prefix of exactly the budget.
        var oversized = new string('x', PalettePreview.MaxPreviewChars * 2);
        var path = TestTempFiles.WriteFile(NewRoot(), "bundle.min.js", oversized + "\nsecond\n");
        var preview = new PalettePreview();

        var text = await preview.ReadAsync(path, 0, CancellationToken.None);

        Assert.NotNull(text);
        Assert.Equal("    1  " + oversized[..PalettePreview.MaxPreviewChars], text);
    }

    [Fact]
    public async Task A_cancelled_read_throws_instead_of_previewing()
    {
        var path = NewFile(10);
        var preview = new PalettePreview();
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview.ReadAsync(path, 1, source.Token));
    }

    private string NewRoot()
    {
        var root = TestTempFiles.NewDirectory("forge-palette-preview-");
        roots.Add(root);
        return root;
    }

    /// <summary>A file whose <paramref name="lineCount"/> lines read <c>line1</c> … <c>lineN</c>, one per line.</summary>
    private string NewFile(int lineCount)
    {
        var text = string.Concat(Enumerable.Range(1, lineCount).Select(line => "line" + line + "\n"));
        return TestTempFiles.WriteFile(NewRoot(), "preview.txt", text);
    }
}
