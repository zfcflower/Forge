using System.Windows.Input;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using ZLogger;

namespace Forge.App.Notifications;

/// <summary>
/// The <c>notification-permission</c> settings section (<see cref="SettingsSectionIds.NotificationPermission"/>): one
/// sentence saying what is known about the system's notification permission, two actions that change it (send a real test
/// notification, open the platform's notification settings) and the "ask again" switch the first-run wizard reads.
/// <para>It holds no state of its own beyond whether a test is running: the sentence reads
/// <see cref="INotificationPermissionGuide.State"/> and the switch reads <see cref="AppPreferences"/> on every access, so a
/// value changed anywhere else shows up here without a reload. The action labels and the sentence are computed in C#,
/// which is why the section drops its <see cref="Loc.LanguageChanged"/> subscription in <see cref="Dispose"/> - the table
/// is process-wide and would otherwise outlive the composition that owns this.</para>
/// </summary>
internal sealed class NotificationPermissionSettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppPreferences preferences;
    private readonly INotificationPermissionGuide guide;

    /// <summary>The thread the section was built on (the UI thread); a language notification from another one is not ours.</summary>
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    private bool sendingTest;
    private bool disposed;

    public NotificationPermissionSettingsViewModel(AppPreferences preferences, INotificationPermissionGuide guide)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(guide);

        this.preferences = preferences;
        this.guide = guide;
        SendTestCommand = new AsyncRelayCommand(SendTestAsync, onError: ReportFailure);
        OpenSettingsCommand = new AsyncRelayCommand(
            async ct =>
            {
                await guide.OpenSettingsAsync(ct).ConfigureAwait(true);
            },
            onError: ReportFailure);

        // The sentence and the action label are formatted here, not bound as keys, so a language switch has to re-read
        // them; the section is disposed with the composition, which drops this subscription.
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Sends one real desktop notification; disabled while it runs.</summary>
    public ICommand SendTestCommand { get; }

    /// <summary>Opens the platform's notification settings; disabled while the attempt runs.</summary>
    public ICommand OpenSettingsCommand { get; }

    /// <summary>The sentence describing <see cref="INotificationPermissionGuide.State"/>, in the current UI language.</summary>
    public string StateText => NotificationPermissionTexts.StateText(guide.State);

    /// <summary>Whether this platform has a notification settings page to open.</summary>
    public bool CanOpenSettings => guide.CanOpenSettings;

    /// <summary>True while a test notification is in flight (the action label switches to its running form).</summary>
    public bool IsSendingTest => sendingTest;

    /// <summary>The label of the test action: its running form while a test is in flight, otherwise the action's own.</summary>
    public string TestActionText => Loc.Instance[sendingTest ? NotificationsStrings.PermissionTestRunning : NotificationsStrings.PermissionTestAction];

    /// <summary>The "ask again" preference, written through to the live <see cref="AppPreferences"/>.</summary>
    public bool Reprompt
    {
        get => preferences.NotificationPermissionReprompt;
        set
        {
            if (preferences.NotificationPermissionReprompt == value)
            {
                return;
            }

            preferences.NotificationPermissionReprompt = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Drops the <see cref="Loc.LanguageChanged"/> subscription; idempotent.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private async Task SendTestAsync(CancellationToken ct)
    {
        if (sendingTest)
        {
            return;
        }

        sendingTest = true;
        OnPropertyChanged(nameof(IsSendingTest));
        OnPropertyChanged(nameof(TestActionText));
        try
        {
            await guide.SendTestAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            // The sentence follows whatever state the guide settled on, so it is re-read whether the test was taken,
            // dropped or cancelled.
            sendingTest = false;
            OnPropertyChanged(nameof(IsSendingTest));
            OnPropertyChanged(nameof(TestActionText));
            OnPropertyChanged(nameof(StateText));
        }
    }

    /// <summary>Re-reads the texts this section computes in C#; the switch's labels are <see cref="Loc"/> keys bound in XAML.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A notification from another thread belongs to a window that is gone (a test host); the section bound to these
        // properties lives on its own thread.
        if (disposed || Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(TestActionText));
    }

    private static void ReportFailure(Exception exception) =>
        AppLogs.Notify.ZLogWarning($"the notification permission section failed: {exception.Message}");
}
