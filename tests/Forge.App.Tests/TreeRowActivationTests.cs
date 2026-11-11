using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

/// <summary>A click anywhere on a project-tree row runs the row's action, not only a click on its icon.</summary>
[Collection("forge-app-ui")]
public sealed class TreeRowActivationTests
{
    [Fact]
    public Task A_session_row_opens_its_session() => RunAsync(() =>
    {
        var open = new CountingCommand();
        var node = new TreeNodeViewModel(TreeNodeKind.Session);
        node.SetCommands(open, null, null, null, null, null);

        Assert.True(node.Activate());
        Assert.Equal(1, open.Count);
    });

    [Fact]
    public Task A_row_with_children_toggles_instead_of_starting_a_session() => RunAsync(() =>
    {
        var create = new CountingCommand();
        var node = new TreeNodeViewModel(TreeNodeKind.Project) { IsExpanded = false };
        node.SetCommands(null, create, null, null, null, null);
        node.Children.Add(new TreeNodeViewModel(TreeNodeKind.Session));

        Assert.True(node.Activate());
        Assert.True(node.IsExpanded);
        Assert.Equal(0, create.Count);
    });

    [Fact]
    public Task A_childless_container_row_starts_a_session() => RunAsync(() =>
    {
        var create = new CountingCommand();
        var node = new TreeNodeViewModel(TreeNodeKind.Project);
        node.SetCommands(null, create, null, null, null, null);

        Assert.True(node.Activate());
        Assert.Equal(1, create.Count);
    });

    [Fact]
    public Task A_row_without_any_action_reports_false() => RunAsync(() =>
    {
        var node = new TreeNodeViewModel(TreeNodeKind.Session);

        Assert.False(node.Activate());
    });

    [Fact]
    public Task Clicking_the_title_text_of_a_row_runs_the_row_action() => RunAsync(() =>
    {
        var parent = new TreeNodeViewModel(TreeNodeKind.Project) { Key = "p", IsExpanded = true };
        parent.Update("Parent row", string.Empty, running: false);
        var child = new TreeNodeViewModel(TreeNodeKind.Session) { Key = "c" };
        child.SetDepth(1);
        child.Update("Child row", string.Empty, running: false);
        parent.Children.Add(child);
        parent.NotifyChildrenChanged();

        var tree = new ProjectTreeViewModel(
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
        tree.Rows.ReplaceAll([parent, child]);
        var view = new ProjectView { DataContext = tree };
        var window = new Window { Width = 400, Height = 300, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var title = window.GetVisualDescendants().OfType<TextBlock>()
            .First(static block => block.Text == "Parent row" && block.IsEffectivelyVisible);
        var point = title.TranslatePoint(new Point(title.Bounds.Width / 2, title.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(parent.IsExpanded);
    });

    [Fact]
    public Task A_truncated_conversation_title_shows_the_full_title_tooltip() => RunAsync(() =>
    {
        const string titleText = "A conversation title that is much longer than the available left panel width";
        var node = new TreeNodeViewModel(TreeNodeKind.Session);
        node.Update(titleText, string.Empty, running: false);
        var tree = new ProjectTreeViewModel(
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
        tree.Rows.ReplaceAll([node]);
        var view = new ProjectView { DataContext = tree };
        var window = new Window { Width = 260, Height = 220, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var title = window.GetVisualDescendants().OfType<TextBlock>()
            .First(static block => block.Text?.StartsWith("A conversation title", StringComparison.Ordinal) == true);

        Assert.True(TrimmedTextToolTip.IsTrimmed(title));
        Assert.Equal(titleText, ToolTip.GetTip(title));
    });

    private static async Task RunAsync(Action body)
    {
        var session = SharedUi.Session;
        await session.Dispatch(body, CancellationToken.None);
    }

    private sealed class CountingCommand : ICommand
    {
        public int Count { get; private set; }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Count++;
    }
}
