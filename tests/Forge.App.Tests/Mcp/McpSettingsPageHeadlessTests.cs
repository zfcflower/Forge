using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.ViewModels;
using Forge.App.ViewModels.Settings;
using Forge.App.Views;
using Forge.App.Tests.Foundation;
using Xunit;

namespace Forge.App.Tests.Mcp;

/// <summary>
/// Headless render of the <c>mcp</c> settings section: the section's app-level data template draws
/// <see cref="McpSettingsView"/> around <see cref="McpSettingsSectionViewModel"/>, the action bar and the four parameter
/// editors carry their automation ids, and the page survives a language switch. The window's composition reads the
/// developer's real <c>~/.forge/mcp.json</c> (read-only), so these tests never assert server-row counts or text and never
/// touch a write path (save / delete / import / enable toggles).
/// </summary>
[Collection("forge-app-ui")]
public sealed class McpSettingsPageHeadlessTests : IClassFixture<SettingsPanelFixture>
{
    private static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(60);

    /// <summary>How many render ticks the nav column may take to realize its buttons; the click reports a miss if it never does.</summary>
    private const int PanelWaitAttempts = 200;

    private readonly SettingsPanelFixture fixture;

    public McpSettingsPageHeadlessTests(SettingsPanelFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Selecting_the_mcp_section_renders_McpSettingsView_bound_to_the_section_view_model()
    {
        var session = fixture.Session;
        await using var shell = await SettingsShell.OpenAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;

        Assert.True(await ClickMcpAsync(session, shell.Window, token), "the mcp nav button must be rendered in the section column");

        var (selectedId, views) = await session.Dispatch(
            () => (
                shell.ViewModel.Settings.SelectedSection?.Id,
                shell.Window.GetVisualDescendants().OfType<McpSettingsView>().ToList()),
            token);

        Assert.Equal(SettingsSectionIds.Mcp, selectedId);
        var view = Assert.Single(views);
        Assert.IsType<McpSettingsSectionViewModel>(view.DataContext);
    }

    [Fact]
    public async Task The_action_bar_exposes_the_add_import_presets_and_reload_buttons()
    {
        var session = fixture.Session;
        await using var shell = await SettingsShell.OpenAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;

        Assert.True(await ClickMcpAsync(session, shell.Window, token), "the mcp nav button must be rendered in the section column");

        var ids = new[] { "mcp-add", "mcp-import", "mcp-presets", "mcp-reload" };
        var found = await session.Dispatch(
            () => ids.Where(id => FindById<Button>(shell.Window, id) is not null).ToArray(),
            token);

        Assert.Equal(ids, found);
    }

    [Fact]
    public async Task The_parameter_block_exposes_the_four_numeric_editors()
    {
        var session = fixture.Session;
        await using var shell = await SettingsShell.OpenAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;

        Assert.True(await ClickMcpAsync(session, shell.Window, token), "the mcp nav button must be rendered in the section column");

        var ids = new[] { "mcp-request-timeout", "mcp-reconnect-delay", "mcp-tool-limit", "mcp-stderr-lines" };
        var found = await session.Dispatch(
            () => ids.Where(id => FindById<NumericUpDown>(shell.Window, id) is not null).ToArray(),
            token);

        Assert.Equal(ids, found);
    }

    [Fact]
    public async Task The_mcp_page_rerenders_after_the_ui_language_switches()
    {
        var session = fixture.Session;
        await using var shell = await SettingsShell.OpenAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;
        var previous = Loc.Instance.Language;

        try
        {
            Assert.True(await ClickMcpAsync(session, shell.Window, token), "the mcp nav button must be rendered in the section column");

            await session.Dispatch(
                () =>
                {
                    Loc.Instance.Language = UiLanguage.Chinese;
                    SharedUi.Settle(shell.Window);
                    return 0;
                },
                token);

            var (addButton, views) = await session.Dispatch(
                () => (
                    FindById<Button>(shell.Window, "mcp-add"),
                    shell.Window.GetVisualDescendants().OfType<McpSettingsView>().Count()),
                token);

            Assert.NotNull(addButton);
            Assert.Equal(1, views);
        }
        finally
        {
            // The process-wide Loc table is shared with the other UI suites; leave it as it was.
            await session.Dispatch(
                () =>
                {
                    Loc.Instance.Language = previous;
                    SharedUi.Settle(shell.Window);
                    return 0;
                },
                token);
        }
    }

    private static Task<bool> ClickMcpAsync(HeadlessUnitTestSession session, Window window, CancellationToken token) =>
        session.Dispatch(
            () =>
            {
                var button = FindById<Button>(window, SettingsSectionIds.Mcp);
                if (button is null)
                {
                    return false;
                }

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                SharedUi.Settle(window);
                return true;
            },
            token);

    private static T? FindById<T>(Window window, string id)
        where T : Control =>
        window.GetVisualDescendants()
            .OfType<T>()
            .FirstOrDefault(control => string.Equals(AutomationProperties.GetAutomationId(control), id, StringComparison.Ordinal));

    /// <summary>
    /// The standalone settings window over a throw-away data root, the same shape as the settings-panel suite's shell: the
    /// environment variables are redirected for its lifetime and restored in teardown, and the window closes before the
    /// composition releases its handles. The composition still reads the real <c>~/.forge/mcp.json</c>, which is why this
    /// suite asserts only controls that exist regardless of the configured servers.
    /// </summary>
    private sealed class SettingsShell : IAsyncDisposable
    {
        private readonly HeadlessUnitTestSession session;
        private readonly string root;
        private readonly EnvironmentSnapshot saved;

        private SettingsShell(
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

        public static async Task<SettingsShell> OpenAsync(HeadlessUnitTestSession session)
        {
            var (root, previous) = TempDataRoot.Redirect("forge-mcp-settings-");

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

                var shell = new SettingsShell(session, root, previous, composition, viewModel, window);
                await shell.WaitForStartupAsync();
                await shell.WaitForNavAsync();
                return shell;
            }
            catch (Exception)
            {
                previous.Restore();
                TestTempFiles.DeleteTree(root);
                throw;
            }
        }

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

        /// <summary>
        /// Waits for <see cref="AppComposition.Initialization"/>: it ends by pushing the stored language into
        /// <see cref="Loc"/>, and a test that switched the language before that landed would be racing the push.
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

        /// <summary>Pumps render ticks until the mcp nav entry is realized; item containers appear after a layout pass.</summary>
        private async Task WaitForNavAsync()
        {
            var token = new CancellationTokenSource(StepBudget).Token;
            await session.Dispatch(
                () =>
                {
                    for (var attempt = 0; attempt < PanelWaitAttempts; attempt++)
                    {
                        SharedUi.Settle(Window);
                        if (FindById<Button>(Window, SettingsSectionIds.Mcp) is not null)
                        {
                            break;
                        }
                    }

                    return 0;
                },
                token);
        }
    }
}
