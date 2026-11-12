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
/// </summary>
internal sealed class SessionQuestionHandler : IUserQuestionHandler, IDisposable
{
    private readonly ApprovalQueue queue;
    private readonly INotifications? notifications;
    private bool disposed;

    public SessionQuestionHandler(ApprovalQueue queue, SessionInfo session, INotifications? notifications = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.notifications = notifications;
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

        var card = new UserQuestionCardViewModel(request with { SessionId = SessionId }, SessionTitle);
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
            new LocText(QuestionsStrings.NotifyBodyKey, SessionTitle, request.Questions.Count > 0 ? request.Questions[0].Prompt : string.Empty),
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
        _ = queue.WithdrawSessionAsync(SessionId);
    }
}
