using System.Globalization;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Automations;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.Core.Agents;
using Forge.Workspace;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>
/// The window's half of session-to-session delivery (forge-core "session messaging"/"reply receipt"): it
/// runs the turn a <c>message_session</c> delivery asks for inside the session's own tab when that tab is open, numbers and
/// closes the checkpoint of a turn Core has to drive itself, reads a turn's change line out of <c>turn_checkpoints</c>, and
/// hands the receipt back to the session that asked.
/// <para>This is the seam Core cannot fill on its own: the root's turns are started here (never by Core), and the transcript,
/// the stop button and the turn numbering belong to the view models. Every member may arrive on a worker thread and
/// marshals itself.</para>
/// </summary>
internal sealed partial class MainWindowViewModel : ISessionMessageEndpoint
{
    /// <summary>
    /// The sessions the <c>@</c> picker of one root offers: the agent sessions the user opened under it, by their stored
    /// name (what the tree and the picker show). Filled from the store on demand and refreshed whenever one is created,
    /// renamed or closed; a draft has no row yet and therefore no name to mention.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<SessionMentionTarget>> mentionTargets = new(StringComparer.Ordinal);

    /// <summary>Roots whose agent sessions are being read from the store right now, so a burst of calls reads once.</summary>
    private readonly HashSet<string> mentionReads = new(StringComparer.Ordinal);

    /// <summary>Hands this window to the runtimes as their delivery seam; called once when the window is built.</summary>
    private void InitializeSessionMessages() => composition.SessionMessagingEndpoint = this;

    // ---------------------------------------------------------------- ISessionMessageEndpoint

    /// <summary>
    /// Whether the session is busy right now (a turn running, queued messages): the tool says "queued" then. Only roots are
    /// asked - a user session's own run state is Core's to see on the instance it drives.
    /// </summary>
    public bool IsBusy(string sessionId) =>
        sessions.TryGetValue(sessionId, out var session)
        && (session.IsRunning || session.HasQueued || session.HasPendingComposer);

    /// <summary>
    /// One delivered turn, run inside the session's own tab. Null means the tab is not open (Core then either drives a user
    /// session itself or reports that the root cannot run), so the loop below keeps polling until the tab appears or the
    /// session stops being busy.
    /// </summary>
    public async Task<SessionTurnOutcome?> RunSessionTurnAsync(SessionTurnRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        while (!ct.IsCancellationRequested)
        {
            if (disposed || closing)
            {
                return new SessionTurnOutcome(string.Empty, SessionReplyStatus.Stopped, "the window is closing", 0);
            }

            var step = await DispatcherUiThread.Instance
                .InvokeAsync(() => TryRunDeliveredTurnAsync(request))
                .ConfigureAwait(false);
            if (!step.HasView)
            {
                return null;
            }

            if (step.Outcome is { } outcome)
            {
                return outcome;
            }

            // The tab is open but busy with the user's own turn: the delivery waits for it (a busy target queues).
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new SessionTurnOutcome(string.Empty, SessionReplyStatus.Stopped, "cancelled", 0);
            }
        }

        return new SessionTurnOutcome(string.Empty, SessionReplyStatus.Stopped, "cancelled", 0);
    }

    /// <summary>
    /// One look at the session on the UI thread: whether its tab exists and, when the tab was free, the turn's outcome.
    /// Opening the tab never takes the user away from the session they are in.
    /// </summary>
    private async Task<(bool HasView, SessionTurnOutcome? Outcome)> TryRunDeliveredTurnAsync(SessionTurnRequest request)
    {
        var session = await FindOrOpenSessionForDeliveryAsync(request.SessionId).ConfigureAwait(true);
        if (session is null || session.IsReadOnlyView)
        {
            return (false, null);
        }

        return (true, await session.RunDeliveredTurnAsync(request.Message, request.FromName).ConfigureAwait(true));
    }

    /// <summary>
    /// The live view of a session, opened in the background when it has none: a delivered message runs in the session's own
    /// tab (its transcript, its checkpoint numbering, its stop button), so a closed tab is opened first and the session the
    /// user is looking at is put back in front afterwards.
    /// </summary>
    private async Task<SessionWorkspaceViewModel?> FindOrOpenSessionForDeliveryAsync(string sessionId)
    {
        if (sessions.TryGetValue(sessionId, out var open))
        {
            return open;
        }

        SessionInfo? info;
        try
        {
            info = await BackgroundRead.RunAsync(token => composition.Sessions.GetSessionAsync(sessionId, token), CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Session.ZLogWarning($"delivered message: session '{sessionId}' not read: {exception.Message}");
            return null;
        }

        if (info is null || info.Status != SessionStatus.Active || disposed || closing)
        {
            return null;
        }

        // Opening activates the new tab; a delivery must not take the user away from the session they are in.
        var previous = ActiveSession;
        try
        {
            if (info.ParentSessionId is not null)
            {
                await OpenAgentSessionByIdAsync(info.Id, CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                await OpenSessionByIdAsync(info.Id, CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
        {
            AppLogs.Session.ZLogWarning($"delivered message: session '{sessionId}' not opened: {exception.Message}");
            return null;
        }

        if (previous is not null && !ReferenceEquals(previous, ActiveSession) && sessions.ContainsKey(previous.SessionId))
        {
            ShowSessionTab(previous);
            ActiveSession = previous;
        }

        return sessions.TryGetValue(sessionId, out var opened) ? opened : null;
    }

    /// <summary>
    /// Opens the checkpoint of a turn Core drives itself (a user session with no tab): the same numbering and the same store
    /// the session's own tab uses, so the receipt's change line is the child's own turn.
    /// </summary>
    public async Task<int> BeginSessionTurnAsync(string sessionId, string workingDirectory, CancellationToken ct)
    {
        var next = await composition.Checkpoints.NextTurnNumberAsync(sessionId, ct).ConfigureAwait(false);
        await composition.Checkpoints.BeginAsync(sessionId, next, workingDirectory, ct).ConfigureAwait(false);
        return next;
    }

    public Task EndSessionTurnAsync(string sessionId, int turn, string workingDirectory, CancellationToken ct) =>
        composition.Checkpoints.CompleteAsync(sessionId, turn, workingDirectory, ct);

    /// <summary>
    /// One turn's change line, from the tree hashes <c>turn_checkpoints</c> recorded around it: how many files changed and how
    /// many lines went in and out. Null when the session has no checkpoint for that turn (a directory that is not a
    /// repository, or a write that failed) - the receipt then says "0 files", which is what the store actually holds.
    /// </summary>
    public async Task<SessionTurnChanges?> ReadChangesAsync(string sessionId, int turn, CancellationToken ct)
    {
        IReadOnlyList<TurnCheckpoint> checkpoints;
        try
        {
            checkpoints = await composition.TurnCheckpoints.ListAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Session.ZLogWarning($"delivered message: checkpoints of '{sessionId}' not read: {exception.Message}");
            return null;
        }

        TurnCheckpoint? checkpoint = null;
        foreach (var stored in checkpoints)
        {
            if (stored.Turn == turn)
            {
                checkpoint = stored;
                break;
            }
        }

        if (checkpoint is not { TreeAfter: { Length: > 0 } after })
        {
            return null;
        }

        try
        {
            var files = await composition.Git
                .GetTreeDiffAsync(checkpoint.WorktreePath, checkpoint.TreeBefore, after, ct)
                .ConfigureAwait(false);
            var added = 0L;
            var removed = 0L;
            foreach (var file in files)
            {
                added += file.AddedLineCount;
                removed += file.RemovedLineCount;
            }

            return new SessionTurnChanges(files.Count, added, removed);
        }
        catch (Exception exception) when (exception is GitException or IOException or InvalidOperationException)
        {
            AppLogs.Session.ZLogWarning($"delivered message: turn diff of '{sessionId}' not read: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// One receipt for the session that asked (forge-core "reply receipt"): a session running a turn takes it at the next
    /// tool-batch boundary of that very turn (the loop hands it to the model there and the transcript draws the card), and an
    /// idle one gets it as an unread message in the transcript - written to its history, so the next turn reads it, and never
    /// turned into a turn of its own.
    /// </summary>
    public void DeliverReceipt(string initiatorSessionId, string text)
    {
        if (string.IsNullOrEmpty(initiatorSessionId) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _ = DeliverReceiptAsync(initiatorSessionId, text));
    }

    private async Task DeliverReceiptAsync(string initiatorSessionId, string text)
    {
        if (disposed || closing)
        {
            return;
        }

        if (!sessions.TryGetValue(initiatorSessionId, out var session))
        {
            // No tab of that session is open here: the receipt still has to reach the model, so it goes into the store and a
            // later reopen replays it as the card it is.
            await AppendReceiptAsync(null, initiatorSessionId, text).ConfigureAwait(true);
            return;
        }

        if (session.TryInterjectSessionMessage(text))
        {
            // The running turn takes it at its next tool-batch boundary (same round, visible there).
            return;
        }

        await AppendReceiptAsync(session, initiatorSessionId, text).ConfigureAwait(true);
        var unseen = !ReferenceEquals(session, activeSession);
        session.ShowSessionReply(text, unseen);
        if (unseen)
        {
            RefreshTreeAgents();
        }
    }

    /// <summary>
    /// Writes a receipt into the session's history without a turn. With the session's tab open its runtime also appends it to
    /// the live instance, so the next turn reads it exactly like a message the user typed; without one the store row is the
    /// whole write (the next resume loads it).
    /// </summary>
    private async Task AppendReceiptAsync(SessionWorkspaceViewModel? session, string sessionId, string text)
    {
        try
        {
            // Through the root's runtime, not around it: that is the side holding the live instance the next turn sends, and
            // it hands the message to a turn that is still running instead of writing behind that turn's back.
            var runtime = session?.Runtime ?? await RuntimeOfAsync(sessionId).ConfigureAwait(true);
            if (runtime is not null)
            {
                await runtime.AppendSessionMessageAsync(sessionId, text, CancellationToken.None).ConfigureAwait(true);
                return;
            }

            await composition.Sessions.AppendAsync(sessionId, ChatMessage.User(text), null, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A receipt that cannot be stored is lost to the model, but it must not take the window down: the log says so.
            AppLogs.Session.ZLogWarning($"session receipt for '{sessionId}' not stored: {exception.Message}");
        }
    }

    /// <summary>
    /// The runtime that owns <paramref name="sessionId"/> - its own for a root, its root's for an agent session - loading it
    /// when no tab has it. Null for a session that is not Active any more: an archived session's receipts are written to the
    /// store only, and its next resume reads them from there.
    /// </summary>
    private async Task<SessionRuntime?> RuntimeOfAsync(string sessionId)
    {
        SessionInfo? info;
        try
        {
            info = await BackgroundRead.RunAsync(token => composition.Sessions.GetSessionAsync(sessionId, token), CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Session.ZLogWarning($"session receipt: session '{sessionId}' not read: {exception.Message}");
            return null;
        }

        if (info is null || info.Status != SessionStatus.Active)
        {
            return null;
        }

        SessionInfo? root = info;
        if (info.ParentSessionId is { } parentId)
        {
            root = await BackgroundRead.RunAsync(token => composition.Sessions.GetSessionAsync(parentId, token), CancellationToken.None)
                .ConfigureAwait(true);
            if (root is null || root.Status != SessionStatus.Active)
            {
                return null;
            }
        }

        try
        {
            return composition.Runtimes.Get(root);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- the @ picker's session group

    /// <summary>
    /// The sessions one tab may mention: an agent-session tab may mention its root; a root session may mention the agent
    /// sessions the user opened under it (open tabs and stored rows).
    /// </summary>
    private IReadOnlyList<SessionMentionTarget> SessionMentionTargetsFor(SessionWorkspaceViewModel view)
    {
        if (view.IsAgentSession)
        {
            return view.AgentSessionRoot is { } root ? [new SessionMentionTarget(root.Id, root.Title)] : [];
        }

        var rootId = view.SessionId;
        var targets = new List<SessionMentionTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (session, _) in agentViews)
        {
            if (string.Equals(session.AgentSessionRoot?.Id, rootId, StringComparison.Ordinal)
                && session.AgentChildSessionId is { } childId
                && seen.Add(childId))
            {
                targets.Add(new SessionMentionTarget(childId, session.AgentSessionName));
            }
        }

        if (mentionTargets.TryGetValue(rootId, out var stored))
        {
            foreach (var target in stored)
            {
                if (seen.Add(target.SessionId))
                {
                    targets.Add(target);
                }
            }
        }
        else if (mentionReads.Add(rootId))
        {
            _ = ReadMentionTargetsAsync(rootId);
        }

        return targets;
    }

    /// <summary>Reads one root's stored agent sessions for the <c>@</c> picker, then refills every view of that root.</summary>
    private async Task ReadMentionTargetsAsync(string rootId)
    {
        IReadOnlyList<SessionMentionTarget> read = [];
        try
        {
            var children = await BackgroundRead.RunAsync(
                    token => composition.Catalog.QueryAsync(new SessionQuery { ParentSessionId = rootId }, token),
                    CancellationToken.None)
                .ConfigureAwait(true);
            List<SessionMentionTarget> targets = [];
            foreach (var child in children)
            {
                if (string.Equals(child.ChildDriver, AgentSessionKinds.ChildDriverUser, StringComparison.Ordinal)
                    && child.Status == SessionStatus.Active)
                {
                    targets.Add(new SessionMentionTarget(child.Id, child.Title));
                }
            }

            read = targets;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Session.ZLogWarning($"the sessions of '{rootId}' not listed for the @ picker: {exception.Message}");
        }
        finally
        {
            mentionReads.Remove(rootId);
        }

        mentionTargets[rootId] = read;
        RefreshSessionMentionsOf(rootId);
    }

    /// <summary>Refills the <c>@</c> picker of every view of one root (its own tab and its agent-session tabs).</summary>
    private void RefreshSessionMentionsOf(string rootId)
    {
        if (disposed)
        {
            return;
        }

        foreach (var (session, _) in agentViews)
        {
            if (string.Equals(session.AgentSessionRoot?.Id, rootId, StringComparison.Ordinal))
            {
                session.RefreshSessionMentions();
            }
        }

        if (sessions.TryGetValue(rootId, out var root) && !root.IsAgentSession)
        {
            root.RefreshSessionMentions();
        }
    }

    /// <summary>
    /// One agent session is gone or was archived: every grant that named it as its target is dead with it, so the marks of the
    /// sessions that held one go (forge-core: a grant lives until the target session ends).
    /// </summary>
    private void OnMessageTargetClosed(string targetSessionId)
    {
        if (string.IsNullOrEmpty(targetSessionId) || disposed)
        {
            return;
        }

        foreach (var session in sessions.Values)
        {
            session.ForgetMessageTarget(targetSessionId);
        }

        // The stored list is stale now: the next open of the picker reads it again.
        mentionTargets.Clear();
    }

    /// <summary>
    /// The root of <paramref name="session"/> left <see cref="SessionStatus.Active"/> (taken over or archived): every grant of
    /// its tree goes, and the marks of all its tabs with it.
    /// </summary>
    private void RevokeSessionMessagesOf(SessionWorkspaceViewModel session)
    {
        if (disposed)
        {
            return;
        }

        var rootId = session.AgentSessionRoot?.Id ?? session.SessionId;
        foreach (var view in sessions.Values)
        {
            if (string.Equals(view.AgentSessionRoot?.Id ?? view.SessionId, rootId, StringComparison.Ordinal))
            {
                view.RevokeSessionMessages();
            }
        }
    }
}
