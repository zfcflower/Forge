using System.Diagnostics;
using Forge.App.Localization;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Notifications;

/// <summary>
/// What the settings section needs on top of the frozen seam wave W0 declared: the state it can bind synchronously, whether
/// there is a settings page to offer, and the test notification that is the only way to learn the state at all.
/// <para>It extends <see cref="INotificationPermissionGuide"/> instead of adding members to it, because that interface is the
/// contract wave W9 already calls; the section asks for this wider facet, a test fakes it, and the wizard never sees it.</para>
/// </summary>
internal interface INotificationPermissionSection : INotificationPermissionGuide
{
    /// <summary>The state as this process knows it, without asking anything; cheap and thread-safe.</summary>
    NotificationPermissionState State { get; }

    /// <summary>Whether this platform has a page the user can open to change the permission.</summary>
    bool CanOpenSettings { get; }

    /// <summary>Raises one real desktop notification and returns the state it settles on.</summary>
    Task<NotificationPermissionState> SendTestAsync(CancellationToken ct);
}

/// <summary>The sentences of one <see cref="NotificationPermissionState"/>, shared by the settings section and wave W9.</summary>
internal static class NotificationPermissionTexts
{
    /// <summary>The <see cref="Loc"/> key of <paramref name="state"/>'s sentence.</summary>
    public static string StateKey(NotificationPermissionState state) => state switch
    {
        NotificationPermissionState.Granted => NotificationsStrings.PermissionStateGranted,
        NotificationPermissionState.Denied => NotificationsStrings.PermissionStateDenied,
        NotificationPermissionState.Unknown => NotificationsStrings.PermissionStateUnknown,
        _ => NotificationsStrings.PermissionStateNotApplicable,
    };

    /// <summary>The sentence of <paramref name="state"/> right now; read on every call, so a language switch applies.</summary>
    public static string StateText(NotificationPermissionState state) => Loc.Instance[StateKey(state)];
}

/// <summary>
/// The guide over the platform's own notification tool. The state starts as
/// <see cref="NotificationPermissionState.NotApplicable"/> when this build has no tool for the platform and as
/// <see cref="NotificationPermissionState.Unknown"/> otherwise, and only a real test notification settles it to
/// <see cref="NotificationPermissionState.Granted"/> or <see cref="NotificationPermissionState.Denied"/>.
/// <para>The probe is a real notification, not a permission query: no platform in the supported set lets an unpackaged
/// program ask whether its notifications are switched on (Windows would need an installed AppUserModelID), so the honest
/// answer is "unknown until something was actually sent"; a dropped notification comes back as a non-zero exit code or a
/// failing tool and settles the state as denied. Test results are remembered for the life of the process; the OS can still
/// change its mind, which is why the section also offers the settings page.</para>
/// <para>Implements the seam wave W0 declared: the first-run wizard (wave W9) calls only
/// <see cref="INotificationPermissionGuide"/>, while the settings section uses the concrete type because it also offers the
/// test notification that settles the state.</para>
/// </summary>
internal sealed class SystemNotificationPermissionGuide : INotificationPermissionSection
{
    /// <summary>How long opening the platform's settings page may take before the attempt is abandoned.</summary>
    public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The notification page of Windows Settings; <c>UseShellExecute</c> hands it to the shell.</summary>
    internal static readonly Uri WindowsSettingsUri = new("ms-settings:notifications");

    /// <summary>The Notifications pane of macOS System Settings.</summary>
    internal static readonly Uri MacOSSettingsUri = new("x-apple.systempreferences:com.apple.preference.notifications");

    private readonly ISystemNotifier notifier;
    private readonly Func<Uri, CancellationToken, Task<bool>> open;
    private readonly NotifyPlatform platform;
    private readonly TimeProvider time;

    /// <summary>Written when a test notification settles; one enum, so a plain volatile field is enough (no lock, readable anywhere).</summary>
    private volatile NotificationPermissionState state;

    /// <param name="notifier">Raises the test notification.</param>
    /// <param name="open">
    /// Opens a URI and reports whether it could be opened; null starts the shell with <c>UseShellExecute</c>. A test passes
    /// a recording fake, so nothing leaves the process.
    /// </param>
    /// <param name="platform">The family to talk to; the running OS when null.</param>
    /// <param name="time">Clock of <see cref="OpenTimeout"/>; a test passes a fake one.</param>
    public SystemNotificationPermissionGuide(
        ISystemNotifier notifier,
        Func<Uri, CancellationToken, Task<bool>>? open = null,
        NotifyPlatform? platform = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(notifier);
        this.notifier = notifier;
        this.open = open ?? OpenWithShellAsync;
        this.platform = platform ?? SystemNotifier.CurrentPlatform();
        this.time = time ?? TimeProvider.System;
        state = this.platform == NotifyPlatform.Unsupported
            ? NotificationPermissionState.NotApplicable
            : NotificationPermissionState.Unknown;
    }

    /// <summary>The state as this process knows it, without asking anything; the section binds to this.</summary>
    public NotificationPermissionState State => state;

    /// <summary>Whether this platform has a page the user can open to change the permission.</summary>
    public bool CanOpenSettings => SettingsUri is not null;

    /// <summary>The settings page of this platform, or null when it has none.</summary>
    internal Uri? SettingsUri => platform switch
    {
        NotifyPlatform.Windows => WindowsSettingsUri,
        NotifyPlatform.MacOS => MacOSSettingsUri,
        _ => null,
    };

    /// <inheritdoc />
    /// <remarks>
    /// Nothing is sent here: the state can only come from a real notification (<see cref="SendTestAsync"/>), so a read
    /// answers what the last one said. It stays async because the seam wave W9 was written against is async, and a future
    /// platform that *can* be queried must be able to answer without changing the contract.
    /// </remarks>
    public Task<NotificationPermissionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(state);
    }

    /// <summary>
    /// Raises one real desktop notification and returns the state it settles on. The caller's cancellation rethrows and
    /// leaves the state untouched; every other failure settles <see cref="NotificationPermissionState.Denied"/> plus a
    /// <c>[notify]</c> trace line, never an exception.
    /// </summary>
    public async Task<NotificationPermissionState> SendTestAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (platform == NotifyPlatform.Unsupported)
        {
            // Nothing was sent, so nothing was learned: the state stays not-applicable.
            AppLogs.Notify.ZLogInformation($"skipped: no notification tool for this platform, so no test notification was sent");
            return NotificationPermissionState.NotApplicable;
        }

        bool taken;
        try
        {
            taken = await notifier.TryNotifyAsync(
                NotificationsStrings.PermissionTestTitle.Resolve(),
                NotificationsStrings.PermissionTestBody.Resolve(),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller's cancellation is the one thing that propagates, and it leaves the state as it was.
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Notify.ZLogWarning($"the test notification failed: {exception.Message}");
            taken = false;
        }

        var settled = taken ? NotificationPermissionState.Granted : NotificationPermissionState.Denied;
        state = settled;
        return settled;
    }

    /// <inheritdoc />
    public async Task<bool> OpenSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SettingsUri is not { } uri)
        {
            AppLogs.Notify.ZLogInformation($"skipped: this platform has no notification settings page");
            return false;
        }

        using var limit = new CancellationTokenSource(OpenTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            // WaitAsync bounds the wait even when a (test) opener ignores the token it was handed.
            return await open(uri, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLogs.Notify.ZLogWarning($"opening {uri.AbsoluteUri} did not finish within {OpenTimeout.TotalSeconds} seconds");
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLogs.Notify.ZLogWarning($"opening {uri.AbsoluteUri} failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// The default opener: the shell starts the protocol handler. The URI is a fixed constant of this class, never text
    /// from the user or a pull request, and it travels as one <see cref="ProcessStartInfo.FileName"/> - no command line is
    /// assembled anywhere.
    /// <para>The launch runs on the pool, not on the caller's thread: <c>ShellExecuteEx</c> blocks until the handler accepts
    /// the request, so a cold or wedged Settings app would otherwise freeze the click (and the UI thread under it) with no
    /// way for <see cref="OpenTimeout"/> to cut it short.</para>
    /// </summary>
    private static Task<bool> OpenWithShellAsync(Uri uri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(
            () =>
            {
                ct.ThrowIfCancellationRequested();

                // The shell's verdict is "no exception": a shell-executed protocol handler may hand the request to an
                // already running Settings app, and Process.Start then returns null even though the page opened. A scheme
                // without a handler throws Win32Exception, which the caller traces and reports as false.
                using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return true;
            },
            ct);
    }
}
