using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TextBlock = Avalonia.Controls.TextBlock;
using Forge.Abstractions;
using Forge.App.Approval;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Approval;

/// <summary>Shared headless plumbing for the approval overlay/settings render tests.</summary>
internal static class ApprovalHeadlessShell
{
    public static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Drains the dispatcher and renders, twice: layout can post follow-up work that only exists after a pass. The
    /// layout pass is run explicitly: the layout manager's own scheduled pass is a dispatcher post that does not
    /// reliably run under <c>RunJobs</c> once an earlier UI class has torn down its per-assembly session, which left
    /// the window shown but the content never measured (no visual tree). <c>UpdateLayout</c> makes it deterministic.
    /// </summary>
    public static void Settle(Window window)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    public static T? FindById<T>(Window window, string id)
        where T : Control =>
        window.GetVisualDescendants()
            .OfType<T>()
            .FirstOrDefault(control => string.Equals(AutomationProperties.GetAutomationId(control), id, StringComparison.Ordinal));

    public static IReadOnlyList<string> ButtonIds(Window window) =>
        window.GetVisualDescendants()
            .OfType<Button>()
            .Select(static button => AutomationProperties.GetAutomationId(button) ?? string.Empty)
            .Where(static id => id.Length > 0)
            .ToList();

    public static string? TextById(Window window, string id) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(block => string.Equals(AutomationProperties.GetAutomationId(block), id, StringComparison.Ordinal))
            ?.Text;

    /// <summary>Every text line inside a templated button (the toggle is a button, not a text block).</summary>
    public static IReadOnlyList<string> TextsInsideButton(Window window, string id) =>
        FindById<Button>(window, id)?
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(static block => block.Text ?? string.Empty)
            .ToList() ?? [];
}

/// <summary>Holds the temp data root the approval headless tests point the composition paths at.</summary>
internal sealed class ApprovalHeadlessRoot : IAsyncDisposable
{
    private readonly (string? Data, string? General, string? Workspace) saved;

    private ApprovalHeadlessRoot(string root, (string? Data, string? General, string? Workspace) saved)
    {
        Root = root;
        this.saved = saved;
    }

    public string Root { get; }

    public string RulesPath => Path.Combine(Root, "data", ApprovalRules.FileName);

    public static ApprovalHeadlessRoot Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-approval-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "workspace"));
        var saved = (
            Data: Environment.GetEnvironmentVariable(AppPaths.DataVariable),
            General: Environment.GetEnvironmentVariable(AppPaths.GeneralVariable),
            Workspace: Environment.GetEnvironmentVariable(AppPaths.WorkspaceVariable));
        Environment.SetEnvironmentVariable(AppPaths.DataVariable, Path.Combine(root, "data"));
        Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, Path.Combine(root, "general"));
        Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, Path.Combine(root, "workspace"));
        return new ApprovalHeadlessRoot(root, saved);
    }

    public async ValueTask DisposeAsync()
    {
        if (ApprovalServices.Current is { } services)
        {
            await services.DisposeAsync();
        }

        Environment.SetEnvironmentVariable(AppPaths.DataVariable, saved.Data);
        Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, saved.General);
        Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, saved.Workspace);
        ApprovalFixtures.DeleteTree(Root);
    }
}

/// <summary>
/// Headless render of the approval queue overlay and the extensions settings section. The overlay resolves the
/// shell's <see cref="ApprovalServices"/> from its own constructor, so the tests build the real services over a
/// throw-away data root instead of the full composition. The class joins the shared non-parallel
/// <c>forge-app-ui</c> collection (a private collection added a second headless lineage to the process).
/// </summary>
[Collection("forge-app-ui")]
public sealed class ApprovalHeadlessTests : IClassFixture<ApprovalHeadlessFixture>
{
    private readonly ApprovalHeadlessFixture fixture;

    public ApprovalHeadlessTests(ApprovalHeadlessFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Empty_queue_renders_no_overlay()
    {
        var session = fixture.Session;
        await using var root = ApprovalHeadlessRoot.Create();
        var token = new CancellationTokenSource(ApprovalHeadlessShell.StepBudget).Token;

        var visible = await session.Dispatch(
            async () =>
            {
                var services = ApprovalServices.Create(AppPaths.Create(), host: null, hostProblem: default);
                try
                {
                    var overlay = new ApprovalQueueOverlay();
                    var window = new Window { Width = 800, Height = 600, Content = overlay };
                    window.Show();
                    ApprovalHeadlessShell.Settle(window);
                    var shown = overlay.IsVisible;
                    window.Close();
                    return shown;
                }
                finally
                {
                    await services.DisposeAsync();
                }
            },
            token);

        Assert.False(visible);
    }

    [Fact]
    public async Task Overlay_renders_a_tool_card_and_an_extension_card()
    {
        var session = fixture.Session;
        await using var root = ApprovalHeadlessRoot.Create();
        var token = new CancellationTokenSource(ApprovalHeadlessShell.StepBudget).Token;

        var found = await session.Dispatch(
            async () =>
            {
                var services = ApprovalServices.Create(AppPaths.Create(), host: null, hostProblem: default);
                try
                {
                    var overlay = new ApprovalQueueOverlay();
                    var window = new Window { Width = 800, Height = 600, Content = overlay };
                    window.Show();

                    var dispatcher = services.Queue.Dispatcher;
                    Assert.True(await services.Queue.EnqueueAsync(
                        new ToolApprovalCardViewModel(
                            new ToolApprovalCardRequest("s1", "Session s1", "p1", "shell_run", ToolSideEffect.Execute, "/tmp", "{\"a\":1}"),
                            dispatcher),
                        CancellationToken.None));
                    Assert.True(await services.Queue.EnqueueAsync(
                        new ExtensionApprovalCardViewModel(
                            ApprovalFixtures.Staged("plug"), "s1", "Session s1", fromPreviousRun: false, dispatcher),
                        CancellationToken.None));

                    ApprovalHeadlessShell.Settle(window);
                    var ids = ApprovalHeadlessShell.ButtonIds(window);
                    var result = (
                        Shown: overlay.IsVisible,
                        Ids: ids,
                        CardTitle: ApprovalHeadlessShell.TextById(window, "approval-card-title"),
                        ExtensionTitle: ApprovalHeadlessShell.TextById(window, "approval-extension-title"));
                    window.Close();
                    return result;
                }
                finally
                {
                    await services.DisposeAsync();
                }
            },
            token);

        Assert.True(found.Shown);
        Assert.Contains("approval-allow", found.Ids);
        Assert.Contains("approval-remember-project", found.Ids);
        Assert.Contains("approval-extension-approve", found.Ids);
        Assert.Contains("approval-queue-toggle", found.Ids);
        Assert.NotEmpty(found.CardTitle ?? string.Empty);
        Assert.NotEmpty(found.ExtensionTitle ?? string.Empty);
    }

    [Fact]
    public async Task Settings_section_renders_all_three_blocks()
    {
        var session = fixture.Session;
        await using var root = ApprovalHeadlessRoot.Create();
        var token = new CancellationTokenSource(ApprovalHeadlessShell.StepBudget).Token;

        var rendered = await session.Dispatch(
            async () =>
            {
                var rules = new ApprovalRules(root.RulesPath);
                try
                {
                    rules.Allow(ApprovalRuleKey.ForSession("shell_run", ToolSideEffect.Execute, "s1"));
                    var admin = new FakeAdmin
                    {
                        Snapshot = new ExtensionSnapshot(
                            [new ExtensionRow(ApprovalFixtures.Installed("running"), ["tool_run"])],
                            [ApprovalFixtures.Staged("fresh")],
                            null),
                    };
                    var section = new ExtensionsSettingsSectionViewModel(
                        admin,
                        new FakeHostState(),
                        rules,
                        (_, _) => Task.FromResult(true),
                        UiThreadDispatcher.Instance);
                    try
                    {
                        await section.RefreshAsync(CancellationToken.None);
                        var view = new ExtensionsSettingsView { DataContext = section };
                        var window = new Window { Width = 800, Height = 600, Content = view };
                        window.Show();

                        ApprovalHeadlessShell.Settle(window);
                        var installed = ApprovalHeadlessShell.TextById(window, "extensions-installed-title");
                        var pending = ApprovalHeadlessShell.TextById(window, "extensions-pending-title");
                        var ruleBlock = ApprovalHeadlessShell.TextById(window, "extensions-rules-title");
                        var ids = ApprovalHeadlessShell.ButtonIds(window);

                        window.Close();
                        return (Installed: installed, Pending: pending, Rules: ruleBlock, Ids: ids);
                    }
                    finally
                    {
                        section.Dispose();
                    }
                }
                finally
                {
                    await rules.DisposeAsync();
                }
            },
            token);

        Assert.Equal(Loc.Instance[ExtensionsStrings.SettingsInstalled], rendered.Installed);
        Assert.Equal(Loc.Instance[ExtensionsStrings.SettingsPending], rendered.Pending);
        Assert.Equal(Loc.Instance[ExtensionsStrings.SettingsRules], rendered.Rules);
        Assert.Contains("extensions-disable", rendered.Ids);
        Assert.Contains("extensions-open-card", rendered.Ids);
        Assert.Contains("extensions-revoke", rendered.Ids);
    }

    [Fact]
    public async Task Settings_section_without_a_host_shows_unavailable_instead_of_an_empty_list()
    {
        var session = fixture.Session;
        await using var root = ApprovalHeadlessRoot.Create();
        var token = new CancellationTokenSource(ApprovalHeadlessShell.StepBudget).Token;

        var rendered = await session.Dispatch(
            async () =>
            {
                var rules = new ApprovalRules(root.RulesPath);
                try
                {
                    var section = new ExtensionsSettingsSectionViewModel(
                        admin: null,
                        new FakeHostState { HasHost = false, HostProblem = "gone" },
                        rules,
                        (_, _) => Task.FromResult(true),
                        UiThreadDispatcher.Instance);
                    try
                    {
                        await section.RefreshAsync(CancellationToken.None);
                        var view = new ExtensionsSettingsView { DataContext = section };
                        var window = new Window { Width = 800, Height = 600, Content = view };
                        window.Show();
                        ApprovalHeadlessShell.Settle(window);

                        var notice = window.GetVisualDescendants()
                            .OfType<TextBlock>()
                            .FirstOrDefault(block => block.Text == section.UnavailableText);
                        var noInstalled = window.GetVisualDescendants()
                            .OfType<TextBlock>()
                            .FirstOrDefault(block => block.Text == Loc.Instance[ExtensionsStrings.SettingsNoInstalled]);
                        var shown = (NoticeVisible: notice?.IsVisible ?? false, EmptyVisible: noInstalled?.IsVisible ?? true);
                        window.Close();
                        return shown;
                    }
                    finally
                    {
                        section.Dispose();
                    }
                }
                finally
                {
                    await rules.DisposeAsync();
                }
            },
            token);

        Assert.True(rendered.NoticeVisible);
        Assert.False(rendered.EmptyVisible);
    }

    /// <summary>
    /// Language switching over the approval overlay, on the class's single shared session. It runs with the only
    /// open window: every earlier window is closed and disposed before it runs, and the process-global string
    /// table is restored afterwards.
    /// </summary>
    [Fact]
    public async Task Switching_language_refreshes_the_overlay_texts()
    {
        // Drop every closed window from the earlier tests before touching the process-global string table.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var session = fixture.Session;
        await using var root = ApprovalHeadlessRoot.Create();
        var token = new CancellationTokenSource(ApprovalHeadlessShell.StepBudget).Token;
        var previous = Loc.Instance.Language;

        try
        {
            var texts = await session.Dispatch(
                async () =>
                {
                    var services = ApprovalServices.Create(AppPaths.Create(), host: null, hostProblem: default);
                    try
                    {
                    var dispatcher = services.Queue.Dispatcher;
                    Assert.True(await services.Queue.EnqueueAsync(
                        new ToolApprovalCardViewModel(
                            new ToolApprovalCardRequest("s1", "Session s1", "p1", "shell_run", ToolSideEffect.Execute, "/tmp", "{}"),
                            dispatcher),
                        CancellationToken.None));

                    var overlay = new ApprovalQueueOverlay();
                    var window = new Window { Width = 800, Height = 600, Content = overlay };
                    window.Show();

                    Loc.Instance.Language = UiLanguage.English;
                    ApprovalHeadlessShell.Settle(window);
                    var englishToggle = ApprovalHeadlessShell.TextsInsideButton(window, "approval-queue-toggle");
                    var englishButtons = ApprovalHeadlessShell.ButtonIds(window);

                    Loc.Instance.Language = UiLanguage.Chinese;
                    ApprovalHeadlessShell.Settle(window);
                    var chineseToggle = ApprovalHeadlessShell.TextsInsideButton(window, "approval-queue-toggle");
                    var chineseButtons = ApprovalHeadlessShell.ButtonIds(window);

                    window.Close();
                    return (EnglishToggle: englishToggle, EnglishButtons: englishButtons, ChineseToggle: chineseToggle, ChineseButtons: chineseButtons);
                    }
                    finally
                    {
                        await services.DisposeAsync();
                    }
                },
                token);

            Assert.Contains("Collapse", texts.EnglishToggle);
            Assert.Contains("收起", texts.ChineseToggle);
            Assert.Contains("approval-queue-toggle", texts.EnglishButtons);
            Assert.Contains("approval-allow", texts.ChineseButtons);
        }
        finally
        {
            await session.Dispatch(
                () =>
                {
                    Loc.Instance.Language = previous;
                    return 0;
                },
                new CancellationTokenSource(ApprovalHeadlessShell.StepBudget).Token);
        }
    }
}

/// <summary>
/// The approval UI class runs on the process-wide session owned by the <c>forge-app-ui</c> collection.
/// A class-level session of its own would keep a second <see cref="Application"/> alive on a second UI thread
/// next to the collection's, and a control that outlives its own session would then be notified from that other
/// thread (<c>VerifyAccess</c> failures in <c>KeyBinding</c>/transitions).
/// </summary>
public sealed class ApprovalHeadlessFixture : IAsyncLifetime
{
    public HeadlessUnitTestSession Session => SharedUi.Session;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;
}
