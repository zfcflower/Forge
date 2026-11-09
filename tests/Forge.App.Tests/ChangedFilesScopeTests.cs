using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Tests.Git;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

[Collection("forge-app-ui")]
public sealed class ChangedFilesScopeTests
{
    private const string SessionId = "changed-files-session";

    [Fact]
    public async Task All_uncommitted_and_turn_scopes_show_only_their_own_files()
    {
        using var workspace = await CreateWorkspaceAsync();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var result = await session.Dispatch(
            async () =>
            {
                var editorOpens = new List<string>();
                using var changes = new ChangesViewModel(
                    workspace.Git,
                    workspace.Checkpoints,
                    editorOpens.Add);
                changes.Attach(new PanelContext(SessionId, workspace.WorkingDirectory, null));
                await changes.LoadAsync(CancellationToken.None);

                var localBranches = changes.LocalBranches.Select(static branch => branch.Name).ToArray();
                var remoteBranches = changes.RemoteBranches.Select(static branch => branch.Name).ToArray();
                var defaultBase = changes.SelectedBranch?.RefName;

                changes.UncommittedCommand.Execute(null);
                await changes.LoadAsync(CancellationToken.None);
                var uncommitted = changes.Files.Select(static file => file.Path).ToArray();

                changes.AllChangesCommand.Execute(null);
                await changes.LoadAsync(CancellationToken.None);
                var all = changes.Files.Select(static file => file.Path).ToArray();
                var selectedBranch = changes.SelectedBranch?.Name;

                Assert.True(changes.NextFileCommand.CanExecute(null));
                var first = changes.SelectedFile!.Path;
                changes.NextFileCommand.Execute(null);
                var second = changes.SelectedFile!.Path;
                changes.PreviousFileCommand.Execute(null);
                changes.OpenInEditorCommand.Execute(null);

                var selectedTurn = Assert.Single(changes.Turns);
                selectedTurn.SelectCommand.Execute(null);
                await changes.LoadAsync(CancellationToken.None);
                var turn = changes.Files.Select(static file => file.Path).ToArray();
                var turnSelected = selectedTurn.IsSelected;

                return (
                    Uncommitted: uncommitted,
                    All: all,
                    Turn: turn,
                    LocalBranches: localBranches,
                    RemoteBranches: remoteBranches,
                    DefaultBase: defaultBase,
                    SelectedBranch: selectedBranch,
                    TurnSelected: turnSelected,
                    FirstFile: first,
                    SecondFile: second,
                    EditorPath: editorOpens.Single());
            },
            CancellationToken.None);

        Assert.Contains("file.txt", result.Uncommitted);
        Assert.Contains("uncommitted.txt", result.Uncommitted);
        Assert.DoesNotContain("turn.txt", result.Uncommitted);
        Assert.DoesNotContain("branch-only.txt", result.Uncommitted);

        Assert.Contains("turn.txt", result.All);
        Assert.Contains("branch-only.txt", result.All);
        Assert.DoesNotContain("file.txt", result.All);
        Assert.DoesNotContain("uncommitted.txt", result.All);
        Assert.Equal(["turn.txt"], result.Turn);

        Assert.Contains("main", result.LocalBranches);
        Assert.Contains("origin/main", result.RemoteBranches);
        Assert.NotNull(result.DefaultBase);
        Assert.NotEqual("refs/heads/feature/session", result.DefaultBase);
        Assert.Contains(result.SelectedBranch, new[] { "main", "origin/main" });
        Assert.True(result.TurnSelected);
        Assert.NotEqual(result.FirstFile, result.SecondFile);
        Assert.Equal(result.FirstFile, result.EditorPath);
    }

    [Fact]
    public async Task Watcher_refresh_updates_only_uncommitted_scope_and_reuses_an_unchanged_fingerprint()
    {
        using var workspace = await CreateWorkspaceAsync();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        ChangesViewModel? changes = null;
        try
        {
            await session.Dispatch(
                async () =>
                {
                    changes = new ChangesViewModel(workspace.Git, workspace.Checkpoints);
                    changes.Attach(new PanelContext(SessionId, workspace.WorkingDirectory, null));
                    await changes.LoadAsync(CancellationToken.None);
                    return 0;
                },
                CancellationToken.None);

            Write(workspace.WorkingDirectory, "later.txt", "another uncommitted file\n");
            var refreshed = await session.Dispatch(
                async () =>
                {
                    await changes!.RefreshFromFileSystemChangeAsync(CancellationToken.None);
                    return changes.Files.Select(static file => file.Path).ToArray();
                },
                CancellationToken.None);
            Assert.Contains("later.txt", refreshed);

            var stableFile = await session.Dispatch(() => changes!.Files[0], CancellationToken.None);
            await session.Dispatch(
                async () =>
                {
                    await changes!.RefreshFromFileSystemChangeAsync(CancellationToken.None);
                    return 0;
                },
                CancellationToken.None);
            var reused = await session.Dispatch(() => ReferenceEquals(stableFile, changes!.Files[0]), CancellationToken.None);
            Assert.True(reused, "an unchanged file fingerprint must not rebuild the diff rows");

            await session.Dispatch(
                async () =>
                {
                    changes!.AllChangesCommand.Execute(null);
                    await changes.LoadAsync(CancellationToken.None);
                    return 0;
                },
                CancellationToken.None);
            var committedFile = await session.Dispatch(() => changes!.Files[0], CancellationToken.None);
            Write(workspace.WorkingDirectory, "watcher-noise.txt", "working tree only\n");
            await session.Dispatch(
                async () =>
                {
                    await changes!.RefreshFromFileSystemChangeAsync(CancellationToken.None);
                    return 0;
                },
                CancellationToken.None);
            var ignored = await session.Dispatch(() => ReferenceEquals(committedFile, changes!.Files[0]), CancellationToken.None);
            Assert.True(ignored, "filesystem events must not rebuild a committed-branch diff");
        }
        finally
        {
            if (changes is not null)
            {
                await session.Dispatch(
                    () =>
                    {
                        changes.Dispose();
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task The_latest_scope_wins_when_range_refreshes_overlap()
    {
        using var workspace = await CreateWorkspaceAsync();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var state = await session.Dispatch(
            async () =>
            {
                using var changes = new ChangesViewModel(workspace.Git, workspace.Checkpoints);
                changes.Attach(new PanelContext(SessionId, workspace.WorkingDirectory, null));
                await changes.LoadAsync(CancellationToken.None);

                changes.AllChangesCommand.Execute(null);
                changes.UncommittedCommand.Execute(null);
                await changes.LoadAsync(CancellationToken.None);
                await Task.Delay(20);
                return (changes.IsUncommitted, Paths: changes.Files.Select(static file => file.Path).ToArray());
            },
            CancellationToken.None);

        Assert.True(state.IsUncommitted);
        Assert.Contains("uncommitted.txt", state.Paths);
        Assert.DoesNotContain("branch-only.txt", state.Paths);
    }

    [Fact]
    public async Task All_scope_reports_unrelated_history_detached_head_and_missing_comparison_branch()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("history");
        GitTestShell.RunChecked(work, "switch", "-c", "feature/current");
        sandbox.CommitFile(work, "feature.txt", "feature\n", "feature commit");
        GitTestShell.RunChecked(work, "switch", "--orphan", "unrelated");
        sandbox.CommitFile(work, "unrelated.txt", "unrelated\n", "unrelated root");
        GitTestShell.RunChecked(work, "switch", "feature/current");

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var messages = await session.Dispatch(
            async () =>
            {
                using var changes = new ChangesViewModel(new GitCli(), new MemoryCheckpointStore([]));
                changes.Attach(new PanelContext(SessionId, work, null));
                await changes.LoadAsync(CancellationToken.None);

                changes.SelectedBranch = Assert.Single(changes.LocalBranches, static branch => branch.Name == "unrelated");
                changes.AllChangesCommand.Execute(null);
                await changes.LoadAsync(CancellationToken.None);
                var unrelated = changes.Message;

                GitTestShell.RunChecked(work, "checkout", "--detach", "HEAD");
                await changes.LoadAsync(CancellationToken.None);
                return (Unrelated: unrelated, Detached: changes.Message);
            },
            CancellationToken.None);

        Assert.Equal(Loc.Instance[InspectorStrings.ChangesNoCommonHistory], messages.Unrelated);
        Assert.Equal(Loc.Instance[InspectorStrings.ChangesNoCurrentBranch], messages.Detached);

        var emptyRoot = GitTestFiles.NewTempDir("forge-changes-no-branch-");
        try
        {
            GitTestShell.RunChecked(emptyRoot, "init", "-q", "--initial-branch=main");
            GitTestShell.RunChecked(emptyRoot, "config", "user.name", "Forge Test");
            GitTestShell.RunChecked(emptyRoot, "config", "user.email", "forge@example.com");
            Write(emptyRoot, "only.txt", "base\n");
            GitTestShell.RunChecked(emptyRoot, "add", "-A");
            GitTestShell.RunChecked(emptyRoot, "commit", "-q", "-m", "only branch");

            var noBranchMessage = await session.Dispatch(
                async () =>
                {
                    using var changes = new ChangesViewModel(new GitCli(), new MemoryCheckpointStore([]));
                    changes.Attach(new PanelContext(SessionId, emptyRoot, null));
                    await changes.LoadAsync(CancellationToken.None);
                    Assert.True(changes.HasNoBranches);
                    changes.AllChangesCommand.Execute(null);
                    await changes.LoadAsync(CancellationToken.None);
                    return changes.Message;
                },
                CancellationToken.None);

            Assert.Equal(Loc.Instance[InspectorStrings.ChangesNoBranches], noBranchMessage);
        }
        finally
        {
            GitTestFiles.DeleteTree(emptyRoot);
        }
    }

    [Fact]
    public async Task Changed_file_panel_uses_self_drawn_scope_and_grouped_branch_flyouts()
    {
        using var workspace = await CreateWorkspaceAsync();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        await session.Dispatch(
            async () =>
            {
                var editorOpens = new List<string>();
                var requestedChanges = new List<string>();
                using var changes = new ChangesViewModel(
                    workspace.Git,
                    workspace.Checkpoints,
                    editorOpens.Add);
                using var files = new FilesPanelViewModel(
                    workspace.Git,
                    static _ => { },
                    static _ => { },
                    path =>
                    {
                        requestedChanges.Add(path);
                        _ = changes.ShowFileAsync(path, CancellationToken.None);
                    },
                    lineCountsFactory: null,
                    changeReview: changes);

                var context = new PanelContext(SessionId, workspace.WorkingDirectory, null);
                changes.Attach(context);
                files.Attach(context);
                await changes.LoadAsync(CancellationToken.None);
                await files.RefreshAsync(CancellationToken.None);
                files.SelectedTab = 1;

                var filesView = new FilesToolView { DataContext = files };
                var changesView = new ChangesView { DataContext = changes };
                var shell = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*") };
                shell.Children.Add(filesView);
                Grid.SetColumn(changesView, 1);
                shell.Children.Add(changesView);
                var window = new Window { Width = 940, Height = 650, Content = shell };
                window.Show();
                Settle(window);

                var scopePicker = filesView.FindControl<Button>("ScopePicker");
                var branchPicker = filesView.FindControl<Button>("CompareBranchPicker");
                Assert.NotNull(scopePicker);
                Assert.NotNull(branchPicker);
                Assert.True(scopePicker!.IsEffectivelyVisible);
                Assert.True(branchPicker!.IsEffectivelyVisible);
                Assert.Empty(filesView.GetVisualDescendants().OfType<ComboBox>());
                Assert.Empty(changesView.GetVisualDescendants().OfType<ComboBox>());

                var scopeFlyout = Assert.IsType<Flyout>(scopePicker.Flyout);
                scopeFlyout.ShowAt(scopePicker);
                Settle(window);
                var scopeMenu = Assert.IsType<Border>(scopeFlyout.Content);
                var scopeStack = Assert.IsType<StackPanel>(scopeMenu.Child);
                var scopeButtons = scopeStack.Children.OfType<Button>().ToArray();
                Assert.Equal("changes-scope-all", AutomationProperties.GetAutomationId(scopeButtons[0]));
                Assert.Equal("changes-scope-uncommitted", AutomationProperties.GetAutomationId(scopeButtons[1]));
                Assert.Single(scopeStack.Children.OfType<Border>(), static item => item.Classes.Contains("divider"));
                Assert.Equal(changes.Turns.Count, Assert.Single(scopeStack.Children.OfType<ItemsControl>()).Items.Count);

                var branchFlyout = Assert.IsType<Flyout>(branchPicker.Flyout);
                branchFlyout.ShowAt(branchPicker);
                Settle(window);
                var branchMenu = Assert.IsType<Border>(branchFlyout.Content);
                var branchStack = Assert.IsType<StackPanel>(branchMenu.Child);
                var branchGroupLabels = branchStack.Children.OfType<Avalonia.Controls.TextBlock>()
                    .Select(static block => block.Text ?? string.Empty)
                    .ToArray();
                Assert.Contains(Loc.Instance[InspectorStrings.ChangesLocalBranches], branchGroupLabels);
                Assert.Contains(Loc.Instance[InspectorStrings.ChangesRemoteBranches], branchGroupLabels);
                Assert.Single(branchStack.Children.OfType<Border>(), static item => item.Classes.Contains("divider"));
                var branchLists = branchStack.Children.OfType<ItemsControl>().ToArray();
                Assert.Equal(2, branchLists.Length);
                Assert.Equal(changes.LocalBranches.Count, branchLists[0].Items.Count);
                Assert.Equal(changes.RemoteBranches.Count, branchLists[1].Items.Count);
                Assert.Contains(branchStack.GetVisualDescendants().OfType<Button>(), button =>
                    AutomationProperties.GetAutomationId(button) == changes.RemoteBranches[0].AutomationId);

                var remoteChoice = changes.RemoteBranches[0];
                var remoteButton = branchStack.GetVisualDescendants().OfType<Button>().Single(button =>
                    AutomationProperties.GetAutomationId(button) == remoteChoice.AutomationId);
                Assert.Same(remoteChoice.SelectCommand, remoteButton.Command);
                remoteButton.Command!.Execute(remoteButton.CommandParameter);
                remoteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle(window);
                Assert.False(branchFlyout.IsOpen);
                Assert.Equal(remoteChoice.RefName, changes.SelectedBranch?.RefName);

                scopeFlyout.ShowAt(scopePicker);
                Settle(window);
                var allButton = scopeStack.Children.OfType<Button>().First();
                allButton.Command!.Execute(allButton.CommandParameter);
                allButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await changes.LoadAsync(CancellationToken.None);
                Settle(window);
                Assert.True(changes.IsAllChanges);
                Assert.False(scopeFlyout.IsOpen);

                var rowList = filesView.FindControl<ListBox>("ChangedDiffList");
                Assert.NotNull(rowList);
                Assert.Equal(changes.Files.Count, rowList!.Items.Count);
                Assert.True(rowList.Items.Count >= 2);
                rowList.SelectedIndex = 0;
                Settle(window);
                var selectedPath = changes.Files[0].Path;
                Assert.Contains(selectedPath, requestedChanges);
                await changes.ShowFileAsync(selectedPath, CancellationToken.None);

                changes.NextFileCommand.Execute(null);
                var nextPath = changes.SelectedFile!.Path;
                changes.PreviousFileCommand.Execute(null);
                changes.OpenInEditorCommand.Execute(null);
                Assert.NotEqual(selectedPath, nextPath);
                Assert.Equal(selectedPath, editorOpens.Single());

                var actionIds = changesView.GetVisualDescendants().OfType<Control>()
                    .Select(AutomationProperties.GetAutomationId)
                    .Where(static id => id is not null)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.Contains("changes-previous-file", actionIds);
                Assert.Contains("changes-next-file", actionIds);
                Assert.Contains("changes-open-in-editor", actionIds);
                Assert.Contains("changes-copy-path", actionIds);
                Assert.Contains("changes-compare-branch", actionIds);
                Assert.Contains("refresh-changes", actionIds);

                var copyButton = changesView.GetVisualDescendants().OfType<Button>().Single(button =>
                    AutomationProperties.GetAutomationId(button) == "changes-copy-path");
                copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle(window);
                if (window.Clipboard is { } clipboard)
                {
                    Assert.Equal(selectedPath, await clipboard.TryGetTextAsync());
                }

                window.Close();
                return 0;
            },
            CancellationToken.None);
    }

    private static async Task<TestWorkspace> CreateWorkspaceAsync()
    {
        var sandbox = GitTestSandbox.Create();
        try
        {
            var workingDirectory = sandbox.CloneTracked("session");
            GitTestShell.RunChecked(workingDirectory, "switch", "-c", "feature/session");
            var git = new GitCli();
            var treeBefore = await git.SnapshotTreeAsync(workingDirectory, CancellationToken.None);
            Write(workingDirectory, "turn.txt", "written during turn\n");
            var treeAfter = await git.SnapshotTreeAsync(workingDirectory, CancellationToken.None);
            GitTestShell.RunChecked(workingDirectory, "add", "-A");
            GitTestShell.RunChecked(workingDirectory, "commit", "-q", "-m", "turn one");
            sandbox.CommitFile(workingDirectory, "branch-only.txt", "committed on feature\n", "feature follow-up");
            Write(workingDirectory, "file.txt", "working-tree edit\n");
            Write(workingDirectory, "uncommitted.txt", "untracked edit\n");

            var now = DateTimeOffset.UtcNow;
            var checkpoint = new TurnCheckpoint(SessionId, 1, workingDirectory, treeBefore, treeAfter, now.AddMinutes(-1), now);
            return new TestWorkspace(sandbox, workingDirectory, git, new MemoryCheckpointStore([checkpoint]));
        }
        catch
        {
            sandbox.Dispose();
            throw;
        }
    }

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Write(string root, string relativePath, string text)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, text);
    }

    private sealed record TestWorkspace(
        GitTestSandbox Sandbox,
        string WorkingDirectory,
        GitCli Git,
        ITurnCheckpointStore Checkpoints) : IDisposable
    {
        public void Dispose() => Sandbox.Dispose();
    }

    private sealed class MemoryCheckpointStore(IReadOnlyList<TurnCheckpoint> checkpoints) : ITurnCheckpointStore
    {
        public Task BeginAsync(string sessionId, int turn, string worktreePath, string treeBefore, DateTimeOffset at, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<bool> CompleteAsync(string sessionId, int turn, string treeAfter, DateTimeOffset at, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<TurnCheckpoint>> ListAsync(string sessionId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<TurnCheckpoint> result = checkpoints.Where(checkpoint => checkpoint.SessionId == sessionId).ToArray();
            return Task.FromResult(result);
        }
    }
}
