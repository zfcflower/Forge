using System.ComponentModel;
using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Providers;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>The three things a first run asks for, in the order they are shown.</summary>
internal enum OnboardingStep
{
    /// <summary>The UI language, applied live so the rest of the wizard shows the choice.</summary>
    Language = 0,

    /// <summary>Connecting a provider: without one nothing can run a turn.</summary>
    Provider = 1,

    /// <summary>The OS notification permission, and where it is granted.</summary>
    Notifications = 2,
}

/// <summary>One choice of the language step: the stored value plus its label in both languages (never translated twice).</summary>
internal sealed record OnboardingLanguageOption(UiLanguage Language, string Label);

/// <summary>
/// The first-run wizard: language, provider, notification permission. It edits the live preferences directly (so a language
/// choice applies at once and survives a skip), reads the provider catalogue for its status line and asks
/// <see cref="INotificationPermissionGuide"/> about the permission — it owns no storage and no OS call of its own. When the
/// last step is finished or the wizard is skipped it raises <see cref="Finished"/>; the shell persists "seen" and hides it.
/// <para>Every sentence the wizard shows is resolved on access, never cached, and re-raised on
/// <see cref="Loc.LanguageChanged"/>; the provider line is additionally re-raised on <see cref="ProviderServices.Changed"/>
/// (posted to the UI thread, because a save from the settings page raises it on the pool thread it wrote on). The wizard
/// runs on the UI thread only — all awaits keep the context so the step swap and the property notifications after them
/// stay on the thread the bindings live on.</para>
/// </summary>
internal sealed class OnboardingViewModel : NotifyingObject, IDisposable
{
    private readonly AppPreferences preferences;
    private readonly ProviderServices providers;
    private readonly INotificationPermissionGuide? guide;

    /// <summary>The thread the wizard was built on (the UI thread); a language notification from another one is not ours.</summary>
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    /// <summary>The language choices, rebuilt on a language switch because the labels (and the system resolution) follow it.</summary>
    private OnboardingLanguageOption[] languages = BuildLanguages();

    private OnboardingStep step = OnboardingStep.Language;

    /// <summary>The permission as the last <see cref="RefreshAsync"/> read it; Unknown until then, which is what that state is for.</summary>
    private NotificationPermissionState permission = NotificationPermissionState.Unknown;

    /// <summary>The hint after a refused <c>OpenSettingsAsync</c>; null while the last attempt opened something (or none ran).</summary>
    private string? notificationHint;

    /// <summary>Cancels the in-flight step refresh; replaced whenever a step change asks for a newer one.</summary>
    private CancellationTokenSource refresh = new();

    private bool finished;
    private bool disposed;

    /// <summary>Builds the wizard over the services the shell already owns.</summary>
    /// <param name="preferences">The live preferences; the language step writes <see cref="AppPreferences.Language"/>.</param>
    /// <param name="providers">The live provider catalogue, read for the status line and watched for a save from the settings page.</param>
    /// <param name="guide">The notification permission guide; null when the composition has none (then the step only explains).</param>
    public OnboardingViewModel(
        AppPreferences preferences,
        ProviderServices providers,
        INotificationPermissionGuide? guide)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(providers);

        this.preferences = preferences;
        this.providers = providers;
        this.guide = guide;
        ProviderSetupCommand = new RelayCommand(RaiseProviderSetup);
        OpenNotificationSettingsCommand = new AsyncRelayCommand(OpenNotificationSettingsAsync, onError: ReportFailure);
        NextCommand = new RelayCommand(GoNext);
        BackCommand = new RelayCommand(GoBack, () => CanGoBack);
        SkipCommand = new RelayCommand(RaiseFinished);

        // The provider line has to follow a save made from the settings page the wizard opened: the catalogue is replaced
        // behind the wizard's back, so the wizard listens rather than re-reading on a timer.
        providers.Changed += OnProvidersChanged;

        // The wizard's own texts are resolved per access, so a switch only needs the properties re-raised.
        Loc.Instance.LanguageChanged += OnLanguageChanged;

        // The selection itself can change without a language switch ("follow the system" while the system already
        // resolves to the language on screen), so the radio group refreshes from the preference, not from Loc.
        preferences.PropertyChanged += OnPreferencesChanged;
    }

    /// <summary>Raised once: the wizard was finished or skipped; the shell records that and closes the overlay.</summary>
    public event EventHandler? Finished;

    /// <summary>Raised by the provider step's button; the shell opens the settings page on the provider section.</summary>
    public event EventHandler? ProviderSetupRequested;

    /// <summary>The step being shown.</summary>
    public OnboardingStep Step => step;

    /// <summary>The step's position, 1-based, for "step 2 of 3".</summary>
    public int StepNumber => (int)step + 1;

    public int StepCount => 3;

    public bool IsLanguageStep => step == OnboardingStep.Language;

    public bool IsProviderStep => step == OnboardingStep.Provider;

    public bool IsNotificationsStep => step == OnboardingStep.Notifications;

    /// <summary>The heading of the current step, in the current language.</summary>
    public string StepTitle => step switch
    {
        OnboardingStep.Language => OnboardingStrings.LanguageTitle,
        OnboardingStep.Provider => OnboardingStrings.ProviderTitle,
        _ => OnboardingStrings.NotificationsTitle,
    };

    /// <summary>The one sentence under the heading that says what the step is for.</summary>
    public string StepIntro => step switch
    {
        OnboardingStep.Language => OnboardingStrings.LanguageIntro,
        OnboardingStep.Provider => OnboardingStrings.ProviderIntro,
        _ => OnboardingStrings.NotificationsIntro,
    };

    /// <summary>The counter above the heading, "step 2 of 3"; re-read on every access so a switch re-renders it.</summary>
    public string StepCaption => OnboardingStrings.StepCaption(StepNumber, StepCount);

    /// <summary>The language choices, in a fixed order; the first label says which language "follow the system" resolves to.</summary>
    public IReadOnlyList<OnboardingLanguageOption> Languages => languages;

    /// <summary>The chosen language; writes the live preference, so the wizard itself re-renders in it.</summary>
    public OnboardingLanguageOption SelectedLanguage
    {
        get
        {
            foreach (var option in languages)
            {
                if (option.Language == preferences.Language)
                {
                    return option;
                }
            }

            // Unreachable while the three choices cover the enum; a value the table does not know falls back to "system",
            // which is also what an unreadable preference means.
            return languages[0];
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (preferences.Language == value.Language)
            {
                return;
            }

            // UI thread only: the setter raises PropertyChanged, which the composition root turns into a Loc update and
            // the store turns into a debounced file write. Loc is not touched here — the root pushes the preference.
            preferences.Language = value.Language;
            RaiseSelection();
        }
    }

    /// <summary>True when the UI follows the system language; checking it selects <see cref="UiLanguage.System"/>.</summary>
    public bool IsSystemLanguage
    {
        get => preferences.Language == UiLanguage.System;
        set => Select(UiLanguage.System, value);
    }

    /// <summary>True when the UI is Chinese; checking it selects <see cref="UiLanguage.Chinese"/>.</summary>
    public bool IsChineseLanguage
    {
        get => preferences.Language == UiLanguage.Chinese;
        set => Select(UiLanguage.Chinese, value);
    }

    /// <summary>True when the UI is English; checking it selects <see cref="UiLanguage.English"/>.</summary>
    public bool IsEnglishLanguage
    {
        get => preferences.Language == UiLanguage.English;
        set => Select(UiLanguage.English, value);
    }

    /// <summary>The "follow system" choice as the wizard words it: the choice plus the language it currently resolves to.</summary>
    public string SystemLanguageText => languages[0].Label;

    /// <summary>The name of the Chinese language, the way the language section names it.</summary>
    public string ChineseLanguageText => languages[1].Label;

    /// <summary>The name of the English language, the way the language section names it.</summary>
    public string EnglishLanguageText => languages[2].Label;

    /// <summary>One sentence about the provider catalogue: connected (with the default model) or not yet.</summary>
    public string ProviderStatus => providers.Catalog.StatusText.Resolve();

    public bool IsProviderConnected => providers.Catalog.IsConfigured;

    /// <summary>Opens the provider settings section through <see cref="ProviderSetupRequested"/>.</summary>
    public RelayCommand ProviderSetupCommand { get; }

    /// <summary>One sentence about the OS notification permission, in the current language.</summary>
    public string NotificationState => guide is null
        ? OnboardingStrings.NotificationsNoGuide
        : NotificationPermissionTexts.StateText(permission);

    /// <summary>Whether the platform has a notification switch to open at all.</summary>
    public bool CanOpenNotificationSettings => guide is not null && permission != NotificationPermissionState.NotApplicable;

    /// <summary>Asks the guide to open the OS notification settings; a refusal becomes the step's hint.</summary>
    public AsyncRelayCommand OpenNotificationSettingsCommand { get; }

    /// <summary>
    /// The sentence shown under the state when the platform refused to open its settings; empty while there is nothing to
    /// explain, which is also why <see cref="HasNotificationHint"/> carries the visibility instead of comparing to empty.
    /// </summary>
    public string NotificationHint => notificationHint ?? string.Empty;

    /// <summary>Whether the hint line is shown at all.</summary>
    public bool HasNotificationHint => notificationHint is not null;

    /// <summary>Advances a step, or finishes the wizard on the last one.</summary>
    public RelayCommand NextCommand { get; }

    /// <summary>Goes back a step; disabled on the first one.</summary>
    public RelayCommand BackCommand { get; }

    /// <summary>Closes the wizard without walking the remaining steps; the same "seen" record is written.</summary>
    public RelayCommand SkipCommand { get; }

    public bool CanGoBack => step != OnboardingStep.Language;

    public bool IsLastStep => step == OnboardingStep.Notifications;

    /// <summary>The primary button's text: "Next" or, on the last step, "Done".</summary>
    public string NextText => IsLastStep ? OnboardingStrings.Done : OnboardingStrings.Next;

    /// <summary>
    /// Re-reads what the current step shows (the permission, the provider catalogue); never throws for a failed read. The
    /// provider line is resolved per access so nothing is cached here; the permission is read because the wizard has no
    /// other way to learn it.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (guide is null)
        {
            return;
        }

        NotificationPermissionState read;
        try
        {
            read = await guide.GetStateAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The wizard is being disposed or a newer refresh superseded this one; the state stays what it was.
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The contract says this never throws; if it still does, the step shows the "unknown" sentence instead of
            // faulting the wizard.
            AppLogs.Notify.ZLogWarning($"the first-run wizard could not read the notification permission: {exception.Message}");
            read = NotificationPermissionState.Unknown;
        }

        permission = read;
        OnPropertyChanged(nameof(NotificationState));
        OnPropertyChanged(nameof(CanOpenNotificationSettings));
    }

    /// <summary>
    /// Drops every subscription the wizard took (the provider catalogue, the language table, the preferences) and cancels
    /// the in-flight refresh. It never raises <see cref="Finished"/>: a disposal is a teardown, not a user decision, and
    /// the shell owns the "seen" record.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        providers.Changed -= OnProvidersChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        preferences.PropertyChanged -= OnPreferencesChanged;
        refresh.Cancel();
        refresh.Dispose();
    }

    private void GoNext()
    {
        if (IsLastStep)
        {
            RaiseFinished();
            return;
        }

        step++;
        RaiseStep();
        RefreshCurrentStep();
    }

    private void GoBack()
    {
        if (!CanGoBack)
        {
            return;
        }

        step--;
        RaiseStep();
        RefreshCurrentStep();
    }

    /// <summary>
    /// Asks for a fresh read of the step that was just entered. Fire-and-forget because the button cannot wait for it:
    /// <see cref="RefreshAsync"/> never throws, so the discarded task is never an unobserved fault. A step change also
    /// cancels the refresh it interrupted — the older answer would land in the newer step's state.
    /// </summary>
    private void RefreshCurrentStep()
    {
        refresh.Cancel();
        refresh.Dispose();
        refresh = new CancellationTokenSource();
        _ = RefreshAsync(refresh.Token);
    }

    /// <summary>
    /// A radio button that became unchecked must not write the preference — only the checked one is the choice. The
    /// labels stay <see cref="Languages"/>'s own, so the group and the list cannot drift apart.
    /// </summary>
    private void Select(UiLanguage language, bool isChecked)
    {
        if (!isChecked || preferences.Language == language)
        {
            return;
        }

        preferences.Language = language;
        RaiseSelection();
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(IsSystemLanguage));
        OnPropertyChanged(nameof(IsChineseLanguage));
        OnPropertyChanged(nameof(IsEnglishLanguage));
    }

    private void RaiseStep()
    {
        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(StepNumber));
        OnPropertyChanged(nameof(IsLanguageStep));
        OnPropertyChanged(nameof(IsProviderStep));
        OnPropertyChanged(nameof(IsNotificationsStep));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepIntro));
        OnPropertyChanged(nameof(StepCaption));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(NextText));
        BackCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Finishes (or skips) the wizard; raised at most once, so a double click on "Done" does not close it twice.</summary>
    private void RaiseFinished()
    {
        if (finished)
        {
            return;
        }

        finished = true;
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseProviderSetup() => ProviderSetupRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Opens the OS notification settings, then re-reads the state; a refusal turns into the step's hint.</summary>
    private async Task OpenNotificationSettingsAsync(CancellationToken ct)
    {
        if (guide is null || !CanOpenNotificationSettings)
        {
            return;
        }

        SetNotificationHint(null);
        bool opened;
        try
        {
            opened = await guide.OpenSettingsAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Defensive only: the contract promises no throw, and a failed attempt is the same as a refusal for the user.
            AppLogs.Notify.ZLogWarning($"the first-run wizard could not open the notification settings: {exception.Message}");
            opened = false;
        }

        if (!opened)
        {
            SetNotificationHint(OnboardingStrings.NotificationsHint);
        }

        await RefreshAsync(ct).ConfigureAwait(true);
    }

    private void SetNotificationHint(string? value)
    {
        if (notificationHint == value)
        {
            return;
        }

        notificationHint = value;
        OnPropertyChanged(nameof(NotificationHint));
        OnPropertyChanged(nameof(HasNotificationHint));
    }

    /// <summary>The catalogue the status line reads was replaced; re-raise the two lines that quote it.</summary>
    private void OnProvidersChanged(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshProviderLines();
            return;
        }

        // A save or reload raises this on the thread it finished on (the pool), while the bindings live on the UI thread.
        Dispatcher.UIThread.Post(RefreshProviderLines);
    }

    private void RefreshProviderLines()
    {
        OnPropertyChanged(nameof(ProviderStatus));
        OnPropertyChanged(nameof(IsProviderConnected));
    }

    /// <summary>
    /// Re-reads the texts the wizard computes (labels, headings, status sentences) by rebuilding the one list that
    /// carries labels and re-raising the rest. A notification from another thread belongs to a window that is gone, so
    /// it is ignored the way the notification permission section ignores it.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (disposed || Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        languages = BuildLanguages();
        OnPropertyChanged(nameof(Languages));
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(SystemLanguageText));
        OnPropertyChanged(nameof(ChineseLanguageText));
        OnPropertyChanged(nameof(EnglishLanguageText));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepIntro));
        OnPropertyChanged(nameof(StepCaption));
        OnPropertyChanged(nameof(NextText));
        OnPropertyChanged(nameof(ProviderStatus));
        OnPropertyChanged(nameof(NotificationState));
        OnPropertyChanged(nameof(NotificationHint));
    }

    /// <summary>The preference changed without the language switching (or while this view model was not the writer).</summary>
    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(AppPreferences.Language), StringComparison.Ordinal))
        {
            RaiseSelection();
        }
    }

    /// <summary>
    /// The three choices: "follow the system" (with the language it currently resolves to, taken from
    /// <see cref="Loc.SystemLanguage"/> — the single place that resolves it), then Chinese and English named the way the
    /// language section names them, so somebody who cannot read the current UI language can still find their own.
    /// </summary>
    private static OnboardingLanguageOption[] BuildLanguages()
    {
        var resolved = Loc.SystemLanguage == UiLanguage.Chinese ? LanguageStrings.ChineseName : LanguageStrings.EnglishName;
        return
        [
            new(UiLanguage.System, LanguageStrings.SystemResolvedText(resolved)),
            new(UiLanguage.Chinese, LanguageStrings.ChineseName),
            new(UiLanguage.English, LanguageStrings.EnglishName),
        ];
    }

    private static void ReportFailure(Exception exception) =>
        AppLogs.Notify.ZLogWarning($"the first-run wizard's notification action failed: {exception.Message}");
}
