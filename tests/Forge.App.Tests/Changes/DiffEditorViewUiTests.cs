using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Forge.App.Changes;
using Forge.App.Review;
using Forge.App.Tests.Workbench;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Changes;

[Collection("forge-app-ui")]
public sealed class DiffEditorViewUiTests
{
    [Fact]
    public Task Only_the_rows_in_view_get_overlay_controls_and_they_sit_on_their_rows() => Ui(() =>
    {
        var files = DiffFixtures.Files(50);
        using var s = Scene.Open(files);
        var placeholders = Enumerable.Range(0, s.View.PrimaryMap.Count).Count(i => s.View.PrimaryMap[i].IsPlaceholder);
        Assert.True(s.View.ActiveOverlayCount > 0);
        Assert.True(s.View.ActiveOverlayCount < placeholders / 4, $"{s.View.ActiveOverlayCount} of {placeholders}");
        var header = s.Header(files[0]);
        Assert.Equal(files[0], header.DataContext);
        var textView = s.View.PrimaryEditor.TextArea.TextView;
        var width = textView.Bounds.Width;
        Assert.InRange(header.Bounds.Width, width - 1, width + 1);
        Assert.InRange(header.Bounds.Height, 36, 37);
        Assert.InRange(s.TopOf(header), -1, 1);
        foreach (var control in s.View.ActiveOverlayControls)
        {
            var top = s.TopOf(control);
            Assert.InRange(top, -control.Bounds.Height - 1, s.View.Bounds.Height + 1);
        }
    });

    [Fact]
    public Task Placeholder_lines_reserve_the_height_of_the_control_that_covers_them() => Ui(() =>
    {
        var files = DiffFixtures.Files(3);
        using var s = Scene.Open(files);
        var textView = s.View.PrimaryEditor.TextArea.TextView;
        var map = s.View.PrimaryMap;
        var header = textView.GetVisualTopByDocumentLine(map.HeaderLine(0));
        var next = textView.GetVisualTopByDocumentLine(map.HeaderLine(0) + 1);
        Assert.InRange(next - header, DiffPlaceholderGenerator.HeaderHeight, DiffPlaceholderGenerator.HeaderHeight + 1);
        var fold = textView.GetVisualTopByDocumentLine(2);
        Assert.InRange(textView.GetVisualTopByDocumentLine(3) - fold, DiffPlaceholderGenerator.FoldHeight, DiffPlaceholderGenerator.FoldHeight + 1);
        var bar = textView.GetVisualTopByDocumentLine(6);
        Assert.InRange(textView.GetVisualTopByDocumentLine(7) - bar, DiffBlockBar.BarHeight, DiffBlockBar.BarHeight + 1);
        var lastFileHeader = textView.GetVisualTopByDocumentLine(map.HeaderLine(2));
        var expected = 0.0;
        for (var i = 0; i < map.HeaderLine(2) - 1; i++)
        {
            var row = map[i];
            expected += row.IsPlaceholder ? DiffPlaceholderGenerator.HeightOf(row, []) : textView.DefaultLineHeight;
        }

        Assert.InRange(lastFileHeader, expected, expected + map.HeaderLine(2));
    });

    [Fact]
    public Task Heights_of_lines_never_scrolled_to_are_known_so_the_document_height_is_right_from_the_start() => Ui(() =>
    {
        var files = DiffFixtures.Files(30);
        using var s = Scene.Open(files);
        var textView = s.View.PrimaryEditor.TextArea.TextView;
        var map = s.View.PrimaryMap;
        var expected = 0.0;
        for (var i = 0; i < map.Count; i++)
        {
            expected += map[i].IsPlaceholder ? DiffPlaceholderGenerator.HeightOf(map[i], []) : textView.DefaultLineHeight;
        }

        Assert.InRange(textView.DocumentHeight, expected, expected + map.Count);
    });

    [Fact]
    public Task The_gutter_is_the_diff_gutter_and_the_editors_are_read_only() => Ui(() =>
    {
        using var s = Scene.Open(DiffFixtures.Files(2));
        var margins = s.View.PrimaryEditor.TextArea.LeftMargins;
        Assert.Single(margins.OfType<DiffLineNumberMargin>());
        Assert.Empty(margins.OfType<LineNumberMargin>());
        Assert.True(s.View.PrimaryEditor.IsReadOnly);
        Assert.True(s.View.SecondaryEditor.IsReadOnly);
        var first = s.View.PrimaryEditor.TextArea.TextView.VisualLines[0];
        Assert.Contains(first.Elements, static element => element is InlineObjectElement);
    });

    [Fact]
    public Task Scrolling_to_a_file_puts_its_header_at_the_top_and_reports_it() => Ui(() =>
    {
        var files = DiffFixtures.Files(20);
        using var s = Scene.Open(files);
        var reported = new List<IDiffFileItem>();
        s.View.TopFileChanged += reported.Add;
        s.View.ScrollToFile(files[7]);
        s.Settle();
        var header = s.Header(files[7]);
        Assert.InRange(s.TopOf(header), -1, 1);
        Assert.Same(files[7], reported[^1]);
    });

    [Fact]
    public Task Scrolling_by_hand_reports_each_file_that_reaches_the_top() => Ui(() =>
    {
        var files = DiffFixtures.Files(10);
        using var s = Scene.Open(files);
        var reported = new List<IDiffFileItem>();
        s.View.TopFileChanged += reported.Add;
        var textView = s.View.PrimaryEditor.TextArea.TextView;
        s.View.ScrollToOffset(textView.GetVisualTopByDocumentLine(s.View.PrimaryMap.HeaderLine(3)) + 5);
        s.Settle();
        Assert.Same(files[3], reported[^1]);
        s.View.ScrollToOffset(0);
        s.Settle();
        Assert.Same(files[0], reported[^1]);
    });

    [Fact]
    public Task Collapsing_a_file_above_keeps_the_file_in_view_where_it_was() => Ui(() =>
    {
        var files = DiffFixtures.Files(12);
        using var s = Scene.Open(files);
        s.View.ScrollToFile(files[6]);
        s.Settle();
        var before = s.TopOf(s.Header(files[6]));
        files[2].IsCollapsed = true;
        s.Settle();
        Assert.Equal(DiffRowKind.FileHeader, s.View.PrimaryMap[s.View.PrimaryMap.HeaderLine(2)].Kind);
        Assert.Equal(1, s.View.PrimaryMap.FileLastLine(2) - s.View.PrimaryMap.HeaderLine(2) + 1);
        var after = s.TopOf(s.Header(files[6]));
        Assert.Equal(before, after, 1);
    });

    [Fact]
    public Task Toggling_the_preview_flag_collapse_or_viewed_state_rebuilds_the_document() => Ui(() =>
    {
        var files = DiffFixtures.Files(2);
        using var s = Scene.Open(files);
        var rows = s.View.PrimaryMap.Count;
        files[0].IsCollapsed = true;
        files[0].IsCollapsed = false;
        files[0].IsViewed = true;
        s.Settle();
        Assert.Equal(rows, s.View.PrimaryMap.Count);
        files[1].IsCollapsed = true;
        s.Settle();
        Assert.Equal(rows - 11, s.View.PrimaryMap.Count);
        files[1].ReplaceDiff(DiffFixtures.Replace("src/f1.cs", 20, 15));
        files[1].IsCollapsed = false;
        s.Settle();
        Assert.Equal(DiffRowKind.Context, s.View.PrimaryMap[s.View.PrimaryMap.HeaderLine(1) + 1].Kind);
    });

    [Fact]
    public Task Clicking_a_fold_bar_expands_the_folded_lines_and_nothing_else() => Ui(() =>
    {
        var files = DiffFixtures.Files(1);
        using var s = Scene.Open(files);
        var fold = s.View.ActiveOverlayControls.OfType<DiffFoldBar>().First(static f => f.Key == 1);
        Assert.True(fold.CanExpand);
        Assert.Equal(12, s.View.PrimaryMap.Count);
        fold.FindControl<Button>("Bar")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        s.Settle();
        Assert.Equal(17, s.View.PrimaryMap.Count);
        Assert.DoesNotContain(s.View.ActiveOverlayControls.OfType<DiffFoldBar>(), static f => f.Key == 1);
        Assert.Contains(s.View.ActiveOverlayControls.OfType<DiffFoldBar>(), static f => f.Key == 14);
    });

    [Fact]
    public Task A_fold_bar_over_lines_the_diff_does_not_carry_cannot_be_expanded() => Ui(() =>
    {
        var files = new List<FakeFile> { DiffFixtures.File("src/a.cs", DiffFixtures.TwoHunks("src/a.cs"), fullContext: false) };
        using var s = Scene.Open(files);
        var gap = s.View.ActiveOverlayControls.OfType<DiffFoldBar>().Single(static f => !f.CanExpand);
        Assert.False(gap.FindControl<Button>("Bar")!.IsEnabled);
        var rows = s.View.PrimaryMap.Count;
        gap.FindControl<Button>("Bar")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        s.Settle();
        Assert.Equal(rows, s.View.PrimaryMap.Count);
    });

    [Fact]
    public Task Every_overlay_control_is_findable_by_automation_id() => Ui(() =>
    {
        var files = DiffFixtures.Files(2);
        using var s = Scene.Open(files);
        Assert.NotNull(WorkbenchUi.TryFind<DiffFileHeader>(s.Window, "diff-file-header"));
        Assert.NotNull(WorkbenchUi.TryFind<DiffBlockBar>(s.Window, "diff-block-bar"));
        Assert.NotNull(WorkbenchUi.TryFind<Button>(s.Window, "diff-fold"));
        Assert.NotNull(WorkbenchUi.TryFind<Button>(s.Window, "diff-file-collapse"));
        Assert.NotNull(WorkbenchUi.TryFind<Button>(s.Window, "diff-block-keep"));
        Assert.NotNull(WorkbenchUi.TryFind<Button>(s.Window, "diff-block-revert"));
        Assert.NotNull(WorkbenchUi.TryFind<CheckBox>(s.Window, "diff-file-viewed"));
        foreach (var button in s.View.ActiveOverlayControls.SelectMany(static c => c.GetVisualDescendants().OfType<Button>()))
        {
            Assert.False(string.IsNullOrEmpty(AutomationProperties.GetAutomationId(button)), button.GetType().Name);
        }
    });

    [Fact]
    public Task Split_mode_shows_two_synchronised_editors_with_the_same_rows() => Ui(() =>
    {
        var files = DiffFixtures.Files(15);
        using var s = Scene.Open(files);
        Assert.False(s.View.SecondaryEditor.IsVisible);
        s.View.Mode = DiffViewMode.Split;
        s.Settle();
        Assert.True(s.View.SecondaryEditor.IsVisible);
        Assert.True(s.View.IsSplit);
        Assert.Equal(s.View.PrimaryEditor.Document.LineCount, s.View.SecondaryEditor.Document.LineCount);
        Assert.Equal(s.View.PrimaryMap.Count, s.View.SecondaryMap.Count);
        Assert.Contains(s.View.ActiveOverlayControls, static c => c is DiffFileHeader);
        var view = s.View.PrimaryEditor.TextArea.TextView;
        s.View.ScrollToOffset(view.GetVisualTopByDocumentLine(s.View.PrimaryMap.HeaderLine(5)));
        s.Settle();
        Assert.Equal(view.ScrollOffset.Y, s.View.SecondaryEditor.TextArea.TextView.ScrollOffset.Y, 1);
        DiffEditorView.SetOffset(s.View.SecondaryEditor, 0);
        s.Settle();
        Assert.Equal(0, view.ScrollOffset.Y, 1);
        s.View.Mode = DiffViewMode.Inline;
        s.Settle();
        Assert.False(s.View.SecondaryEditor.IsVisible);
        Assert.False(s.View.IsSplit);
    });

    [Fact]
    public Task Split_mode_headers_span_both_editors_and_the_old_side_shows_the_removed_text() => Ui(() =>
    {
        var files = DiffFixtures.Files(2);
        using var s = Scene.Open(files, DiffViewMode.Split);
        var header = s.Header(files[0]);
        var left = s.View.PrimaryEditor.Bounds.Left;
        var right = s.View.SecondaryEditor.Bounds.Right;
        Assert.InRange(header.Bounds.Width, (right - left) * 0.9, right - left + 1);
        Assert.Contains("old10", s.View.PrimaryEditor.Document.Text);
        Assert.DoesNotContain("new10", s.View.PrimaryEditor.Document.Text);
        Assert.Contains("new10", s.View.SecondaryEditor.Document.Text);
    });

    [Fact]
    public Task Word_wrap_applies_to_the_inline_editor_only() => Ui(() =>
    {
        using var s = Scene.Open(DiffFixtures.Files(1));
        s.View.WordWrap = true;
        Assert.True(s.View.PrimaryEditor.WordWrap);
        s.View.Mode = DiffViewMode.Split;
        s.Settle();
        Assert.False(s.View.PrimaryEditor.WordWrap);
        Assert.False(s.View.SecondaryEditor.WordWrap);
    });

    [Fact]
    public Task A_markdown_file_shows_the_factory_control_in_place_of_its_diff_until_switched_to_source() => Ui(() =>
    {
        var files = new List<FakeFile>
        {
            DiffFixtures.File("docs/a.md"),
            DiffFixtures.File("src/b.cs"),
        };
        var created = new List<Border>();
        using var s = Scene.Open(files, factory: item =>
        {
            var border = new Border { Height = 120, Background = Avalonia.Media.Brushes.Gray };
            created.Add(border);
            return border;
        });
        Assert.Single(created);
        Assert.Equal(2, s.View.PrimaryMap.HeaderLine(1) - s.View.PrimaryMap.HeaderLine(0) );
        Assert.Contains(created[0], s.View.ActiveOverlayControls);
        Assert.InRange(created[0].Bounds.Height, 120, 121);
        var textView = s.View.PrimaryEditor.TextArea.TextView;
        var gap = textView.GetVisualTopByDocumentLine(s.View.PrimaryMap.HeaderLine(1)) - textView.GetVisualTopByDocumentLine(2);
        Assert.InRange(gap, 120, 121);

        files[0].IsMarkdownPreview = false;
        s.Settle();
        Assert.DoesNotContain(created[0], s.View.ActiveOverlayControls);
        Assert.Contains("new10", s.View.PrimaryEditor.Document.Text);
        Assert.Contains(DiffRowKind.Added, Enumerable.Range(0, s.View.PrimaryMap.Count).Select(i => s.View.PrimaryMap[i].Kind));
    });

    [Fact]
    public Task Without_a_preview_factory_a_markdown_file_is_shown_as_a_source_diff() => Ui(() =>
    {
        using var s = Scene.Open([DiffFixtures.File("docs/a.md")]);
        Assert.Contains("new10", s.View.PrimaryEditor.Document.Text);
    });

    [Fact]
    public Task Replacing_the_files_carries_expanded_folds_by_path_and_stops_listening_to_the_old_items() => Ui(() =>
    {
        var first = DiffFixtures.Files(1);
        using var s = Scene.Open(first);
        s.View.ActiveOverlayControls.OfType<DiffFoldBar>().First(static f => f.Key == 1)
            .FindControl<Button>("Bar")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        s.Settle();
        Assert.Equal(17, s.View.PrimaryMap.Count);
        var second = DiffFixtures.Files(1);
        s.View.Files = second;
        s.Settle();
        Assert.Equal(17, s.View.PrimaryMap.Count);
        var map = s.View.PrimaryMap;
        first[0].IsCollapsed = true;
        s.Settle();
        Assert.Same(map, s.View.PrimaryMap);

        // A reload whose diff has no fold with that key drops the state instead of expanding something else.
        s.View.Files = new List<FakeFile> { DiffFixtures.File("src/f0.cs", DiffFixtures.Replace("src/f0.cs", 6, 3)) };
        s.Settle();
        s.View.Files = DiffFixtures.Files(1);
        s.Settle();
        Assert.Equal(12, s.View.PrimaryMap.Count);
        s.View.Files = null;
        s.Settle();
        Assert.Equal(0, s.View.PrimaryMap.Count);
        Assert.Equal(0, s.View.ActiveOverlayCount);
    });

    [Fact]
    public Task Toggling_one_file_in_a_many_file_diff_rebuilds_only_that_file() => Ui(() =>
    {
        var files = DiffFixtures.Files(40);
        using var s = Scene.Open(files);
        s.View.ScrollToFile(files[20]);
        s.Settle();
        var top = s.TopOf(s.Header(files[20]));
        var rows = s.View.PrimaryMap.Count;
        var built = s.View.FilesBuilt;
        var scroll = s.View.PrimaryEditor.TextArea.TextView.ScrollOffset.Y;
        files[5].IsViewed = true;
        files[5].IsCollapsed = true;
        s.Settle();
        Assert.Equal(built + 1, s.View.FilesBuilt);
        Assert.Equal(rows - 11, s.View.PrimaryMap.Count);
        s.AssertConsistent(files);
        Assert.Equal(top, s.TopOf(s.Header(files[20])), 1);
        Assert.NotEqual(scroll, s.View.PrimaryEditor.TextArea.TextView.ScrollOffset.Y);

        files[5].IsCollapsed = false;
        s.Settle();
        Assert.Equal(built + 2, s.View.FilesBuilt);
        Assert.Equal(rows, s.View.PrimaryMap.Count);
        s.AssertConsistent(files);
        Assert.Equal(top, s.TopOf(s.Header(files[20])), 1);

        // Several changes in one turn still touch only those files.
        files[1].IsCollapsed = true;
        files[30].IsCollapsed = true;
        s.Settle();
        Assert.Equal(built + 4, s.View.FilesBuilt);
        s.AssertConsistent(files);

        files[30].ReplaceDiff(DiffFixtures.Replace("src/f30.cs", 20, 15));
        s.Settle();
        Assert.Equal(built + 5, s.View.FilesBuilt);
        s.AssertConsistent(files);
    });

    [Fact]
    public Task Expanding_a_fold_and_toggling_the_markdown_preview_rebuild_only_their_file() => Ui(() =>
    {
        var files = new List<FakeFile>
        {
            DiffFixtures.File("src/a.cs"),
            DiffFixtures.File("docs/b.md"),
            DiffFixtures.File("src/c.cs"),
            DiffFixtures.File("docs/d.md"),
        };
        var created = new List<Border>();
        using var s = Scene.Open(files, factory: _ =>
        {
            var border = new Border { Height = 80, Background = Avalonia.Media.Brushes.Gray };
            created.Add(border);
            return border;
        });
        Assert.Equal(2, created.Count);
        var built = s.View.FilesBuilt;
        s.View.ActiveOverlayControls.OfType<DiffFoldBar>().First(static f => f.Key == 1)
            .FindControl<Button>("Bar")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        s.Settle();
        Assert.Equal(built + 1, s.View.FilesBuilt);
        Assert.Equal(2, created.Count);
        s.AssertConsistent(files);

        files[1].IsMarkdownPreview = false;
        s.Settle();
        Assert.Equal(built + 2, s.View.FilesBuilt);
        Assert.Equal(2, created.Count);
        s.AssertConsistent(files);
        files[1].IsMarkdownPreview = true;
        s.Settle();
        Assert.Equal(built + 3, s.View.FilesBuilt);
        Assert.Equal(2, created.Count);
        s.AssertConsistent(files);
    });

    [Fact]
    public Task A_detached_view_stops_listening_to_its_items_and_listens_again_when_attached() => Ui(() =>
    {
        var files = DiffFixtures.Files(3);
        using var s = Scene.Open(files);
        s.Window.Content = null;
        s.Settle();
        var built = s.View.FilesBuilt;
        files[0].IsCollapsed = true;
        files[1].ReplaceDiff(DiffFixtures.Replace("src/f1.cs", 20, 15));
        WorkbenchUi.Settle(s.Window);
        Assert.Equal(built, s.View.FilesBuilt);

        s.Window.Content = s.View;
        s.Settle();
        Assert.Equal(1, s.View.PrimaryMap.FileLastLine(0) - s.View.PrimaryMap.HeaderLine(0) + 1);
        built = s.View.FilesBuilt;
        files[2].IsCollapsed = true;
        s.Settle();
        Assert.Equal(built + 1, s.View.FilesBuilt);
        s.AssertConsistent(files);
    });

    [Fact]
    public Task The_review_status_never_sits_on_the_first_file_header() => Ui(async () =>
    {
        var files = DiffFixtures.Files(2);
        using var s = Scene.Open(files);
        var status = WorkbenchUi.Find<TextBlock>(s.Window, "diff-review-status");
        Assert.False(status.IsVisible);
        await s.AttachReviewAsync();
        var draft = s.View.ReviewController.BeginDraft(8, 8)!;
        Assert.True(await s.View.ReviewController.SaveAsync(draft, "note"));
        s.Settle();
        Assert.True(status.IsVisible);
        var header = s.Header(files[0]);
        var headerBottom = header.TranslatePoint(new Point(0, header.Bounds.Height), s.Window)!.Value.Y;
        var statusTop = status.TranslatePoint(default, s.Window)!.Value.Y;
        Assert.True(statusTop >= headerBottom, $"status top {statusTop} vs header bottom {headerBottom}");
    });

    [Fact]
    public Task Ctrl_C_copies_the_selected_code_lines_without_the_placeholder_lines() => Ui(async () =>
    {
        var files = DiffFixtures.Files(1);
        using var s = Scene.Open(files);
        var editor = s.View.PrimaryEditor;
        editor.TextArea.Focus();
        editor.SelectAll();
        var clipboard = s.Window.Clipboard;
        Assert.NotNull(clipboard);
        await clipboard.ClearAsync();
        editor.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.C,
            KeyModifiers = KeyModifiers.Control,
            Source = editor.TextArea,
        });
        var text = await clipboard.TryGetTextAsync();
        Assert.Equal("line7\nline8\nline9\nold10\nnew10\nline11\nline12\nline13", text);
    });

    [Fact]
    public Task Selecting_a_new_side_line_offers_a_comment_and_a_removed_line_does_not() => Ui(async () =>
    {
        var files = DiffFixtures.Files(1);
        using var s = Scene.Open(files);
        await s.AttachReviewAsync();
        var editor = s.View.ReviewEditor;
        var added = editor.Document.GetLineByNumber(8);
        Assert.Equal("new10", editor.Document.GetText(added));
        editor.TextArea.Focus();
        editor.Select(added.Offset, added.Length);
        s.Settle();
        var icon = WorkbenchUi.Find<Button>(s.Window, "review-add");
        Assert.True(icon.IsVisible);
        var removed = editor.Document.GetLineByNumber(7);
        editor.Select(removed.Offset, removed.Length);
        s.Settle();
        Assert.False(icon.IsVisible);
        var header = editor.Document.GetLineByNumber(1);
        editor.Select(header.Offset, header.Length);
        s.Settle();
        Assert.False(icon.IsVisible);
    });

    [Fact]
    public Task A_saved_comment_shows_a_marker_on_its_line_and_survives_a_rebuild() => Ui(async () =>
    {
        var files = DiffFixtures.Files(1);
        using var s = Scene.Open(files);
        await s.AttachReviewAsync();
        var controller = s.View.ReviewController;
        var draft = controller.BeginDraft(8, 8)!;
        Assert.True(await controller.SaveAsync(draft, "marked"));
        s.Settle();
        Assert.Single(s.Markers());
        files[0].IsCollapsed = true;
        s.Settle();
        await s.IdleReviewAsync();
        s.Settle();
        Assert.Single(controller.Drafts);
        files[0].IsCollapsed = false;
        s.Settle();
        await s.IdleReviewAsync();
        s.Settle();
        Assert.Single(controller.Drafts);
        Assert.True(controller.Drafts[0].TryGetLines(out var line, out _));
        Assert.Equal(8, line);
    });

    [Fact]
    public Task Switching_to_split_moves_commenting_to_the_new_side_editor() => Ui(async () =>
    {
        var files = DiffFixtures.Files(1);
        using var s = Scene.Open(files, DiffViewMode.Split);
        await s.AttachReviewAsync();
        Assert.Same(s.View.SecondaryEditor, s.View.ReviewEditor);
        var added = s.View.SecondaryEditor.Document.GetLineByNumber(8);
        s.View.SecondaryEditor.TextArea.Focus();
        s.View.SecondaryEditor.Select(added.Offset, added.Length);
        s.Settle();
        Assert.True(WorkbenchUi.Find<Button>(s.Window, "review-add").IsVisible);
        s.View.Mode = DiffViewMode.Inline;
        s.Settle();
        await s.IdleReviewAsync();
        Assert.Same(s.View.PrimaryEditor, s.View.ReviewEditor);
        Assert.True(s.View.ReviewController.CanCommentOn(8, 8));
    });

    private static Task Ui(Func<Task> body) => SharedUi.RunAsync(body);

    private static Task Ui(Action body) => Ui(() =>
    {
        body();
        return Task.CompletedTask;
    });

    private sealed class Scene : IDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("forge-diff-ui-").FullName;

        private Scene(Window window, DiffEditorView view)
        {
            Window = window;
            View = view;
        }

        public Window Window { get; }

        public DiffEditorView View { get; }

        public static Scene Open(
            IReadOnlyList<IDiffFileItem> files,
            DiffViewMode mode = DiffViewMode.Inline,
            Func<IDiffFileItem, Control>? factory = null,
            double width = 900,
            double height = 600)
        {
            var view = new DiffEditorView { Mode = mode, MarkdownPreviewFactory = factory, Files = files };
            var window = new Window { Width = width, Height = height, Content = view };
            window.Show();
            var scene = new Scene(window, view);
            scene.Settle();
            return scene;
        }

        public void Settle()
        {
            WorkbenchUi.Settle(Window);
            View.SettleNow();
            WorkbenchUi.Settle(Window);
            View.SettleNow();
        }

        /// <summary>The row map, the document and the overlay controls agree with each other and with the items.</summary>
        public void AssertConsistent(List<FakeFile> files)
        {
            var map = View.PrimaryMap;
            var textView = View.PrimaryEditor.TextArea.TextView;
            Assert.Equal(map.Count, View.PrimaryEditor.Document.LineCount);
            for (var i = 0; i < files.Count; i++)
            {
                var header = map.HeaderLine(i);
                Assert.Equal(DiffRowKind.FileHeader, map.ForLine(header).Kind);
                Assert.Equal(i, map.ForLine(header).File);
                Assert.Equal(i, map.ForLine(map.FileLastLine(i)).File);
                if (i > 0)
                {
                    Assert.Equal(map.FileLastLine(i - 1) + 1, header);
                }
            }

            Assert.Equal(map.Count, map.FileLastLine(files.Count - 1));
            var scroll = textView.ScrollOffset.Y;
            foreach (var control in View.ActiveOverlayControls)
            {
                int line;
                if (control is DiffFileHeader h)
                {
                    line = map.HeaderLine(files.IndexOf((FakeFile)h.DataContext!));
                }
                else if (control is DiffBlockBar bar)
                {
                    var block = (IDiffBlockItem)bar.DataContext!;
                    var file = files.FindIndex(f => f.Blocks.Contains(block));
                    var index = files[file].Blocks.ToList().IndexOf(block);
                    line = Enumerable.Range(map.HeaderLine(file), map.FileLastLine(file) - map.HeaderLine(file) + 1)
                        .First(n => map.ForLine(n).Kind == DiffRowKind.BlockBar && map.ForLine(n).Block == index);
                }
                else
                {
                    continue;
                }

                Assert.Equal(textView.GetVisualTopByDocumentLine(line) - scroll, TopOf(control), 1.5);
            }
        }

        public IReadOnlyList<Button> Markers() =>
            [.. Window.GetVisualDescendants().OfType<Button>().Where(static b => b.Classes.Contains("reviewMarker") && b.IsVisible)];

        public DiffFileHeader Header(IDiffFileItem item) =>
            View.ActiveOverlayControls.OfType<DiffFileHeader>().SingleOrDefault(header => ReferenceEquals(header.DataContext, item))
            ?? throw new InvalidOperationException(
                $"No header for {item.Path}; active=[{string.Join(", ", View.ActiveOverlayControls.OfType<DiffFileHeader>().Select(h => (h.DataContext as IDiffFileItem)?.Path))}] "
                + $"scroll={View.PrimaryEditor.TextArea.TextView.ScrollOffset.Y} valid={View.PrimaryEditor.TextArea.TextView.VisualLinesValid} "
                + $"docHeight={View.PrimaryEditor.TextArea.TextView.DocumentHeight}");

        /// <summary>Top edge of <paramref name="control"/> relative to the top of the diff view.</summary>
        public double TopOf(Control control) => control.TranslatePoint(default, View.PrimaryEditor.TextArea.TextView)!.Value.Y;

        public async Task AttachReviewAsync()
        {
            var store = new ReviewStore(Path.Combine(root, ".review-data"));
            View.Review = new DiffReviewContext(
                new ReviewServices(store, static (_, _, _) => true, static () => { }), new ReviewOwner(root, "A"), "base");
            await IdleReviewAsync();
            Settle();
        }

        public async Task IdleReviewAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            do
            {
                await Task.Delay(10);
                Assert.True(DateTime.UtcNow < deadline, "The diff review controller never became idle.");
            }
            while (View.ReviewController.IsBusy);
        }

        public void Dispose()
        {
            Window.Close();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
