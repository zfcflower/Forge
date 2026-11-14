using System.Diagnostics;
using System.Text;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Notifications;

/// <summary>
/// Raises one desktop notification. Implementations never throw except for cancellation. The switch that decides whether a
/// notification happens at all lives one layer up, in <see cref="NotificationCenter"/>.
/// </summary>
internal interface ISystemNotifier
{
    /// <summary>
    /// Shows <paramref name="title"/> and <paramref name="body"/> as a system notification. A notification that cannot be
    /// shown (no tool on this machine, the tool failed or hung) is skipped after a
    /// <c>[notify]</c> trace line; only <paramref name="ct"/> being cancelled throws. The outcome is not surfaced:
    /// <see cref="NotificationCenter"/> never acts on it, and a caller that has to know asks <see cref="TryNotifyAsync"/>.
    /// </summary>
    Task NotifyAsync(string title, string body, CancellationToken ct);

    /// <summary>
    /// The same notification as <see cref="NotifyAsync"/>, reporting whether the platform took it: true when the tool
    /// accepted it (exit code 0), false when this build has no tool for the platform, the tool failed, or it hung and was
    /// abandoned. Only <paramref name="ct"/> being cancelled throws. True means "handed to the platform", not "the user saw
    /// it": an OS with its notifications switched off still answers 0.
    /// </summary>
    Task<bool> TryNotifyAsync(string title, string body, CancellationToken ct);
}

/// <summary>The desktop families the notifier knows how to talk to; the seam that lets a test pick one on any machine.</summary>
internal enum NotifyPlatform
{
    /// <summary>No supported notification mechanism: the notification is skipped.</summary>
    Unsupported,
    Windows,
    MacOS,
    Linux,
}

/// <summary>
/// Desktop notification by the tool the platform already has, started as a child process:
/// <list type="bullet">
/// <item>Windows: Windows PowerShell (<c>powershell.exe -NoProfile -NonInteractive -Command</c>) raising a toast through the
/// WinRT <c>ToastNotificationManager</c>. The toast is attributed to Windows PowerShell's own application id: an
/// unpackaged program has no identity of its own, and registering one needs a Start Menu shortcut with an
/// AppUserModelID, which Forge does not install.</item>
/// <item>macOS: <c>osascript</c> running <c>display notification</c>.</item>
/// <item>Linux: <c>notify-send</c>.</item>
/// </list>
/// The title and body never become part of a script or a command line that a shell or interpreter parses: Windows passes
/// them through two environment variables the (fixed) script reads, macOS as the arguments of the AppleScript
/// <c>run</c> handler, Linux behind <c>--</c> in <see cref="ProcessStartInfo.ArgumentList"/>. So text from a pull request
/// cannot inject anything.
/// <para>Bounded: a notification that takes longer than <see cref="Timeout"/> is abandoned (the default runner kills the
/// whole process tree), and a missing tool, a non-zero exit code or any other failure is traced and, on
/// <see cref="TryNotifyAsync"/>, reported as not taken (<see cref="NotifyAsync"/> drops that result, as
/// <see cref="NotificationCenter"/> always did). The macOS and Linux
/// paths are written against the tools' documented interfaces and have not been run by the developers of this build; the
/// Windows path has.</para>
/// </summary>
internal sealed class SystemNotifier : ISystemNotifier
{
    /// <summary>How long one notification may take before it is abandoned.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Names of the two environment variables that carry the text to the Windows script.</summary>
    internal const string TitleVariable = "FORGE_NOTIFY_TITLE";

    internal const string BodyVariable = "FORGE_NOTIFY_BODY";

    /// <summary>
    /// The fixed script of the Windows toast. Statements are separated by semicolons on one line, so the single
    /// <c>-Command</c> argument needs no quoting beyond what <see cref="ProcessStartInfo.ArgumentList"/> adds. Text is put
    /// into the toast with <c>CreateTextNode</c>, which makes XML escaping the platform's job.
    /// </summary>
    internal const string WindowsScript =
        "$ErrorActionPreference = 'Stop'; " +
        "[void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]; " +
        "[void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]; " +
        "$xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02); " +
        "$nodes = $xml.GetElementsByTagName('text'); " +
        "[void]$nodes.Item(0).AppendChild($xml.CreateTextNode($env:" + TitleVariable + ")); " +
        "[void]$nodes.Item(1).AppendChild($xml.CreateTextNode($env:" + BodyVariable + ")); " +
        "$toast = [Windows.UI.Notifications.ToastNotification]::new($xml); " +
        "$appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe'; " +
        "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)";

    private const int MaxCapturedErrorCharacters = 1024;

    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> run;
    private readonly NotifyPlatform platform;
    private readonly TimeProvider time;

    /// <param name="run">
    /// Starts the process and returns its exit code. It must honour the token (cancel = kill the process tree) and may throw
    /// when the tool cannot be started; the default does both. A test passes a recording fake.
    /// </param>
    /// <param name="platform">The family to talk to; the running OS when null.</param>
    /// <param name="time">Clock of the 5 second limit; a test passes a fake one.</param>
    public SystemNotifier(
        Func<ProcessStartInfo, CancellationToken, Task<int>>? run = null,
        NotifyPlatform? platform = null,
        TimeProvider? time = null)
    {
        this.run = run ?? RunProcessAsync;
        this.platform = platform ?? CurrentPlatform();
        this.time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task NotifyAsync(string title, string body, CancellationToken ct) => TryNotifyAsync(title, body, ct);

    /// <inheritdoc />
    public async Task<bool> TryNotifyAsync(string title, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        ct.ThrowIfCancellationRequested();

        var info = CreateStartInfo(title, body);
        if (info is null)
        {
            AppLogs.Notify.ZLogInformation($"skipped: no notification tool for this platform");
            return false;
        }

        using var limit = new CancellationTokenSource(Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        try
        {
            var exitCode = await run(info, linked.Token).ConfigureAwait(false);
            if (exitCode != 0)
            {
                AppLogs.Notify.ZLogWarning($"{info.FileName} exited with code {exitCode}");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLogs.Notify.ZLogWarning($"{info.FileName} did not finish within {Timeout.TotalSeconds} seconds");
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLogs.Notify.ZLogWarning($"{info.FileName} failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// The command line of one notification, or null when the platform has no supported tool. Pure (starts nothing), so the
    /// exact arguments are testable.
    /// </summary>
    internal ProcessStartInfo? CreateStartInfo(string title, string body)
    {
        ProcessStartInfo info;
        switch (platform)
        {
            case NotifyPlatform.Windows:
                info = new ProcessStartInfo("powershell.exe");
                info.ArgumentList.Add("-NoProfile");
                info.ArgumentList.Add("-NonInteractive");
                info.ArgumentList.Add("-Command");
                info.ArgumentList.Add(WindowsScript);
                info.Environment[TitleVariable] = title;
                info.Environment[BodyVariable] = body;
                break;
            case NotifyPlatform.MacOS:
                // `on run argv` receives the two trailing arguments; neither is ever part of the script text.
                info = new ProcessStartInfo("osascript");
                info.ArgumentList.Add("-e");
                info.ArgumentList.Add("on run argv");
                info.ArgumentList.Add("-e");
                info.ArgumentList.Add("display notification (item 2 of argv) with title (item 1 of argv)");
                info.ArgumentList.Add("-e");
                info.ArgumentList.Add("end run");
                info.ArgumentList.Add(title);
                info.ArgumentList.Add(body);
                break;
            case NotifyPlatform.Linux:
                info = new ProcessStartInfo("notify-send");
                info.ArgumentList.Add("--app-name=Forge");
                info.ArgumentList.Add("--");
                info.ArgumentList.Add(title);
                info.ArgumentList.Add(body);
                break;
            default:
                return null;
        }

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        return info;
    }

    /// <summary>The platform family this process runs on; shared with <see cref="SystemNotificationPermissionGuide"/>.</summary>
    internal static NotifyPlatform CurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return NotifyPlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return NotifyPlatform.MacOS;
        }

        return OperatingSystem.IsLinux() ? NotifyPlatform.Linux : NotifyPlatform.Unsupported;
    }

    /// <summary>
    /// The default runner. Stdin is closed at once and both output streams are drained (a full pipe would stall the tool),
    /// and cancelling the token kills the whole process tree before the cancellation propagates.
    /// </summary>
    private static async Task<int> RunProcessAsync(ProcessStartInfo info, CancellationToken ct)
    {
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("The process could not be started.");
        var error = new StringBuilder();
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null && error.Length < MaxCapturedErrorCharacters)
            {
                lock (error)
                {
                    error.AppendLine(e.Data);
                }
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            string captured;
            lock (error)
            {
                captured = error.ToString().Trim();
            }

            if (captured.Length > 0)
            {
                // The tool may echo the notification title or body, so only its size goes into the log.
                AppLogs.Notify.ZLogDebug($"exit {process.ExitCode}, {captured.Length} chars on stderr");
            }
        }

        return process.ExitCode;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone, or not ours to kill any more: nothing left to clean up.
        }
    }
}
