using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Forge.App.Tests.Logging;
using Xunit;

namespace Forge.App.Tests.Notifications;

/// <summary>
/// Pins <see cref="SystemNotificationPermissionGuide"/>: the state starts where the platform puts it (no tool means
/// <see cref="NotificationPermissionState.NotApplicable"/>, anything else <see cref="NotificationPermissionState.Unknown"/>)
/// and only a real test notification settles it; the settings page is opened with the platform's own URI, is never
/// attempted where there is none, and a failing opener is a false plus a <c>[notify]</c> trace line instead of an
/// exception. A recording notifier and a recording opener replace the process start, so nothing is launched and no test
/// depends on the machine having PowerShell, osascript, notify-send, a desktop session or a Settings app.
/// </summary>
public sealed class NotificationPermissionGuideTests
{
    [Fact]
    public async Task A_platform_without_a_tool_starts_unsupported_and_a_test_stays_there()
    {
        var notifier = new GuideFakeNotifier();
        var opener = new GuideFakeOpener();
        var guide = new SystemNotificationPermissionGuide(notifier, opener.OpenAsync, NotifyPlatform.Unsupported);

        Assert.Equal(NotificationPermissionState.NotApplicable, guide.State);
        Assert.False(guide.CanOpenSettings);
        Assert.Null(guide.SettingsUri);

        Assert.Equal(NotificationPermissionState.NotApplicable, await guide.SendTestAsync(CancellationToken.None));

        Assert.Equal(NotificationPermissionState.NotApplicable, guide.State);
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task A_notifier_that_throws_settles_denied_and_traces_the_failure()
    {
        // The shipped notifier swallows its own failures, so this is the defensive branch: a notifier that throws must still
        // leave a state the page can show, never an exception in the click handler.
        using var trace = new LogCapture();
        var notifier = new GuideFakeNotifier { Failure = new InvalidOperationException("powershell.exe is gone") };
        var guide = new SystemNotificationPermissionGuide(notifier, new GuideFakeOpener().OpenAsync, NotifyPlatform.Windows);

        Assert.Equal(NotificationPermissionState.Denied, await guide.SendTestAsync(CancellationToken.None));
        Assert.Equal(NotificationPermissionState.Denied, guide.State);
        Assert.Contains(
            trace.Lines,
            static line => line.Contains("[notify] the test notification failed: powershell.exe is gone", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void A_supported_platform_starts_unknown_and_knows_whether_it_has_a_settings_page(
        int platformValue,
        bool hasSettingsPage)
    {
        // The numbers are the enum members in declaration order (Unsupported 0, Windows 1, MacOS 2, Linux 3): an attribute
        // argument of an internal enum type does not compile in a public test method.
        var guide = new SystemNotificationPermissionGuide(
            new GuideFakeNotifier(),
            platform: (NotifyPlatform)platformValue);

        Assert.Equal(NotificationPermissionState.Unknown, guide.State);
        Assert.Equal(hasSettingsPage, guide.CanOpenSettings);
    }

    [Fact]
    public async Task A_test_notification_the_platform_took_settles_allowed()
    {
        var notifier = new GuideFakeNotifier { Taken = true };
        var guide = new SystemNotificationPermissionGuide(notifier, platform: NotifyPlatform.Windows);

        Assert.Equal(NotificationPermissionState.Granted, await guide.SendTestAsync(CancellationToken.None));

        Assert.Equal(NotificationPermissionState.Granted, guide.State);
        Assert.Single(notifier.Calls);
    }

    [Fact]
    public async Task A_test_notification_the_platform_dropped_settles_denied()
    {
        var notifier = new GuideFakeNotifier { Taken = false };
        var guide = new SystemNotificationPermissionGuide(notifier, platform: NotifyPlatform.Linux);

        Assert.Equal(NotificationPermissionState.Denied, await guide.SendTestAsync(CancellationToken.None));

        Assert.Equal(NotificationPermissionState.Denied, guide.State);

        // Dropping is a result, not a failure: no exception, and the notifier has already traced why.
        Assert.Single(notifier.Calls);
    }

    [Fact]
    public async Task A_second_test_overrides_the_first()
    {
        var notifier = new GuideFakeNotifier { Taken = true };
        var guide = new SystemNotificationPermissionGuide(notifier, platform: NotifyPlatform.MacOS);

        Assert.Equal(NotificationPermissionState.Granted, await guide.SendTestAsync(CancellationToken.None));

        notifier.Taken = false;
        Assert.Equal(NotificationPermissionState.Denied, await guide.SendTestAsync(CancellationToken.None));
        Assert.Equal(NotificationPermissionState.Denied, guide.State);
        Assert.Equal(2, notifier.Calls.Count);

        notifier.Taken = true;
        Assert.Equal(NotificationPermissionState.Granted, await guide.SendTestAsync(CancellationToken.None));
        Assert.Equal(NotificationPermissionState.Granted, guide.State);
    }

    [Fact]
    public async Task The_test_notification_carries_the_localized_title_and_body()
    {
        using var _ = new LanguageScope(UiLanguage.English);
        var notifier = new GuideFakeNotifier();
        var guide = new SystemNotificationPermissionGuide(notifier, platform: NotifyPlatform.Windows);

        await guide.SendTestAsync(CancellationToken.None);

        var call = Assert.Single(notifier.Calls);
        Assert.Equal(Loc.Instance[NotificationsStrings.PermissionTestTitleKey], call.Title);
        Assert.Equal(Loc.Instance[NotificationsStrings.PermissionTestBodyKey], call.Body);
        Assert.DoesNotContain('[', call.Title);
        Assert.DoesNotContain('[', call.Body);
    }

    [Fact]
    public async Task A_cancelled_token_throws_and_sends_nothing()
    {
        var notifier = new GuideFakeNotifier();
        var guide = new SystemNotificationPermissionGuide(notifier, platform: NotifyPlatform.Windows);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guide.SendTestAsync(cancellation.Token));

        Assert.Empty(notifier.Calls);
        Assert.Equal(NotificationPermissionState.Unknown, guide.State);
    }

    // ------------------------------------------------------------ opening the platform's own page

    [Fact]
    public async Task Opening_the_settings_page_uses_the_windows_uri_and_returns_the_openers_result()
    {
        var opener = new GuideFakeOpener { Result = true };
        var guide = new SystemNotificationPermissionGuide(new GuideFakeNotifier(), opener.OpenAsync, NotifyPlatform.Windows);

        Assert.True(await guide.OpenSettingsAsync(CancellationToken.None));
        Assert.Equal("ms-settings:notifications", Assert.Single(opener.Opened).AbsoluteUri);
        Assert.Equal(SystemNotificationPermissionGuide.WindowsSettingsUri, guide.SettingsUri);

        opener.Result = false;
        Assert.False(await guide.OpenSettingsAsync(CancellationToken.None));
        Assert.Equal(2, opener.Opened.Count);
    }

    [Fact]
    public async Task Opening_the_settings_page_uses_the_macos_uri()
    {
        var opener = new GuideFakeOpener();
        var guide = new SystemNotificationPermissionGuide(new GuideFakeNotifier(), opener.OpenAsync, NotifyPlatform.MacOS);

        Assert.True(await guide.OpenSettingsAsync(CancellationToken.None));

        Assert.Equal(
            "x-apple.systempreferences:com.apple.preference.notifications",
            Assert.Single(opener.Opened).AbsoluteUri);
        Assert.Equal(SystemNotificationPermissionGuide.MacOSSettingsUri, guide.SettingsUri);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task A_platform_without_a_settings_page_returns_false_without_calling_the_opener(int platformValue)
    {
        // 0 = Unsupported, 3 = Linux (the enum's declaration order); Windows and macOS are the two with a page.
        using var trace = new LogCapture();
        var opener = new GuideFakeOpener();
        var guide = new SystemNotificationPermissionGuide(
            new GuideFakeNotifier(),
            opener.OpenAsync,
            (NotifyPlatform)platformValue);

        Assert.False(await guide.OpenSettingsAsync(CancellationToken.None));

        Assert.Empty(opener.Opened);
        Assert.Contains(
            trace.Lines,
            static line => line.Contains("[notify] skipped: this platform has no notification settings page", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_throwing_opener_yields_false_and_a_traced_failure_instead_of_an_exception()
    {
        using var trace = new LogCapture();
        var opener = new GuideFakeOpener { Failure = new InvalidOperationException("no-settings-app") };
        var guide = new SystemNotificationPermissionGuide(new GuideFakeNotifier(), opener.OpenAsync, NotifyPlatform.Windows);

        Assert.False(await guide.OpenSettingsAsync(CancellationToken.None));

        Assert.Single(opener.Opened);
        Assert.Contains(
            trace.Lines,
            static line => line.Contains("[notify] opening ms-settings:notifications failed: no-settings-app", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_opener_that_never_finishes_is_abandoned_after_the_open_timeout()
    {
        using var trace = new LogCapture();
        var time = new NotifyControlledTime();
        var guide = new SystemNotificationPermissionGuide(
            new GuideFakeNotifier(),
            static (_, _) => new TaskCompletionSource<bool>().Task,
            NotifyPlatform.MacOS,
            time);

        var pending = guide.OpenSettingsAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);

        time.Advance(SystemNotificationPermissionGuide.OpenTimeout);

        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(
            trace.Lines,
            static line => line.Contains("did not finish within 5 seconds", StringComparison.Ordinal));
        Assert.Equal(0, time.ActiveTimers);
    }

    [Fact]
    public void The_open_timeout_is_five_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), SystemNotificationPermissionGuide.OpenTimeout);
    }

    // ------------------------------------------------------------ fakes

    /// <summary>A notifier that records the texts it was asked to show and reports what the platform would have answered.</summary>
    private sealed class GuideFakeNotifier : ISystemNotifier
    {
        private readonly List<(string Title, string Body)> calls = [];
        private readonly object gate = new();

        /// <summary>What <see cref="TryNotifyAsync"/> reports; false is a notification the platform dropped.</summary>
        public bool Taken { get; set; } = true;

        /// <summary>When set, the notifier throws it instead of answering, like a tool that vanished under the request.</summary>
        public Exception? Failure { get; set; }

        public IReadOnlyList<(string Title, string Body)> Calls
        {
            get
            {
                lock (gate)
                {
                    return [.. calls];
                }
            }
        }

        public Task NotifyAsync(string title, string body, CancellationToken ct) => TryNotifyAsync(title, body, ct);

        public Task<bool> TryNotifyAsync(string title, string body, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                calls.Add((title, body));
            }

            return Failure is { } failure ? Task.FromException<bool>(failure) : Task.FromResult(Taken);
        }
    }

    /// <summary>An opener that records every URI it was handed and answers with a fixed result or throws a fixed failure.</summary>
    private sealed class GuideFakeOpener
    {
        private readonly List<Uri> opened = [];
        private readonly object gate = new();

        public bool Result { get; set; } = true;

        /// <summary>When set, the opener throws it instead of answering, like a system without a handler for the scheme.</summary>
        public Exception? Failure { get; set; }

        public IReadOnlyList<Uri> Opened
        {
            get
            {
                lock (gate)
                {
                    return [.. opened];
                }
            }
        }

        public Task<bool> OpenAsync(Uri uri, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                opened.Add(uri);
            }

            if (Failure is { } failure)
            {
                throw failure;
            }

            return Task.FromResult(Result);
        }
    }
}
