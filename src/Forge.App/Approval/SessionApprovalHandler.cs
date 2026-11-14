using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Logging;
using ZLogger;

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
/// <para>Several requests of one session can be open at once: Core's approval gate serializes the agent loop's own calls, but
/// plugin and workflow <c>host.tool_call</c> steps reach this handler through <see cref="SessionToolCallRouter"/> without it.
/// Each request owns its card and its own wait, so decisions and cancellations never touch another request's card.</para>
/// <para>A <c>run_code</c> script's inner calls (<see cref="ApprovalRequest.RunKey"/> non-null) are serialized per tool of one
/// run: the first call shows its card with how many more wait behind it, the rest queue without cards, and the card's
/// "allow the rest of this run" answers them all plus the calls the script has not made yet. The grant lives only in this
/// handler (never in the rule store), keyed by a run id that is unique per run, so it cannot outlive its run semantically;
/// the map is capped so a long-lived session cannot accumulate one.</para>
/// </summary>
internal sealed class SessionApprovalHandler : IApprovalHandler, IDisposable
{
    /// <summary>
    /// How many run grants and run groups are kept before the oldest is dropped. A run key is unique per run, so the entry
    /// of a finished run is inert (it can never match again) and dropping it only frees memory; dropping the entry of a
    /// live run costs one extra card, never a wrong allow.
    /// </summary>
    private const int RunStateCap = 64;
    private readonly ApprovalQueue queue;
    private readonly ApprovalRules rules;
    private readonly string projectId;
    private bool disposed;

    public SessionApprovalHandler(
        ApprovalQueue queue,
        ApprovalRules rules,
        SessionInfo session,
        INotifications? notifications = null,
        Func<string, McpApprovalServer?>? mcpToolInfo = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.mcpToolInfo = mcpToolInfo;
        SessionId = session.Id;
        SessionTitle = string.IsNullOrWhiteSpace(session.Title) ? session.Id : session.Title;

        // The general workspace's sessions carry the General project id; a session without one disables "remember for this
        // project" instead of guessing a project.
        projectId = session.ProjectId ?? string.Empty;
        this.notifications = notifications;
    }

    /// <summary>The shell's notification entry; null in a handler built on its own.</summary>
    private readonly INotifications? notifications;

    /// <summary>Maps a tool name to what the card may show about its MCP server; null when no MCP information exists.</summary>
    private readonly Func<string, McpApprovalServer?>? mcpToolInfo;

    public string SessionId { get; }

    public string SessionTitle { get; }

    /// <summary>Empty when the session has no project; the project "remember" button is then disabled.</summary>
    public string ProjectId => projectId;

    /// <summary>Tools whose rest was allowed for one run, by that run's key; see the class summary.</summary>
    private readonly Dictionary<string, HashSet<string>> runGrants = new(StringComparer.Ordinal);

    /// <summary>Insertion order of <see cref="runGrants"/>, for dropping the oldest at the cap.</summary>
    private readonly Queue<string> grantOrder = new();

    /// <summary>The per-(run, tool) serialization groups of script inner calls.</summary>
    private readonly Dictionary<(string RunKey, string Tool), RunCallGroup> runGroups = new();

    /// <summary>Insertion order of <see cref="runGroups"/>, for dropping the oldest idle group at the cap.</summary>
    private readonly Queue<(string RunKey, string Tool)> groupOrder = new();

    /// <summary>Guards the two maps, their queues and every group's counter and leader slot.</summary>
    private readonly Lock batchGate = new();

    /// <summary>One run's calls of one tool waiting to be asked: the first holds the turn and shows the card, the rest wait.</summary>
    private sealed class RunCallGroup
    {
        /// <summary>Held while one call is being asked (its card is queued); the next waiter takes it after that ask ends.</summary>
        public SemaphoreSlim Turn { get; } = new(1, 1);

        /// <summary>Calls inside this group that have not been answered yet (the current ask included).</summary>
        public int Pending;

        /// <summary>The card the current ask is showing, for the waiting-count line; null while the group has no ask.</summary>
        public ToolApprovalCardViewModel? Leader;
    }

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
            try
            {
                await rules.LoadAsync(ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // A failed rule load means "no remembered rules": the user is asked, never an exception to the agent loop.
                AppLogs.Approval.ZLogWarning($"the remembered rules could not be loaded: {exception.Message}");
            }
        }

        if (rules.IsAllowed(request.ToolName, request.SideEffect, SessionId, projectId))
        {
            return ApprovalDecision.Allow;
        }

        // A run-scoped allow answers like a remembered rule, but only for that run's calls of that tool.
        if (request.RunKey is { } grantedRun && IsGranted(grantedRun, request.ToolName))
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
        // A script run's inner calls join their (run, tool) group first: the group hands out one ask at a time, so a
        // fanned-out call waits behind the card the fan-out's first call already showing instead of stacking its own.
        RunCallGroup? group = null;
        var held = false;
        try
        {
            if (request.RunKey is { } runKey)
            {
                group = JoinGroup(runKey, request.ToolName);
                await group.Turn.WaitAsync(ct).ConfigureAwait(false);
                held = true;

                // The user may have allowed the rest of this run while this call was waiting behind the leader.
                if (IsGranted(runKey, request.ToolName))
                {
                    return ApprovalDecision.Allow;
                }
            }

            var card = new ToolApprovalCardViewModel(
                new ToolApprovalCardRequest(
                    SessionId,
                    SessionTitle,
                    projectId.Length == 0 ? null : projectId,
                    request.ToolName,
                    request.SideEffect,
                    request.WorkingDirectory,
                    request.ArgumentsJson,
                    mcpToolInfo?.Invoke(request.ToolName),
                    request.RunKey),
                queue.Dispatcher,
                rules);

            if (group is not null)
            {
                SetLeader(group, card);
            }

            await queue.EnqueueAsync(card, ct).ConfigureAwait(false);
            notifications.Notify(
                NotificationLevel.Warning,
                NotificationsStrings.ApprovalWaitingTitle,
                NotificationsStrings.ApprovalWaiting(SessionTitle, request.ToolName),
                "approval:" + card.Id);
            try
            {
                var outcome = await card.Decision.WaitAsync(ct).ConfigureAwait(false);
                if (outcome.Remember is { } scope)
                {
                    Remember(request, scope);
                }

                if (outcome.AllowRestOfRun && request.RunKey is { } grantedRun)
                {
                    GrantRestOfRun(grantedRun, request.ToolName);
                }

                return outcome.IsAllowed ? ApprovalDecision.Allow : ApprovalDecision.Deny;
            }
            finally
            {
                if (group is not null)
                {
                    ClearLeader(group, card);
                }

                // Whatever happened - a decision, a cancellation or a session release - the card leaves the queue here.
                await queue.WithdrawAsync(card).ConfigureAwait(false);
            }
        }
        finally
        {
            if (group is not null)
            {
                LeaveGroup(request.RunKey!, request.ToolName, group, held);
            }
        }
    }

    /// <summary>Joins the run's group for this tool and counts the caller in; creates the group at its first caller.</summary>
    private RunCallGroup JoinGroup(string runKey, string toolName)
    {
        RunCallGroup group;
        lock (batchGate)
        {
            if (!runGroups.TryGetValue((runKey, toolName), out group!))
            {
                group = new RunCallGroup();
                runGroups[(runKey, toolName)] = group;
                groupOrder.Enqueue((runKey, toolName));
                if (runGroups.Count > RunStateCap)
                {
                    // Drop the oldest idle group; a busy one is kept and re-queued, so the map may temporarily overshoot
                    // the cap by as many runs as are actually asking at once - which the app already bounds.
                    for (var visited = 0; visited < groupOrder.Count; visited++)
                    {
                        var oldest = groupOrder.Dequeue();
                        if (runGroups.TryGetValue(oldest, out var candidate) && candidate.Pending == 0)
                        {
                            runGroups.Remove(oldest);
                            break;
                        }

                        groupOrder.Enqueue(oldest);
                    }
                }
            }

            group.Pending++;
        }

        // The shown card's waiting line counts the callers behind it, so it grows as callers join.
        RefreshLeaderCount(group);
        return group;
    }

    /// <summary>Answers the caller out of the group: one fewer pending call, the turn (if held) back, the leader's count refreshed.</summary>
    private void LeaveGroup(string runKey, string toolName, RunCallGroup group, bool held)
    {
        lock (batchGate)
        {
            group.Pending--;
            if (held)
            {
                group.Turn.Release();
            }
        }

        RefreshLeaderCount(group);
    }

    /// <summary>Makes <paramref name="card"/> the group's shown card and tells it how many calls wait behind it.</summary>
    private void SetLeader(RunCallGroup group, ToolApprovalCardViewModel card)
    {
        lock (batchGate)
        {
            group.Leader = card;
        }

        RefreshLeaderCount(group);
    }

    /// <summary>Clears the leader slot when the ask that owned it ends (its card is leaving the queue with it).</summary>
    private void ClearLeader(RunCallGroup group, ToolApprovalCardViewModel card)
    {
        lock (batchGate)
        {
            if (ReferenceEquals(group.Leader, card))
            {
                group.Leader = null;
            }
        }
    }

    /// <summary>Pushes the waiting count to the shown card; nothing to do while the group has no card (the count is only context).</summary>
    private void RefreshLeaderCount(RunCallGroup group)
    {
        ToolApprovalCardViewModel? leader;
        int waiting;
        lock (batchGate)
        {
            leader = group.Leader;
            waiting = group.Pending - 1;
        }

        if (leader is null)
        {
            return;
        }

        var count = Math.Max(0, waiting);
        try
        {
            // Fire and forget: the count is context, not correctness, and the card lives on the UI thread.
            _ = queue.Dispatcher.InvokeAsync(() => leader.PendingCount = count);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Approval.ZLogWarning($"the waiting count could not be shown: {exception.Message}");
        }
    }

    /// <summary>Whether the rest of this run's calls of this tool were allowed.</summary>
    private bool IsGranted(string runKey, string toolName)
    {
        lock (batchGate)
        {
            return runGrants.TryGetValue(runKey, out var tools) && tools.Contains(toolName);
        }
    }

    /// <summary>Allows the rest of this run's calls of this tool; the grant never enters the rule store.</summary>
    private void GrantRestOfRun(string runKey, string toolName)
    {
        lock (batchGate)
        {
            if (!runGrants.TryGetValue(runKey, out var tools))
            {
                tools = new HashSet<string>(StringComparer.Ordinal);
                runGrants[runKey] = tools;
                grantOrder.Enqueue(runKey);
                if (runGrants.Count > RunStateCap)
                {
                    // The oldest entry goes; see RunStateCap for why dropping a live run's grant is acceptable.
                    runGrants.Remove(grantOrder.Dequeue());
                }
            }

            tools.Add(toolName);
        }
    }

    private void Remember(ApprovalRequest request, ApprovalScope scope)
    {
        var key = ApprovalRules.KeyFor(request.ToolName, request.SideEffect, scope, SessionId, projectId);
        rules.Allow(key);
    }
}
