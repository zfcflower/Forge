using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.Palette;

/// <summary>
/// The four palette behaviours the palette's selection and preview depend on and that a rebuild, a notice row or a
/// close can each break: where movement starts when nothing is selected, that a notice is never a place the arrows can
/// land, that the selection still addresses the right row after a rebuild hands over equal instances, and that a file
/// read finishing after <c>Close</c> leaves the preview pane empty.
/// <para>Every wait is bounded (<see cref="TestPump.UntilAsync"/>); nothing here sleeps on a wall clock, and every row
/// assertion names the exact instance the view model holds.</para>
/// </summary>
public sealed class PaletteSelectionAndPreviewTests
{
    private const int Bound = 200;

    // ---------------------------------------------------------------- movement from no selection

    [Fact]
    public void Moving_down_with_nothing_selected_starts_at_the_first_row()
    {
        var host = HeldFiles("a.cs", "b.cs", "c.cs");
        using var viewModel = new PaletteViewModel(host, new AppPreferences());
        viewModel.Open(PaletteSource.Files);
        Assert.Equal(["a.cs", "b.cs", "c.cs"], Titles(viewModel));

        // The opening selected the first row; drop that so the move below is the one that has to find a start.
        viewModel.SelectedItem = null;
        Assert.Null(viewModel.SelectedItem);

        viewModel.MoveSelection(1);

        Assert.Same(viewModel.Items[0], viewModel.SelectedItem);
    }

    [Fact]
    public void Moving_up_with_nothing_selected_starts_at_the_last_row()
    {
        var host = HeldFiles("a.cs", "b.cs", "c.cs");
        using var viewModel = new PaletteViewModel(host, new AppPreferences());
        viewModel.Open(PaletteSource.Files);
        Assert.Equal(["a.cs", "b.cs", "c.cs"], Titles(viewModel));

        viewModel.SelectedItem = null;
        Assert.Null(viewModel.SelectedItem);

        viewModel.MoveSelection(-1);

        Assert.Same(viewModel.Items[2], viewModel.SelectedItem);
    }

    // ---------------------------------------------------------------- notice rows

    [Fact]
    public void A_list_of_one_notice_row_has_no_selection_and_movement_cannot_give_it_one()
    {
        var host = new FakePaletteHost { Workspace = null };
        host.HoldFilesRefresh();
        using var viewModel = new PaletteViewModel(host, new AppPreferences());

        viewModel.Open(PaletteSource.Files);

        var notice = Assert.Single(viewModel.Items);
        Assert.True(notice.IsNotice);
        Assert.False(notice.IsNavigable);
        Assert.Equal(Loc.Instance[PaletteStrings.NoticeNoWorkspace], notice.Title);
        Assert.Null(viewModel.SelectedItem);

        viewModel.MoveSelection(1);
        Assert.Null(viewModel.SelectedItem);

        viewModel.MoveSelection(-1);
        Assert.Null(viewModel.SelectedItem);
    }

    [Fact]
    public void A_notice_above_a_file_row_is_skipped_so_the_file_is_reachable_from_no_selection()
    {
        var notice = Notice();
        var file = FileRow("src/notes.txt");
        using var viewModel = new PaletteViewModel(new FakePaletteHost { Workspace = null }, new AppPreferences());
        InstallRows(viewModel, notice, file);

        viewModel.MoveSelection(1);
        Assert.Same(viewModel.Items[1], viewModel.SelectedItem);

        viewModel.SelectedItem = null;
        viewModel.MoveSelection(-1);
        Assert.Same(viewModel.Items[1], viewModel.SelectedItem);

        // Further up there is only the notice: the move must leave the selection where it is rather than onto it.
        viewModel.MoveSelection(-1);
        Assert.Same(viewModel.Items[1], viewModel.SelectedItem);
    }

    [Fact]
    public void Movement_steps_over_a_notice_that_sits_between_two_file_rows()
    {
        var first = FileRow("src/a.cs");
        var notice = Notice();
        var last = FileRow("src/c.cs");
        using var viewModel = new PaletteViewModel(new FakePaletteHost { Workspace = null }, new AppPreferences());
        InstallRows(viewModel, first, notice, last);
        viewModel.SelectedItem = first;
        Assert.Same(viewModel.Items[0], viewModel.SelectedItem);

        viewModel.MoveSelection(1);
        Assert.Same(viewModel.Items[2], viewModel.SelectedItem);

        viewModel.MoveSelection(-1);
        Assert.Same(viewModel.Items[0], viewModel.SelectedItem);
    }

    // ---------------------------------------------------------------- selection across a rebuild

    [Fact]
    public async Task The_selection_survives_a_rebuild_that_hands_over_equal_rows()
    {
        var host = HeldFiles("a.cs", "b.cs", "c.cs");
        using var viewModel = new PaletteViewModel(host, new AppPreferences());
        viewModel.Open(PaletteSource.Files);
        Assert.Equal(["a.cs", "b.cs", "c.cs"], Titles(viewModel));
        viewModel.MoveSelection(1);
        viewModel.MoveSelection(1);
        var selectedBefore = viewModel.Items[2];
        Assert.Same(selectedBefore, viewModel.SelectedItem);

        // The refresh answers with the same three files, so the rebuild hands over equal rows as new instances.
        host.ReleaseFilesRefresh(["a.cs", "b.cs", "c.cs"]);
        Assert.True(
            await TestPump.UntilAsync(
                () => viewModel.Items.Count == 3 && !ReferenceEquals(selectedBefore, viewModel.Items[2]),
                Bound),
            "the answered refresh never rebuilt the list");
        Assert.Equal(["a.cs", "b.cs", "c.cs"], Titles(viewModel));
        Assert.NotSame(selectedBefore, viewModel.Items[2]);

        viewModel.MoveSelection(-1);
        Assert.Same(viewModel.Items[1], viewModel.SelectedItem);

        viewModel.MoveSelection(1);
        Assert.Same(viewModel.Items[2], viewModel.SelectedItem);
    }

    // ---------------------------------------------------------------- a preview that outlives the palette

    [Fact]
    public async Task A_preview_that_finishes_after_Close_does_not_repaint_the_pane()
    {
        var root = TestTempFiles.NewDirectory("forge-palette-closed-preview-");
        try
        {
            // One long line rather than a short file: the read cannot finish in the microseconds between the palette
            // handing the row over and the Close below, so the read really does answer a palette that is already
            // closed. That ordering is the whole reason the read's continuation checks the palette is still its own.
            TestTempFiles.WriteFile(root, "notes.txt", new string('x', 2 * 1024 * 1024));
            var host = new FakePaletteHost { Workspace = root, CachedFiles = ["notes.txt"], FilesAnswer = ["notes.txt"] };
            host.HoldFilesRefresh();
            using var viewModel = new PaletteViewModel(host, new AppPreferences());

            // Opening selects the file row, which starts the read on the thread pool; the close follows at once.
            viewModel.Open(PaletteSource.Files);
            var row = Assert.Single(viewModel.Items);
            Assert.Same(row, viewModel.SelectedItem);
            Assert.Equal(Loc.Instance[PaletteStrings.PreviewFile], viewModel.PreviewTitle);
            Assert.True(viewModel.IsPreviewLoading, "the read has to be in flight when the palette closes");
            viewModel.Close();

            // Anything that repaints the pane after this point is the late read.
            var repaints = new List<string?>();
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PaletteViewModel.PreviewText) or nameof(PaletteViewModel.PreviewTitle))
                {
                    repaints.Add(e.PropertyName);
                }
            };

            Assert.False(
                await TestPump.UntilAsync(() => repaints.Count > 0, Bound),
                $"the closed pane was repainted through {string.Join(", ", repaints)}");

            Assert.False(viewModel.IsOpen);
            Assert.Equal(string.Empty, viewModel.PreviewTitle);
            Assert.Equal(string.Empty, viewModel.PreviewText);
            Assert.False(viewModel.HasPreview);
            Assert.Empty(viewModel.Items);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// A file host whose refresh never answers, so the rows and the selected instance stay exactly what the cached
    /// snapshot produced; a landing refresh rebuilds the list, and a test that asserts on one row's identity or moves the
    /// selection would then be racing it.
    /// </summary>
    private static FakePaletteHost HeldFiles(params string[] files)
    {
        var host = new FakePaletteHost { CachedFiles = files, FilesAnswer = files };
        host.HoldFilesRefresh();
        return host;
    }

    private static string[] Titles(PaletteViewModel viewModel) => [.. viewModel.Items.Select(static item => item.Title)];

    /// <summary>A notice row; the sentence is any localized one, because the row is only ever the one place Enter cannot go.</summary>
    private static PaletteItem Notice() =>
        PaletteItem.Notice(PaletteSource.Files, Loc.Instance[PaletteStrings.NoticeNoWorkspace]);

    private static PaletteItem FileRow(string path) =>
        new() { Source = PaletteSource.Files, Kind = PaletteItemKind.File, Title = path, Path = path };

    /// <summary>
    /// Installs an exact row list through the view model's documented test seam: no source ever emits a list that mixes a
    /// notice with a file row - a notice is only ever the whole list - so a mixed list cannot be arranged through the
    /// palette's own surface. Everything the test then does with the list goes through the public members, which is what
    /// makes the mixed case worth pinning at all.
    /// </summary>
    private static void InstallRows(PaletteViewModel viewModel, params PaletteItem[] rows) =>
        viewModel.InstallRowsForTest(rows);
}
