using Forge.Abstractions;
using Forge.App.Localization;

namespace Forge.App.Automations;

/// <summary>
/// <see cref="ISessionMailbox"/> over the window's live sessions. A message waits until its session is idle (red line 2),
/// then runs as one turn; it never hands over, archives or replaces a session (red line 1). A session that is taken over,
/// archived, removed or whose tab is closed while the message waits makes the delivery <see cref="MailboxStatus.Withdrawn"/>.
/// </summary>
internal sealed class SessionMailbox : ISessionMailbox
{
    private readonly IAutomationHost host;
    private readonly IUiThread ui;
    private readonly TimeProvider time;
    private readonly TimeSpan pollInterval;

    public SessionMailbox(IAutomationHost host, IUiThread ui, TimeProvider time, TimeSpan? pollInterval = null)
    {
        this.host = host;
        this.ui = ui;
        this.time = time;
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    }

    public bool IsIdle(string sessionId) => host.FindOpen(sessionId) is { IsAutomationIdle: true };

    public async Task<MailboxResult> DeliverAsync(MailboxMessage message, IProgress<bool>? waiting, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var opened = false;
        var reportedWaiting = false;
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                return new MailboxResult(MailboxStatus.Cancelled);
            }

            var step = await ui.InvokeAsync(() => TryDeliverAsync(message, !opened, ct)).ConfigureAwait(false);
            opened = true;
            if (step is { } done)
            {
                if (reportedWaiting)
                {
                    waiting?.Report(false);
                }

                return done;
            }

            if (!reportedWaiting)
            {
                reportedWaiting = true;
                waiting?.Report(true);
            }

            try
            {
                await Task.Delay(pollInterval, time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new MailboxResult(MailboxStatus.Cancelled);
            }
        }
    }

    /// <summary>One look at the session on the UI thread: a result ends the delivery, null means "still busy, wait".</summary>
    private async Task<MailboxResult?> TryDeliverAsync(MailboxMessage message, bool mayOpen, CancellationToken ct)
    {
        var session = host.FindOpen(message.SessionId);
        if (session is null && mayOpen)
        {
            session = await host.OpenAsync(message.SessionId, ct).ConfigureAwait(true);
        }

        if (session is null)
        {
            return new MailboxResult(
                MailboxStatus.Withdrawn,
                mayOpen ? AutomationStrings.SessionUnavailableText : AutomationStrings.SessionClosedText);
        }

        if (session.IsAutomationClosed)
        {
            return new MailboxResult(MailboxStatus.Withdrawn, AutomationStrings.SessionUnavailableText);
        }

        if (!session.IsAutomationIdle)
        {
            return null;
        }

        if (ct.IsCancellationRequested)
        {
            return new MailboxResult(MailboxStatus.Cancelled);
        }

        return await session.RunAutomationTurnAsync(message.Text, AutomationStrings.OriginNotice(message.Origin)).ConfigureAwait(true);
    }
}

/// <summary><see cref="IChildAgentLauncher"/> over the window's live sessions (opens the parent without taking the focus).</summary>
internal sealed class SessionChildAgentLauncher(IAutomationHost host, IUiThread ui) : IChildAgentLauncher
{
    public async Task<ChildAgentResult> LaunchAsync(ChildAgentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = await ui.InvokeAsync(async () =>
        {
            var session = host.FindOpen(request.ParentSessionId)
                          ?? await host.OpenAsync(request.ParentSessionId, ct).ConfigureAwait(true);
            if (session is null || session.IsAutomationClosed)
            {
                throw new InvalidOperationException(AutomationStrings.SessionUnavailableText);
            }

            return session.LaunchChildAsync(request.Kind, request.Task, ct);
        }).ConfigureAwait(false);
        return await started.ConfigureAwait(false);
    }
}
