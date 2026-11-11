using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Forge.App.Composition;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins <see cref="GitSettingsSectionViewModel"/>: the picker reflects and edits the preferences (with
/// clamping), its labels are re-read after a language switch, and the section is registered with id
/// <c>git</c> at order 500 and renders in the settings page.
/// A language-switching test must own its session: each one below starts a fresh headless session, shows
/// no window in it, and restores the process-wide language afterwards.
/// </summary>
[Collection("forge-app-ui")]
public sealed class GitSettingsSectionTests
{

    // ------------------------------------------------------------ view model

    [Fact]
    public void Selected_pull_strategy_reflects_and_edits_the_preferences()
    {
        var preferences = new AppPreferences();
        var section = new GitSettingsSectionViewModel(preferences);

        Assert.Equal(GitPullStrategy.FastForwardOnly, section.SelectedPullStrategy.Value);

        preferences.GitPullStrategy = "merge";
        Assert.Equal(GitPullStrategy.Merge, section.SelectedPullStrategy.Value);

        section.SelectedPullStrategy = section.PullStrategies.Single(option => option.Value == GitPullStrategy.Rebase);
        Assert.Equal("rebase", preferences.GitPullStrategy);
    }

    [Fact]
    public void Network_timeout_round_trips_and_clamps_through_the_preferences()
    {
        var preferences = new AppPreferences();
        var section = new GitSettingsSectionViewModel(preferences);

        section.NetworkTimeoutSeconds = 30;
        Assert.Equal(30, preferences.GitNetworkTimeoutSeconds);

        section.NetworkTimeoutSeconds = 5;
        Assert.Equal(AppPreferences.MinGitNetworkTimeoutSeconds, section.NetworkTimeoutSeconds);
        Assert.Equal(AppPreferences.MinGitNetworkTimeoutSeconds, preferences.GitNetworkTimeoutSeconds);

        section.NetworkTimeoutSeconds = 5000;
        Assert.Equal(AppPreferences.MaxGitNetworkTimeoutSeconds, section.NetworkTimeoutSeconds);
    }

    [Fact]
    public async Task Picker_labels_are_re_read_after_a_language_switch()
    {
        var session = SharedUi.Session;
        var previous = await session.Dispatch(() => Loc.Instance.Language, CancellationToken.None);
        try
        {
            var section = new GitSettingsSectionViewModel(new AppPreferences { GitPullStrategy = "rebase" });

            await session.Dispatch(() => { Loc.Instance.Language = UiLanguage.English; return 0; }, CancellationToken.None);
            var english = await session.Dispatch(
                () => section.PullStrategies.Select(static option => option.Label).ToList(),
                CancellationToken.None);

            await session.Dispatch(() => { Loc.Instance.Language = UiLanguage.Chinese; return 0; }, CancellationToken.None);
            var chinese = await session.Dispatch(
                () => section.PullStrategies.Select(static option => option.Label).ToList(),
                CancellationToken.None);

            Assert.Equal(["Fast-forward only", "Rebase", "Merge"], english);
            Assert.Equal(["仅快进", "变基", "合并"], chinese);

            // Replacing the options list on a switch must not lose the selection.
            Assert.Equal(
                GitPullStrategy.Rebase,
                await session.Dispatch(() => section.SelectedPullStrategy.Value, CancellationToken.None));
        }
        finally
        {
            var restore = previous;
            await session.Dispatch(() => { Loc.Instance.Language = restore; return 0; }, CancellationToken.None);
        }
    }

    // ------------------------------------------------------------ registration and rendering

    [Fact]
    public async Task The_section_is_registered_with_id_git_at_order_500()
    {
        var session = SharedUi.Session;
        var section = await session.Dispatch(
            async () =>
            {
                await using var harness = AppHarness.Open();
                var found = harness.Composition.Settings.Items.SingleOrDefault(
                    static item => string.Equals(item.Id, SettingsSectionIds.Git, StringComparison.Ordinal));
                return found is null
                    ? null
                    : new
                    {
                        found.Id,
                        found.Order,
                        ViewModel = found.ViewModel.GetType().FullName,
                    };
            },
            CancellationToken.None);

        Assert.NotNull(section);
        Assert.Equal("git", section!.Id);
        Assert.Equal(500, section.Order);
        Assert.Equal(SettingsSectionIds.GitOrder, section.Order);
        Assert.Equal(typeof(GitSettingsSectionViewModel).FullName, section.ViewModel);
    }

    [Fact]
    public async Task The_section_renders_between_provider_and_paths_with_its_picker()
    {
        var session = SharedUi.Session;
        var previous = await session.Dispatch(() => Loc.Instance.Language, CancellationToken.None);
        try
        {
            // The app tests pin the UI to Chinese (the harness resolves follow-the-system that way), and a live switch
            // of XAML-bound Loc text is only asserted in Forge.App.LiveSwitch.Tests; this one reads the rendered section
            // in the pinned language and the labels from the section view model.
            var rendered = await session.Dispatch(
                async () =>
                {
                    await using var harness = AppHarness.Open();
                    var shell = new SettingsShell(harness.Composition);
                    try
                    {
                        shell.OpenSettings();
                        var english = shell.ReadGitSection();
                        var git = Assert.IsType<GitSettingsSectionViewModel>(
                            shell.ViewModel.Settings.Sections.Items.Single(
                                static item => string.Equals(item.Id, SettingsSectionIds.Git, StringComparison.Ordinal)).ViewModel);

                        return (English: english, Labels: git.PullStrategies.Select(static option => option.Label).ToList());
                    }
                    finally
                    {
                        shell.Dispose();
                    }
                },
                CancellationToken.None);

            Assert.Equal("git", rendered.English.Id);
            Assert.Equal("Git", rendered.English.Title);
            Assert.True(rendered.English.Top > rendered.English.ProviderTop, "the git section renders below the provider section");
            Assert.True(rendered.English.Top < rendered.English.PathsTop, "the git section renders above the paths section");
            Assert.True(rendered.English.HasStrategyPicker);
            Assert.True(rendered.English.HasTimeoutInput);
            Assert.Contains("拉取方式", rendered.English.Texts, StringComparer.Ordinal);
            Assert.Equal(["仅快进", "变基", "合并"], rendered.Labels);
        }
        finally
        {
            var restore = previous;
            await session.Dispatch(() => { Loc.Instance.Language = restore; return 0; }, CancellationToken.None);
        }
    }

    // ------------------------------------------------------------ harness

    /// <summary>One throw-away data root with a real composition root; nothing touches the user's database.</summary>
    private sealed class AppHarness : IAsyncDisposable
    {
        private readonly string root;
        private readonly (string? Data, string? General, string? Workspace) saved;

        private AppHarness(string root, (string? Data, string? General, string? Workspace) saved, AppComposition composition)
        {
            this.root = root;
            this.saved = saved;
            Composition = composition;
        }

        public AppComposition Composition { get; }

        public static AppHarness Open()
        {
            var root = Path.Combine(Path.GetTempPath(), "forge-git-settings-" + Guid.NewGuid().ToString("N"));
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
                return new AppHarness(root, saved, AppComposition.Create());
            }
            catch (Exception)
            {
                Restore(saved);
                DeleteTree(root);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Composition.DisposeAsync();
            Restore(saved);
            DeleteTree(root);
        }

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

    /// <summary>A real main window with the settings overlay opened, read on the UI thread.</summary>
    private sealed class SettingsShell : IDisposable
    {
        private readonly MainWindowViewModel viewModel;
        private bool disposed;

        public SettingsShell(AppComposition composition)
        {
            viewModel = new MainWindowViewModel(composition);
            Window = new MainWindow { DataContext = viewModel };
            Window.Show();
            Settle(Window);
        }

        public MainWindowViewModel ViewModel => viewModel;

        public MainWindow Window { get; }

        public void OpenSettings()
        {
            viewModel.Settings.IsOpen = true;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                Settle(Window);
                if (RenderedSectionIds().Count == viewModel.Settings.Sections.Items.Count
                    && HeadingsSettled())
                {
                    break;
                }
            }

            // The git section body only renders once selected; this test reads it, so select it before reading.
            viewModel.Settings.SelectedSection = viewModel.Settings.Sections.Items.Single(
                static item => string.Equals(item.Id, SettingsSectionIds.Git, StringComparison.Ordinal));
            Settle(Window);
        }

        /// <summary>All three section headings are laid out at distinct heights, so order assertions are stable.</summary>
        private bool HeadingsSettled()
        {
            var tops = new[] { Heading("git"), Heading(SettingsSectionIds.Provider), Heading(SettingsSectionIds.Paths) }
                .Select(TopOf)
                .ToList();
            return tops.All(static top => !double.IsNaN(top)) && tops.Distinct().Count() == tops.Count;
        }

        public (string? Id, string? Title, double Top, double ProviderTop, double PathsTop, bool HasStrategyPicker, bool HasTimeoutInput, IReadOnlyList<string> Texts) ReadGitSection()
        {
            var heading = Heading("git");
            var provider = Heading(SettingsSectionIds.Provider);
            var paths = Heading(SettingsSectionIds.Paths);
            var texts = ControlOf<ScrollViewer>("settings-body") is { } body
                ? body.GetVisualDescendants().OfType<TextBlock>().Select(static block => block.Text ?? string.Empty).ToList()
                : new List<string>();
            return (
                heading is null ? null : AutomationProperties.GetAutomationId(heading),
                heading?.Text,
                TopOf(heading),
                TopOf(provider),
                TopOf(paths),
                ControlOf<ComboBox>("git-pull-strategy") is not null,
                ControlOf<NumericUpDown>("git-network-timeout") is not null,
                texts);
        }

        public IReadOnlyList<string> RenderedSectionIds() =>
            Window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(static block => AutomationProperties.GetAutomationId(block) is "provider" or "git" or "paths")
                .OrderBy(block => TopOf(block))
                .Select(static block => AutomationProperties.GetAutomationId(block)!)
                .ToList();

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Window.Close();
            viewModel.Dispose();
        }

        public static void Settle(Window window)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            Dispatcher.UIThread.RunJobs();
        }

        private TextBlock? Heading(string id) =>
            Window.GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(block => string.Equals(AutomationProperties.GetAutomationId(block), id, StringComparison.Ordinal));

        private T? ControlOf<T>(string automationId)
            where T : Control =>
            Window.GetVisualDescendants()
                .OfType<T>()
                .FirstOrDefault(control =>
                    string.Equals(AutomationProperties.GetAutomationId(control), automationId, StringComparison.Ordinal));

        private double TopOf(TextBlock? block) =>
            block?.TranslatePoint(new Point(0, 0), Window)?.Y ?? double.NaN;
    }
}
