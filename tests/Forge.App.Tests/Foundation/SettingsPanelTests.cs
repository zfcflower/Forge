using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.Foundation;

/// <summary>
/// Headless render of the settings page (forge-app spec, "偏好、文案与设置分区"): the page body is the section registry and
/// every section's view model is drawn by its app-level data template, so a feature only has to register one. Pins that
/// the two sections the shell owns today (<c>provider</c>, <c>paths</c>) really are on screen, in order and with their
/// bodies rendered - a registry without a template would show an empty box.
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
    public async Task The_settings_panel_renders_the_provider_and_paths_sections_in_order()
    {
        var session = fixture.Session;
        await using var shell = await OpensAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;

        var provided = await session.Dispatch(() => PanelShell.Section(shell.Window, SettingsSectionIds.Provider), token);
        var paths = await session.Dispatch(() => PanelShell.Section(shell.Window, SettingsSectionIds.Paths), token);

        Assert.Equal(Loc.Instance[CoreStrings.SettingsSectionProvider], provided.Title);
        Assert.Equal(Loc.Instance[CoreStrings.SettingsSectionPaths], paths.Title);
        Assert.True(provided.Top < paths.Top, "the provider section must be drawn above the paths section");

        // The provider section is selected by default and its body renders in the right pane.
        var provider = Assert.IsType<ProviderSettingsSectionViewModel>(
            shell.ViewModel.Settings.Sections.Items[0].ViewModel);
        var providerTexts = await session.Dispatch(() => PanelShell.SelectedTexts(shell.Window), token);
        Assert.Contains(provider.Detail, providerTexts);
        Assert.Contains(provider.Hint, providerTexts);

        // Selecting the paths section swaps its body into the right pane.
        await session.Dispatch(
            () =>
            {
                shell.ViewModel.Settings.SelectedSection = shell.ViewModel.Settings.Sections.Items[^1];
                PanelShell.Settle(shell.Window);
                return 0;
            },
            token);
        var pathsTexts = await session.Dispatch(() => PanelShell.SelectedTexts(shell.Window), token);
        Assert.Contains(shell.Composition.Paths.SettingsPath, pathsTexts);
        Assert.Contains(shell.Composition.Paths.GeneralWorkspaceDirectory, pathsTexts);
    }

    [Fact]
    public async Task The_settings_panel_shows_exactly_the_registered_sections()
    {
        var session = fixture.Session;
        await using var shell = await OpensAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;

        var rendered = await session.Dispatch(() => PanelShell.RenderedSectionIds(shell.Window), token);

        Assert.Equal(
            shell.ViewModel.Settings.Sections.Items.Select(static section => section.Id),
            rendered);
        // Features add their own sections in parallel; the shell's two stay at both ends.
        Assert.Equal(SettingsSectionIds.Provider, rendered[0]);
        Assert.Equal(SettingsSectionIds.Paths, rendered[^1]);
        Assert.Contains(SettingsSectionIds.Extensions, rendered);
    }

    [Fact]
    public async Task Switching_the_language_refreshes_the_open_panel()
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
                    PanelShell.Settle(shell.Window);
                    return 0;
                },
                token);

            await SwitchAsync(session, shell.Window, UiLanguage.Chinese, token);

            var chinese = await session.Dispatch(
                () => PanelShell.Read(shell.Window),
                token);
            Assert.Equal("模型提供方", chinese.ProviderTitle);
            Assert.Equal("路径与服务", chinese.PathsTitle);
            Assert.Contains("重新加载设置", chinese.Buttons);
            Assert.Contains("数据目录", chinese.Texts);

            await SwitchAsync(session, shell.Window, UiLanguage.English, token);

            var english = await session.Dispatch(
                () => PanelShell.Read(shell.Window),
                token);
            Assert.Equal("Model provider", english.ProviderTitle);
            Assert.Equal("Paths and services", english.PathsTitle);
            Assert.Contains("Reload settings", english.Buttons);
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
                PanelShell.Settle(window);
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
                shell.ViewModel.Settings.IsOpen = true;
                for (var attempt = 0; attempt < PanelWaitAttempts; attempt++)
                {
                    PanelShell.Settle(shell.Window);
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
    /// The real shell over a throw-away data root: the environment variables are set for its lifetime and restored
    /// afterwards, and teardown closes the window before the composition root releases its handles.
    /// </summary>
    private sealed class PanelShell : IAsyncDisposable
    {
        private const int SettlePasses = 2;

        private readonly HeadlessUnitTestSession session;
        private readonly string root;
        private readonly (string? Data, string? General, string? Workspace) saved;

        private PanelShell(
            HeadlessUnitTestSession session,
            string root,
            (string? Data, string? General, string? Workspace) saved,
            AppComposition composition,
            MainWindowViewModel viewModel,
            MainWindow window)
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

        public MainWindow Window { get; }

        public static async Task<PanelShell> OpenAsync(HeadlessUnitTestSession session)
        {
            var root = Path.Combine(Path.GetTempPath(), "forge-settings-panel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "workspace"));
            var saved = (
                Data: Environment.GetEnvironmentVariable(AppPaths.DataVariable),
                General: Environment.GetEnvironmentVariable(AppPaths.GeneralVariable),
                Workspace: Environment.GetEnvironmentVariable(AppPaths.WorkspaceVariable));
            Environment.SetEnvironmentVariable(AppPaths.DataVariable, Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, Path.Combine(root, "general"));
            Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, Path.Combine(root, "workspace"));

            try
            {
                var (composition, viewModel, window) = await session.Dispatch(
                    () =>
                    {
                        var composed = AppComposition.Create();
                        var model = new MainWindowViewModel(composed);
                        var shell = new MainWindow { DataContext = model };
                        shell.Show();
                        Settle(shell);
                        return (composed, model, shell);
                    },
                    new CancellationTokenSource(StepBudget).Token);

                var panel = new PanelShell(session, root, saved, composition, viewModel, window);
                await panel.WaitForStartupAsync();
                return panel;
            }
            catch (Exception)
            {
                Restore(saved);
                DeleteTree(root);
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

        /// <summary>One nav entry: its heading and where it sits in the left column.</summary>
        public static (string? Title, double Top) Section(Window window, string id)
        {
            var heading = Heading(window, id);
            var origin = heading?.TranslatePoint(new Point(0, 0), window);
            return (heading?.Text, origin?.Y ?? double.NaN);
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

        /// <summary>Everything the open panel shows, read in one UI dispatch.</summary>
        public static (string? ProviderTitle, string? PathsTitle, IReadOnlyList<string> Buttons, IReadOnlyList<string> Texts) Read(Window window) =>
            (
                Section(window, SettingsSectionIds.Provider).Title,
                Section(window, SettingsSectionIds.Paths).Title,
                window.GetVisualDescendants()
                    .OfType<Button>()
                    .Select(static button => button.Content as string)
                    .Where(static text => !string.IsNullOrEmpty(text))
                    .Select(static text => text!)
                    .ToList(),
                SelectedTexts(window));

        public static IReadOnlyList<string> RenderedSectionIds(Window window) =>
            window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(static block => AutomationProperties.GetAutomationId(block) is { } id && KnownSectionIds.Contains(id))
                .OrderBy(block => block.TranslatePoint(new Point(0, 0), window)?.Y ?? 0)
                .Select(static block => AutomationProperties.GetAutomationId(block)!)
                .ToList();

        /// <summary>Drains the dispatcher and renders, twice: layout can post follow-up work that only exists after a pass.</summary>
        public static void Settle(Window window)
        {
            for (var pass = 0; pass < SettlePasses; pass++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            Dispatcher.UIThread.RunJobs();
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
                Restore(saved);
                DeleteTree(root);
            }
        }

        // Every reserved section id, so the parallel feature tasks need not edit this list as they land.
        private static readonly string[] KnownSectionIds =
        [
            SettingsSectionIds.Provider,
            SettingsSectionIds.Appearance,
            SettingsSectionIds.Language,
            SettingsSectionIds.Terminal,
            SettingsSectionIds.Git,
            SettingsSectionIds.Extensions,
            SettingsSectionIds.Paths,
        ];

        private static TextBlock? Heading(Window window, string id) =>
            window.GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(block => string.Equals(AutomationProperties.GetAutomationId(block), id, StringComparison.Ordinal));

        private static void Restore((string? Data, string? General, string? Workspace) saved)
        {
            Environment.SetEnvironmentVariable(AppPaths.DataVariable, saved.Data);
            Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, saved.General);
            Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, saved.Workspace);
        }

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
}

/// <summary>
/// One headless session for the whole test class. Every session sets up its own headless platform and render loop, and
/// starting one per test multiplies the exposure to the platform's cross-session startup race; sharing it also keeps every
/// window on one UI thread, so a closed window cannot be notified from another thread.
/// </summary>
public sealed class SettingsPanelFixture : IAsyncLifetime
{
    private HeadlessUnitTestSession? session;

    public HeadlessUnitTestSession Session =>
        session ?? throw new InvalidOperationException("the headless session is not running");

    public Task InitializeAsync()
    {
        // Per-assembly isolation: the class shares one session, so the headless application is built once instead of
        // once per dispatch (the per-dispatch app builder is where the headless compositor and render loop race).
        session = HeadlessUnitTestSession.StartNew(typeof(App), AvaloniaTestIsolationLevel.PerAssembly);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (session is not null)
        {
            await session.DisposeAsync();
            session = null;
        }
    }
}
