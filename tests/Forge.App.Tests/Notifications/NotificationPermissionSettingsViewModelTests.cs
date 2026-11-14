using System.Windows.Input;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Xunit;

namespace Forge.App.Tests.Notifications;

/// <summary>
/// Pins the <c>notification-permission</c> settings section: the sentence follows the guide's state (before and after a
/// finished test), the test action is disabled while it runs and says so, the open-settings action is only offered where
/// the platform has a page, the "ask again" switch writes the live preference, and a language switch re-renders the two
/// texts the section computes in C#. A fake guide replaces the platform, so nothing is started or opened.
/// </summary>
public sealed class NotificationPermissionSettingsViewModelTests
{
    /// <summary>Upper bound of every wait on a fake; reaching it fails the test instead of hanging.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    // ------------------------------------------------------------ the sentence

    [Theory]
    [InlineData(0, NotificationsStrings.PermissionStateUnknown)]
    [InlineData(1, NotificationsStrings.PermissionStateGranted)]
    [InlineData(2, NotificationsStrings.PermissionStateDenied)]
    [InlineData(3, NotificationsStrings.PermissionStateNotApplicable)]
    public void The_sentence_follows_the_guide(int stateValue, string key)
    {
        // The numbers are the enum members (Unknown, Granted, Denied, NotApplicable): an attribute argument of an internal
        // enum type does not compile in a public test method.
        using var _ = new LanguageScope(UiLanguage.Chinese);
        using var section = new NotificationPermissionSettingsViewModel(
            new AppPreferences(),
            new FakeGuide { State = (NotificationPermissionState)stateValue });

        Assert.Equal(Loc.Instance[key], section.StateText);
        Assert.NotEqual("[" + key + "]", section.StateText);
    }

    [Fact]
    public async Task A_finished_test_updates_the_sentence()
    {
        using var _ = new LanguageScope(UiLanguage.English);
        var guide = new FakeGuide { State = NotificationPermissionState.Unknown, NextState = NotificationPermissionState.Granted };
        using var section = new NotificationPermissionSettingsViewModel(new AppPreferences(), guide);
        Assert.Equal(Loc.Instance[NotificationsStrings.PermissionStateUnknown], section.StateText);

        await RunAsync(section.SendTestCommand);

        Assert.Equal(1, guide.Sent);
        Assert.Equal(Loc.Instance[NotificationsStrings.PermissionStateGranted], section.StateText);
        Assert.Equal(NotificationPermissionState.Granted, guide.State);
    }

    // ------------------------------------------------------------ the test action

    [Fact]
    public async Task The_test_action_is_disabled_and_renamed_while_it_runs()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guide = new FakeGuide { Gate = gate.Task, NextState = NotificationPermissionState.Denied };
        using var section = new NotificationPermissionSettingsViewModel(new AppPreferences(), guide);

        Assert.True(section.SendTestCommand.CanExecute(null));
        Assert.Equal(Loc.Instance[NotificationsStrings.PermissionTestAction], section.TestActionText);
        Assert.False(section.IsSendingTest);

        var idle = IdleAsync(section.SendTestCommand);
        section.SendTestCommand.Execute(null);

        // The body is suspended on the gate: the button says so and cannot be pressed again.
        Assert.False(section.SendTestCommand.CanExecute(null));
        Assert.True(section.IsSendingTest);
        Assert.Equal(Loc.Instance[NotificationsStrings.PermissionTestRunning], section.TestActionText);

        gate.SetResult();
        await idle;

        Assert.True(section.SendTestCommand.CanExecute(null));
        Assert.False(section.IsSendingTest);
        Assert.Equal(Loc.Instance[NotificationsStrings.PermissionTestAction], section.TestActionText);
        Assert.Equal(Loc.Instance[NotificationsStrings.PermissionStateDenied], section.StateText);
        Assert.Equal(1, guide.Sent);
    }

    // ------------------------------------------------------------ the open-settings action

    [Fact]
    public void The_open_settings_action_is_offered_only_where_the_platform_has_a_page()
    {
        using var withPage = new NotificationPermissionSettingsViewModel(
            new AppPreferences(),
            new FakeGuide { CanOpenSettings = true });
        using var withoutPage = new NotificationPermissionSettingsViewModel(
            new AppPreferences(),
            new FakeGuide { CanOpenSettings = false });

        Assert.True(withPage.CanOpenSettings);
        Assert.False(withoutPage.CanOpenSettings);
    }

    [Fact]
    public async Task The_open_settings_action_asks_the_guide()
    {
        var guide = new FakeGuide { CanOpenSettings = true };
        using var section = new NotificationPermissionSettingsViewModel(new AppPreferences(), guide);

        await RunAsync(section.OpenSettingsCommand);

        Assert.Equal(1, guide.Opened);
    }

    // ------------------------------------------------------------ the "ask again" switch

    [Fact]
    public void The_reprompt_switch_writes_the_live_preference_and_raises_a_change()
    {
        var preferences = new AppPreferences();
        using var section = new NotificationPermissionSettingsViewModel(preferences, new FakeGuide());
        var raised = new List<string?>();
        section.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        Assert.True(section.Reprompt);
        Assert.True(preferences.NotificationPermissionReprompt);

        section.Reprompt = false;

        Assert.False(preferences.NotificationPermissionReprompt);
        Assert.Equal([nameof(NotificationPermissionSettingsViewModel.Reprompt)], raised);

        // Setting it to the value it already has changes nothing and says nothing.
        section.Reprompt = false;
        Assert.Single(raised);

        // Written from elsewhere, it is read back without a reload.
        preferences.NotificationPermissionReprompt = true;
        Assert.True(section.Reprompt);
    }

    // ------------------------------------------------------------ language switch and disposal

    [Fact]
    public void A_language_switch_rerenders_the_computed_texts()
    {
        using var chinese = new LanguageScope(UiLanguage.Chinese);
        using var section = new NotificationPermissionSettingsViewModel(
            new AppPreferences(),
            new FakeGuide { State = NotificationPermissionState.Granted });
        var raised = new List<string?>();
        section.PropertyChanged += (_, args) => raised.Add(args.PropertyName);
        var chineseState = section.StateText;
        var chineseAction = section.TestActionText;

        using (new LanguageScope(UiLanguage.English))
        {
            Assert.Contains(nameof(NotificationPermissionSettingsViewModel.StateText), raised);
            Assert.Contains(nameof(NotificationPermissionSettingsViewModel.TestActionText), raised);

            Assert.Equal(Loc.Instance[NotificationsStrings.PermissionStateGranted], section.StateText);
            Assert.Equal(Loc.Instance[NotificationsStrings.PermissionTestAction], section.TestActionText);
            Assert.NotEqual(chineseState, section.StateText);
            Assert.NotEqual(chineseAction, section.TestActionText);
        }
    }

    [Fact]
    public void Reload_re_reads_what_the_shared_guide_knows()
    {
        // The guide is one instance shared with the first-run wizard and its interface has no change event, so the sentence has
        // to be re-raised when the page shows this section again: the text itself is computed, but a bound view never learns
        // that it moved unless Reload says so.
        using var _ = new LanguageScope(UiLanguage.English);
        var guide = new FakeGuide { State = NotificationPermissionState.Unknown };
        using var section = new NotificationPermissionSettingsViewModel(new AppPreferences(), guide);
        var before = section.StateText;
        var raised = new List<string?>();
        section.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        guide.State = NotificationPermissionState.Granted;
        Assert.NotEqual(before, section.StateText);

        section.Reload();

        Assert.Contains(nameof(NotificationPermissionSettingsViewModel.StateText), raised);
        Assert.Contains(nameof(NotificationPermissionSettingsViewModel.CanOpenSettings), raised);
    }

    [Fact]
    public void Dispose_drops_the_language_subscription()
    {
        using var chinese = new LanguageScope(UiLanguage.Chinese);
        var section = new NotificationPermissionSettingsViewModel(new AppPreferences(), new FakeGuide());
        var raised = new List<string?>();
        section.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        section.Dispose();
        section.Dispose();

        using var english = new LanguageScope(UiLanguage.English);
        Assert.Empty(raised);
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Completes once <paramref name="command"/> is idle again (the <see cref="AsyncRelayCommand"/> reports it).</summary>
    private static async Task IdleAsync(ICommand command)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCanExecuteChanged(object? sender, EventArgs args)
        {
            if (command.CanExecute(null))
            {
                idle.TrySetResult();
            }
        }

        // Subscribed before the caller executes the command; an async method runs to its first await synchronously.
        command.CanExecuteChanged += OnCanExecuteChanged;
        try
        {
            await idle.Task.WaitAsync(Budget);
        }
        finally
        {
            command.CanExecuteChanged -= OnCanExecuteChanged;
        }
    }

    /// <summary>Executes a command and waits until it is idle again; fails (not hangs) after <see cref="Budget"/>.</summary>
    private static async Task RunAsync(ICommand command)
    {
        var idle = IdleAsync(command);
        command.Execute(null);
        await idle;
    }

    /// <summary>A guide whose state and open-settings answer the test decides, and whose test notification can be held open.</summary>
    private sealed class FakeGuide : INotificationPermissionSection
    {
        /// <summary>When set, <see cref="SendTestAsync"/> stays suspended on it until the test completes it.</summary>
        public Task? Gate { get; init; }

        public NotificationPermissionState NextState { get; init; } = NotificationPermissionState.Granted;

        public int Sent { get; private set; }

        public int Opened { get; private set; }

        public NotificationPermissionState State { get; set; } = NotificationPermissionState.Unknown;

        public bool CanOpenSettings { get; init; }

        /// <summary>The read the frozen seam asks for: a fake answers what it was told and counts the call.</summary>
        public Task<NotificationPermissionState> GetStateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(State);
        }

        public int Reads { get; private set; }

        public async Task<NotificationPermissionState> SendTestAsync(CancellationToken ct)
        {
            Sent++;
            if (Gate is { } gate)
            {
                await gate.WaitAsync(ct).ConfigureAwait(true);
            }

            State = NextState;
            return State;
        }

        public Task<bool> OpenSettingsAsync(CancellationToken ct)
        {
            Opened++;
            return Task.FromResult(CanOpenSettings);
        }
    }
}