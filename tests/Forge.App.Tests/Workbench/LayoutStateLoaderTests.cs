using Avalonia.Headless;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Pins the contract of <see cref="LayoutStateLoader"/> (forge-app spec, "布局快照自管"): both snapshots fall back to
/// the default when the key is missing, the stored schema version or the version inside the JSON is foreign, or the
/// JSON does not parse; a v3 layout and a v2 open-tabs snapshot read back; writes round-trip. The fake
/// <see cref="IUiStateStore"/> keeps the tests off the database.
///
/// The store never sees the UI thread: the loader routes every store call through <c>BackgroundRead</c>, which is what
/// the spec's "UI 线程不碰存储" rule requires of the call sites in <c>MainWindowViewModel</c>.
/// </summary>
[Collection("forge-app-ui")]
public sealed class LayoutStateLoaderTests
{
    private const int DockEraLayoutVersion = 2;
    private const int ForeignOpenTabsVersion = 1;
    private const int NotAVersion = 0;
    private const double PreviewWindowX = 120;
    private const double PreviewWindowY = 80;

    private const string ValidLayoutJson = """
        {"version":5,"order":["Main","Project","Inspector"],"regions":[{"role":"Main","width":0}],
         "previewWidth":345,"inspectorSplit":0.4,"bottomHeight":180,"bottomMinimized":true,
         "tools":["todo","review"],"toolsActive":"review",
         "floating":[{"id":"preview-file","x":120,"y":80,"width":700,"height":500,"maximized":false}]}
        """;

    /// <summary>A pre-v4 snapshot: no left-column split, and the bottom group's state stored as "collapsed to a strip".</summary>
    private const string V3LayoutJson = """
        {"version":3,"order":["Main","Project","Inspector"],"regions":[{"role":"Main","width":0}],
         "previewWidth":345,"inspectorSplit":0.4,"bottomHeight":180,"bottomCollapsed":true}
        """;

    /// <summary>A v4 snapshot: the left-column split exists, the bottom group's flag is still the strip one.</summary>
    private const string V4LayoutJson = """
        {"version":4,"order":["Main","Project","Inspector"],"regions":[{"role":"Main","width":0}],
         "previewWidth":345,"inspectorSplit":0.4,"projectSplit":0.35,"bottomHeight":180,"bottomCollapsed":true}
        """;

    private const string DockEraLayoutJson = """
        {"version":2,"order":["Main","Project","Inspector"],"regions":[{"role":"Main","width":0}]}
        """;

    private const string VersionlessLayoutJson = """
        {"order":["Main","Project","Inspector"],"regions":[{"role":"Main","width":0}]}
        """;

    private const string ValidOpenTabsJson = """
        {"version":2,"sessions":["s1","s2"],"activeSession":"s2","previewFile":"C:/work/a.txt",
         "pinnedFiles":["C:/work/b.txt"],"changesOpen":true,"activePreview":"changes"}
        """;

    private const string ForeignOpenTabsJson = """
        {"version":1,"sessions":["s1"],"activeSession":"s1"}
        """;

    private const string VersionlessOpenTabsJson = """
        {"sessions":["s1"]}
        """;

    private const string BrokenJson = """{"version":3,"order":["Main",""";

    [Fact]
    public async Task ReadLayout_returns_null_when_the_key_is_missing()
    {
        var store = new FakeUiStateStore();
        var loader = new LayoutStateLoader(store);

        var layout = await loader.ReadLayoutAsync(CancellationToken.None);

        Assert.Null(layout);
    }

    [Fact]
    public async Task ReadOpenTabs_returns_null_when_the_key_is_missing()
    {
        var store = new FakeUiStateStore();
        var loader = new LayoutStateLoader(store);

        var tabs = await loader.ReadOpenTabsAsync(CancellationToken.None);

        Assert.Null(tabs);
    }

    [Fact]
    public async Task ReadLayout_returns_null_when_the_stored_entry_has_no_positive_version()
    {
        var store = new FakeUiStateStore();
        store.Seed("layout", NotAVersion, ValidLayoutJson);
        var loader = new LayoutStateLoader(store);

        var layout = await loader.ReadLayoutAsync(CancellationToken.None);

        Assert.Null(layout);
    }

    [Fact]
    public async Task ReadOpenTabs_returns_null_when_the_stored_entry_has_no_positive_version()
    {
        var store = new FakeUiStateStore();
        store.Seed("open-tabs", NotAVersion, ValidOpenTabsJson);
        var loader = new LayoutStateLoader(store);

        var tabs = await loader.ReadOpenTabsAsync(CancellationToken.None);

        Assert.Null(tabs);
    }

    [Fact]
    public async Task ReadLayout_returns_null_for_the_dock_era_v2_json()
    {
        var store = new FakeUiStateStore();
        store.Seed("layout", DockEraLayoutVersion, DockEraLayoutJson);
        var loader = new LayoutStateLoader(store);

        var layout = await loader.ReadLayoutAsync(CancellationToken.None);

        Assert.Null(layout);
    }

    [Fact]
    public async Task ReadLayout_returns_null_when_the_json_has_no_version_field()
    {
        var store = new FakeUiStateStore();
        store.Seed("layout", LayoutSnapshot.CurrentVersion, VersionlessLayoutJson);
        var loader = new LayoutStateLoader(store);

        var layout = await loader.ReadLayoutAsync(CancellationToken.None);

        Assert.Null(layout);
    }

    [Fact]
    public async Task ReadOpenTabs_returns_null_for_a_foreign_json_version()
    {
        var store = new FakeUiStateStore();
        store.Seed("open-tabs", ForeignOpenTabsVersion, ForeignOpenTabsJson);
        var loader = new LayoutStateLoader(store);

        var tabs = await loader.ReadOpenTabsAsync(CancellationToken.None);

        Assert.Null(tabs);
    }

    [Fact]
    public async Task ReadOpenTabs_returns_null_when_the_json_has_no_version_field()
    {
        var store = new FakeUiStateStore();
        store.Seed("open-tabs", OpenTabsSnapshot.CurrentVersion, VersionlessOpenTabsJson);
        var loader = new LayoutStateLoader(store);

        var tabs = await loader.ReadOpenTabsAsync(CancellationToken.None);

        Assert.Null(tabs);
    }

    [Fact]
    public async Task ReadLayout_returns_null_when_the_json_does_not_parse()
    {
        var store = new FakeUiStateStore();
        store.Seed("layout", LayoutSnapshot.CurrentVersion, BrokenJson);
        var loader = new LayoutStateLoader(store);

        var layout = await loader.ReadLayoutAsync(CancellationToken.None);

        Assert.Null(layout);
    }

    [Fact]
    public async Task ReadOpenTabs_returns_null_when_the_json_does_not_parse()
    {
        var store = new FakeUiStateStore();
        store.Seed("open-tabs", OpenTabsSnapshot.CurrentVersion, BrokenJson);
        var loader = new LayoutStateLoader(store);

        var tabs = await loader.ReadOpenTabsAsync(CancellationToken.None);

        Assert.Null(tabs);
    }

    [Fact]
    public async Task ReadLayout_migrates_a_v3_snapshot_by_resetting_the_left_column_split()
    {
        var store = new FakeUiStateStore();
        store.Seed("layout", 3, V3LayoutJson);
        var loader = new LayoutStateLoader(store);

        var layout = await loader.ReadLayoutAsync(CancellationToken.None);

        Assert.NotNull(layout);
        Assert.Equal(LayoutSnapshot.CurrentVersion, layout.Version);
        Assert.Equal(WorkbenchMetrics.DefaultProjectSplitRatio, layout.ProjectSplitRatio);
        // The strip is gone, so its "collapsed" flag survives as the group's minimized state.
        Assert.True(layout.BottomMinimized);
        Assert.False(layout.BottomCollapsed);
    }

    [Fact]
    public async Task ReadLayout_migrates_a_v4_snapshot_and_keeps_the_left_column_split()
    {
        var store = new FakeUiStateStore();
        store.Seed("layout", 4, V4LayoutJson);
        var loader = new LayoutStateLoader(store);

        var layout = await loader.ReadLayoutAsync(CancellationToken.None);

        Assert.NotNull(layout);
        Assert.Equal(LayoutSnapshot.CurrentVersion, layout.Version);
        Assert.Equal(0.35, layout.ProjectSplitRatio);
        Assert.True(layout.BottomMinimized);
        Assert.False(layout.BottomCollapsed);
    }

    [Fact]
    public async Task A_written_layout_carries_the_minimized_state_and_no_retired_collapsed_field()
    {
        var store = new FakeUiStateStore();
        var loader = new LayoutStateLoader(store);

        await loader.WriteLayoutAsync(new LayoutSnapshot { BottomMinimized = true }, CancellationToken.None);

        var json = Assert.Single(store.Writes).Json;
        Assert.Contains("\"bottomMinimized\":true", json);
        Assert.DoesNotContain("bottomCollapsed", json);
    }

    [Fact]
    public async Task ReadLayout_reads_a_current_version_snapshot()
    {
        var store = new FakeUiStateStore();
        store.Seed("layout", LayoutSnapshot.CurrentVersion, ValidLayoutJson);
        var loader = new LayoutStateLoader(store);

        var layout = await loader.ReadLayoutAsync(CancellationToken.None);

        Assert.NotNull(layout);
        Assert.Equal(LayoutSnapshot.CurrentVersion, layout.Version);
        Assert.Equal([RegionRole.Main, RegionRole.Project, RegionRole.Inspector], RolesOf(layout));
        Assert.True(layout.BottomMinimized);
        Assert.Equal(["todo", "review"], layout.ToolsOrder);
        var floating = Assert.Single(layout.Floating);
        Assert.Equal(WorkbenchIds.PreviewTab, floating.Id);
        Assert.Equal(PreviewWindowX, floating.X);
        Assert.Equal(PreviewWindowY, floating.Y);
    }

    [Fact]
    public async Task ReadOpenTabs_reads_a_current_version_snapshot()
    {
        var store = new FakeUiStateStore();
        store.Seed("open-tabs", OpenTabsSnapshot.CurrentVersion, ValidOpenTabsJson);
        var loader = new LayoutStateLoader(store);

        var tabs = await loader.ReadOpenTabsAsync(CancellationToken.None);

        Assert.NotNull(tabs);
        Assert.Equal(OpenTabsSnapshot.CurrentVersion, tabs.Version);
        Assert.Equal(["s1", "s2"], tabs.Sessions);
        Assert.Equal("s2", tabs.ActiveSession);
        Assert.True(tabs.ChangesOpen);
        Assert.Equal(WorkbenchIds.ChangesTab, tabs.ActivePreview);
        Assert.Single(tabs.PinnedFiles);
    }

    [Fact]
    public async Task ReadLayout_throws_OperationCanceledException_for_an_already_cancelled_token()
    {
        var store = new FakeUiStateStore();
        store.Seed("layout", LayoutSnapshot.CurrentVersion, ValidLayoutJson);
        var loader = new LayoutStateLoader(store);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loader.ReadLayoutAsync(cancellation.Token));
        Assert.Equal(0, store.GetCalls);
    }

    [Fact]
    public async Task ReadOpenTabs_throws_OperationCanceledException_for_an_already_cancelled_token()
    {
        var store = new FakeUiStateStore();
        store.Seed("open-tabs", OpenTabsSnapshot.CurrentVersion, ValidOpenTabsJson);
        var loader = new LayoutStateLoader(store);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loader.ReadOpenTabsAsync(cancellation.Token));
        Assert.Equal(0, store.GetCalls);
    }

    [Fact]
    public async Task ReadLayout_returns_null_when_the_store_fails_with_a_tolerated_exception()
    {
        var store = new FakeUiStateStore { GetFailure = new IOException("database is locked") };
        var loader = new LayoutStateLoader(store);

        var layout = await loader.ReadLayoutAsync(CancellationToken.None);

        Assert.Null(layout);
    }

    [Fact]
    public async Task ReadOpenTabs_returns_null_when_the_store_fails_with_a_tolerated_exception()
    {
        var store = new FakeUiStateStore { GetFailure = new InvalidOperationException("connection closed") };
        var loader = new LayoutStateLoader(store);

        var tabs = await loader.ReadOpenTabsAsync(CancellationToken.None);

        Assert.Null(tabs);
    }

    [Fact]
    public async Task Layout_written_then_read_carries_every_persisted_field()
    {
        var store = new FakeUiStateStore();
        var loader = new LayoutStateLoader(store);
        const double previewWidth = 512;
        const double inspectorSplit = 0.35;
        const double bottomHeight = 260;
        var written = new LayoutSnapshot
        {
            Order = [nameof(RegionRole.Inspector), nameof(RegionRole.Main), nameof(RegionRole.Project)],
            Regions = [new RegionSnapshot { Role = nameof(RegionRole.Inspector), Width = 420, AutoHidden = true }],
            PreviewWidth = previewWidth,
            InspectorSplitRatio = inspectorSplit,
            BottomHeight = bottomHeight,
            BottomMinimized = true,
            ToolsOrder = [WorkbenchIds.ReviewTab, WorkbenchIds.TodoTab],
            ToolsActive = WorkbenchIds.ReviewTab,
            Floating = [new FloatingSnapshot { Id = WorkbenchIds.FilesPanel, X = 40, Y = 60, Width = 300, Height = 400 }],
        };

        await loader.WriteLayoutAsync(written, CancellationToken.None);
        var read = await loader.ReadLayoutAsync(CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(LayoutSnapshot.CurrentVersion, read.Version);
        Assert.Equal(written.Order, read.Order);
        var region = Assert.Single(read.Regions);
        Assert.Equal(written.Regions[0].Role, region.Role);
        Assert.Equal(written.Regions[0].Width, region.Width);
        Assert.True(region.AutoHidden);
        Assert.Equal(previewWidth, read.PreviewWidth);
        Assert.Equal(inspectorSplit, read.InspectorSplitRatio);
        Assert.Equal(bottomHeight, read.BottomHeight);
        Assert.True(read.BottomMinimized);
        Assert.Equal(written.ToolsOrder, read.ToolsOrder);
        Assert.Equal(WorkbenchIds.ReviewTab, read.ToolsActive);
        var floating = Assert.Single(read.Floating);
        Assert.Equal(WorkbenchIds.FilesPanel, floating.Id);
        Assert.Equal(40, floating.X);
        Assert.Equal(60, floating.Y);
        Assert.Equal(300, floating.Width);
        Assert.Equal(400, floating.Height);
    }

    [Fact]
    public async Task Open_tabs_written_then_read_carries_every_persisted_field()
    {
        var store = new FakeUiStateStore();
        var loader = new LayoutStateLoader(store);
        var written = new OpenTabsSnapshot
        {
            Sessions = ["a", "b"],
            ActiveSession = "b",
            PreviewFile = "C:/work/a.txt",
            PinnedFiles = ["C:/work/b.txt", "C:/work/c.txt"],
            ChangesOpen = true,
            ActivePreview = WorkbenchIds.PreviewTab,
        };

        await loader.WriteOpenTabsAsync(written, CancellationToken.None);
        var read = await loader.ReadOpenTabsAsync(CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(OpenTabsSnapshot.CurrentVersion, read.Version);
        Assert.Equal(written.Sessions, read.Sessions);
        Assert.Equal("b", read.ActiveSession);
        Assert.Equal("C:/work/a.txt", read.PreviewFile);
        Assert.Equal(written.PinnedFiles, read.PinnedFiles);
        Assert.True(read.ChangesOpen);
        Assert.Equal(WorkbenchIds.PreviewTab, read.ActivePreview);
    }

    [Fact]
    public async Task Writes_store_the_current_schema_version_under_the_documented_keys()
    {
        var store = new FakeUiStateStore();
        var loader = new LayoutStateLoader(store);

        await loader.WriteLayoutAsync(new LayoutSnapshot(), CancellationToken.None);
        await loader.WriteOpenTabsAsync(new OpenTabsSnapshot(), CancellationToken.None);

        Assert.Equal(["layout", "open-tabs"], store.Writes.Select(static write => write.Key));
        Assert.Equal(
            [LayoutSnapshot.CurrentVersion, OpenTabsSnapshot.CurrentVersion],
            store.Writes.Select(static write => write.Version));
    }

    [Fact]
    public async Task Store_reads_run_off_the_ui_thread()
    {
        var session = SharedUi.Session;
        var store = new FakeUiStateStore();
        store.Seed("layout", LayoutSnapshot.CurrentVersion, ValidLayoutJson);
        store.Seed("open-tabs", OpenTabsSnapshot.CurrentVersion, ValidOpenTabsJson);
        var loader = new LayoutStateLoader(store);

        // The headless session puts the test body on the dispatcher thread; a store call made directly from there
        // would observe CheckAccess() == true, which is exactly what the spec forbids for database work.
        var (onUiThreadDuringReads, onUiThreadWhileAwaiting) = await session.Dispatch(
            async () =>
            {
                Assert.True(Dispatcher.UIThread.CheckAccess(), "the test must run on the dispatcher thread");
                await loader.ReadLayoutAsync(CancellationToken.None);
                await loader.ReadOpenTabsAsync(CancellationToken.None);
                return (store.SawUiThread, Dispatcher.UIThread.CheckAccess());
            },
            CancellationToken.None);

        Assert.False(onUiThreadDuringReads);
        Assert.True(onUiThreadWhileAwaiting);
    }

    private static List<RegionRole> RolesOf(LayoutSnapshot snapshot) =>
        snapshot.Order
            .Select(static name => name switch
            {
                nameof(RegionRole.Project) => RegionRole.Project,
                nameof(RegionRole.Main) => RegionRole.Main,
                _ => RegionRole.Inspector,
            })
            .ToList();

    /// <summary>In-memory <see cref="IUiStateStore"/>: one entry per key, optional failure injection, write log.</summary>
    private sealed class FakeUiStateStore : IUiStateStore
    {
        private readonly Dictionary<string, UiStateEntry> entries = new(StringComparer.Ordinal);

        public Exception? GetFailure { get; init; }

        public List<(string Key, int Version, string Json)> Writes { get; } = [];

        public int GetCalls { get; private set; }

        /// <summary>True when a store call ran on the Avalonia dispatcher thread.</summary>
        public bool SawUiThread { get; private set; }

        public void Seed(string key, int version, string json) =>
            entries[key] = new UiStateEntry(key, version, json, DateTimeOffset.UnixEpoch);

        public Task<UiStateEntry?> GetAsync(string key, CancellationToken ct)
        {
            GetCalls++;
            SawUiThread |= Dispatcher.UIThread.CheckAccess();
            if (GetFailure is not null)
            {
                return Task.FromException<UiStateEntry?>(GetFailure);
            }

            return Task.FromResult(entries.TryGetValue(key, out var entry) ? entry : null);
        }

        public Task PutAsync(string key, int version, string json, CancellationToken ct)
        {
            Writes.Add((key, version, json));
            entries[key] = new UiStateEntry(key, version, json, DateTimeOffset.UnixEpoch);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string key, CancellationToken ct) => Task.FromResult(entries.Remove(key));
    }
}
