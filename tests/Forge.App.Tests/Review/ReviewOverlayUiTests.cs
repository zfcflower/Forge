using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Forge.App.Review;
using Forge.App.Tests.Workbench;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests.Review;

[Collection("forge-app-ui")]
public sealed class ReviewOverlayUiTests
{
    private const string Four = "one\ntwo\nthree\nfour\n";

    [Fact]
    public Task Clicking_a_line_number_shows_the_icon_beside_that_line_and_types_nothing() => Ui(async () =>
    {
        await using var s = await Scene.OpenAsync(Four);
        var view = s.Editor.TextArea.TextView;
        var margin = s.Editor.TextArea.LeftMargins.OfType<LineNumberMargin>().Single();
        var lineTop = view.GetVisualTopByDocumentLine(2) - view.ScrollOffset.Y;
        var y = view.TranslatePoint(new Point(0, lineTop + view.DefaultLineHeight / 2), s.Window)!.Value.Y;
        var x = margin.TranslatePoint(new Point(margin.Bounds.Width / 2, 0), s.Window)!.Value.X;
        var log = new List<string>();
        s.Editor.TextArea.AddHandler(InputElement.PointerPressedEvent,
            (object? _, PointerPressedEventArgs e) =>
                log.Add($"down src={e.Source?.GetType().Name} handled={e.Handled} capture={e.Pointer.Captured?.GetType().Name}"),
            Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        s.Editor.TextArea.AddHandler(InputElement.PointerReleasedEvent,
            (object? _, PointerReleasedEventArgs e) =>
                log.Add($"up src={e.Source?.GetType().Name} initial={e.InitialPressMouseButton} handled={e.Handled}"),
            Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        var hit = s.Window.InputHitTest(new Point(x, y))?.GetType().Name;
        WorkbenchUi.Click(s.Window, new Point(x, y));
        WorkbenchUi.Settle(s.Window);
        var icon = Find<Button>(s.Window, "review-add");
        var area = s.Editor.TextArea;
        Assert.True(icon.IsVisible,
            $"hit={hit} click=({x},{y}) margin={Bounds(s.Window, margin)} sel={area.Selection.IsEmpty}/{area.Selection.SurroundingSegment?.Offset} caretLine={area.Caret.Line} events=[{string.Join("; ", log)}]");
        // The icon hangs off the bottom right of the clicked row: below that line, just right of the text start.
        var lineBottom = view.TranslatePoint(new Point(0, lineTop + view.DefaultLineHeight), s.Window)!.Value.Y;
        var textLeft = view.TranslatePoint(new Point(0, lineTop), s.Window)!.Value.X;
        Assert.InRange(Bounds(s.Window, icon).Top, lineBottom - 1.5, lineBottom + 10);
        Assert.InRange(Bounds(s.Window, icon).Left, textLeft + 1, textLeft + 14);
        Assert.Equal(Four, s.Editor.Document.Text);
        Assert.False(s.Model.IsDirty);
    });

    [Fact]
    public Task A_keyboard_selection_over_two_lines_places_the_icon_at_the_last_line_without_changing_text() => Ui(async () =>
    {
        await using var s = await Scene.OpenAsync(Four);
        s.Editor.TextArea.Focus();
        s.Editor.Select(4, 9);
        WorkbenchUi.Settle(s.Window);
        var icon = Find<Button>(s.Window, "review-add");
        Assert.True(icon.IsVisible);
        Assert.Equal("two\nthree", s.Editor.SelectedText);
        var view = s.Editor.TextArea.TextView;
        var lineTop = view.GetVisualTopByDocumentLine(3) - view.ScrollOffset.Y;
        var lineBottom = view.TranslatePoint(new Point(0, lineTop + view.DefaultLineHeight), s.Window)!.Value.Y;
        var end = view.GetVisualPosition(s.Editor.TextArea.Selection.EndPosition, VisualYPosition.LineBottom);
        var endX = view.TranslatePoint(new Point(end.X, 0), s.Window)!.Value.X;
        // Right of the selection's moving end and below its last line, not glued to the viewport edge.
        Assert.InRange(Bounds(s.Window, icon).Top, lineBottom - 1.5, lineBottom + 10);
        Assert.InRange(Bounds(s.Window, icon).Left, endX - 1, endX + 14);
        Assert.Equal(Four, s.Editor.Document.Text);
    });

    [Fact]
    public Task An_anchor_beyond_the_right_edge_keeps_the_icon_inside_the_editor() => Ui(async () =>
    {
        var longLine = new string('x', 300) + ((char)10);
        await using var s = await Scene.OpenAsync(longLine);
        s.Editor.TextArea.Focus();
        s.Editor.Select(0, 300);
        WorkbenchUi.Settle(s.Window);
        var icon = Find<Button>(s.Window, "review-add");
        Assert.True(icon.IsVisible);

        var editor = Bounds(s.Window, s.Editor);
        var iconBounds = Bounds(s.Window, icon);
        Assert.InRange(iconBounds.Right, editor.Left, editor.Right);
        Assert.InRange(iconBounds.Bottom, editor.Top, editor.Bottom);
    });

    [Fact]
    public Task The_composer_keeps_its_own_typing_and_copy_and_escape_discards_and_returns_focus_to_the_editor() => Ui(async () =>
    {
        await using var s = await Scene.OpenAsync(Four);
        await s.OpenComposerOnLineAsync(2);
        var body = Find<TextBox>(s.Window, "review-body");
        Assert.True(body.IsFocused);
        s.Window.KeyTextInput("note");
        WorkbenchUi.Settle(s.Window);
        Assert.Equal("note", body.Text);
        Assert.Equal(Four, s.Editor.Document.Text);
        s.Window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        s.Window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
        WorkbenchUi.Settle(s.Window);
        Assert.Equal("note", await s.Window.Clipboard!.TryGetTextAsync());
        s.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        WorkbenchUi.Settle(s.Window);
        Assert.False(Find<Border>(s.Window, "review-composer").IsVisible);
        Assert.True(s.Editor.TextArea.IsFocused);
        Assert.Empty(s.Model.ReviewController.Drafts);
        Assert.Empty(await s.Store.GetDraftsAsync(s.Root, "A", "a.cs"));
        Assert.Equal(Four, s.Editor.Document.Text);
    });

    [Fact]
    public Task Save_and_send_buttons_persist_then_queue_the_current_file_draft_and_the_marker_reopens_it() => Ui(async () =>
    {
        await using var s = await Scene.OpenAsync(Four);
        await s.OpenComposerOnLineAsync(2);
        s.Window.KeyTextInput("fix it");
        WorkbenchUi.Settle(s.Window);
        WorkbenchUi.Click(s.Window, Center(s.Window, Find<Button>(s.Window, "review-save")));
        await s.WaitUntil(() => s.Model.ReviewController.Drafts.Count == 1 && !s.Model.ReviewController.IsBusy);
        var stored = Assert.Single(await s.Store.GetDraftsAsync(s.Root, "A", "a.cs"));
        Assert.Equal(("fix it", 2, 2), (stored.Body, stored.NewLineStart, stored.NewLineEnd));
        Assert.False(Find<Border>(s.Window, "review-composer").IsVisible);
        var marker = Assert.Single(s.Markers());
        Assert.Equal("fix it", Assert.IsType<ReviewDraft>(marker.Tag).Body);
        WorkbenchUi.Click(s.Window, Center(s.Window, marker));
        await s.WaitUntil(() => Find<Border>(s.Window, "review-composer").IsVisible);
        Assert.Equal("fix it", Find<TextBox>(s.Window, "review-body").Text);
        Assert.True(Find<Button>(s.Window, "review-delete").IsVisible);
        WorkbenchUi.Click(s.Window, Center(s.Window, Find<Button>(s.Window, "review-send")));
        await s.WaitUntil(() => s.Sent.Count == 1 && s.Model.ReviewController.Drafts.Count == 0 && !s.Model.ReviewController.IsBusy);
        Assert.Equal("A", s.Sent[0].Session);
        Assert.Contains("fix it", s.Sent[0].Text, StringComparison.Ordinal);
        Assert.NotNull((await s.Store.GetAsync(stored.Id))!.SentAt);
        Assert.Empty(s.Markers());
    });

    [Fact]
    public Task Several_drafts_on_one_line_get_distinct_non_overlapping_markers_inside_the_viewport() => Ui(async () =>
    {
        await using var s = await Scene.OpenAsync(Four, seed: async scene =>
        {
            await scene.SeedAsync("A", 2, 2, "first");
            await scene.SeedAsync("A", 2, 2, "second");
            await scene.SeedAsync("A", 3, 3, "third");
        });
        var markers = s.Markers();
        Assert.Equal(3, markers.Count);
        var layer = Bounds(s.Window, s.View.FindControl<Canvas>("ReviewLayer")!);
        var rects = markers.Select(m => Bounds(s.Window, m)).ToList();
        Assert.All(rects, r => Assert.True(layer.Contains(r.TopLeft) && layer.Contains(r.BottomRight - new Vector(0.5, 0.5)), r.ToString()));
        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                Assert.False(rects[i].Intersects(rects[j]), $"markers {i} and {j} overlap: {rects[i]} {rects[j]}");
            }
        }
    });

    [Fact]
    public Task Markers_follow_scrolling_and_the_composer_stays_inside_a_narrow_window() => Ui(async () =>
    {
        var text = string.Join("\n", Enumerable.Range(1, 80).Select(static n => "line " + n)) + "\n";
        await using var s = await Scene.OpenAsync(text, width: 420, height: 300, seed: async scene =>
            await scene.SeedAsync("A", 70, 70, "deep"));
        Assert.Empty(s.Markers());
        s.Editor.ScrollToLine(70);
        WorkbenchUi.Settle(s.Window);
        var layer = Bounds(s.Window, s.View.FindControl<Canvas>("ReviewLayer")!);
        var marker = Assert.Single(s.Markers());
        var view = s.Editor.TextArea.TextView;
        var lineTop = view.TranslatePoint(new Point(0, view.GetVisualTopByDocumentLine(70) - view.ScrollOffset.Y), s.Window)!.Value.Y;
        Assert.InRange(Bounds(s.Window, marker).Top, lineTop - 0.5, lineTop + 3);
        Assert.True(layer.Contains(Bounds(s.Window, marker).TopLeft));
        WorkbenchUi.Click(s.Window, Center(s.Window, marker));
        await s.WaitUntil(() => Find<Border>(s.Window, "review-composer").IsVisible);
        var composer = Bounds(s.Window, Find<Border>(s.Window, "review-composer"));
        Assert.True(composer.Left >= layer.Left - 0.5 && composer.Right <= layer.Right + 0.5, $"{composer} vs {layer}");
        Assert.True(composer.Top >= layer.Top - 0.5 && composer.Bottom <= layer.Bottom + 0.5, $"{composer} vs {layer}");
        foreach (var id in new[] { "review-delete", "review-cancel", "review-save", "review-send" })
        {
            var button = Find<Button>(s.Window, id);
            Assert.True(button.IsVisible, id);
            var rect = Bounds(s.Window, button);
            Assert.True(rect.Right <= composer.Right + 0.5 && rect.Left >= composer.Left - 0.5 && rect.Bottom <= composer.Bottom + 0.5, $"{id} {rect} vs {composer}");
        }
    });

    private static T Find<T>(Window window, string id) where T : Visual => WorkbenchUi.Find<T>(window, id);
    private static Rect Bounds(Window window, Visual visual) => new(visual.TranslatePoint(default, window)!.Value, visual.Bounds.Size);
    private static Point Center(Window window, Visual visual) =>
        visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), window)!.Value;

    private static async Task Ui(Func<Task> body)
    {
        var session = SharedUi.Session;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await session.Dispatch(async () => { await body(); return 0; }, budget.Token);
    }

    private sealed class Scene : IAsyncDisposable
    {
        private Scene(string root, string filePath, FileDocumentViewModel model)
        {
            Root = root;
            FilePath = filePath;
            Model = model;
            Store = new ReviewStore(Path.Combine(root, ".review-data"));
        }
        public string Root { get; }
        public string FilePath { get; }
        public FileDocumentViewModel Model { get; }
        public ReviewStore Store { get; }
        public List<(string Session, string Text)> Sent { get; } = [];
        public Window Window { get; private set; } = null!;
        public FileDocumentView View { get; private set; } = null!;
        public TextEditor Editor { get; private set; } = null!;

        public static async Task<Scene> OpenAsync(
            string text, double width = 900, double height = 600, Func<Scene, Task>? seed = null)
        {
            var root = Directory.CreateTempSubdirectory("forge-review-ui-").FullName;
            var file = Path.Combine(root, "a.cs");
            await File.WriteAllTextAsync(file, text);
            var model = FileDocumentViewModel.Create();
            await model.LoadAsync(file, CancellationToken.None);
            Assert.False(model.HasError, model.Error);
            var scene = new Scene(root, file, model);
            if (seed is not null)
            {
                await seed(scene);
            }
            model.ConfigureReview(
                new ReviewServices(scene.Store, (session, message, _) =>
                {
                    scene.Sent.Add((session, message));
                    return true;
                }, static () => { }),
                new ReviewOwner(root, "A"));
            scene.View = new FileDocumentView { DataContext = model };
            scene.Window = new Window { Width = width, Height = height, Content = scene.View };
            scene.Window.Show();
            WorkbenchUi.Settle(scene.Window);
            scene.Editor = scene.View.GetVisualDescendants().OfType<TextEditor>().Single();
            await scene.WaitUntil(() => !model.ReviewController.IsBusy);
            return scene;
        }

        public async Task SeedAsync(string session, int start, int end, string body)
        {
            var lines = ReviewTextAnchors.RangeLines(Model.EditorMirror!.Document, start, end);
            await Store.AddAsync(new NewReviewComment
            {
                WorkspacePath = Root, SessionId = session, FilePath = "a.cs",
                NewLineStart = start, NewLineEnd = end, Body = body,
                Anchor = ReviewTextAnchors.Create("a.cs", lines, start),
            });
        }
        public List<Button> Markers() =>
            [.. View.FindControl<Canvas>("ReviewLayer")!.Children.OfType<Button>()
                .Where(static b => b.Classes.Contains("reviewMarker") && b.IsVisible)];

        public async Task OpenComposerOnLineAsync(int line)
        {
            var offset = Editor.Document.GetLineByNumber(line).Offset;
            Editor.TextArea.Focus();
            Editor.Select(offset, Editor.Document.GetLineByNumber(line).Length);
            WorkbenchUi.Settle(Window);
            WorkbenchUi.Click(Window, Center(Window, Find<Button>(Window, "review-add")));
            await WaitUntil(() => Find<Border>(Window, "review-composer").IsVisible);
            WorkbenchUi.Settle(Window);
        }
        public async Task WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
                await Task.Delay(10);
                WorkbenchUi.Settle(Window);
            }
        }
        public async ValueTask DisposeAsync()
        {
            Window.Close();
            Model.DisposeReview();
            await Model.ReleaseDocumentAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}
