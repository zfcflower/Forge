using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Forge.App.Tests.Foundation;

/// <summary>
/// Headless render of the resource inspector (wave W8): the overlay's chrome, one running tool row with its live tail and
/// one running sub-agent row, the row collapse, the idle state, Escape closing it, the live tail view the transcript uses
/// and the composer menu row that opens it. Everything is read back by the automation ids and the text a screen reader
/// sees, so a renamed or dropped piece fails here instead of disappearing.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ResourceInspectorHeadlessTests
{
    private const string Snapshot = "build started\ncompiling 12 files\nok";
    private const string ChildKind = "Explore";
    private const string ChildTask = "look at the diff";
    private const string SessionId = "inspector-session";

    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- the overlay

    [Fact]
    public async Task The_overlay_renders_a_running_tool_and_a_running_sub_agent()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var (model, tool, agent) = CreateModel();
                using (model)
                {
                    var window = Show(model);
                    var ids = AutomationIds(window);
                    var visible = AutomationIds(window, onlyVisible: true);
                    var count = TextsInside(window, "inspector-count");
                    var tail = VisibleTexts(window);
                    window.Close();
                    return (Ids: ids, Visible: visible, Count: count, Tail: tail, Tool: tool, Agent: agent,
                        RootLabel: ResourceInspectorStrings.RootAgent);
                }
            },
            CancellationToken.None);

        foreach (var id in new[]
                 {
                     "resource-inspector", "inspector-close", "inspector-count", "inspector-tool-toggle",
                     "inspector-stop-tool", "inspector-stop-agent",
                 })
        {
            Assert.Contains(id, state.Ids);
            Assert.Contains(id, state.Visible);
        }

        // The count chip and both rows carry their model's text, not just their ids.
        Assert.Contains("2 项进行中", state.Count);
        Assert.Contains(state.RootLabel, state.Tail);
        Assert.Contains(ChildKind, state.Tail);
        Assert.Contains(state.Tool.TailText, state.Tail);
        Assert.Contains(state.Agent.TaskSummary, state.Tail);
    }

    [Fact]
    public async Task Collapsing_the_tool_row_hides_its_tail_and_expanding_it_brings_it_back()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var (model, tool, _) = CreateModel();
                using (model)
                {
                    var window = Show(model);
                    var toggle = Find<Button>(window, "inspector-tool-toggle");
                    Assert.NotNull(toggle);
                    var expandGlyph = TextsInside(window, "inspector-tool-toggle");

                    var before = (Shown: VisibleTexts(window), Expanded: tool.IsExpanded);
                    Click(window, toggle!);
                    var collapsed = (Shown: VisibleTexts(window), Expanded: tool.IsExpanded);
                    Click(window, toggle!);
                    var expanded = (Shown: VisibleTexts(window), Expanded: tool.IsExpanded);
                    window.Close();
                    return (before, collapsed, expanded, Glyph: expandGlyph, Snapshot: tool.TailText, TailLabel: Loc.Instance[ResourceInspectorStrings.TailKey]);
                }
            },
            CancellationToken.None);

        Assert.True(state.before.Expanded);
        Assert.Contains(state.Snapshot, state.before.Shown);
        Assert.Contains("\u25be", state.Glyph);

        Assert.False(state.collapsed.Expanded);
        Assert.DoesNotContain(state.Snapshot, state.collapsed.Shown);
        Assert.DoesNotContain(state.TailLabel, state.collapsed.Shown);

        Assert.True(state.expanded.Expanded);
        Assert.Contains(state.Snapshot, state.expanded.Shown);
    }

    [Fact]
    public async Task An_idle_session_shows_the_empty_state_instead_of_the_list()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var model = new ResourceInspectorViewModel(
                    () => new ResourceInspectorSession("demo session", [], []),
                    new AppPreferences(),
                    close: () => { },
                    schedule: NoTimer);
                using (model)
                {
                    model.Refresh();
                    var window = Show(model);
                    var shown = VisibleTexts(window);
                    var ids = AutomationIds(window, onlyVisible: true);
                    Assert.True(model.IsEmpty);
                    window.Close();
                    return (Shown: shown, Ids: ids, model.IsEmpty,
                        Empty: ResourceInspectorStrings.Empty, EmptyHint: ResourceInspectorStrings.EmptyHint);
                }
            },
            CancellationToken.None);

        Assert.True(state.IsEmpty);
        Assert.Contains(state.Empty, state.Shown);
        Assert.Contains(state.EmptyHint, state.Shown);
        Assert.DoesNotContain("inspector-count", state.Ids);
        Assert.DoesNotContain("inspector-tool-toggle", state.Ids);
    }

    [Fact]
    public async Task Escape_closes_the_overlay_once()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var closed = 0;
                var model = new ResourceInspectorViewModel(
                    () => new ResourceInspectorSession("demo session", [], []),
                    new AppPreferences(),
                    close: () => closed++,
                    schedule: NoTimer);
                using (model)
                {
                    model.Refresh();
                    var window = Show(model);
                    window.Activate();
                    SharedUi.Settle(window);
                    // The view focuses its close button on attach; a window built for the test activates after that, so the
                    // test puts the keyboard there itself and then sends the key a user would.
                    var closeButton = Find<Button>(window, "inspector-close");
                    Assert.NotNull(closeButton);
                    closeButton!.Focus();
                    SharedUi.Settle(window);
                    var focused = ReferenceEquals(window.FocusManager?.GetFocusedElement(), closeButton);

                    window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                    SharedUi.Settle(window);
                    var afterEscape = closed;
                    window.Close();
                    return (Focused: focused, AfterEscape: afterEscape, CanClose: model.CloseCommand.CanExecute(null));
                }
            },
            CancellationToken.None);

        Assert.True(state.Focused, "the close button could not take the keyboard");
        Assert.Equal(1, state.AfterEscape);
        Assert.True(state.CanClose);
    }

    // ---------------------------------------------------------------- the transcript's live tail

    [Fact]
    public async Task The_tool_rows_own_tail_view_appears_with_the_first_snapshot_and_leaves_with_the_finish()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var row = new ToolRowViewModel("Bash", """{"command":"npm test"}""");
                var live = new LiveToolViewModel("root", static () => null, new ToolCall("call-1", "Bash", "{}"), At, static () => { });
                row.AttachLive(live);
                live.RecordTail(Snapshot);
                live.Tick(AppPreferences.DefaultInspectorLogTailLines, At.AddSeconds(1));
                var tail = row.LiveOutputText;

                var window = SharedUi.Show(new ToolRowTailView { DataContext = row }, 600, 200);
                SharedUi.Settle(window);
                var shown = VisibleTexts(window);

                live.Finish();
                SharedUi.Settle(window);
                var afterFinish = VisibleTexts(window);
                var afterFinishText = row.LiveOutputText;
                window.Close();
                return (Shown: shown, AfterFinish: afterFinish, RowHasOutput: row.HasLiveOutput, Snapshot: tail,
                    AfterFinishText: afterFinishText, TailLabel: Loc.Instance[ResourceInspectorStrings.TailKey]);
            },
            CancellationToken.None);

        Assert.Contains(state.Snapshot, state.Shown);
        Assert.Contains(state.TailLabel, state.Shown);
        Assert.False(state.RowHasOutput);
        Assert.Equal(string.Empty, state.AfterFinishText);
        Assert.DoesNotContain(state.Snapshot, state.AfterFinish);
    }

    // ---------------------------------------------------------------- the composer menu row

    [Fact]
    public async Task The_resources_menu_row_opens_the_inspector_once_when_a_window_wired_it()
    {
        var session = SharedUi.Session;
        var root = TestTempFiles.NewDirectory("forge-inspector-menu-");
        try
        {
            var state = await session.Dispatch(
                () =>
                {
                    var opened = 0;
                    using var workspace = Workspace(root, () => opened++);
                    var window = SharedUi.Show(new SessionView { DataContext = workspace }, 900, 700);
                    SharedUi.Settle(window);

                    var item = ResourcesMenuItem(window, "strip-resources-inspector");
                    var visible = (item.IsVisible, item.IsEffectivelyVisible);
                    var boundToInspector = ReferenceEquals(item.Command, workspace.Instruments.OpenInspectorCommand);
                    // MenuItem has no public click entry point in Avalonia (RaiseClick is an explicit, internal
                    // interface implementation); executing the command the row is bound to is exactly what a click invokes.
                    item.Command!.Execute(item.CommandParameter);
                    SharedUi.Settle(window);
                    var afterClick = opened;
                    window.Close();
                    return (Visible: visible, BoundToInspector: boundToInspector, AfterClick: afterClick, Opened: opened);
                },
                CancellationToken.None);

            Assert.Equal((true, true), state.Visible);
            Assert.True(state.BoundToInspector, "the row is not bound to the session's inspector command");
            Assert.Equal(1, state.AfterClick);
            Assert.Equal(1, state.Opened);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task The_resources_menu_row_is_hidden_when_no_window_wired_the_overlay()
    {
        var session = SharedUi.Session;
        var root = TestTempFiles.NewDirectory("forge-inspector-menu-off-");
        try
        {
            var state = await session.Dispatch(
                () =>
                {
                    using var workspace = Workspace(root, openInspector: null);
                    var window = SharedUi.Show(new SessionView { DataContext = workspace }, 900, 700);
                    SharedUi.Settle(window);

                    var item = ResourcesMenuItem(window, "strip-resources-inspector");
                    var visible = (item.IsVisible, item.IsEffectivelyVisible);
                    window.Close();
                    return visible;
                },
                CancellationToken.None);

            Assert.Equal((false, false), state);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static readonly Func<TimeSpan, Action, IDisposable> NoTimer = static (_, _) => new NoopTicker();

    private static (ResourceInspectorViewModel Model, LiveToolViewModel Tool, SubAgentViewModel Agent) CreateModel()
    {
        var tool = new LiveToolViewModel("root", static () => null, new ToolCall("call-1", "Bash", """{"command":"npm test"}"""), At, static () => { });
        tool.RecordTail(Snapshot);
        var agent = new SubAgentViewModel(SessionId, "child-1", "child-session", ChildKind, ChildTask, "model", stopRequested: null);
        agent.MarkRunning(At.AddSeconds(1));
        var model = new ResourceInspectorViewModel(
            () => new ResourceInspectorSession(
                "demo session",
                new List<LiveToolViewModel> { tool },
                new List<SubAgentViewModel> { agent }),
            new AppPreferences(),
            close: () => { },
            schedule: NoTimer);
        // One pull before the window exists: the rows are already in the list on the first frame.
        model.Refresh();
        return (model, tool, agent);
    }

    private static Window Show(ResourceInspectorViewModel model)
    {
        var window = SharedUi.Show(new ResourceInspectorView { DataContext = model }, 1000, 760);
        SharedUi.Settle(window);
        return window;
    }

    private static SessionWorkspaceViewModel Workspace(string root, Action? openInspector)
    {
        var info = new SessionInfo(SessionId, root, "resource inspector", At);
        var store = new EmptySessionStore(At);
        return new SessionWorkspaceViewModel(
            info,
            new SessionRuntime(
                new AgentRuntimeServices(
                    static _ => new MissingCredentialProvider("unconfigured", ProviderStrings.SectionMissingText()),
                    HostClient: null,
                    store,
                    new EmptySessionCatalog(At),
                    new EmptyTodoStore("t1", At),
                    new ModelSettings("model")),
                info),
            checkpoints: null,
            store,
            TestProviderServices.UnconfiguredProbe(),
            preferences: new AppPreferences(),
            openInspector: openInspector);
    }

    /// <summary>The menu row of the strip's resources flyout; the flyout is opened the way the button opens it.</summary>
    private static MenuItem ResourcesMenuItem(Window window, string id)
    {
        var button = Find<Button>(window, "strip-resources")
            ?? throw new InvalidOperationException("the strip's resources button is gone");
        var flyout = button.Flyout as MenuFlyout
            ?? throw new InvalidOperationException("the resources button no longer opens a MenuFlyout");
        flyout.ShowAt(button);
        SharedUi.Settle(window);
        return flyout.Items
            .OfType<MenuItem>()
            .Single(item => string.Equals(AutomationProperties.GetAutomationId(item), id, StringComparison.Ordinal));
    }

    /// <summary>A real pointer click at the control's centre: the path a user's click takes, not a command call.</summary>
    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("the control is not laid out against the window");
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        SharedUi.Settle(window);
    }

    private static T? Find<T>(Window window, string id)
        where T : Control =>
        window.GetVisualDescendants()
            .OfType<T>()
            .FirstOrDefault(control => string.Equals(AutomationProperties.GetAutomationId(control), id, StringComparison.Ordinal));

    private static List<string> AutomationIds(Window window, bool onlyVisible = false) =>
        window.GetVisualDescendants()
            .Where(control => !onlyVisible || control.IsEffectivelyVisible)
            .Select(AutomationProperties.GetAutomationId)
            .Where(static id => !string.IsNullOrEmpty(id))
            .Select(static id => id!)
            .ToList();

    private static List<string> TextsInside(Window window, string id) =>
        window.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control => string.Equals(AutomationProperties.GetAutomationId(control), id, StringComparison.Ordinal))
            ?.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(static block => block.Text ?? string.Empty)
            .ToList() ?? [];

    private static List<string> VisibleTexts(Window window) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(static block => block.IsEffectivelyVisible)
            .Select(static block => block.Text ?? string.Empty)
            .ToList();

    /// <summary>A timer the tests never fire: the overlay is drawn from one explicit pull.</summary>
    private sealed class NoopTicker : IDisposable
    {
        public void Dispose()
        {
        }
    }
}