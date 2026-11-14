using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.App.Tests.Git;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// The first-run decision of the shell (wave W9) against the real composition root and a throw-away database: an empty
/// database opens the wizard, an installation that already holds a project silently records that it has seen it (an upgrade
/// is not a first run), finishing or skipping the wizard closes the overlay and writes the record, and a host that never asks
/// shows no overlay at all — <see cref="MainWindowViewModel.ShowFirstRunWizardOnStartup"/> is opt-in for exactly that reason.
/// <para>The general workspace every install registers is a <see cref="ProjectKind.General" /> row, so the first-run check
/// counts repository projects only; <see cref="A_fresh_database_already_holds_the_general_workspace_project"/> pins that the
/// registry is never empty on a fresh install, which is why the distinction exists.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class OnboardingShellTests
{
    [Fact]
    public async Task A_fresh_database_already_holds_the_general_workspace_project()
    {
        await WithShellAsync(async shell =>
        {
            var projects = await shell.Composition.Projects.ListAsync(CancellationToken.None);

            // The premise the first-run check is built on: the registry is not empty on a fresh install, it holds the one
            // project every install owns.
            var project = Assert.Single(projects);
            Assert.Equal(ProjectKind.General, project.Kind);
        });
    }

    [Fact]
    public async Task An_empty_database_opens_the_wizard()
    {
        await WithShellAsync(async shell =>
        {
            Assert.Null(shell.Onboarding);

            await shell.ShowFirstRunWizardIfNeededAsync(CancellationToken.None);

            Assert.IsType<OnboardingViewModel>(shell.Onboarding);

            // Asking again cannot take the overlay away from the user; it stays what it is (a wizard in the slot).
            await shell.ShowFirstRunWizardIfNeededAsync(CancellationToken.None);
            Assert.IsType<OnboardingViewModel>(shell.Onboarding);
        });
    }

    [Fact]
    public async Task Skipping_the_wizard_closes_it_and_records_the_round_trip()
    {
        await WithShellAsync(async shell =>
        {
            await shell.ShowFirstRunWizardIfNeededAsync(CancellationToken.None);
            var model = Assert.IsType<OnboardingViewModel>(shell.Onboarding);

            model.SkipCommand.Execute(null);

            Assert.Null(shell.Onboarding);
            await WaitUntilAsync(
                async () => await shell.Composition.UiState.GetAsync(OnboardingState.Key, CancellationToken.None) is not null,
                "the onboarding record to be written");

            // The record is the whole answer: a later check on the same database leaves the wizard closed.
            await shell.ShowFirstRunWizardIfNeededAsync(CancellationToken.None);
            Assert.Null(shell.Onboarding);
        });
    }

    [Fact]
    public async Task Finishing_the_last_step_also_closes_the_wizard_and_records_it()
    {
        await WithShellAsync(async shell =>
        {
            await shell.ShowFirstRunWizardIfNeededAsync(CancellationToken.None);
            var model = Assert.IsType<OnboardingViewModel>(shell.Onboarding);

            model.NextCommand.Execute(null);
            model.NextCommand.Execute(null);
            Assert.NotNull(shell.Onboarding);
            model.NextCommand.Execute(null);

            Assert.Null(shell.Onboarding);
            await WaitUntilAsync(
                async () => await shell.Composition.UiState.GetAsync(OnboardingState.Key, CancellationToken.None) is not null,
                "the onboarding record to be written");
        });
    }

    [Fact]
    public async Task An_installation_that_already_has_a_project_is_never_put_through_setup()
    {
        await WithShellAsync(async shell =>
        {
            using var repository = GitTestSandbox.Create();
            var project = repository.CloneTracked("existing");
            await Task.Run(() => shell.Composition.Projects.AddAsync(project, "existing", ProjectKind.Repository, CancellationToken.None));

            // Nothing has been decided yet, so the record below is this call's own answer to the project list.
            Assert.Null(await shell.Composition.UiState.GetAsync(OnboardingState.Key, CancellationToken.None));

            await shell.ShowFirstRunWizardIfNeededAsync(CancellationToken.None);

            Assert.Null(shell.Onboarding);
            Assert.NotNull(await shell.Composition.UiState.GetAsync(OnboardingState.Key, CancellationToken.None));

            // The record was written silently, so the second start does not even have to look at the projects again.
            await shell.ShowFirstRunWizardIfNeededAsync(CancellationToken.None);
            Assert.Null(shell.Onboarding);
        });
    }

    [Fact]
    public async Task The_startup_switch_is_off_until_the_application_turns_it_on()
    {
        await WithShellAsync(shell =>
        {
            // A host that builds its own window (a recorder, a headless test) must not get an overlay it cannot render.
            Assert.False(shell.Shell.ShowFirstRunWizardOnStartup);
            Assert.Null(shell.Onboarding);
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- the real composition root

    /// <summary>Runs <paramref name="body"/> over a fresh data root, a real composition root and a real main-window view model.</summary>
    private static async Task WithShellAsync(Func<WizardShell, Task> body)
    {
        await SharedUi.Session.Dispatch(
            async () =>
            {
                await using var shell = await WizardShell.CreateAsync();
                await body(shell);
                return 0;
            },
            CancellationToken.None);
    }

    /// <summary>A bounded wait over an asynchronous condition; the headless dispatcher keeps running while it waits.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }

    private sealed class WizardShell : IAsyncDisposable
    {
        private readonly string root;
        private readonly EnvironmentSnapshot previous;

        private WizardShell(string root, EnvironmentSnapshot previous, AppComposition composition, MainWindowViewModel shell)
        {
            this.root = root;
            this.previous = previous;
            Composition = composition;
            Shell = shell;
        }

        public AppComposition Composition { get; }

        public MainWindowViewModel Shell { get; }

        public object? Onboarding => Shell.Onboarding;

        public Task ShowFirstRunWizardIfNeededAsync(CancellationToken ct) => Shell.ShowFirstRunWizardIfNeededAsync(ct);

        public static async Task<WizardShell> CreateAsync()
        {
            var (root, previous) = TempDataRoot.Redirect("forge-onboarding-shell-");
            var composition = AppComposition.Create();
            var shell = new MainWindowViewModel(composition);
            await shell.InitializeAsync();
            return new WizardShell(root, previous, composition, shell);
        }

        public async ValueTask DisposeAsync()
        {
            Shell.Dispose();
            await Composition.DisposeAsync();
            SqliteConnection.ClearAllPools();
            previous.Restore();
            TestTempFiles.DeleteTree(root);
        }
    }
}