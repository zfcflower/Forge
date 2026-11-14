using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.Foundation;

/// <summary>
/// Headless render of the standalone settings window: the left column is the grouped registry
/// (<c>AGENT</c> / <c>界面</c> / <c>系统</c>) and every section's view model is drawn by its app-level data template.
/// Rewritten for the multi-provider window: the provider row is <c>ProvidersSettingsViewModel</c> (title
/// "提供商"/"Providers"), the window is separate from the main shell, and the nav order is grouped rather than flat.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SettingsPanelTests : IClassFixture<SettingsPanelFixture>
{
    private static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(60);

    /// <summary>How long the panel may take to realize its items; the assertions report what is missing if it never does.</summary>
    private const int PanelWaitAttempts = 200;

    private readonly SettingsPanelFixture fixture;

    public SettingsPanelTests(SettingsPanelFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task The_settings_window_renders_the_provider_and_paths_sections_in_grouped_order()
    {
        var session = fixture.Session;
        await using var shell = await OpensAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;

        var provided = await session.Dispatch(() => PanelShell.Section(shell.Window, SettingsSectionIds.Provider), token);
        var paths = await session.Dispatch(() => PanelShell.Section(shell.Window, SettingsSectionIds.Paths), token);

        Assert.Equal(Loc.Instance[Forge.App.Localization.ProviderStrings.SectionTitleKey], provided.Title);
        Assert.Equal(Loc.Instance[CoreStrings.SettingsSectionPaths], paths.Title);
        Assert.True(provided.Top < paths.Top, "the provider section must be drawn above the paths section");

        // The provider section is selected by default and its body renders in the right pane.
        var providerSection = shell.ViewModel.Settings.Sections.Items[0];
        Assert.Equal(SettingsSectionIds.Provider, providerSection.Id);
        var providerModel = Assert.IsType<ProvidersSettingsViewModel>(providerSection.ViewModel);
        var providerTexts = await session.Dispatch(() => PanelShell.SelectedTexts(shell.Window), token);
        Assert.Contains(providerModel.BuiltInTitle, providerTexts);
        Assert.Contains(providerModel.ConnectedTitle, providerTexts);

        // Selecting the paths section swaps its body into the right pane.
        await session.Dispatch(
            () =>
            {
                shell.ViewModel.Settings.SelectedSection = shell.ViewModel.Settings.Sections.Items[^1];
                SharedUi.Settle(shell.Window);
                return 0;
            },
            token);
        var pathsTexts = await session.Dispatch(() => PanelShell.SelectedTexts(shell.Window), token);
        Assert.Contains(shell.Composition.Paths.SettingsPath, pathsTexts);
        Assert.Contains(shell.Composition.Paths.GeneralWorkspaceDirectory, pathsTexts);
    }

    [Theory]
    [InlineData(SettingsSectionIds.ModelsVisibility, "models-visibility-search")]
    [InlineData(SettingsSectionIds.Agents, "agents-max-parallel")]
    [InlineData(SettingsSectionIds.IdleArchive, "idle-archive-days")]
    [InlineData(SettingsSectionIds.NotificationPermission, "notification-permission-state")]
    public async Task A_wave_section_renders_its_own_body(string sectionId, string marker)
    {
        // The registry knows the four sections, but only a view registered through SectionViewTemplate gives them a body: without
        // one the pane stays blank and every registry test still passes, so each section is selected and asked for an element
        // of its own.
        var session = fixture.Session;
        await using var shell = await OpensAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;

        var shown = await session.Dispatch(
            () =>
            {
                var section = shell.ViewModel.Settings.Sections.Items.Single(item => item.Id == sectionId);
                shell.ViewModel.Settings.SelectedSection = section;
                SharedUi.Settle(shell.Window);
                return PanelShell.SelectedAutomationIds(shell.Window);
            },
            token);

        Assert.Contains(marker, shown);
    }

    [Fact]
    public async Task A_wave_section_body_binds_the_section_as_its_data_context()
    {
        // A registered view with no data context would still show its AutomationIds, so one section is asked for text it can
        // only render from its view model.
        var session = fixture.Session;
        await using var shell = await OpensAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;

        var (expected, shown) = await session.Dispatch(
            () =>
            {
                var section = shell.ViewModel.Settings.Sections.Items.Single(
                    item => item.Id == SettingsSectionIds.NotificationPermission);
                shell.ViewModel.Settings.SelectedSection = section;
                SharedUi.Settle(shell.Window);
                var viewModel = Assert.IsType<NotificationPermissionSettingsViewModel>(section.ViewModel);
                return (viewModel.StateText, PanelShell.SelectedTexts(shell.Window));
            },
            token);

        Assert.Contains(expected, shown);
    }

    [Fact]
    public async Task The_settings_window_groups_sections_agent_then_interface_then_system()
    {
        var session = fixture.Session;
        await using var shell = await OpensAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;

        var rendered = await session.Dispatch(() => PanelShell.RenderedSectionIds(shell.Window), token);
        var groups = await session.Dispatch(() => PanelShell.GroupOrder(shell.Window), token);

        // The left column follows SettingsGroups.All, not the flat registry order. The rendered column carries the
        // group titles (they follow the language), so the ids come from the page model and the titles pin the order.
        Assert.Equal(["agent", "interface", "system"], shell.ViewModel.Settings.NavGroups.Select(static group => group.Id));
        Assert.Equal(shell.ViewModel.Settings.NavGroups.Select(static group => group.Title), groups);
        Assert.Equal(
            shell.ViewModel.Settings.NavGroups.SelectMany(static group => group.Items.Select(static item => item.Id)),
            rendered);
        // Features add their own sections in parallel; the shell's two stay at both ends.
        Assert.Equal(SettingsSectionIds.Provider, rendered[0]);
        Assert.Equal(SettingsSectionIds.Paths, rendered[^1]);
        Assert.Contains(SettingsSectionIds.Extensions, rendered);
    }

    [Fact]
    public async Task The_chat_and_files_sections_draw_their_bodies()
    {
        var session = fixture.Session;
        await using var shell = await OpensAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;

        // A section body is built by its data template, which needs a layout pass; a loaded machine needs more than one, so
        // each wait pumps until the section's own hint line appears instead of assuming one tick is enough.
        async Task<string[]> TextsOf(string id, string expected)
        {
            var texts = Array.Empty<string>();
            for (var attempt = 0; attempt < PanelWaitAttempts; attempt++)
            {
                texts = await session.Dispatch(
                    () =>
                    {
                        shell.ViewModel.Settings.SelectedSection =
                            shell.ViewModel.Settings.Sections.Items.First(section => section.Id == id);
                        SharedUi.Settle(shell.Window);
                        return PanelShell.SelectedTexts(shell.Window).ToArray();
                    },
                    token);
                if (texts.Contains(expected, StringComparer.Ordinal))
                {
                    break;
                }
            }

            return texts;
        }

        // A section whose data template is missing draws nothing at all, so the hint line proves the template resolved.
        var chat = await TextsOf(SettingsSectionIds.Chat, Loc.Instance[ChatStrings.SettingsHint]);
        Assert.Contains(Loc.Instance[ChatStrings.SettingsHint], chat);
        Assert.Contains(Loc.Instance[ChatStrings.SettingsSendToolImages], chat);

        var files = await TextsOf(SettingsSectionIds.Files, Loc.Instance[PreviewStrings.SettingsHintKey]);
        Assert.Contains(Loc.Instance[PreviewStrings.SettingsHintKey], files);
        Assert.Contains(Loc.Instance[PreviewStrings.SettingsRowsKey], files);
    }

    [Fact]
    public async Task A_narrow_column_folds_itself_and_the_toggle_overrides_the_width()
    {
        var session = fixture.Session;
        await using var shell = await OpensAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;

        var result = await session.Dispatch(
            () =>
            {
                var page = shell.ViewModel.Settings;

                page.UpdateAvailableWidth(SettingsViewModel.AutoCollapseWidth - 120);
                var folded = page.IsNavCollapsed;
                var foldedWidth = page.NavWidth.Value;
                var titlesHidden = !page.ShowNavTitles;

                // The manual toggle wins over the width rule in both directions: in a narrow window it expands the rail.
                page.ToggleNav();
                var expandedWhileNarrow = !page.IsNavCollapsed;

                // Widening keeps a manual choice: the user asked for this state.
                page.UpdateAvailableWidth(1400);
                var stillExpanded = !page.IsNavCollapsed;

                // Toggling again folds it, and the fold survives widening too.
                page.ToggleNav();
                page.UpdateAvailableWidth(1600);
                return (folded, foldedWidth, titlesHidden, expandedWhileNarrow, stillExpanded, FoldedAgain: page.IsNavCollapsed);
            },
            token);

        Assert.True(result.folded, "a column narrower than the threshold folds itself");
        Assert.Equal(SettingsViewModel.CollapsedNavWidth, result.foldedWidth);
        Assert.True(result.titlesHidden, "the rail hides the section titles");
        Assert.True(result.expandedWhileNarrow, "the toggle expands the column even in a narrow window");
        Assert.True(result.stillExpanded, "widening does not overrule a manual choice");
        Assert.True(result.FoldedAgain, "the second toggle folds it again");
    }

    [Fact]
    public async Task Switching_the_language_refreshes_the_open_window()
    {
        var session = fixture.Session;
        await using var shell = await OpensAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;
        var previous = Loc.Instance.Language;

        try
        {
            // Select the paths section so its rows are the ones in the right pane.
            await session.Dispatch(
                () =>
                {
                    shell.ViewModel.Settings.SelectedSection = shell.ViewModel.Settings.Sections.Items[^1];
                    SharedUi.Settle(shell.Window);
                    return 0;
                },
                token);

            await SwitchAsync(session, shell.Window, UiLanguage.Chinese, token);

            var chinese = await session.Dispatch(
                () => PanelShell.Read(shell.Window),
                token);
            Assert.Equal("提供商", chinese.ProviderTitle);
            Assert.Equal("路径与服务", chinese.PathsTitle);
            Assert.Contains("AGENT", chinese.Groups);
            Assert.Contains("界面", chinese.Groups);
            Assert.Contains("系统", chinese.Groups);
            Assert.Contains("数据目录", chinese.Texts);

            await SwitchAsync(session, shell.Window, UiLanguage.English, token);

            var english = await session.Dispatch(
                () => PanelShell.Read(shell.Window),
                token);
            Assert.Equal("Providers", english.ProviderTitle);
            Assert.Equal("Paths and services", english.PathsTitle);
            Assert.Contains("AGENT", english.Groups);
            Assert.Contains("INTERFACE", english.Groups);
            Assert.Contains("SYSTEM", english.Groups);
            Assert.Contains("Data directory", english.Texts);
        }
        finally
        {
            await SwitchAsync(session, shell.Window, previous, token);
        }
    }

    private static Task SwitchAsync(
        HeadlessUnitTestSession session,
        Window window,
        UiLanguage language,
        CancellationToken token) =>
        session.Dispatch(
            () =>
            {
                Loc.Instance.Language = language;
                SharedUi.Settle(window);
                return 0;
            },
            token);

    private static async Task<PanelShell> OpensAsync(HeadlessUnitTestSession session)
    {
        var shell = await PanelShell.OpenAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;

        // One dispatch with an in-thread pump loop: item containers are only realized by a layout pass, and a loaded
        // machine can need more than one render tick.
        await session.Dispatch(
            () =>
            {
                for (var attempt = 0; attempt < PanelWaitAttempts; attempt++)
                {
                    SharedUi.Settle(shell.Window);
                    if (PanelShell.RenderedSectionIds(shell.Window).Count == shell.ViewModel.Settings.Sections.Items.Count)
                    {
                        break;
                    }
                }

                return 0;
            },
            token);

        return shell;
    }

    /// <summary>
    /// The standalone settings window over a throw-away data root: the environment variables are set for its lifetime
    /// and restored afterwards, and teardown closes the window before the composition root releases its handles.
    /// </summary>
    private sealed class PanelShell : IAsyncDisposable
    {
        private const int SettlePasses = 2;

        private readonly HeadlessUnitTestSession session;
        private readonly string root;
        private readonly EnvironmentSnapshot saved;

        private PanelShell(
            HeadlessUnitTestSession session,
            string root,
            EnvironmentSnapshot saved,
            AppComposition composition,
            MainWindowViewModel viewModel,
            SettingsWindow window)
        {
            this.session = session;
            this.root = root;
            this.saved = saved;
            Composition = composition;
            ViewModel = viewModel;
            Window = window;
        }

        public AppComposition Composition { get; }

        public MainWindowViewModel ViewModel { get; }

        public SettingsWindow Window { get; }

        public static async Task<PanelShell> OpenAsync(HeadlessUnitTestSession session)
        {
            var (root, previous) = TempDataRoot.Redirect("forge-settings-panel-");

            try
            {
                var (composition, viewModel, window) = await session.Dispatch(
                    () =>
                    {
                        var composed = AppComposition.Create();
                        var model = new MainWindowViewModel(composed);
                        var settings = new SettingsWindow(model.Settings);
                        settings.Show();
                        SharedUi.Settle(settings);
                        return (composed, model, settings);
                    },
                    new CancellationTokenSource(StepBudget).Token);

                var panel = new PanelShell(session, root, previous, composition, viewModel, window);
                await panel.WaitForStartupAsync();
                return panel;
            }
            catch (Exception)
            {
                previous.Restore();
                TestTempFiles.DeleteTree(root);
                throw;
            }
        }

        /// <summary>
        /// Waits for <see cref="AppComposition.Initialization"/>: it ends by pushing the stored language into
        /// <see cref="Loc"/>, and a test that switched the language before that landed would be racing the push.
        /// The dispatcher only runs while something dispatches to it, so the wait pumps a no-op job.
        /// </summary>
        private async Task WaitForStartupAsync()
        {
            var token = new CancellationTokenSource(StepBudget).Token;

            // An async dispatch pushes a frame, so the startup continuations run on this dispatcher and the task
            // completes inside one dispatch instead of one dispatch per poll.
            await session.Dispatch(
                async () =>
                {
                    await Composition.Initialization;
                    return 0;
                },
                token);
        }

        /// <summary>One nav entry: its title and where it sits in the left column.</summary>
        public static (string? Title, double Top) Section(Window window, string id)
        {
            var button = NavButton(window, id);
            var title = (button?.DataContext as SettingsNavItemViewModel)?.Title;
            var origin = button?.TranslatePoint(new Point(0, 0), window);
            return (title, origin?.Y ?? double.NaN);
        }

        /// <summary>The text lines of the section body shown in the right pane (the selected section).</summary>
        public static IReadOnlyList<string> SelectedTexts(Window window)
        {
            var body = window.GetVisualDescendants()
                .FirstOrDefault(control => string.Equals(AutomationProperties.GetAutomationId(control), "settings-body", StringComparison.Ordinal));
            return body is null
                ? []
                : body.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Select(static block => block.Text ?? string.Empty)
                    .ToList();
        }

        /// <summary>The automation ids inside the section body, so a test can prove which section rendered what.</summary>
        public static IReadOnlyList<string> SelectedAutomationIds(Window window)
        {
            var body = window.GetVisualDescendants()
                .FirstOrDefault(control => string.Equals(AutomationProperties.GetAutomationId(control), "settings-body", StringComparison.Ordinal));
            return body is null
                ? []
                : body.GetVisualDescendants()
                    .Select(AutomationProperties.GetAutomationId)
                    .Where(static id => !string.IsNullOrEmpty(id))
                    .Select(static id => id!)
                    .ToList();
        }

        /// <summary>Everything the open window shows, read in one UI dispatch.</summary>
        public static (string? ProviderTitle, string? PathsTitle, IReadOnlyList<string> Groups, IReadOnlyList<string> Texts) Read(Window window) =>
            (
                Section(window, SettingsSectionIds.Provider).Title,
                Section(window, SettingsSectionIds.Paths).Title,
                GroupOrder(window),
                SelectedTexts(window));

        public static IReadOnlyList<string> RenderedSectionIds(Window window) =>
            window.GetVisualDescendants()
                .OfType<Button>()
                .Where(static button => AutomationProperties.GetAutomationId(button) is { } id && KnownSectionIds.Contains(id))
                .OrderBy(button => button.TranslatePoint(new Point(0, 0), window)?.Y ?? 0)
                .Select(static button => AutomationProperties.GetAutomationId(button)!)
                .ToList();

        public static IReadOnlyList<string> GroupOrder(Window window) =>
            window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(static block => block.Text ?? string.Empty)
                .Where(static text => text is "AGENT" or "界面" or "INTERFACE" or "系统" or "SYSTEM")
                .Distinct()
                .ToList();


        public async ValueTask DisposeAsync()
        {
            try
            {
                await session.Dispatch(
                    () =>
                    {
                        Window.Close();
                        return 0;
                    },
                    new CancellationTokenSource(StepBudget).Token);
                await session.Dispatch(
                    () =>
                    {
                        ViewModel.Dispose();
                        return 0;
                    },
                    new CancellationTokenSource(StepBudget).Token);
                await Composition.DisposeAsync();
            }
            finally
            {
                saved.Restore();
                TestTempFiles.DeleteTree(root);
            }
        }

        // Every reserved section id, so the parallel feature tasks need not edit this list as they land.
        private static readonly string[] KnownSectionIds =
        [
            SettingsSectionIds.Provider,
            SettingsSectionIds.Automations,
            SettingsSectionIds.Prompts,
            Forge.App.Web.WebSettingsSectionViewModel.SectionId,
            SettingsSectionIds.Appearance,
            SettingsSectionIds.Chat,
            SettingsSectionIds.Language,
            SettingsSectionIds.Editor,
            SettingsSectionIds.Files,
            SettingsSectionIds.Terminal,
            SettingsSectionIds.Git,
            SettingsSectionIds.Extensions,
            SettingsSectionIds.Approvals,
            SettingsSectionIds.Skills,
            SettingsSectionIds.Mcp,
            SettingsSectionIds.Tools,
            SettingsSectionIds.Notifications,
            SettingsSectionIds.Network,
            SettingsSectionIds.ModelsVisibility,
            SettingsSectionIds.Agents,
            SettingsSectionIds.IdleArchive,
            SettingsSectionIds.NotificationPermission,
            SettingsSectionIds.Paths,
        ];

        private static Button? NavButton(Window window, string id) =>
            window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(button => string.Equals(AutomationProperties.GetAutomationId(button), id, StringComparison.Ordinal));

    }
}

/// <summary>
/// The settings-panel class runs on the process-wide session owned by the <c>forge-app-ui</c> collection, the same
/// one every other UI class uses. Its own per-assembly session would keep a second <see cref="Application"/> alive
/// on a second UI thread next to the collection's, and a control created under either one is then notified from the
/// other thread (<c>VerifyAccess</c> failures in <c>KeyBinding</c>/transitions).
/// </summary>
public sealed class SettingsPanelFixture : IAsyncLifetime
{
    public HeadlessUnitTestSession Session => SharedUi.Session;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;
}
