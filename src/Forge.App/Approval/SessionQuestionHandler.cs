using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Notifications;

namespace Forge.App.Approval;

/// <summary>
/// One main session's <see cref="IUserQuestionHandler"/>. The question becomes a card in the shared
/// <see cref="ApprovalQueue"/> (so it shares the pending count and the notification with tool approvals) and the tool
/// waits for the user. Cancelling the wait withdraws the card and reports <see cref="UserQuestionOutcome.Cancelled"/>;
/// releasing the session withdraws it through the queue (the approval handler's session release covers every card of the
/// session, this handler's own <see cref="Dispose"/> does the same for a session built without one).
/// <para>A card belongs to the session that asked: the root's own questions and a model-spawned child's keep the root's id,
/// while a question of a user-driven child session (forge-app "Agent 会话的界面与接线") keeps that session's id, so the
/// workbench docks it in the child's own tab.</para>
/// </summary>
internal sealed class SessionQuestionHandler : IUserQuestionHandler, IDisposable
{
    private readonly ApprovalQueue queue;
    private readonly INotifications? notifications;

    /// <summary>
    /// The user-driven child sessions of this root, so a question asked from one of them docks in that session's own tab;
    /// null for a handler built without a runtime (a card then always belongs to the root).
    /// </summary>
    private readonly IAgentSessionScope? childSessions;

    private bool disposed;

    public SessionQuestionHandler(
        ApprovalQueue queue,
        SessionInfo session,
        INotifications? notifications = null,
        IAgentSessionScope? childSessions = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.notifications = notifications;
        this.childSessions = childSessions;
        SessionId = session.Id;
        SessionTitle = string.IsNullOrWhiteSpace(session.Title) ? session.Id : session.Title;
    }

    public string SessionId { get; }

    public string SessionTitle { get; }

    public async ValueTask<UserQuestionResponse> AskAsync(UserQuestionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (disposed || ct.IsCancellationRequested)
        {
            return UserQuestionResponse.Cancelled;
        }

        // The question stays with the session that asked: a user-driven child session keeps its own id (the card is docked in
        // that tab), a model-spawned child's question and the root's own stay on the root, exactly as before.
        var (cardSessionId, cardTitle) = AgentSessionCardOwner.Resolve(
            childSessions,
            request.SessionId,
            SessionId,
            SessionTitle);
        var card = new UserQuestionCardViewModel(request with { SessionId = cardSessionId }, cardTitle);
        try
        {
            await queue.EnqueueAsync(card, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            card.Dispose();
            return UserQuestionResponse.Cancelled;
        }

        notifications.Notify(
            NotificationLevel.Warning,
            new LocText(QuestionsStrings.NotifyTitleKey),
            new LocText(QuestionsStrings.NotifyBodyKey, cardTitle, request.Questions.Count > 0 ? request.Questions[0].Prompt : string.Empty),
            "question:" + card.Id);
        try
        {
            var outcome = await card.Decision.WaitAsync(ct).ConfigureAwait(false);
            return outcome.Decision switch
            {
                ApprovalCardDecision.Allow when card.Response is { } answered => answered,
                ApprovalCardDecision.Deny => UserQuestionResponse.Declined,
                _ => UserQuestionResponse.Cancelled,
            };
        }
        catch (OperationCanceledException)
        {
            return UserQuestionResponse.Cancelled;
        }
        finally
        {
            await queue.WithdrawAsync(card).ConfigureAwait(false);
        }
    }

    /// <summary>Withdraws this session's cards; the waiting tool calls then report "cancelled".</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        _ = WithdrawAsync();
    }

    /// <summary>
    /// Withdraws the root's cards and the cards of the user-driven child sessions that ran under it; the waiting tool calls
    /// then report "cancelled".
    /// </summary>
    private async Task WithdrawAsync()
    {
        await queue.WithdrawSessionAsync(SessionId).ConfigureAwait(false);
        foreach (var childSessionId in childSessions?.ChildSessionIds ?? [])
        {
            await queue.WithdrawSessionAsync(childSessionId).ConfigureAwait(false);
        }
    }
}
