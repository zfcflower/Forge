using System.Text.Json;
using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Pins the persisted workbench layout (forge-app spec, "布局快照自管"): what <see cref="WorkbenchLayout.Capture"/> writes,
/// how <see cref="WorkbenchLayout.Apply"/> and <see cref="WorkbenchLayout.ApplyFloating"/> read it back, and which
/// invalid values fall back to the defaults. Serialization goes through <see cref="WorkbenchJsonContext"/>, as in production.
/// </summary>
public sealed class LayoutSnapshotTests
{
    // Wide enough that Main never reaches its minimum, so restored widths are exactly the stored ones.
    private const double WideWindowWidth = 2000;
    private const int LayoutVersion = 5;
    private const int DockEraVersion = 2;
    private const int OpenTabsVersion = 2;
    private const string InspectorRole = "Inspector";
    private const string ReviewId = "review";
    private const string SpecsId = "specs";
    private const string TodoId = "todo";
    private const string GhostId = "ghost";
    private const string SubAgentId = "1";
    private const double FloatX = 120;
    private const double FloatY = 80;
    private const double FloatWidth = 700;
    private const double FloatHeight = 500;
    private const double DragDelta = 50;

    // Visual index of the splitter between Project (1) and Inspector (2) once Main has moved to the front.
    private const int ProjectInspectorSplitter = 1;

    [Fact]
    public void Round_trip_restores_region_order()
    {
        var restored = RestoreFrom(EditedLayout());

        Assert.Equal(
            new[] { RegionRole.Main, RegionRole.Project, RegionRole.Inspector },
            restored.Order.Select(region => region.Role).ToArray());
    }

    [Fact]
    public void Round_trip_restores_region_widths()
    {
        var source = EditedLayout();

        var restored = RestoreFrom(source);

        Assert.Equal(source.Project.Width, restored.Project.Width);
        Assert.Equal(source.Main.Width, restored.Main.Width);
        Assert.Equal(source.Inspector.Width, restored.Inspector.Width);
    }

    [Fact]
    public void Round_trip_restores_auto_hidden_side_region()
    {
        var restored = RestoreFrom(EditedLayout());

        Assert.True(restored.Project.IsAutoHidden);
        Assert.False(restored.Inspector.IsAutoHidden);
        Assert.False(restored.Main.IsAutoHidden);
    }

    [Fact]
    public void Round_trip_restores_bottom_height_and_minimized_state()
    {
        const double bottomHeight = 300;
        var source = EditedLayout();
        source.BottomHeight = bottomHeight;
        source.IsBottomMinimized = true;

        var restored = RestoreFrom(source);

        Assert.Equal(bottomHeight, restored.BottomHeight);
        Assert.True(restored.IsBottomMinimized);
    }

    [Fact]
    public void Round_trip_restores_inspector_tool_tab_order()
    {
        var restored = RestoreFrom(EditedLayout());

        Assert.Equal(new[] { ReviewId, SpecsId, TodoId }, restored.InspectorTools.Tabs.Select(tab => tab.Id).ToArray());
    }

    [Fact]
    public void Round_trip_restores_active_inspector_tool_tab()
    {
        var restored = RestoreFrom(EditedLayout());

        Assert.Equal(SpecsId, restored.InspectorTools.ActiveTab?.Id);
    }

    [Fact]
    public void Round_trip_restores_floating_tab_and_its_geometry()
    {
        var geometry = new FloatGeometry(FloatX, FloatY, FloatWidth, FloatHeight, IsMaximized: true);

        var restored = RestoreFrom(EditedLayout());

        var item = Assert.Single(restored.Floating);
        Assert.Equal(ReviewId, item.Id);
        Assert.Equal(geometry, item.Geometry);
    }

    [Fact]
    public void Round_trip_restores_preview_width_and_inspector_split_ratio()
    {
        const double previewWidth = 500;
        const double splitRatio = 0.4;
        var source = EditedLayout();
        source.PreviewWidth = previewWidth;
        source.InspectorSplitRatio = splitRatio;

        var restored = RestoreFrom(source);

        Assert.Equal(previewWidth, restored.PreviewWidth);
        Assert.Equal(splitRatio, restored.InspectorSplitRatio);
    }

    [Fact]
    public void Restored_layout_captures_the_same_snapshot_again()
    {
        var source = EditedLayout();
        var restored = RestoreFrom(source);

        Assert.Equal(Serialize(source.Capture()), Serialize(restored.Capture()));
    }

    [Fact]
    public void Valid_order_permutation_is_applied()
    {
        var layout = NewLayout();
        var snapshot = new LayoutSnapshot { Order = ["Inspector", "Main", "Project"] };

        layout.Apply(snapshot);

        Assert.Equal(
            new[] { RegionRole.Inspector, RegionRole.Main, RegionRole.Project },
            layout.Order.Select(region => region.Role).ToArray());
    }

    [Theory]
    [MemberData(nameof(InvalidOrders))]
    public void Invalid_order_keeps_the_default_order(string[] order)
    {
        var layout = NewLayout();
        var snapshot = new LayoutSnapshot { Order = order.ToList() };

        layout.Apply(snapshot);

        Assert.Equal(
            new[] { RegionRole.Project, RegionRole.Main, RegionRole.Inspector },
            layout.Order.Select(region => region.Role).ToArray());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-10.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Non_positive_or_non_finite_region_width_keeps_the_default_width(double width)
    {
        var layout = NewLayout();
        var snapshot = new LayoutSnapshot
        {
            Regions = [new RegionSnapshot { Role = "Project", Width = width }],
        };

        layout.Apply(snapshot);

        Assert.Equal(WorkbenchMetrics.DefaultProjectWidth, layout.Project.Width);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Out_of_range_inspector_split_ratio_keeps_the_default_ratio(double ratio)
    {
        var layout = NewLayout();
        var snapshot = new LayoutSnapshot { InspectorSplitRatio = ratio };

        layout.Apply(snapshot);

        Assert.Equal(WorkbenchMetrics.DefaultInspectorSplitRatio, layout.InspectorSplitRatio);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-10.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Non_positive_or_non_finite_preview_width_keeps_the_default_width(double width)
    {
        var layout = NewLayout();
        var snapshot = new LayoutSnapshot { PreviewWidth = width };

        layout.Apply(snapshot);

        Assert.Equal(WorkbenchMetrics.DefaultPreviewWidth, layout.PreviewWidth);
    }

    [Fact]
    public void Apply_clamps_preview_width_up_to_preview_min_width()
    {
        var layout = NewLayout();
        var snapshot = new LayoutSnapshot { PreviewWidth = 1.0 };

        layout.Apply(snapshot);

        Assert.Equal(WorkbenchMetrics.PreviewMinWidth, layout.PreviewWidth);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-10.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Non_positive_or_non_finite_bottom_height_keeps_the_default_height(double height)
    {
        var layout = NewLayout();
        var snapshot = new LayoutSnapshot { BottomHeight = height };

        layout.Apply(snapshot);

        Assert.Equal(WorkbenchMetrics.DefaultBottomHeight, layout.BottomHeight);
    }

    [Fact]
    public void Unknown_tool_id_is_dropped_and_known_tools_keep_their_order()
    {
        var layout = NewLayout();
        var snapshot = new LayoutSnapshot { ToolsOrder = [GhostId, SpecsId, ReviewId] };

        layout.Apply(snapshot);

        Assert.Equal(new[] { SpecsId, ReviewId, TodoId }, layout.InspectorTools.Tabs.Select(tab => tab.Id).ToArray());
    }

    [Fact]
    public void Unknown_active_tool_id_keeps_the_current_active_tool()
    {
        var layout = NewLayout();
        var snapshot = new LayoutSnapshot { ToolsActive = GhostId };

        layout.Apply(snapshot);

        Assert.Equal(TodoId, layout.InspectorTools.ActiveTab?.Id);
    }

    [Fact]
    public void Unknown_floating_id_is_ignored_on_restore()
    {
        var layout = NewLayout();
        var snapshot = new LayoutSnapshot
        {
            Floating = [new FloatingSnapshot { Id = GhostId }],
        };

        layout.ApplyFloating(snapshot);

        Assert.Empty(layout.Floating);
    }

    [Fact]
    public void Current_layout_version_is_5()
    {
        Assert.Equal(LayoutVersion, LayoutSnapshot.CurrentVersion);
    }

    [Fact]
    public void Dock_era_v2_json_deserializes_with_version_2()
    {
        const string dockEraJson = """{"version":2,"order":["Project","Main","Inspector"],"regions":[]}""";

        var snapshot = JsonSerializer.Deserialize(dockEraJson, WorkbenchJsonContext.Default.LayoutSnapshot);

        Assert.Equal(DockEraVersion, snapshot?.Version);
    }

    [Fact]
    public void Layout_json_without_version_is_not_the_current_version()
    {
        const string json = """{"order":["Project","Main","Inspector"],"regions":[]}""";

        var snapshot = JsonSerializer.Deserialize(json, WorkbenchJsonContext.Default.LayoutSnapshot);

        Assert.NotEqual(LayoutSnapshot.CurrentVersion, snapshot?.Version);
    }

    [Fact]
    public void Open_tabs_json_without_version_is_not_the_current_version()
    {
        const string json = """{"sessions":[]}""";

        var snapshot = JsonSerializer.Deserialize(json, WorkbenchJsonContext.Default.OpenTabsSnapshot);

        Assert.NotEqual(OpenTabsSnapshot.CurrentVersion, snapshot?.Version);
    }

    [Fact]
    public void Capture_writes_the_current_version()
    {
        var layout = NewLayout();

        Assert.Equal(LayoutSnapshot.CurrentVersion, layout.Capture().Version);
    }

    [Fact]
    public void Unplaced_floating_window_is_saved_without_coordinates()
    {
        var layout = NewLayout();
        layout.FloatTab(ToolTab(layout, ReviewId));

        var json = Serialize(layout.Capture());

        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement.GetProperty("floating")[0];
        var names = entry.EnumerateObject().Select(property => property.Name).ToList();
        Assert.DoesNotContain("x", names);
        Assert.DoesNotContain("y", names);
    }

    [Fact]
    public void Unplaced_floating_window_restores_as_unplaced()
    {
        var layout = NewLayout();
        layout.FloatTab(ToolTab(layout, ReviewId));

        var restored = RestoreFrom(layout);

        Assert.True(Assert.Single(restored.Floating).Geometry.IsUnplaced);
    }

    [Fact]
    public void Captured_inspector_width_excludes_the_preview_column()
    {
        var layout = NewLayout();
        OpenPreview(layout);

        var snapshot = layout.Capture();

        Assert.Equal(WorkbenchMetrics.DefaultInspectorWidth, snapshot.Regions.Single(region => region.Role == InspectorRole).Width);
    }

    [Fact]
    public void Reopening_preview_after_restore_does_not_borrow_its_width_twice()
    {
        var source = NewLayout();
        OpenPreview(source);
        double sourceInspectorWidth = source.Inspector.Width;
        var restored = RestoreFrom(source);

        OpenPreview(restored);

        Assert.Equal(sourceInspectorWidth, restored.Inspector.Width);
    }

    [Fact]
    public void Open_tabs_round_trip_keeps_session_and_preview_fields()
    {
        var snapshot = new OpenTabsSnapshot
        {
            Sessions = ["session-a", "session-b"],
            ActiveSession = "session-b",
            PreviewFile = "src/Program.cs",
            ChangesOpen = true,
            ActivePreview = "changes",
        };

        var restored = RoundTripOpenTabs(snapshot);

        Assert.Equal(new[] { "session-a", "session-b" }, restored.Sessions);
        Assert.Equal("session-b", restored.ActiveSession);
        Assert.Equal("src/Program.cs", restored.PreviewFile);
        Assert.True(restored.ChangesOpen);
        Assert.Equal("changes", restored.ActivePreview);
    }

    [Fact]
    public void Open_tabs_round_trip_keeps_pinned_file_order()
    {
        var snapshot = new OpenTabsSnapshot { PinnedFiles = ["b.cs", "a.cs", "c.cs"] };

        var restored = RoundTripOpenTabs(snapshot);

        Assert.Equal(new[] { "b.cs", "a.cs", "c.cs" }, restored.PinnedFiles);
    }

    [Fact]
    public void Open_tabs_round_trip_keeps_version_2()
    {
        var restored = RoundTripOpenTabs(new OpenTabsSnapshot());

        Assert.Equal(OpenTabsVersion, restored.Version);
    }

    public static TheoryData<string[]> InvalidOrders() => new()
    {
        new[] { "Project", "Project", "Inspector" },
        new[] { "Project", "Main" },
        new[] { "Project", "Main", "Inspector", "Main" },
        new[] { "Project", "Main", "Sidebar" },
        Array.Empty<string>(),
    };

    /// <summary>A layout whose every persisted part differs from the default, built through the public operations.</summary>
    private static WorkbenchLayout EditedLayout()
    {
        var layout = NewLayout();

        // Main | Project | Inspector, then widen Inspector by dragging its splitter with Project.
        layout.SwapRegions(RegionRole.Project, RegionRole.Main);
        layout.BeginSplitterDrag(ProjectInspectorSplitter);
        layout.UpdateSplitterDrag(DragDelta);
        layout.EndSplitterDrag();

        layout.SetAutoHidden(RegionRole.Project, true);

        layout.OpenTab(layout.BottomTabs, WorkbenchIds.SubAgentTab(SubAgentId), TabKind.SubAgent, "agent", "agent");

        // Default tool order is Todo | Review | Specs; after moving Todo to the end and activating Specs, Review floats.
        var tools = layout.InspectorTools;
        layout.MoveTab(tools, 0, 2);
        layout.ActivateTab(tools.Tabs[1]);
        layout.FloatTab(tools.Tabs[0], new FloatGeometry(FloatX, FloatY, FloatWidth, FloatHeight, IsMaximized: true));

        return layout;
    }

    private static WorkbenchLayout NewLayout()
    {
        var layout = new WorkbenchLayout("project", "files", "todo", "review", "specs");
        layout.SetWindowWidth(WideWindowWidth);
        return layout;
    }

    /// <summary>Captures, serializes, deserializes and applies to a fresh layout, the way the next launch restores it.</summary>
    private static WorkbenchLayout RestoreFrom(WorkbenchLayout source)
    {
        var json = Serialize(source.Capture());
        var snapshot = JsonSerializer.Deserialize(json, WorkbenchJsonContext.Default.LayoutSnapshot)
            ?? throw new InvalidOperationException("layout snapshot did not deserialize");

        var restored = NewLayout();
        restored.Apply(snapshot);
        restored.ApplyFloating(snapshot);
        return restored;
    }

    private static string Serialize(LayoutSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, WorkbenchJsonContext.Default.LayoutSnapshot);

    private static OpenTabsSnapshot RoundTripOpenTabs(OpenTabsSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, WorkbenchJsonContext.Default.OpenTabsSnapshot);
        return JsonSerializer.Deserialize(json, WorkbenchJsonContext.Default.OpenTabsSnapshot)
            ?? throw new InvalidOperationException("open-tabs snapshot did not deserialize");
    }

    private static TabModel ToolTab(WorkbenchLayout layout, string id) =>
        layout.InspectorTools.Tabs.Single(tab => tab.Id == id);

    private static void OpenPreview(WorkbenchLayout layout) =>
        layout.OpenTab(layout.PreviewTabs, WorkbenchIds.PreviewTab, TabKind.FilePreview, "preview", "preview");
}
