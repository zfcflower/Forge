using Forge.Abstractions;
using Forge.App.Logging;
using Forge.App.Preferences;
using Forge.App.Services;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>
/// The window's first-run wizard slot (wave W9) and the one decision behind it: whether this database has already been
/// through the wizard. The slot is the overlay the window shows — null hides it, and the template registered for
/// <see cref="OnboardingViewModel"/> in <c>App.axaml</c> draws it.
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private object? onboarding;

    public object? Onboarding
    {
        get => onboarding;
        private set => Set(ref onboarding, value);
    }

    /// <summary>
    /// Whether a window that finishes initializing asks for the first-run wizard. Set by the application when it creates the
    /// real window and left off by every host that builds its own (a headless test, a recorder): those assert on the
    /// workbench, and an overlay in front of it would only be in the way. The overlay itself — and whether this database has
    /// already seen it — is decided by <see cref="ShowFirstRunWizardIfNeededAsync"/>.
    /// </summary>
    public bool ShowFirstRunWizardOnStartup { get; set; }

    /// <summary>
    /// Shows the wizard when this is a genuine first run: the database holds no "seen" record yet <em>and</em> no repository
    /// project. An install that already has one is not a first run — it gets the record written silently, so upgrading never
    /// puts a working user through setup — and a store that cannot be read is treated the same way. Called once by the
    /// window after <see cref="InitializeAsync"/>, never by the view model's own startup: a headless host (a test, a
    /// recorder) has no overlay to show it in.
    /// </summary>
    internal async Task ShowFirstRunWizardIfNeededAsync(CancellationToken ct)
    {
        var state = new OnboardingState(composition.UiState);
        if (await state.HasCompletedAsync(ct).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var projects = await BackgroundRead.RunAsync(token => composition.Projects.ListAsync(token), ct).ConfigureAwait(true);

            // Only a repository counts: the general workspace every install registers at startup is a
            // ProjectKind.General row, so counting rows would make every fresh install look like a returning one.
            foreach (var project in projects)
            {
                if (project.Kind == ProjectKind.Repository)
                {
                    await state.MarkCompletedAsync(ct).ConfigureAwait(true);
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // Without the project list there is no way to tell a first run from a returning one; skipping the wizard is the
            // quiet choice, and the settings pages keep offering everything the wizard would have asked for.
            AppLogs.Shell.ZLogWarning(exception, $"first-run check skipped");
            return;
        }

        ShowOnboarding(state);
    }

    /// <summary>
    /// Opens the wizard over the workbench. Its three steps edit the live preferences, read the provider catalogue and ask
    /// the notification guide; this method owns what happens around it: the settings page it deep-links to for providers,
    /// and the "seen" record written when it closes, whichever way it closes.
    /// </summary>
    private void ShowOnboarding(OnboardingState state)
    {
        // A second wizard replaces the first one: the replaced model unsubscribes from the static language event and from the
        // provider catalogue in `Dispose`, so letting it go without that would leak both subscriptions for the process.
        (Onboarding as OnboardingViewModel)?.Dispose();

        var model = new OnboardingViewModel(
            composition.Preferences,
            composition.Providers,
            composition.NotificationPermissionGuide);

        // The provider step does not re-implement the provider editor: it opens the settings section that owns that flow, so
        // there is one place a provider is added and one validation chain behind it.
        model.ProviderSetupRequested += (_, _) => OpenSettingsSection(SettingsSectionIds.Provider);
        model.Finished += async (_, _) => await FinishOnboardingAsync(state).ConfigureAwait(true);
        Onboarding = model;

        // The permission and the provider catalogue are read when the step is reached, but the first step's own refresh is
        // what makes the overlay show the current truth from the moment it appears.
        _ = model.RefreshAsync(CancellationToken.None);
    }

    /// <summary>Closes the wizard and records that this database has seen it; a second call is a no-op.</summary>
    private async Task FinishOnboardingAsync(OnboardingState state)
    {
        if (Onboarding is not OnboardingViewModel model)
        {
            return;
        }

        Onboarding = null;
        model.Dispose();
        await state.MarkCompletedAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>
    /// Drops the wizard, if one is open, without writing the "seen" record: called when the window itself is disposed, where
    /// the only thing that matters is that the model stops listening to the process-wide language and provider events.
    /// </summary>
    private void CloseOnboarding()
    {
        if (Onboarding is not OnboardingViewModel model)
        {
            return;
        }

        Onboarding = null;
        model.Dispose();
    }
}
