using System.Text.Json;
using Avalonia.Headless;
using Forge.App.Composition;
using Forge.App.ViewModels;
using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Pins the workbench rules that live in <see cref="MainWindowViewModel"/> and only show up with the real collaborators
/// (forge-app spec, "工作台布局", "浮动窗口", "子 Agent 区" and "布局快照自管"): the reusable preview tab, "open in new
/// tab", the fixed changes tab, the bottom group following the active session, the per-session collapse state, and the
/// restore order (<see cref="WorkbenchLayout.Apply"/> → reopen the tabs → <see cref="WorkbenchLayout.ApplyFloating"/>).
///
/// The view model takes the composition root, so each test builds one against a throw-away data root
/// (<see cref="AppPaths.DataVariable"/> and friends) and disposes it again; nothing touches the user's database. The
/// panel and file actions are fire-and-forget on the dispatcher, so the steps that wait for them pump the loop with a
/// bounded wait instead of asserting on a half-finished action.
/// </summary>
[Collection("forge-app-ui")]
public sealed class MainWindowWorkbenchIntegrationTests
{
    private const string AlphaFile = "alpha.txt";
    private const string BetaFile = "beta.txt";
    private const string SubAgentId = "agent-a";
    private const string SubAgentChildSessionId = "child-a";
    private const string SubAgentKind = "explore";
    private const string SubAgentTask = "review the diff";
    private const string SubAgentModel = "model";
    private const int PumpIterations = 400;
    private const int PumpDelayMilliseconds = 5;
    private const double RestoredPreviewX = 150;
    private const double RestoredPreviewY = 90;
    private const double RestoredPreviewWidth = 700;
    private const double RestoredPreviewHeight = 500;
    private const double RestoredInspectorWidth = 320;
    private const double WideWindowWidth = 1600;
    private const double RegionDragDelta = 40;
    private const double AdjustedBottomHeight = 300;
    private const double AdjustedPreviewWidth = 500;
    private const double AdjustedInspectorSplit = 0.7;
    private const int Precision = 3;

    [Fact]
    public async Task Single_click_reuses_the_preview_tab_for_the_next_file()
    {
        await RunAsync(
            async harness =>
            {
                var first = await harness.OpenFromPanelAsync(AlphaFile, newTab: false);
                var second = await harness.OpenFromPanelAsync(BetaFile, newTab: false);

                Assert.Same(first, second);
                Assert.Equal(
                    1,
                    harness.ViewModel.Workbench.PreviewTabs.Tabs.Count(static tab => tab.Id == WorkbenchIds.PreviewTab));
            });
    }

    [Fact]
    public async Task Single_click_retargets_the_reused_preview_tab_to_the_new_file()
    {
        await RunAsync(
            async harness =>
            {
                var tab = await harness.OpenFromPanelAsync(AlphaFile, newTab: false);

                var retargeted = await harness.OpenFromPanelAsync(BetaFile, newTab: false);

                Assert.Same(tab, retargeted);
                Assert.EndsWith(BetaFile, ((FileDocumentViewModel)tab.Content).Path);
                Assert.Contains(BetaFile, tab.Title);
            });
    }

    [Fact]
    public async Task Single_click_shows_the_clicked_files_content_in_the_preview_tab()
    {
        await RunAsync(
            async harness =>
            {
                var tab = await harness.OpenFromPanelAsync(AlphaFile, newTab: false);

                var document = (FileDocumentViewModel)tab.Content;
                Assert.False(document.HasError, document.Error);
                Assert.True(document.HasDocument);
                Assert.False(string.IsNullOrEmpty(document.EditText));
            });
    }

    [Fact]
    public async Task Open_in_new_tab_creates_one_tab_per_path()
    {
        await RunAsync(
            async harness =>
            {
                await harness.OpenFromPanelAsync(AlphaFile, newTab: true);
                await harness.OpenFromPanelAsync(BetaFile, newTab: true);

                Assert.NotNull(harness.ViewModel.Workbench.FindTab(WorkbenchIds.PinnedFileTab(Path.Combine(harness.WorkDirectory, AlphaFile))));
                Assert.NotNull(harness.ViewModel.Workbench.FindTab(WorkbenchIds.PinnedFileTab(Path.Combine(harness.WorkDirectory, BetaFile))));
            });
    }

    [Fact]
    public async Task Open_in_new_tab_reuses_the_tab_of_a_path_that_is_already_open()
    {
        await RunAsync(
            async harness =>
            {
                var first = await harness.OpenFromPanelAsync(AlphaFile, newTab: true);
                var pinnedCount = harness.ViewModel.Workbench.PreviewTabs.Tabs.Count;

                var again = await harness.OpenFromPanelAsync(AlphaFile, newTab: true);

                Assert.Same(first, again);
                Assert.Equal(pinnedCount, harness.ViewModel.Workbench.PreviewTabs.Tabs.Count);
            });
    }

    [Fact]
    public async Task Open_in_new_tab_shows_the_clicked_files_content()
    {
        await RunAsync(
            async harness =>
            {
                var tab = await harness.OpenFromPanelAsync(AlphaFile, newTab: true);

                var document = (FileDocumentViewModel)tab.Content;
                Assert.False(document.HasError, document.Error);
                Assert.True(document.HasDocument);
                Assert.False(string.IsNullOrEmpty(document.EditText));
            });
    }

    [Fact]
    public async Task Opening_the_same_path_as_preview_and_pinned_shares_one_document()
    {
        await RunAsync(async harness =>
        {
            var preview = await harness.OpenFromPanelAsync(AlphaFile, newTab: false);
            var document = (FileDocumentViewModel)preview.Content;
            var row = harness.Files.Rows.First(row => string.Equals(row.RelativePath, AlphaFile, StringComparison.Ordinal));
            row.OpenInNewTabCommand.Execute(null);
            await Task.Delay(300);
            var tabs = harness.ViewModel.Workbench.PreviewTabs.Tabs
                .Where(static tab => tab.Content is FileDocumentViewModel d && d.Path.EndsWith(AlphaFile, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var only = Assert.Single(tabs);
            Assert.Same(preview, only);
            Assert.Same(document, only.Content);
        });
    }

    [Fact]
    public async Task Show_changes_opens_the_fixed_changes_tab()
    {
        await RunAsync(
            harness =>
            {
                harness.ViewModel.ShowChangesCommand.Execute(null);

                var tab = Assert.IsType<TabModel>(harness.ViewModel.Workbench.FindTab(WorkbenchIds.ChangesTab));
                Assert.False(tab.IsClosable);
                Assert.Same(harness.ViewModel.Workbench.PreviewTabs, tab.Group);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Show_changes_reuses_the_changes_tab()
    {
        await RunAsync(
            harness =>
            {
                harness.ViewModel.ShowChangesCommand.Execute(null);
                var first = harness.ViewModel.Workbench.FindTab(WorkbenchIds.ChangesTab);

                harness.ViewModel.ShowChangesCommand.Execute(null);

                var second = harness.ViewModel.Workbench.FindTab(WorkbenchIds.ChangesTab);
                Assert.Same(first, second);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Switching_to_another_session_drops_the_previous_sessions_sub_agent_tab()
    {
        await RunAsync(
            sessionCount: 2,
            body: harness =>
            {
                var active = Assert.IsType<SessionWorkspaceViewModel>(harness.ViewModel.ActiveSession);
                harness.AddSubAgentTab(active.SessionId);
                Assert.NotNull(harness.ViewModel.Workbench.FindTab(WorkbenchIds.SubAgentTab(SubAgentId)));

                harness.ViewModel.Workbench.ActivateTab(
                    Assert.IsType<TabModel>(harness.ViewModel.Workbench.FindTab(WorkbenchIds.SessionTab(harness.OtherSessionId))));

                Assert.Null(harness.ViewModel.Workbench.FindTab(WorkbenchIds.SubAgentTab(SubAgentId)));
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Collapsed_bottom_group_state_follows_the_active_session()
    {
        await RunAsync(
            sessionCount: 2,
            body: harness =>
            {
                var first = Assert.IsType<SessionWorkspaceViewModel>(harness.ViewModel.ActiveSession);
                harness.ViewModel.Workbench.IsBottomCollapsed = true;
                Assert.True(first.SubAgentsCollapsed);

                harness.ViewModel.Workbench.ActivateTab(
                    Assert.IsType<TabModel>(harness.ViewModel.Workbench.FindTab(WorkbenchIds.SessionTab(harness.OtherSessionId))));
                Assert.False(harness.ViewModel.Workbench.IsBottomCollapsed);

                harness.ViewModel.Workbench.ActivateTab(
                    Assert.IsType<TabModel>(harness.ViewModel.Workbench.FindTab(WorkbenchIds.SessionTab(first.SessionId))));
                Assert.True(harness.ViewModel.Workbench.IsBottomCollapsed);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Restore_applies_the_layout_then_reopens_the_tabs_then_floats_the_restored_tab()
    {
        await RunAsync(
            scenario: new Scenario
            {
                Files = [AlphaFile],
                Layout = new LayoutSnapshot
                {
                    Order = [nameof(RegionRole.Inspector), nameof(RegionRole.Main), nameof(RegionRole.Project)],
                    Regions = [new RegionSnapshot { Role = nameof(RegionRole.Inspector), Width = RestoredInspectorWidth }],
                    Floating =
                    [
                        new FloatingSnapshot
                        {
                            Id = WorkbenchIds.PreviewTab,
                            X = RestoredPreviewX,
                            Y = RestoredPreviewY,
                            Width = RestoredPreviewWidth,
                            Height = RestoredPreviewHeight,
                        },
                    ],
                },
                ConfigureTabs = (tabs, work) => tabs.PreviewFile = Path.Combine(work, AlphaFile),
            },
            harness =>
            {
                Assert.Equal(
                    [RegionRole.Inspector, RegionRole.Main, RegionRole.Project],
                    harness.ViewModel.Workbench.Order.Select(static region => region.Role));

                var preview = Assert.IsType<TabModel>(harness.ViewModel.Workbench.FindTab(WorkbenchIds.PreviewTab));
                Assert.True(preview.IsFloating);

                var floating = Assert.Single(harness.ViewModel.Workbench.Floating);
                Assert.Equal(WorkbenchIds.PreviewTab, floating.Id);
                Assert.Equal(new FloatGeometry(
                    RestoredPreviewX,
                    RestoredPreviewY,
                    RestoredPreviewWidth,
                    RestoredPreviewHeight,
                    IsMaximized: false), floating.Geometry);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Restored_preview_file_shows_its_content()
    {
        await RunAsync(
            scenario: new Scenario
            {
                Files = [AlphaFile],
                ConfigureTabs = (tabs, work) => tabs.PreviewFile = Path.Combine(work, AlphaFile),
            },
            harness =>
            {
                var tab = Assert.IsType<TabModel>(harness.ViewModel.Workbench.FindTab(WorkbenchIds.PreviewTab));
                var document = Assert.IsType<FileDocumentViewModel>(tab.Content);
                Assert.False(document.HasError, document.Error);
                Assert.True(document.HasDocument);
                Assert.False(string.IsNullOrEmpty(document.EditText));
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Adjusted_region_panel_and_preview_widths_are_saved_and_restored_after_a_restart()
    {
        await RunAsync(
            async harness =>
            {
                var layout = harness.ViewModel.Workbench;
                layout.SetWindowWidth(WideWindowWidth);
                await harness.OpenFromPanelAsync(AlphaFile, newTab: false);

                // The exact paths the splitters drive: region drag, then the panel height, split ratio and preview width.
                layout.BeginSplitterDrag(0);
                layout.UpdateSplitterDrag(RegionDragDelta);
                layout.EndSplitterDrag();
                layout.BottomHeight = AdjustedBottomHeight;
                layout.InspectorSplitRatio = AdjustedInspectorSplit;
                layout.PreviewWidth = AdjustedPreviewWidth;
                double adjustedProject = layout.Project.Width;

                // The 800 ms debounced save reaches the store by itself; pump the dispatcher with real time until it does.
                await PumpUntilAsync(async () =>
                {
                    var entry = await Task.Run(() => harness.Composition.UiState.GetAsync("layout", CancellationToken.None));
                    if (entry is not { Version: LayoutSnapshot.CurrentVersion } || entry.Json is not { } json)
                    {
                        return false;
                    }

                    var saved = JsonSerializer.Deserialize(json, WorkbenchJsonContext.Default.LayoutSnapshot);
                    return saved is { BottomHeight: AdjustedBottomHeight, PreviewWidth: AdjustedPreviewWidth, InspectorSplitRatio: AdjustedInspectorSplit };
                });

                // A restart: a fresh view model over the same store restores what the adjustments produced.
                var second = new MainWindowViewModel(harness.Composition);
                try
                {
                    await second.InitializeAsync();

                    Assert.Equal(adjustedProject, second.Workbench.Project.Width, Precision);
                    Assert.Equal(AdjustedBottomHeight, second.Workbench.BottomHeight, Precision);
                    Assert.Equal(AdjustedInspectorSplit, second.Workbench.InspectorSplitRatio, Precision);
                    Assert.Equal(AdjustedPreviewWidth, second.Workbench.PreviewWidth, Precision);
                    Assert.True(second.Workbench.PreviewTabs.IsVisible);
                }
                finally
                {
                    second.Dispose();
                }
            });
    }

    // ---------------------------------------------------------------- harness

    /// <summary>What a test wants seeded before the view model restores: files, the layout, and the open tabs.</summary>
    private sealed record Scenario
    {
        public int SessionCount { get; init; } = 1;

        public IReadOnlyList<string> Files { get; init; } = [AlphaFile, BetaFile];

        public LayoutSnapshot? Layout { get; init; }

        /// <summary>Final say over the open-tabs snapshot; the second argument is the session working directory.</summary>
        public Action<OpenTabsSnapshot, string>? ConfigureTabs { get; init; }
    }

    private static Task RunAsync(Func<Harness, Task> body) => RunAsync(new Scenario(), body);

    [Fact]
    public async Task A_pinned_file_keeps_the_review_owner_of_the_session_that_opened_it_when_another_session_reopens_it()
    {
        await RunAsync(
            2,
            async harness =>
            {
                var owner = Assert.IsType<SessionWorkspaceViewModel>(harness.ViewModel.ActiveSession);
                var tab = await harness.OpenFromPanelAsync(AlphaFile, newTab: true);
                var document = Assert.IsType<FileDocumentViewModel>(tab.Content);
                Assert.Equal(owner.SessionId, document.ReviewOwner?.SessionId);
                harness.ViewModel.Workbench.ActivateTab(
                    Assert.IsType<TabModel>(harness.ViewModel.Workbench.FindTab(WorkbenchIds.SessionTab(harness.OtherSessionId))));
                Assert.Equal(harness.OtherSessionId, harness.ViewModel.ActiveSession?.SessionId);
                Assert.NotEqual(owner.SessionId, harness.OtherSessionId);
                var again = await harness.OpenFromPanelAsync(AlphaFile, newTab: true);
                Assert.Same(tab, again);
                Assert.Same(document, again.Content);
                Assert.Equal(owner.SessionId, document.ReviewOwner?.SessionId);
            });
    }

    [Fact]
    public async Task A_reused_preview_tab_rebinds_its_review_owner_to_the_session_that_opens_the_next_file()
    {
        await RunAsync(
            2,
            async harness =>
            {
                var first = Assert.IsType<SessionWorkspaceViewModel>(harness.ViewModel.ActiveSession);
                var tab = await harness.OpenFromPanelAsync(AlphaFile, newTab: false);
                var document = Assert.IsType<FileDocumentViewModel>(tab.Content);
                Assert.Equal(first.SessionId, document.ReviewOwner?.SessionId);
                harness.ViewModel.Workbench.ActivateTab(
                    Assert.IsType<TabModel>(harness.ViewModel.Workbench.FindTab(WorkbenchIds.SessionTab(harness.OtherSessionId))));
                var reused = await harness.OpenFromPanelAsync(BetaFile, newTab: false);
                Assert.Same(document, reused.Content);
                Assert.EndsWith(BetaFile, document.Path, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(harness.OtherSessionId, document.ReviewOwner?.SessionId);
            });
    }

    private static Task RunAsync(int sessionCount, Func<Harness, Task> body) =>
        RunAsync(new Scenario { SessionCount = sessionCount }, body);

    private static async Task RunAsync(Scenario scenario, Func<Harness, Task> body)
    {
        var session = SharedUi.Session;
        await session.Dispatch(
            async () =>
            {
                var harness = await Harness.CreateAsync(scenario);
                try
                {
                    await body(harness);
                }
                finally
                {
                    await harness.DisposeAsync();
                }

                return 0;
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Pumps the dispatcher until <paramref name="condition"/> holds. The file and tab actions of the view model are
    /// fire-and-forget and read from disk on a pool thread, so a plain <c>Task.Yield</c> loop would race the read.
    /// </summary>
    private static async Task PumpUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < PumpIterations; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(PumpDelayMilliseconds);
        }

        Assert.True(condition(), "the pending file action did not finish");
    }

    /// <summary>Like <see cref="PumpUntilAsync(Func{bool})"/>, for conditions that have to read the store first.</summary>
    private static async Task PumpUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < PumpIterations; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(PumpDelayMilliseconds);
        }

        Assert.True(await condition(), "the pending layout save did not finish");
    }

    /// <summary>One throw-away data root, a real composition root, and the root view model restored over it.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private const string LayoutKey = "layout";
        private const string OpenTabsKey = "open-tabs";

        private readonly string root;
        private readonly string workDirectory;
        private readonly EnvironmentSnapshot previous;

        private Harness(
            string root,
            string workDirectory,
            EnvironmentSnapshot previous,
            AppComposition composition,
            MainWindowViewModel viewModel,
            IReadOnlyList<string> sessionIds)
        {
            this.root = root;
            this.workDirectory = workDirectory;
            this.previous = previous;
            Composition = composition;
            ViewModel = viewModel;
            SessionIds = sessionIds;
        }

        public AppComposition Composition { get; }

        public MainWindowViewModel ViewModel { get; }

        public IReadOnlyList<string> SessionIds { get; }

        public FilesPanelViewModel Files => ViewModel.Files;

        /// <summary>The session working directory the file panel rows are relative to.</summary>
        public string WorkDirectory => workDirectory;

        /// <summary>The session a two-session scenario is not looking at.</summary>
        public string OtherSessionId => SessionIds[^1];

        public static async Task<Harness> CreateAsync(Scenario scenario)
        {
            var root = Path.Combine(Path.GetTempPath(), "forge-workbench-" + Guid.NewGuid().ToString("N"));
            var data = Path.Combine(root, "data");
            var workspace = Path.Combine(root, "workspace");
            var general = Path.Combine(root, "general");
            var work = Path.Combine(root, "work");
            foreach (var directory in new[] { data, workspace, general, work })
            {
                Directory.CreateDirectory(directory);
            }

            foreach (var file in scenario.Files)
            {
                File.WriteAllLines(Path.Combine(work, file), ["first line of " + file, "second line of " + file]);
            }

            var previous = EnvironmentSnapshot.Capture();
            Environment.SetEnvironmentVariable(AppPaths.DataVariable, data);
            Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, workspace);
            Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, general);

            var composition = AppComposition.Create();
            var sessionIds = new List<string>(scenario.SessionCount);
            for (var i = 0; i < scenario.SessionCount; i++)
            {
                var info = await Task.Run(() => composition.Sessions.CreateSessionAsync(work, "session " + i, CancellationToken.None));
                sessionIds.Add(info.Id);
            }

            if (scenario.Layout is { } layout)
            {
                var json = JsonSerializer.Serialize(layout, WorkbenchJsonContext.Default.LayoutSnapshot);
                await Task.Run(() => composition.UiState.PutAsync(LayoutKey, LayoutSnapshot.CurrentVersion, json, CancellationToken.None));
            }

            var tabs = new OpenTabsSnapshot { Sessions = sessionIds, ActiveSession = sessionIds[0] };
            scenario.ConfigureTabs?.Invoke(tabs, work);
            var tabsJson = JsonSerializer.Serialize(tabs, WorkbenchJsonContext.Default.OpenTabsSnapshot);
            await Task.Run(() => composition.UiState.PutAsync(OpenTabsKey, OpenTabsSnapshot.CurrentVersion, tabsJson, CancellationToken.None));

            var viewModel = new MainWindowViewModel(composition);
            await viewModel.InitializeAsync();
            return new Harness(root, work, previous, composition, viewModel, sessionIds);
        }

        /// <summary>Invokes the click a file row in the files panel performs and waits for the tab it opens.</summary>
        public async Task<TabModel> OpenFromPanelAsync(string fileName, bool newTab)
        {
            await Files.RefreshAsync(CancellationToken.None);

            var row = Files.Rows.First(row => string.Equals(row.RelativePath, fileName, StringComparison.Ordinal));
            var id = newTab ? WorkbenchIds.PinnedFileTab(Path.Combine(workDirectory, fileName)) : WorkbenchIds.PreviewTab;
            if (newTab)
            {
                row.OpenInNewTabCommand.Execute(null);
            }
            else
            {
                row.OpenCommand.Execute(null);
            }

            await PumpUntilAsync(
                () => ViewModel.Workbench.FindTab(id) is { Content: FileDocumentViewModel document } tab &&
                      document.Path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase) &&
                      tab.Title.Contains(fileName, StringComparison.Ordinal));

            return Assert.IsType<TabModel>(ViewModel.Workbench.FindTab(id));
        }

        public void AddSubAgentTab(string sessionId)
        {
            var agent = new SubAgentViewModel(
                sessionId,
                SubAgentId,
                SubAgentChildSessionId,
                SubAgentKind,
                SubAgentTask,
                SubAgentModel,
                stopRequested: null);
            ViewModel.Workbench.OpenTab(
                ViewModel.Workbench.BottomTabs,
                WorkbenchIds.SubAgentTab(SubAgentId),
                TabKind.SubAgent,
                agent,
                "sub-agent tab");
        }

        public async ValueTask DisposeAsync()
        {
            // The files panel keeps a FileSystemWatcher on the session directory; detaching it releases the handle the
            // temp root would otherwise be locked by.
            ViewModel.Files.Attach(PanelContext.Empty);
            ViewModel.Dispose();
            await Composition.DisposeAsync();

            previous.Restore();

            DeleteTree(root);
        }

        /// <summary>git writes its objects read-only, so the temp tree is only removable once the attributes are reset.</summary>
        private static void DeleteTree(string path)
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The three data-root variables the composition root reads, captured so a test leaves no trace.</summary>
    private readonly record struct EnvironmentSnapshot(string DataRoot, string Workspace, string GeneralWorkspace)
    {
        public static EnvironmentSnapshot Capture() =>
            new(
                Environment.GetEnvironmentVariable(AppPaths.DataVariable) ?? string.Empty,
                Environment.GetEnvironmentVariable(AppPaths.WorkspaceVariable) ?? string.Empty,
                Environment.GetEnvironmentVariable(AppPaths.GeneralVariable) ?? string.Empty);

        public void Restore()
        {
            Environment.SetEnvironmentVariable(AppPaths.DataVariable, DataRoot);
            Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, Workspace);
            Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, GeneralWorkspace);
        }
    }
}
