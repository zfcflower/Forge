using System.Diagnostics;
using Forge.App.Localization;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Notifications;

/// <summary>
/// What this build knows about the system's notification permission, in the order it comes to know it:
/// <list type="bullet">
/// <item><see cref="Unsupported"/>: this build has no notification tool for the platform, so nothing can be shown or
/// asked;</item>
/// <item><see cref="Unknown"/>: the platform cannot be asked, and nothing has been tried yet;</item>
/// <item><see cref="Allowed"/> / <see cref="Denied"/>: the result of a real test notification (taken, or dropped).</item>
/// </list>
/// </summary>
internal enum NotificationPermissionState
{
    /// <summary>This build has no notification tool for the platform; no test is attempted.</summary>
    Unsupported,

    /// <summary>The platform cannot be asked and nothing was tried yet.</summary>
    Unknown,

    /// <summary>A test notification was handed to the platform.</summary>
    Allowed,

    /// <summary>A test notification was not taken: a missing tool, a failed tool or an OS that dropped it.</summary>
    Denied,
}

/// <summary>
/// Tells the user whether desktop notifications can arrive, and gets them there: the settings section
/// (<see cref="NotificationPermissionSettingsViewModel"/>) and the first-run wizard (wave W9) both go through this seam,
/// so neither of them talks to the platform itself.
/// <para>No implementation throws except for cancellation. A failure is a state (<see cref="NotificationPermissionState.Denied"/>)
/// and a <c>[notify]</c> trace line, never an exception a caller has to handle.</para>
/// </summary>
internal interface INotificationPermissionGuide
{
    /// <summary>The best current knowledge; cheap to read from any thread.</summary>
    NotificationPermissionState State { get; }

    /// <summary>Whether this platform has a page the user can open to change the permission.</summary>
    bool CanOpenSettings { get; }

    /// <summary>Raises one real desktop notification and returns the state it settles on.</summary>
    Task<NotificationPermissionState> SendTestAsync(CancellationToken ct);

    /// <summary>
    /// Opens the platform's notification settings; false when there is no such page or it could not be opened. Never
    /// throws except for cancellation.
    /// </summary>
    Task<bool> OpenSettingsAsync(CancellationToken ct);
}

/// <summary>The sentences of one <see cref="NotificationPermissionState"/>, shared by the settings section and wave W9.</summary>
internal static class NotificationPermissionTexts
{
    /// <summary>The <see cref="Loc"/> key of <paramref name="state"/>'s sentence.</summary>
    public static string StateKey(NotificationPermissionState state) => state switch
    {
        NotificationPermissionState.Allowed => NotificationsStrings.PermissionStateAllowed,
        NotificationPermissionState.Denied => NotificationsStrings.PermissionStateDenied,
        NotificationPermissionState.Unknown => NotificationsStrings.PermissionStateUnknown,
        _ => NotificationsStrings.PermissionStateUnsupported,
    };

    /// <summary>The sentence of <paramref name="state"/> right now; read on every call, so a language switch applies.</summary>
    public static string StateText(NotificationPermissionState state) => Loc.Instance[StateKey(state)];
}

/// <summary>
/// The guide over the platform's own notification tool. The state starts as <see cref="NotificationPermissionState.Unsupported"/>
/// when this build has no tool for the platform and as <see cref="NotificationPermissionState.Unknown"/> otherwise, and only
/// a real test notification settles it to allowed or denied.
/// <para>The probe is a real notification, not a permission query: no platform in the supported set lets an unpackaged
/// program ask whether its notifications are switched on (Windows would need an installed AppUserModelID), so the honest
/// answer is "unknown until something was actually sent"; a dropped notification comes back as a non-zero exit code or a
/// failing tool and settles the state as denied. Test results are remembered for the life of the process; the OS can still
/// change its mind, which is why the section also offers the settings page.</para>
/// </summary>
internal sealed class SystemNotificationPermissionGuide : INotificationPermissionGuide
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
            ? NotificationPermissionState.Unsupported
            : NotificationPermissionState.Unknown;
    }

    /// <inheritdoc />
    public NotificationPermissionState State => state;

    /// <inheritdoc />
    public bool CanOpenSettings => SettingsUri is not null;

    /// <summary>The settings page of this platform, or null when it has none.</summary>
    internal Uri? SettingsUri => platform switch
    {
        NotifyPlatform.Windows => WindowsSettingsUri,
        NotifyPlatform.MacOS => MacOSSettingsUri,
        _ => null,
    };

    /// <inheritdoc />
    public async Task<NotificationPermissionState> SendTestAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (platform == NotifyPlatform.Unsupported)
        {
            // Nothing was sent, so nothing was learned: the state stays unsupported.
            AppLogs.Notify.ZLogInformation($"skipped: no notification tool for this platform, so no test notification was sent");
            return NotificationPermissionState.Unsupported;
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

        var settled = taken ? NotificationPermissionState.Allowed : NotificationPermissionState.Denied;
        state = settled;
        return settled;
    }

    /// <inheritdoc />
    public async Task<bool> OpenSettingsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (SettingsUri is not { } uri)
        {
            AppLogs.Notify.ZLogInformation($"skipped: this platform has no notification settings page");
            return false;
        }

        using var limit = new CancellationTokenSource(OpenTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        try
        {
            // WaitAsync bounds the wait even when a (test) opener ignores the token it was handed.
            return await open(uri, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
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
    /// </summary>
    private static Task<bool> OpenWithShellAsync(Uri uri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // The shell's verdict is "no exception": a shell-executed protocol handler may hand the request to an already
        // running Settings app, and Process.Start then returns null even though the page opened. A scheme without a
        // handler throws Win32Exception, which the caller traces and reports as false.
        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.FromResult(true);
    }
}
