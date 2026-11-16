using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.ViewModels;

namespace Forge.App.Settings;

/// <summary>One choice in the frequency or channel picker; <see cref="Label"/> re-reads <see cref="Loc"/> on every get.</summary>
internal sealed record UpdatesChoiceOption(string Value, string LabelKey)
{
    public string Label => Loc.Instance[LabelKey];
}

/// <summary>
/// The <c>updates</c> settings section (<see cref="SettingsSectionIds.Updates"/>): the manifest URL, the automatic-check
/// frequency, the release channel, the running version and the check-now action with progress and a readable result. The
/// three preferences are edited live and persisted by the preferences store's debounce; the check itself runs through
/// <see cref="UpdateService"/> and is cancelled by the section's cancel button. The result and the last-check time are
/// transient display state (the service persists them to <c>ui_state</c> for the automatic schedule).
/// <para>
/// Wave W10c adds "install and restart": once a check has downloaded and verified a package, the section hands it to
/// <see cref="UpdateService.InstallAndRestartAsync"/>, which unpacks it and starts the applier. Before that the view asks the
/// user (the confirmation names the version and says the app closes); afterwards the view closes the app, so the applier can
/// replace the install directory. Both interactions are seams the view supplies — a headless host has no window to ask in and
/// no application to close, and the section must stay usable there.
/// </para>
/// </summary>
internal sealed class UpdatesSettingsSectionViewModel : ObservableObject, IReloadableSettingsSection, IDisposable
{
    private readonly AppPreferences preferences;
    private readonly UpdateService service;

    /// <summary>The thread the section was built on (the UI thread); a language notification from another one is not ours.</summary>
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    /// <summary>Replaced (not mutated) on a language switch, so realized item containers re-read their labels.</summary>
    private UpdatesChoiceOption[] frequencyOptions = CreateFrequencyOptions();

    private UpdatesChoiceOption[] channelOptions = CreateChannelOptions();

    private CancellationTokenSource? checkCts;
    private bool isChecking;
    private bool isInstalling;
    private LocText result;
    private LocText progress;
    private LocText installResult;
    private string notes = string.Empty;
    private DateTimeOffset? lastCheckAt;
    private bool disposed;

    public UpdatesSettingsSectionViewModel(AppPreferences preferences, UpdateService service)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(service);
        this.preferences = preferences;
        this.service = service;

        CheckNowCommand = new AsyncRelayCommand(_ => CheckNowAsync(), () => !isChecking);
        CancelCommand = new RelayCommand(Cancel, () => isChecking);
        OpenDownloadLocationCommand = new RelayCommand(OpenDownloadLocation, () => service.StagedPackagePath is not null);
        InstallAndRestartCommand = new AsyncRelayCommand(_ => InstallAndRestartAsync(), () => CanInstallAndRestart);

        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public string ManifestUrl
    {
        get => preferences.UpdatesManifestUrl;
        set
        {
            preferences.UpdatesManifestUrl = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The three frequencies in display order, with localized labels.</summary>
    public IReadOnlyList<UpdatesChoiceOption> Frequencies => frequencyOptions;

    public UpdatesChoiceOption SelectedFrequency
    {
        get => ChoiceOfFrequency(preferences.UpdatesCheckFrequency);
        set
        {
            if (value is null)
            {
                return;
            }

            preferences.UpdatesCheckFrequency = value.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>The two channels in display order, with localized labels.</summary>
    public IReadOnlyList<UpdatesChoiceOption> Channels => channelOptions;

    public UpdatesChoiceOption SelectedChannel
    {
        get => ChoiceOfChannel(preferences.UpdatesChannel);
        set
        {
            if (value is null)
            {
                return;
            }

            preferences.UpdatesChannel = value.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>The version this process reports (assembly <c>InformationalVersion</c>, CI-stamped).</summary>
    public string CurrentVersion => service.CurrentVersion;

    public bool IsChecking => isChecking;

    /// <summary>The last check's readable outcome; empty until a check has run.</summary>
    public string ResultText => result.Resolve();

    public bool HasResult => !result.IsEmpty;

    /// <summary>The offered version's release notes (raw Markdown); empty until a new version is staged.</summary>
    public string Notes => notes;

    public bool HasNotes => notes.Length > 0;

    /// <summary>The download progress line; empty when no download is running.</summary>
    public string ProgressText => progress.Resolve();

    /// <summary>When the last check ran, in the current language's short stamp; "not yet" before the first.</summary>
    public string LastCheckText => lastCheckAt is { } at ? TextFormat.ShortStamp(at) : UpdatesStrings.SettingsLastCheckNever;

    public AsyncRelayCommand CheckNowCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand OpenDownloadLocationCommand { get; }

    /// <summary>Unpacks the verified package and starts the applier; the view confirms first and closes the app after.</summary>
    public AsyncRelayCommand InstallAndRestartCommand { get; }

    /// <summary>
    /// The confirmation the view supplies before the app is closed. Null (a headless host) means the section installs
    /// without asking — the only caller that can reach this state is the product's own view, which always sets it.
    /// </summary>
    internal Func<string, Task<bool>>? ConfirmInstallAsync { get; set; }

    /// <summary>Closes the application once the applier runs; the view supplies it, a headless host has nothing to close.</summary>
    internal Action? RequestExit { get; set; }

    /// <summary>Whether a staged package exists, so the open-location button can be shown and enabled.</summary>
    public bool CanOpenDownloadLocation => service.StagedPackagePath is not null;

    /// <summary>
    /// "Install and restart" is offered only for a package that was downloaded and passed its SHA-256 check, and never while
    /// a check or another install runs.
    /// </summary>
    public bool CanInstallAndRestart => !isChecking && !isInstalling && service.StagedOffer is not null;

    /// <summary>True while the package is being unpacked or the applier is being started.</summary>
    public bool IsInstalling => isInstalling;

    /// <summary>What the last install attempt said (the applier's start, or why nothing was installed); empty before.</summary>
    public string InstallText => installResult.Resolve();

    public bool HasInstallResult => !installResult.IsEmpty;

    /// <summary>Re-reads the persisted last-check time when the section is shown; fire-and-forget (see <see cref="ReloadAsync"/>).</summary>
    public void Reload() => _ = ReloadAsync();

    /// <summary>Reads the persisted last-check time on the thread pool and updates the section on the UI thread.</summary>
    public async Task ReloadAsync()
    {
        lastCheckAt = await service.GetLastCheckAsync(CancellationToken.None).ConfigureAwait(true);
        OnPropertyChanged(nameof(LastCheckText));
    }

    /// <summary>Runs a manual check; the section's cancel button cancels it through <see cref="checkCts"/>.</summary>
    internal async Task CheckNowAsync()
    {
        if (isChecking)
        {
            return;
        }

        var dispatcher = Dispatcher.UIThread;
        checkCts = new CancellationTokenSource();
        UpdateBusy(true);
        result = default;
        progress = default;
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ProgressText));

        void OnProgress(UpdateDownloadProgress update)
        {
            if (dispatcher.CheckAccess())
            {
                ApplyProgress(update);
                return;
            }

            dispatcher.Post(() => ApplyProgress(update));
        }

        try
        {
            var outcome = await service.CheckForUpdateAsync(manual: true, checkCts.Token, OnProgress).ConfigureAwait(true);
            result = outcome.Message;
            notes = outcome.Offer?.Notes ?? string.Empty;
            lastCheckAt = DateTimeOffset.Now;
        }
        catch (OperationCanceledException)
        {
            result = UpdatesStrings.ResultCancelled;
            notes = string.Empty;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The check reads a preference, a URL and a file system; anything this method does not already turn into an
            // outcome must still reach the user, not vanish inside the command wrapper (which leaves the section blank).
            result = UpdatesStrings.ErrorCheckFailed(exception.Message);
            notes = string.Empty;
        }
        finally
        {
            checkCts.Dispose();
            checkCts = null;
            UpdateBusy(false);
            OnPropertyChanged(nameof(ResultText));
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(Notes));
            OnPropertyChanged(nameof(HasNotes));
            OnPropertyChanged(nameof(LastCheckText));
            OnPropertyChanged(nameof(ProgressText));
            OpenDownloadLocationCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanOpenDownloadLocation));
            InstallAndRestartCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanInstallAndRestart));
        }
    }

    /// <summary>
    /// Asks for confirmation, unpacks the staged package and starts the applier; when the applier runs, the app closes so it
    /// can replace the install directory. A failure leaves the section open with the reason, and the app untouched.
    /// </summary>
    internal async Task InstallAndRestartAsync()
    {
        if (isInstalling || service.StagedOffer is not { } offer)
        {
            return;
        }

        if (ConfirmInstallAsync is { } confirm && !await confirm(offer.Version).ConfigureAwait(true))
        {
            return;
        }

        SetInstalling(true);
        installResult = UpdatesStrings.InstallStarting;
        NotifyInstall();
        try
        {
            var outcome = await service.InstallAndRestartAsync(CancellationToken.None).ConfigureAwait(true);
            installResult = outcome.Message;
            if (outcome.Started)
            {
                RequestExit?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            installResult = UpdatesStrings.ResultCancelled;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The install touches the file system and starts a process; an unreadable directory or a policy block must be
            // told to the user, not swallowed by the command wrapper.
            installResult = UpdatesStrings.InstallErrorFailed(exception.Message);
        }
        finally
        {
            SetInstalling(false);
            NotifyInstall();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        checkCts?.Cancel();
        checkCts?.Dispose();
        checkCts = null;
    }

    private void ApplyProgress(UpdateDownloadProgress update)
    {
        progress = UpdatesStrings.SettingsProgress(update.BytesRead, update.TotalBytes);
        OnPropertyChanged(nameof(ProgressText));
    }

    private void SetInstalling(bool installing)
    {
        if (isInstalling == installing)
        {
            return;
        }

        isInstalling = installing;
        OnPropertyChanged(nameof(IsInstalling));
        OnPropertyChanged(nameof(CanInstallAndRestart));
        InstallAndRestartCommand.NotifyCanExecuteChanged();
        CheckNowCommand.NotifyCanExecuteChanged();
    }

    private void NotifyInstall()
    {
        OnPropertyChanged(nameof(InstallText));
        OnPropertyChanged(nameof(HasInstallResult));
        OnPropertyChanged(nameof(CanInstallAndRestart));
        InstallAndRestartCommand.NotifyCanExecuteChanged();
    }

    private void Cancel() => checkCts?.Cancel();

    private void OpenDownloadLocation() => service.OpenDownloadLocation();

    private void UpdateBusy(bool busy)
    {
        if (isChecking == busy)
        {
            return;
        }

        isChecking = busy;
        OnPropertyChanged(nameof(IsChecking));
        CheckNowCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OpenDownloadLocationCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanOpenDownloadLocation));
        OnPropertyChanged(nameof(CanInstallAndRestart));
        InstallAndRestartCommand.NotifyCanExecuteChanged();
    }

    private UpdatesChoiceOption ChoiceOfFrequency(string token) => token switch
    {
        UpdateSchedule.Weekly => frequencyOptions[1],
        UpdateSchedule.Never => frequencyOptions[2],
        _ => frequencyOptions[0],
    };

    private UpdatesChoiceOption ChoiceOfChannel(string token) =>
        token == "beta" ? channelOptions[1] : channelOptions[0];

    private static UpdatesChoiceOption[] CreateFrequencyOptions() =>
    [
        new(UpdateSchedule.Daily, UpdatesStrings.SettingsFrequencyDailyKey),
        new(UpdateSchedule.Weekly, UpdatesStrings.SettingsFrequencyWeeklyKey),
        new(UpdateSchedule.Never, UpdatesStrings.SettingsFrequencyNeverKey),
    ];

    private static UpdatesChoiceOption[] CreateChannelOptions() =>
    [
        new(AppPreferences.DefaultUpdatesChannel, UpdatesStrings.SettingsChannelStableKey),
        new("beta", UpdatesStrings.SettingsChannelBetaKey),
    ];

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A notification from another thread belongs to a window that is gone: the section bound to these
        // properties lives on its own thread.
        if (disposed || Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        frequencyOptions = CreateFrequencyOptions();
        channelOptions = CreateChannelOptions();
        OnPropertyChanged(nameof(Frequencies));
        OnPropertyChanged(nameof(SelectedFrequency));
        OnPropertyChanged(nameof(Channels));
        OnPropertyChanged(nameof(SelectedChannel));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(LastCheckText));
        OnPropertyChanged(nameof(InstallText));
    }
}
