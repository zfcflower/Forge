using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Forge.App.Changes;
using Forge.App.Changes.Markdown;
using Forge.App.Localization;
using Forge.App.Views;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Changes;

[Collection("forge-app-ui")]
public sealed class MarkdownDiffViewTests
{
    // New side (blank lines are context): H1, intro, H2 A, a1, a2, [a3 changed], a4, a5, a6, H2 B, b1.
    private static readonly string[] Spec =
    [
        " # Title", " ", " intro", " ", " ## A", " ", " a1", " ", " a2", " ",
        "-a3 old", "+a3 **new**", " ", " a4", " ", " a5", " ", " a6", " ", " ## B", " ", " b1",
    ];

    private static FileDiff Full(FileDiffStatus status, params string[] spec)
    {
        var lines = new List<DiffLine>();
        int o = 0, n = 0;
        foreach (var raw in spec)
        {
            var text = raw[1..];
            switch (raw[0])
            {
                case '+': lines.Add(new DiffLine(DiffLineKind.Added, 0, ++n, text)); break;
                case '-': lines.Add(new DiffLine(DiffLineKind.Removed, ++o, 0, text)); break;
                default: lines.Add(new DiffLine(DiffLineKind.Context, ++o, ++n, text)); break;
            }
        }

        return new FileDiff("a.md", "a.md", status, false, [new DiffHunk(o == 0 ? 0 : 1, o, n == 0 ? 0 : 1, n, lines)]);
    }

    private static Task Ui(Func<Task> body) =>
        SharedUi.Session.Dispatch(async () => { await body(); return 0; }, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);

    private static List<Control> Children(MarkdownDiffView view) => ((StackPanel)view.Content!).Children.ToList();

    private static int Count(MarkdownDiffView view, string cls) =>
        Children(view).Count(c => c.Classes.Contains(cls));

    [Fact]
    public Task Changed_deleted_and_bar_are_grouped_in_order() => Ui(() =>
    {
        var file = new FakeFile(Full(FileDiffStatus.Modified, Spec));
        var view = new MarkdownDiffView(file);
        var children = Children(view);
        Assert.Single(children, c => c.Classes.Contains("md-changed"));
        Assert.Single(children, c => c.Classes.Contains("md-deleted"));
        var bars = children.OfType<DiffBlockBar>().ToList();
        Assert.Single(bars);
        Assert.Same(file.Blocks[0], bars[0].DataContext);

        var barAt = children.IndexOf(bars[0]);
        var deletedAt = children.FindIndex(c => c.Classes.Contains("md-deleted"));
        var changedAt = children.FindIndex(c => c.Classes.Contains("md-changed"));
        Assert.True(barAt < deletedAt && deletedAt < changedAt && changedAt == deletedAt + 1);
        // The removed text is rendered as markdown too, and the new text carries its bold run.
        Assert.Equal("a3 old", Text(children[deletedAt]));
        Assert.Equal("a3 new", Text(children[changedAt]));
        return Task.CompletedTask;
    });

    private static string Text(Control block) =>
        ((SelectableTextBlock)((Border)block).Child!).Inlines!.OfType<Avalonia.Controls.Documents.Run>().Aggregate("", (s, r) => s + r.Text)
        + string.Concat(((SelectableTextBlock)((Border)block).Child!).Inlines!.OfType<Avalonia.Controls.Documents.Span>().SelectMany(s => s.Inlines).OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));

    [Fact]
    public Task Unchanged_runs_fold_keeping_context_and_show_the_nearest_heading() => Ui(() =>
    {
        var view = new MarkdownDiffView(new FakeFile(Full(FileDiffStatus.Modified, Spec)));
        var folds = Children(view).OfType<Button>().Where(b => b.Classes.Contains("md-fold")).ToList();
        Assert.Equal(2, folds.Count);

        string Label(Button b, int column) =>
            ((TextBlock)((Grid)b.Content!).Children.First(c => Grid.GetColumn(c) == column)).Text!;

        // Before the change: Title, intro, A, a1 are hidden; a2 stays as context (no heading precedes the fold).
        Assert.Equal("\u2195 " + Loc.Instance.Format(MarkdownStrings.Folded, 4), Label(folds[0], 0));
        Assert.Single(((Grid)folds[0].Content!).Children);
        // After the change: a4 stays, then a5, a6, B, b1 are hidden under "A".
        Assert.Equal("\u2195 " + Loc.Instance.Format(MarkdownStrings.Folded, 4), Label(folds[1], 0));
        Assert.Equal("\u00A7 A", Label(folds[1], 1));

        var unchanged = Children(view).Count(c => c.Classes.Contains("md-block") && !c.Classes.Contains("md-changed") && !c.Classes.Contains("md-deleted"));
        Assert.Equal(2, unchanged); // a2 before, a4 after
        return Task.CompletedTask;
    });

    [Fact]
    public Task Clicking_a_fold_expands_it_and_raises_the_height_event() => Ui(() =>
    {
        var view = new MarkdownDiffView(new FakeFile(Full(FileDiffStatus.Modified, Spec)));
        var raised = 0;
        view.LayoutHeightChanged += (_, _) => raised++;
        var fold = Children(view).OfType<Button>().First(b => b.Classes.Contains("md-fold"));
        var before = Children(view).Count;

        fold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var after = Children(view);
        Assert.Equal(1, raised);
        Assert.Equal(before - 1 + 4, after.Count);
        Assert.Equal(1, after.OfType<Button>().Count(b => b.Classes.Contains("md-fold")));
        Assert.Equal("Title", ((SelectableTextBlock)((Border)after[0]).Child!).Text ?? string.Concat(((SelectableTextBlock)((Border)after[0]).Child!).Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)));
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_pure_deletion_places_the_removed_block_between_its_neighbours() => Ui(() =>
    {
        var diff = Full(FileDiffStatus.Modified, " first", " ", "-gone", "-", " second");
        var view = new MarkdownDiffView(new FakeFile(diff));
        var children = Children(view);
        var kinds = children.Select(c => c switch
        {
            DiffBlockBar => "bar",
            _ when c.Classes.Contains("md-deleted") => "del",
            _ when c.Classes.Contains("md-changed") => "chg",
            _ => "same",
        }).ToList();
        Assert.Equal(["same", "bar", "del", "same"], kinds);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_pure_insertion_has_a_bar_and_a_changed_block_but_no_deleted_block() => Ui(() =>
    {
        var view = new MarkdownDiffView(new FakeFile(Full(FileDiffStatus.Modified, " one", " ", "+two", "+", " three")));
        var children = Children(view);
        Assert.Single(children.OfType<DiffBlockBar>());
        Assert.Equal(0, Count(view, "md-deleted"));
        Assert.Equal(1, Count(view, "md-changed"));
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_deleted_file_renders_its_old_text_as_deleted_blocks() => Ui(() =>
    {
        var view = new MarkdownDiffView(new FakeFile(Full(FileDiffStatus.Deleted, "-# Gone", "-", "-body")));
        Assert.Equal(2, Count(view, "md-deleted"));
        Assert.Equal(0, Count(view, "md-changed"));
        Assert.Single(Children(view).OfType<DiffBlockBar>());
        return Task.CompletedTask;
    });

    [Fact]
    public Task An_added_file_is_entirely_changed_with_one_bar() => Ui(() =>
    {
        var view = new MarkdownDiffView(new FakeFile(Full(FileDiffStatus.Added, "+# New", "+", "+body", "+", "+- item")));
        Assert.Equal(3, Count(view, "md-changed"));
        Assert.Single(Children(view).OfType<DiffBlockBar>());
        Assert.Empty(Children(view).OfType<Button>());
        return Task.CompletedTask;
    });

    [Fact]
    public Task Partial_diffs_render_hunks_separated_by_a_gap_bar() => Ui(() =>
    {
        DiffHunk Hunk(int oldStart, int newStart, params DiffLine[] lines) => new(oldStart, lines.Length - 1, newStart, lines.Length, lines);
        var diff = new FileDiff("a.md", "a.md", FileDiffStatus.Modified, false,
        [
            Hunk(1, 1,
                new DiffLine(DiffLineKind.Context, 1, 1, "top"),
                new DiffLine(DiffLineKind.Added, 0, 2, "added one")),
            Hunk(20, 21,
                new DiffLine(DiffLineKind.Context, 20, 21, "mid"),
                new DiffLine(DiffLineKind.Added, 0, 22, "added two")),
        ]);
        var view = new MarkdownDiffView(new FakeFile(diff) { HasFullContextValue = false });
        var gaps = Children(view).OfType<Border>().Where(b => b.Classes.Contains("md-gap")).ToList();
        var gap = Assert.Single(gaps);
        Assert.Equal(Loc.Instance.Format(MarkdownStrings.Gap, 18), ((TextBlock)gap.Child!).Text);
        Assert.Equal(2, Children(view).OfType<DiffBlockBar>().Count());
        return Task.CompletedTask;
    });

    [Fact]
    public Task Raising_Diff_changed_while_attached_rebuilds_the_preview() => Ui(() =>
    {
        var file = new FakeFile(Full(FileDiffStatus.Modified, Spec));
        var view = new MarkdownDiffView(file);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var raised = 0;
        view.LayoutHeightChanged += (_, _) => raised++;
        Assert.Equal(1, Count(view, "md-changed"));

        file.Replace(Full(FileDiffStatus.Added, "+# New", "+", "+body"));

        Assert.Equal(2, Count(view, "md-changed"));
        Assert.Equal(0, Count(view, "md-deleted"));
        Assert.Equal(1, raised);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Links_are_only_launchable_for_http_and_https() => Ui(() =>
    {
        Assert.True(MarkdownRenderer.IsOpenable("https://x.dev"));
        Assert.True(MarkdownRenderer.IsOpenable("http://x.dev/a"));
        Assert.False(MarkdownRenderer.IsOpenable("file:///c:/x"));
        Assert.False(MarkdownRenderer.IsOpenable("javascript:alert(1)"));
        Assert.False(MarkdownRenderer.IsOpenable("mailto:a@b.c"));
        Assert.False(MarkdownRenderer.IsOpenable("relative/path.md"));
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_mermaid_fence_in_the_preview_renders_a_diagram_block() => Ui(() =>
    {
        // The new side of the file is one added Markdown fence; the preview renders it through the same MarkdownRenderer
        // the chat uses, so the diagram must arrive as a Mermaid block and not as a code block.
        var file = new FakeFile(Full(
            FileDiffStatus.Modified, " ", "+```mermaid", "+flowchart TD", "+  A-->B", "+```"));

        var view = new MarkdownDiffView(file);

        var mermaid = Assert.Single(
            Children(view).OfType<Border>().Select(static border => border.Child).OfType<MermaidBlockView>());
        Assert.Equal("flowchart TD\n  A-->B", mermaid.CopyText);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Every_block_kind_renders_without_throwing() => Ui(() =>
    {
        var blocks = MarkdownBlockParser.Parse(
            "---\nt: 1\n---\n# H\n\ntext [l](https://x.dev) `c`\n\n- a\n  - [x] b\n1. n\n\n> q\n\n```\ncode\n```\n\n| a | b |\n|---|--:|\n| 1 | 2 |\n\n***\n\n<div>x</div>".Split('\n'));
        var window = new Window { Width = 500, Height = 800, Content = MarkdownRenderer.RenderBlocks(blocks) };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.Bounds.Width > 0);
        window.Close();
        return Task.CompletedTask;
    });

    private sealed class FakeBlock(ChangeBlock block) : IDiffBlockItem
    {
        public ChangeBlock Block { get; } = block;

        public bool IsKept => false;

        public ICommand KeepCommand { get; } = new Noop();

        public ICommand RevertCommand { get; } = new Noop();

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    private sealed class Noop : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }

    private sealed class FakeFile : IDiffFileItem
    {
        private FileDiff diff;
        private IReadOnlyList<IDiffBlockItem> blocks;

        public FakeFile(FileDiff diff)
        {
            this.diff = diff;
            blocks = Make(diff);
        }

        public bool HasFullContextValue { get; init; } = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public FileDiff Diff => diff;

        public string Path => diff.Path;

        public string Badge => "M";

        public int Added => diff.AddedLineCount;

        public int Removed => diff.RemovedLineCount;

        public IReadOnlyList<IDiffBlockItem> Blocks => blocks;

        public int KeptCount => 0;

        public string KeptSummary => "";

        public bool IsViewed { get; set; }

        public bool IsCollapsed { get; set; }

        public bool IsMarkdown => true;

        public bool IsMarkdownPreview { get; set; } = true;

        public bool HasFullContext => HasFullContextValue;

        public bool CanLoadContext => false;

        public Task<bool> LoadFullContextAsync() => Task.FromResult(HasFullContextValue);

        public bool CanRevert => true;

        public ICommand RevertFileCommand { get; } = new Noop();

        public ICommand OpenInEditorCommand { get; } = new Noop();

        public ICommand CopyPathCommand { get; } = new Noop();

        public ICommand ToggleCollapsedCommand { get; } = new Noop();

        public void Replace(FileDiff next)
        {
            diff = next;
            blocks = Make(next);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Diff)));
        }

        private static IReadOnlyList<IDiffBlockItem> Make(FileDiff d) => DiffBlocks.Split(d).Select(b => (IDiffBlockItem)new FakeBlock(b)).ToList();
    }
}
