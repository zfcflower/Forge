using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.App.Workbench.Controls;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Tree visual revision: outlined folder/file icons with an open-folder variant, per-ancestor guide lines that follow
/// expansion, a hover surface whose left edge tracks the indent, and agent rows whose chevron follows the title and whose
/// kind icon is coloured (and, when running, breathing) by state.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TreeVisualTests : IDisposable
{
    private readonly List<string> roots = [];

    public void Dispose()
    {
        foreach (var root in roots)
        {
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

    [Fact]
    public async Task Folder_expand_swaps_to_open_folder_geometry()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var root = NewRoot();
        Write(root, "a/x.txt");

        var icons = await session.Dispatch(
            async () =>
            {
                var panel = NewFilesPanel();
                panel.Attach(new PanelContext("s1", root, null));
                await panel.RefreshAsync(CancellationToken.None);

                var folder = panel.Rows.Single(static row => row.RelativePath == "a");
                var closed = folder.Icon;
                folder.IsExpanded = true;
                var open = folder.Icon;
                var file = panel.Rows.Single(static row => row.RelativePath == "a/x.txt").Icon;
                return (Closed: ReferenceEquals(TreeIcons.Folder, closed),
                    Open: ReferenceEquals(TreeIcons.FolderOpen, open),
                    File: ReferenceEquals(TreeIcons.File, file));
            },
            CancellationToken.None);

        Assert.True(icons.Closed);
        Assert.True(icons.Open);
        Assert.True(icons.File);
    }

    [Fact]
    public async Task Guide_levels_track_expanded_ancestors_and_collapse_removes_the_level()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var root = NewRoot();
        Write(root, "a/b/c.txt");
        Write(root, "a/sibling.txt");
        Write(root, "top.txt");

        var result = await session.Dispatch(
            async () =>
            {
                var panel = NewFilesPanel();
                panel.Attach(new PanelContext("s1", root, null));
                await panel.RefreshAsync(CancellationToken.None);

                var a = panel.Rows.Single(static row => row.RelativePath == "a");
                a.IsExpanded = true;
                var b = panel.Rows.Single(static row => row.RelativePath == "a/b");
                b.IsExpanded = true;

                var deep = panel.Rows.Single(static row => row.RelativePath == "a/b/c.txt");
                var sibling = panel.Rows.Single(static row => row.RelativePath == "a/sibling.txt");
                var top = panel.Rows.Single(static row => row.RelativePath == "top.txt");

                var before = (Top: Levels(top), Sibling: Levels(sibling), Deep: Levels(deep));

                b.IsExpanded = false;

                return (Before: before, AfterDeep: Levels(deep), AfterB: Levels(b), RowCount: panel.Rows.Count);
            },
            CancellationToken.None);

        Assert.Empty(result.Before.Top);
        Assert.Equal([0], result.Before.Sibling);
        Assert.Equal([0, 1], result.Before.Deep);
        Assert.Equal([0], result.AfterDeep);
        Assert.Equal([0], result.AfterB);
        Assert.Equal(4, result.RowCount);
    }

    [Fact]
    public async Task Hover_surface_left_edge_moves_right_with_indent()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var root = NewRoot();
        Write(root, "d1/d2/deep.txt");
        Write(root, "top.txt");

        var edges = await session.Dispatch(
            async () =>
            {
                var panel = NewFilesPanel();
                panel.Attach(new PanelContext("s1", root, null));
                await panel.RefreshAsync(CancellationToken.None);

                panel.Rows.Single(static row => row.RelativePath == "d1").IsExpanded = true;
                panel.Rows.Single(static row => row.RelativePath == "d1/d2").IsExpanded = true;

                var view = new FilesToolView { DataContext = panel };
                var window = new Window { Width = 420, Height = 320, Content = view };
                window.Show();
                Settle(window);

                var tree = FilesTree(view);
                var topItem = ItemWithTitle(tree, "top.txt")!;
                var deepItem = ItemWithTitle(tree, "deep.txt")!;

                return (Top: ContentBorder(topItem).Bounds.X, Deep: ContentBorder(deepItem).Bounds.X);
            },
            CancellationToken.None);

        Assert.Equal(2 * IndentMarks.Step, edges.Deep - edges.Top, 1);
    }

    [Fact]
    public async Task Child_chevron_slot_aligns_with_parent_icon_left()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var root = NewRoot();
        Write(root, "a/b/c.txt");

        var edges = await session.Dispatch(
            async () =>
            {
                var panel = NewFilesPanel();
                panel.Attach(new PanelContext("s1", root, null));
                await panel.RefreshAsync(CancellationToken.None);

                panel.Rows.Single(static row => row.RelativePath == "a").IsExpanded = true;

                var view = new FilesToolView { DataContext = panel };
                var window = new Window { Width = 420, Height = 320, Content = view };
                window.Show();
                Settle(window);

                var tree = FilesTree(view);
                var parent = ItemWithTitle(tree, "a")!;
                var child = ItemWithTitle(tree, "b")!;

                return (ParentIconLeft: AbsoluteLeft(IconOf(parent), window),
                    ChildChevronLeft: AbsoluteLeft(ChevronOf(child), window));
            },
            CancellationToken.None);

        // The 12 px chevron overhangs its 10 px slot by a pixel on each side (its stroke would vanish at a smaller
        // size, see ProjectView.axaml), so the glyph's left edge sits exactly one pixel left of the slot: the slot
        // itself is what lines up under the parent icon (IndentMarks.Step = ChevronSlot + IconGap).
        Assert.Equal(edges.ParentIconLeft, edges.ChildChevronLeft + 1, 0.5);
    }

    [Fact]
    public async Task Spec_tree_uses_file_tree_guides_and_icons()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var root = NewRoot();
        WriteSpec(root, "specs/00-root.md", "root", null, "Root", "draft");
        WriteSpec(root, "specs/01-app.md", "app", "root", "App", "draft");
        WriteSpec(root, "specs/02-views.md", "views", "app", "Views", "active");
        WriteSpec(root, "specs/02-viewmodels.md", "viewmodels", "app", "ViewModels", "active");

        var result = await session.Dispatch(
            async () =>
            {
                var panel = NewSpecPanel(root);
                panel.Attach(new PanelContext("s1", root, root));
                await panel.RefreshAsync(CancellationToken.None);

                var rootRow = panel.Rows.Single(static row => row.Id == "root");
                var appRow = panel.Rows.Single(static row => row.Id == "app");
                var viewsRow = panel.Rows.Single(static row => row.Id == "views");

                var model = (RootLevels: Levels(rootRow),
                    AppLevels: Levels(appRow),
                    ViewsLevels: Levels(viewsRow),
                    RootOpenFolder: ReferenceEquals(TreeIcons.FolderOpen, rootRow.Icon),
                    AppOpenFolder: ReferenceEquals(TreeIcons.FolderOpen, appRow.Icon),
                    ViewsFile: ReferenceEquals(TreeIcons.File, viewsRow.Icon));

                rootRow.IsExpanded = false;
                var afterCollapse = (AppLevels: Levels(appRow), ViewsLevels: Levels(viewsRow));
                rootRow.IsExpanded = true;

                var view = new SpecTreeToolView { DataContext = panel };
                var window = new Window { Width = 420, Height = 240, Content = view };
                window.Show();
                Settle(window);

                var tree = SpecTree(view);
                var rootItem = ItemWithTitle(tree, "Root")!;
                var appItem = ItemWithTitle(tree, "App")!;
                var viewsItem = ItemWithTitle(tree, "Views")!;

                return (Model: model,
                    AfterCollapse: afterCollapse,
                    RootGuides: GuideLines(rootItem).Count(),
                    AppGuides: GuideLines(appItem).Count(),
                    ViewsGuides: GuideLines(viewsItem).Count(),
                    RootIcon: ReferenceEquals(TreeIcons.FolderOpen, IconData(IconOf(rootItem))),
                    AppIcon: ReferenceEquals(TreeIcons.FolderOpen, IconData(IconOf(appItem))),
                    ViewsIcon: ReferenceEquals(TreeIcons.File, IconData(IconOf(viewsItem))));
            },
            CancellationToken.None);

        Assert.Empty(result.Model.RootLevels);
        Assert.Equal([0], result.Model.AppLevels);
        Assert.Equal([0, 1], result.Model.ViewsLevels);
        Assert.True(result.Model.RootOpenFolder);
        Assert.True(result.Model.AppOpenFolder);
        Assert.True(result.Model.ViewsFile);

        Assert.Empty(result.AfterCollapse.AppLevels);
        Assert.Equal([1], result.AfterCollapse.ViewsLevels);

        Assert.Equal(0, result.RootGuides);
        Assert.Equal(1, result.AppGuides);
        Assert.Equal(2, result.ViewsGuides);
        Assert.True(result.RootIcon);
        Assert.True(result.AppIcon);
        Assert.True(result.ViewsIcon);
    }

    [Fact]
    public async Task Agent_rows_put_chevron_after_title_and_colour_and_animate_by_state()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var result = await session.Dispatch(
            () =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                var view = new ProjectView { DataContext = BuildAgentViewModel() };
                var window = new Window { Width = 440, Height = 240, Content = view };
                window.Show();
                Settle(window);

                var tree = ProjectTree(view);
                var mainItem = ItemWithTitle(tree, "Ran 3 subagents")!;
                var runningItem = ItemWithTitle(tree, "Research pricing")!;
                var completedItem = ItemWithTitle(tree, "Draft proposal")!;
                var failedItem = ItemWithTitle(tree, "Scrape reviews")!;

                var mainTitle = Visible<TextBlock>(mainItem)
                    .First(static block => block.Text == "Ran 3 subagents");
                var mainChevron = Visible<FluentIcon>(mainItem)
                    .First(static icon => icon.Classes.Contains("tree-chevron"));

                return (ChevronAfterTitle: mainChevron.Bounds.X >= mainTitle.Bounds.Right,
                    RunningAnimated: IconOf(runningItem).Classes.Contains("agent-running-animated"),
                    RunningRunning: IconOf(runningItem).Classes.Contains("agent-running"),
                    RunningColour: IconColour(IconOf(runningItem)),
                    CompletedAnimated: IconOf(completedItem).Classes.Contains("agent-running-animated"),
                    CompletedColour: IconColour(IconOf(completedItem)),
                    FailedColour: IconColour(IconOf(failedItem)),
                    ExpectedRunning: ColourOf("RunningBrush"),
                    ExpectedCompleted: ColourOf("SuccessBrush"),
                    ExpectedFailed: ColourOf("DangerBrush"));
            },
            CancellationToken.None);

        Assert.True(result.ChevronAfterTitle, "The agent chevron must follow the title text.");
        Assert.True(result.RunningRunning);
        Assert.True(result.RunningAnimated);
        Assert.Equal(result.ExpectedRunning, result.RunningColour);
        Assert.False(result.CompletedAnimated);
        Assert.Equal(result.ExpectedCompleted, result.CompletedColour);
        Assert.Equal(result.ExpectedFailed, result.FailedColour);
    }

    [Fact]
    public void Agent_state_classifier_maps_each_status_text()
    {
        Assert.Equal(AgentState.Running, AgentStateClassifier.Classify(SessionStrings.SubAgentRunning));
        Assert.Equal(AgentState.Queued, AgentStateClassifier.Classify(SessionStrings.SubAgentQueued));
        Assert.Equal(AgentState.Completed, AgentStateClassifier.Classify(SessionStrings.SubAgentCompleted));
        Assert.Equal(AgentState.Failed, AgentStateClassifier.Classify(SessionStrings.SubAgentFailed));
        Assert.Equal(AgentState.Stopped, AgentStateClassifier.Classify(SessionStrings.SubAgentStopped));
        Assert.Equal(AgentState.Ended, AgentStateClassifier.Classify(SessionStrings.SubAgentEnded));
        Assert.Equal(AgentState.Ended, AgentStateClassifier.Classify(string.Empty));
    }

    [Fact]
    public async Task Screenshot_trees_when_forge_tree_shots_is_set()
    {
        var output = Environment.GetEnvironmentVariable("FORGE_TREE_SHOTS");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        Directory.CreateDirectory(output);

        var filesRoot = NewRoot();
        Write(filesRoot, "Leads/Accounts/Acme Corp.md");
        Write(filesRoot, "Leads/Accounts/Globex.md");
        Write(filesRoot, "Leads/Inbox.md");
        Write(filesRoot, "Projects/site.md");
        Write(filesRoot, "Archive/old.md");
        Write(filesRoot, "README.md");

        var specRoot = NewRoot();
        WriteSpec(specRoot, "specs/00-root.md", "root", null, "Root", "draft");
        WriteSpec(specRoot, "specs/01-app.md", "app", "root", "App", "draft");
        WriteSpec(specRoot, "specs/01-core.md", "core", "root", "Core", "draft");
        WriteSpec(specRoot, "specs/01-workspace.md", "workspace", "root", "Workspace", "draft");
        WriteSpec(specRoot, "specs/02-views.md", "views", "app", "Views", "active");
        WriteSpec(specRoot, "specs/02-viewmodels.md", "viewmodels", "app", "ViewModels", "active");

        await using var session = HeadlessUnitTestSession.StartNew(typeof(TreeScreenshotAppBuilder));
        await session.Dispatch(
            async () =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Light;

                var files = NewFilesPanel();
                files.Attach(new PanelContext("s1", filesRoot, null));
                await files.RefreshAsync(CancellationToken.None);
                files.Rows.Single(static row => row.RelativePath == "Leads").IsExpanded = true;
                files.Rows.Single(static row => row.RelativePath == "Leads/Accounts").IsExpanded = true;

                var filesView = new FilesToolView { DataContext = files };
                var filesWindow = new Window { Width = 360, Height = 400, Content = filesView };
                filesWindow.Show();
                Settle(filesWindow);
                using (var frame = filesWindow.CaptureRenderedFrame())
                {
                    frame?.Save(System.IO.Path.Combine(output, "files-tree.png"), PngBitmapEncoderOptions.Default);
                }

                var specs = NewSpecPanel(specRoot);
                specs.Attach(new PanelContext("s1", specRoot, specRoot));
                await specs.RefreshAsync(CancellationToken.None);

                var specView = new SpecTreeToolView { DataContext = specs };
                var specWindow = new Window { Width = 360, Height = 260, Content = specView };
                specWindow.Show();
                Settle(specWindow);
                using (var frame = specWindow.CaptureRenderedFrame())
                {
                    frame?.Save(System.IO.Path.Combine(output, "spec-tree.png"), PngBitmapEncoderOptions.Default);
                }

                var agents = new ProjectView { DataContext = BuildAgentViewModel() };
                var agentsWindow = new Window { Width = 360, Height = 240, Content = agents };
                agentsWindow.Show();
                Settle(agentsWindow);
                using (var frame = agentsWindow.CaptureRenderedFrame())
                {
                    frame?.Save(System.IO.Path.Combine(output, "agent-tree.png"), PngBitmapEncoderOptions.Default);
                }

                return 0;
            },
            CancellationToken.None);
    }

    private static FilesPanelViewModel NewFilesPanel() => new(new GitCli(), _ => { }, _ => { }, _ => { });

    private static SpecTreeViewModel NewSpecPanel(string root) => new(_ => new SpecGraph(root), _ => { });

    private static ProjectTreeViewModel BuildAgentViewModel()
    {
        var main = AgentRow(TreeNodeKind.MainAgent, "Ran 3 subagents", string.Empty, 0, TreeIcons.MainAgent);
        var running = AgentRow(TreeNodeKind.SubAgent, "Research pricing", SessionStrings.SubAgentRunning, 1, TreeIcons.Scout);
        var completed = AgentRow(TreeNodeKind.SubAgent, "Draft proposal", SessionStrings.SubAgentCompleted, 1, TreeIcons.Worker);
        var failed = AgentRow(TreeNodeKind.SubAgent, "Scrape reviews", SessionStrings.SubAgentFailed, 1, TreeIcons.Reviewer);
        main.Children.Add(running);
        main.Children.Add(completed);
        main.Children.Add(failed);
        main.NotifyChildrenChanged();

        var viewModel = new ProjectTreeViewModel(
            projects: null!,
            catalog: null!,
            worktrees: null!,
            readHead: (_, _) => Task.FromResult(new GitHead(GitHeadKind.Branch, "main")),
            openSession: (_, _) => Task.CompletedTask,
            createConfirmed: _ => Task.FromResult(false),
            promptText: (_, _) => Task.FromResult<string?>(null),
            pickNewBranch: (_, _) => Task.FromResult<NewBranchChoice?>(null),
            reveal: _ => Task.CompletedTask,
            addProject: _ => Task.CompletedTask);
        viewModel.Rows.ReplaceAll([main, running, completed, failed]);
        return viewModel;
    }

    private static TreeNodeViewModel AgentRow(TreeNodeKind kind, string title, string statusText, int depth, Geometry icon)
    {
        var node = new TreeNodeViewModel(kind) { Key = "agent:" + title, IsExpanded = true };
        node.SetDepth(depth);
        node.Update(title, string.Empty, running: false);
        node.StatusText = statusText;
        node.Icon = icon;
        return node;
    }

    private static int[] Levels(FileNodeViewModel node) => node.Guides.Select(static mark => mark.Level).ToArray();

    private static int[] Levels(SpecNodeViewModel node) => node.Guides.Select(static mark => mark.Level).ToArray();

    private static ListBox FilesTree(Visual root) => root.GetVisualDescendants().OfType<ListBox>()
        .First(static list => AutomationProperties.GetAutomationId(list) == "files-tree");

    private static ListBox ProjectTree(Visual root) => root.GetVisualDescendants().OfType<ListBox>()
        .First(static list => AutomationProperties.GetAutomationId(list) == "project-tree");

    private static ListBox SpecTree(Visual root) => root.GetVisualDescendants().OfType<ListBox>()
        .First(static list => AutomationProperties.GetAutomationId(list) == "spec-tree");

    private static IEnumerable<T> Visible<T>(Visual root) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().Where(static visual => visual.IsEffectivelyVisible);

    private static ListBoxItem? ItemWithTitle(Visual root, string text) =>
        root.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(item => Visible<TextBlock>(item).Any(block => block.Text == text));

    private static Border ContentBorder(Visual item) =>
        Visible<Border>(item).First(static border => border.Classes.Contains("tree-row-content"));

    /// <summary>
    /// The row's kind icon. Every tree paints it with the built-in <see cref="FluentIcon"/>, which carries the geometry in
    /// its <see cref="FluentIcon.Data"/> and the colour it fills with in the inherited foreground.
    /// </summary>
    private static Control IconOf(Visual item) =>
        Visible<Control>(item).First(static control => control.Classes.Contains("tree-icon"));

    private static Geometry? IconData(Control icon) => icon is FluentIcon path
        ? path.Data
        : throw new InvalidOperationException($"the icon '{icon.GetType().Name}' carries no geometry.");

    private static Color IconColour(Control icon) => icon is FluentIcon path
        ? ((SolidColorBrush)path.Foreground!).Color
        : throw new InvalidOperationException($"the icon '{icon.GetType().Name}' carries no colour.");

    private static FluentIcon ChevronOf(Visual item) =>
        Visible<FluentIcon>(item).First(static icon => icon.Classes.Contains("tree-chevron"));

    private static IEnumerable<Border> GuideLines(Visual item) =>
        Visible<Border>(item).Where(static border => border.Width == 1);

    private static double AbsoluteLeft(Visual visual, Visual root) =>
        visual.TranslatePoint(new Point(0, 0), root)?.X ?? double.NaN;

    private static Color ColourOf(string key)
    {
        Application.Current!.TryGetResource(key, ThemeVariant.Light, out var value);
        return ((SolidColorBrush)value!).Color;
    }

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        roots.Add(root);
        return root;
    }

    private static void Write(string root, string relativePath)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
    }

    private static void WriteSpec(string root, string relativePath, string id, string? parent, string title, string status)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var parentLine = parent is null ? string.Empty : "parent: " + parent + "\n";
        File.WriteAllText(full,
            "---\nid: " + id + "\ntype: module-design\nstatus: " + status + "\ntitle: " + title + "\n" +
            parentLine + "---\n\n# " + title + "\n");
    }
}

/// <summary>Skia-backed headless host for screenshot tests: real rendering, no headless framebuffer drawing.</summary>
public static class TreeScreenshotAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
