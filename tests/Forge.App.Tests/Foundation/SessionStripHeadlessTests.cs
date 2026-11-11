using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Forge.App.Tests.Foundation;

/// <summary>
/// Headless render of the composer card (task spec "B2", <c>SessionView.axaml</c>): the strip carries the four counters and
/// the project button on the right, and the status row that used to sit under the card is gone — its four pieces moved into
/// the strip. The test reads the same automation ids a screen reader (and the old row) uses, so a renamed or dropped piece
/// of the strip fails here instead of silently disappearing.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionStripHeadlessTests
{
    private static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(60);

    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private const int PumpAttempts = 600;

    private const int PollMilliseconds = 5;

    [Fact]
    public async Task The_composer_strip_renders_the_resources_button_and_the_workspace_button()
    {
        // The strip's labels are read from Loc, so the assertions below name the language they expect.
        using var language = new LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var root = Path.Combine(Path.GetTempPath(), "forge-strip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var state = await session.Dispatch(
                async () =>
                {
                    var sources = new FakeSources();
                    using var workspace = Workspace(root, sources);

                    // The window pushes the project and the git markers; the strip only renders them. The button itself
                    // names the workspace (the session's working directory), not the project.
                    workspace.Instruments.SetProject("forge", root);
                    workspace.Instruments.SetGit("↑1 ↓2");

                    var view = new SessionView { DataContext = workspace };
                    var window = new Window { Width = 1200, Height = 700, Content = view };
                    window.Show();
                    Settle(window);

                    sources.Records = 5;
                    workspace.Instruments.RefreshSources();
                    var settled = await UntilAsync(() => workspace.Instruments.ApprovalText == "2/5");
                    Settle(window);

                    var ids = AutomationIds(window);
                    var visible = AutomationIds(window, onlyVisible: true);
                    var resources = TextsInside(window, "strip-resources");
                    var workspaceButton = TextsInside(window, "strip-workspace");
                    var stripTexts = Texts(window);

                    // Dragged narrow, only the branch group goes; the resources button, the workspace button and the token
                    // readout stay.
                    window.Width = 600;
                    Settle(window);
                    var narrow = (Ids: AutomationIds(window), Visible: AutomationIds(window, onlyVisible: true));

                    window.Width = 1200;
                    Settle(window);
                    var wide = AutomationIds(window, onlyVisible: true);

                    window.Close();
                    return (Settled: settled, Ids: ids, Visible: visible, Resources: resources,
                        Workspace: workspaceButton, StripTexts: stripTexts, Narrow: narrow, Wide: wide);
                },
                new CancellationTokenSource(StepBudget).Token);

            Assert.True(state.Settled, "the strip never showed the counter it was given");

            foreach (var id in new[] { "strip-resources", "strip-workspace", "strip-context" })
            {
                Assert.Contains(id, state.Ids);
                Assert.Contains(id, state.Visible);
            }

            // The strip is bound to the session's instruments, not just present: the resources button carries its own label,
            // and the only four-chip row is gone (the four readings live in the button's menu now).
            Assert.Contains(InstrumentsStrings.Resources, state.Resources);
            Assert.DoesNotContain("strip-approvals", state.Ids);
            Assert.DoesNotContain("strip-overflow", state.Ids);

            var workspaceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
            Assert.Contains(workspaceName, state.Workspace);
            // The branch is no longer part of the strip: the workspace button and the project tree name it.
            Assert.DoesNotContain("feature/strip", state.StripTexts);

            // The ladder the pure function describes is really applied by the view: at 600 px the branch group is gone while
            // the resources button, the workspace button and the readout stay; at 1200 px the branch is back.
            Assert.Contains("strip-resources", state.Narrow.Visible);
            Assert.Contains("strip-workspace", state.Narrow.Visible);
            Assert.Contains("strip-context", state.Narrow.Visible);
            Assert.Contains("strip-resources", state.Wide);
            Assert.Contains("strip-workspace", state.Wide);

            // The session status row under the card is gone with it: its sub-agent toggle has no element any more.
            Assert.DoesNotContain("toggle-subagents", state.Ids);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static SessionWorkspaceViewModel Workspace(string root, ISessionInstrumentSources sources)
    {
        var info = new SessionInfo("strip-session", root, "strip", At) { Branch = "feature/strip" };
        var store = new EmptyStore();
        return new SessionWorkspaceViewModel(
            info,
            new SessionRuntime(
                new AgentRuntimeServices(
                    static _ => new MissingCredentialProvider("unconfigured", ProviderStrings.SectionMissingText()),
                    HostClient: null,
                    store,
                    new EmptyCatalog(),
                    new EmptyTodoStore(),
                    new ModelSettings("model")),
                info),
            checkpoints: null,
            store,
            UnconfiguredProviders(root),
            instrumentSources: sources);
    }

    /// <summary>An empty catalogue (a legacy section with no usable fields) so the strip test keeps its "no provider" shape.</summary>
    private static ProviderServices UnconfiguredProviders(string root)
    {
        var settingsPath = Path.Combine(root, "providers-settings.json");
        File.WriteAllText(settingsPath, """{"provider":{"kind":"","baseUrl":""}}""");
        var dataDir = Path.Combine(root, "provider-data");
        Directory.CreateDirectory(dataDir);
        // Fake handler, never used for network here; the workspace does not own it so it is left for reclamation.
        return new ProviderServices(settingsPath, dataDir, new HttpClient(new ThrowingHandler()));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network in this test");
    }

    private static List<string> AutomationIds(Window window, bool onlyVisible = false) =>
        window.GetVisualDescendants()
            .Where(control => !onlyVisible || control.IsEffectivelyVisible)
            .Select(static control => AutomationProperties.GetAutomationId(control))
            .Where(static id => !string.IsNullOrEmpty(id))
            .Select(static id => id!)
            .ToList();

    private static List<string> Texts(Window window) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(static block => block.Text ?? string.Empty)
            .ToList();

    [Fact]
    public async Task The_resources_menu_lists_the_counters_as_plain_rows_without_submenu_chevrons()
    {
        // Regression: the rows used to carry their label and count as child elements, which Avalonia turns into a *submenu*
        // (the row then renders as a chevron with nothing beside it). Each row is now a plain header string.
        using var language = new LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var root = Path.Combine(Path.GetTempPath(), "forge-strip-menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var state = await session.Dispatch(
                () =>
                {
                    var sources = new FakeSources { Records = 5 };
                    using var workspace = Workspace(root, sources);
                    var view = new SessionView { DataContext = workspace };
                    var window = new Window { Width = 600, Height = 700, Content = view };
                    window.Show();
                    Settle(window);

                    var button = window.GetVisualDescendants()
                        .OfType<Button>()
                        .FirstOrDefault(candidate => string.Equals(
                            AutomationProperties.GetAutomationId(candidate), "strip-resources", StringComparison.Ordinal));
                    Assert.NotNull(button);
                    var flyout = Assert.IsType<MenuFlyout>(button!.Flyout);

                    flyout.ShowAt(button);
                    Settle(window);

                    var items = flyout.Items.OfType<MenuItem>().ToList();

                    // The sub-agent row is hidden while the session has none, so four counter rows are visible.
                    var visible = items.Where(static item => item.IsVisible).ToList();
                    var rows = visible
                        .Select(static item => item.Header as string ?? string.Empty)
                        .Where(static header => header.Length > 0)
                        .ToList();
                    var withChildren = items.Count(static item => item.Items.Count > 0);
                    window.Close();
                    return (Rows: rows, WithChildren: withChildren, Headerless: visible.Count - rows.Count);
                },
                new CancellationTokenSource(StepBudget).Token);

            Assert.Equal(4, state.Rows.Count);
            Assert.Contains(state.Rows, row => row.Contains(InstrumentsStrings.ApprovalsLabel, StringComparison.Ordinal) && row.Contains("2/5", StringComparison.Ordinal));
            Assert.Contains(state.Rows, row => row.Contains(InstrumentsStrings.SkillsLabel, StringComparison.Ordinal));
            Assert.Contains(state.Rows, row => row.Contains(InstrumentsStrings.McpLabel, StringComparison.Ordinal));
            Assert.Contains(state.Rows, row => row.Contains(InstrumentsStrings.ToolsLabel, StringComparison.Ordinal));

            // No row may be a submenu header, and no row may be blank: that combination is what the chevron-only menu was.
            Assert.Equal(0, state.WithChildren);
            Assert.Equal(0, state.Headerless);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static List<string> TextsInside(Window window, string id) =>
        window.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control => string.Equals(AutomationProperties.GetAutomationId(control), id, StringComparison.Ordinal))
            ?.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(static block => block.Text ?? string.Empty)
            .ToList() ?? [];

    /// <summary>
    /// Drains the dispatcher and lays the window out twice: layout can post follow-up work that only exists after a pass,
    /// and the layout manager's own scheduled pass does not reliably run under <c>RunJobs</c> alone.
    /// </summary>
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

    /// <summary>Waits for a background read to land; the wait yields to the dispatcher instead of blocking it.</summary>
    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < PumpAttempts; attempt++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(PollMilliseconds, CancellationToken.None);
        }

        return condition();
    }

    /// <summary>Only the two counters this test asserts on are real; everything else is a quiet zero.</summary>
    private sealed class FakeSources : ISessionInstrumentSources
    {
        public bool HasVsCode => false;

        public int Pending => 2;

        public int? Records { get; set; }

        public int PendingApprovals(string sessionId) => Pending;

        public Task<int?> ApprovalRecordsAsync(CancellationToken ct) => Task.FromResult(Records);

        public (int Ready, int Configured) McpServers() => (1, 2);

        public IReadOnlyList<SlashEntry> Skills(string workspacePath) => [new SlashEntry("review", "review skill", true)];

        public ModelMetadata ModelMetadata(string modelId, string? providerId = null) => new(200_000, new ModelPricing(3.00, 15.00, 0.30, 3.75));

        public void OpenInVsCode(string path)
        {
        }

        public void OpenInExplorer(string path)
        {
        }

        public void OpenSettingsSection(string sectionId)
        {
        }
    }

    private sealed class EmptyStore : ISessionStore
    {
        public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
            Task.FromResult(new SessionInfo("created", workspacePath, title, At));

        public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) => Task.FromResult<SessionInfo?>(null);

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct) =>
            Task.FromResult(1L);

        public IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) =>
            Empty();

        public Task<long> LastSeqAsync(string sessionId, CancellationToken ct) => Task.FromResult(0L);

        public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SummaryRecord>>([]);

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchHit>>([]);

        private static async IAsyncEnumerable<StoredMessage> Empty()
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class EmptyCatalog : ISessionCatalog
    {
        public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) =>
            Task.FromResult(new SessionInfo("created", request.WorkspacePath, request.Title, At));

        public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => Task.FromResult(true);

        public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class EmptyTodoStore : ITodoStore
    {
        public event Action<string>? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TodoItem>>([]);

        public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct) =>
            Task.FromResult(new TodoItem("todo-1", sessionId, title, TodoStatus.Pending, note, 0, At));

        public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct) =>
            Task.FromResult<TodoItem?>(null);

        public Task<bool> RemoveAsync(string id, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> MoveAsync(string id, int order, CancellationToken ct) => Task.FromResult(false);
    }
}
