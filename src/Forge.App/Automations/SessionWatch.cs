using System.Globalization;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Notifications;

namespace Forge.App.Automations;

/// <summary>What the session watch saw; carried in the run's detail as <c>kind\nsessionId\nminutes\ntitle</c>.</summary>
internal enum WatchKind
{
    Stalled,
    Approval,
    Finished,

    /// <summary>No agent event for a long time, but a tool call (a build, a long command) is still executing.</summary>
    ToolRunning,
}

internal sealed record WatchFinding(WatchKind Kind, string SessionId, int Minutes, string Title)
{
    public string Serialize() => string.Join(
        '\n',
        Kind.ToString(),
        SessionId,
        Minutes.ToString(CultureInfo.InvariantCulture),
        Title.Replace('\n', ' '));

    public static WatchFinding? Parse(string? detail)
    {
        var parts = detail?.Split('\n');
        if (parts is not { Length: >= 4 } ||
            !Enum.TryParse<WatchKind>(parts[0], out var kind) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return null;
        }

        return new WatchFinding(kind, parts[1], minutes, parts[3]);
    }
}

/// <summary>
/// <see cref="BuiltInAutomations.SessionWatch"/>: looks at the open root sessions once a minute and records a run (with a
/// notification) when a turn has made no progress for <see cref="StallAfter"/> (<see cref="ToolStallAfter"/> while a tool call
/// is executing: a long build is silent, not stuck), an approval card has waited
/// <see cref="ApprovalAfter"/>, or a turn finished while the window was in the background. It never acts on a session.
/// </summary>
internal sealed class SessionWatch(
    IAutomationEngine engine,
    IAutomationHost host,
    Func<IReadOnlyList<(string SessionId, DateTimeOffset CreatedAt)>> approvals,
    Func<bool> enabled,
    TimeProvider time)
{
    public static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan ApprovalAfter = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan ToolStallAfter = TimeSpan.FromMinutes(60);

    /// <summary>One look; runs on the UI thread (the host's sessions are UI objects). Returns the findings it started.</summary>
    public IReadOnlyList<WatchFinding> Check()
    {
        if (!enabled())
        {
            return [];
        }

        var now = time.GetUtcNow();
        var found = new List<(WatchFinding Finding, string Occurrence)>();
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var session in host.OpenSessions)
        {
            titles[session.SessionId] = session.Title;
            if (!session.IsRunning || session.IsAutomationClosed)
            {
                continue;
            }

            var quiet = now - session.LastActivityAt;
            var tool = session.IsToolRunning;
            if (quiet >= (tool ? ToolStallAfter : StallAfter))
            {
                found.Add((
                    new WatchFinding(tool ? WatchKind.ToolRunning : WatchKind.Stalled, session.SessionId, (int)quiet.TotalMinutes, session.Title),
                    (tool ? "watch:tool:" : "watch:stall:") + session.SessionId + ":" + session.LastActivityAt.UtcTicks.ToString(CultureInfo.InvariantCulture)));
            }
        }

        foreach (var (sessionId, createdAt) in approvals())
        {
            var waited = now - createdAt;
            if (waited >= ApprovalAfter)
            {
                found.Add((
                    new WatchFinding(WatchKind.Approval, sessionId, (int)waited.TotalMinutes, titles.GetValueOrDefault(sessionId, string.Empty)),
                    "watch:approval:" + sessionId + ":" + createdAt.UtcTicks.ToString(CultureInfo.InvariantCulture)));
            }
        }

        foreach (var (finding, occurrence) in found)
        {
            Start(finding, occurrence);
        }

        return found.ConvertAll(static entry => entry.Finding);
    }

    /// <summary>A root turn ended; worth a run only while the window is in the background.</summary>
    public void OnTurnCompleted(IAutomationSession session, bool automationTurn)
    {
        if (!enabled() || host.IsWindowActive || automationTurn)
        {
            return;
        }

        var now = time.GetUtcNow();
        Start(
            new WatchFinding(WatchKind.Finished, session.SessionId, 0, session.Title),
            "watch:done:" + session.SessionId + ":" + now.UtcTicks.ToString(CultureInfo.InvariantCulture));
    }

    private void Start(WatchFinding finding, string occurrence) =>
        _ = StartAsync(finding, occurrence);

    private async Task StartAsync(WatchFinding finding, string occurrence)
    {
        try
        {
            await engine.StartAsync(
                    new AutomationRunRequest(BuiltInAutomations.SessionWatch, AutomationTriggerSource.Event)
                    {
                        Occurrence = occurrence,
                        TargetSessionId = finding.SessionId,
                        Detail = finding.Serialize(),
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("[automation] session watch start failed: " + exception.Message);
        }
    }
}

/// <summary>Turns one watch finding into its notification; the run itself is the history entry.</summary>
internal sealed class SessionWatchRunner(INotifications? notifications) : IAutomationRunner
{
    public Task<AutomationRunOutcome> RunAsync(IAutomationRunContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (WatchFinding.Parse(context.Run.Detail) is not { } finding)
        {
            return Task.FromResult(AutomationRunOutcome.Skipped(string.Empty));
        }

        var minutes = finding.Minutes.ToString(CultureInfo.InvariantCulture);
        var (level, title, body) = finding.Kind switch
        {
            WatchKind.Stalled => (NotificationLevel.Warning, AutomationStrings.WatchStalledTitle, AutomationStrings.WatchStalledBody(finding.Title, minutes)),
            WatchKind.ToolRunning => (NotificationLevel.Warning, AutomationStrings.WatchToolTitle, AutomationStrings.WatchToolBody(finding.Title, minutes)),
            WatchKind.Approval => (NotificationLevel.Warning, AutomationStrings.WatchApprovalTitle, AutomationStrings.WatchApprovalBody(finding.Title, minutes)),
            _ => (NotificationLevel.Success, AutomationStrings.WatchFinishedTitle, AutomationStrings.WatchFinishedBody(finding.Title)),
        };
        notifications.Notify(level, title, body, "watch:" + finding.Kind + ":" + finding.SessionId);
        return Task.FromResult(AutomationRunOutcome.Completed());
    }
}
