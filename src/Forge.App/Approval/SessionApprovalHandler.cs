using Forge.Abstractions;

namespace Forge.App.Approval;

/// <summary>
/// The live titles of the sessions that own approval work. A tool call card carries the session it came from; a plugin
/// card is built from an <see cref="ToolContext"/> that only carries the session id, so the title is looked up here. The
/// directory keeps a title after a session is released: a late host event should still name the session it belongs to.
/// </summary>
internal sealed class ApprovalSessionDirectory
{
    private readonly Dictionary<string, string> titles = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    /// <summary>Remembers the title of a session; the newest title wins (a rename is reflected on the next card).</summary>
    public void Register(string sessionId, string? title)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (gate)
        {
            titles[sessionId] = string.IsNullOrWhiteSpace(title) ? sessionId : title;
        }
    }

    /// <summary>The title of a session, or the id itself when the session was never seen.</summary>
    public string TitleOf(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return string.Empty;
        }

        lock (gate)
        {
            return titles.TryGetValue(sessionId, out var title) ? title : sessionId;
        }
    }
}

/// <summary>
/// One main session's approval handler (<see cref="IApprovalHandler"/>). The agent loop asks it before a side-effecting
/// tool runs: a remembered rule answers immediately, anything else becomes a card in the <see cref="ApprovalQueue"/> and
/// the loop waits for the user. Cancelling the waiting call withdraws the card (a late click then cannot revive it) and
/// reports cancellation; releasing the session withdraws every card of that session as a denial.
/// <para>Requests are serialized per session by Core's approval gate, so one session shows at most one tool card; cards of
/// different sessions sit side by side in the queue.</para>
/// </summary>
internal sealed class SessionApprovalHandler : IApprovalHandler, IDisposable
{
    private readonly ApprovalQueue queue;
    private readonly ApprovalRules rules;
    private readonly string projectId;
    private bool disposed;

    public SessionApprovalHandler(ApprovalQueue queue, ApprovalRules rules, SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        SessionId = session.Id;
        SessionTitle = string.IsNullOrWhiteSpace(session.Title) ? session.Id : session.Title;

        // The general workspace's sessions carry the General project id; a session without one disables "remember for this
        // project" instead of guessing a project.
        projectId = session.ProjectId ?? string.Empty;
    }

    public string SessionId { get; }

    public string SessionTitle { get; }

    /// <summary>Empty when the session has no project; the project "remember" button is then disabled.</summary>
    public string ProjectId => projectId;

    public async ValueTask<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (disposed)
        {
            return ApprovalDecision.Deny;
        }

        // A remembered allow is answered without a card. Rules are read after the (single, shared) file read so a rule
        // remembered in an earlier run is never missed just because startup is still reading it; once it is loaded this
        // stays synchronous, so the waiting call does not pay for an extra dispatcher hop.
        if (!rules.IsAllowed(request.ToolName, request.SideEffect, SessionId, projectId) && !rules.IsLoaded)
        {
            await rules.LoadAsync(ct).ConfigureAwait(false);
        }

        if (rules.IsAllowed(request.ToolName, request.SideEffect, SessionId, projectId))
        {
            return ApprovalDecision.Allow;
        }

        return await AskAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Withdraws this session's cards as a denial; the waiting agent loops then report an ordinary denial.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        _ = queue.WithdrawSessionAsync(SessionId);
    }

    private async Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken ct)
    {
        var card = new ToolApprovalCardViewModel(
            new ToolApprovalCardRequest(
                SessionId,
                SessionTitle,
                projectId.Length == 0 ? null : projectId,
                request.ToolName,
                request.SideEffect,
                request.WorkingDirectory,
                request.ArgumentsJson),
            queue.Dispatcher);

        await queue.EnqueueAsync(card, ct).ConfigureAwait(false);
        try
        {
            var outcome = await card.Decision.WaitAsync(ct).ConfigureAwait(false);
            if (outcome.Remember is { } scope)
            {
                Remember(request, scope);
            }

            return outcome.IsAllowed ? ApprovalDecision.Allow : ApprovalDecision.Deny;
        }
        finally
        {
            // Whatever happened - a decision, a cancellation or a session release - the card leaves the queue here.
            await queue.WithdrawAsync(card).ConfigureAwait(false);
        }
    }

    private void Remember(ApprovalRequest request, ApprovalScope scope)
    {
        var key = ApprovalRules.KeyFor(request.ToolName, request.SideEffect, scope, SessionId, projectId);
        rules.Allow(key);
    }
}
