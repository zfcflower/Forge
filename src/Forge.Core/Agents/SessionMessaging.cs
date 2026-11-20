using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Storage;

namespace Forge.Core.Agents;

/// <summary>
/// One pair the user authorized by picking the target in the composer's <c>@</c> picker: "this session may deliver to that
/// one". The root it belongs to is recorded with it so a root that leaves <see cref="SessionStatus.Active"/> can drop every
/// pair of its tree in one call. In memory only - a restart forgets every grant, exactly like the table itself.
/// </summary>
public sealed record SessionGrant(string RootSessionId, string InitiatorSessionId, string TargetSessionId, string TargetName);

/// <summary>
/// Who may deliver to whom (forge-core "授权通信"): pairs of (initiating session, target session) written only by the user's
/// structured <c>@</c> mention, kept until the target session ends. The only state is the pairs; every expiry is a removal
/// from this table, so <see cref="IsGranted"/> can never answer for a session that is gone but was not pruned yet - the
/// messenger prunes before it resolves a target (see <see cref="SessionMessenger"/>), and the app prunes on the events it
/// sees (tab closed, archived, root left Active, the user pressed revoke).
/// </summary>
public sealed class SessionAuthorizationTable
{
    private readonly Lock gate = new();
    private readonly Dictionary<(string Initiator, string Target), SessionGrant> grants = [];

    /// <summary>Whether the user let <paramref name="initiatorSessionId"/> deliver to <paramref name="targetSessionId"/>.</summary>
    public bool IsGranted(string? initiatorSessionId, string? targetSessionId)
    {
        if (string.IsNullOrEmpty(initiatorSessionId) || string.IsNullOrEmpty(targetSessionId))
        {
            return false;
        }

        lock (gate)
        {
            return grants.ContainsKey((initiatorSessionId, targetSessionId));
        }
    }

    /// <summary>Records the pair (a second grant of the same pair only renames the target).</summary>
    public SessionGrant Grant(SessionGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (gate)
        {
            grants[(grant.InitiatorSessionId, grant.TargetSessionId)] = grant;
            return grant;
        }
    }

    /// <summary>The user withdrew one pair; false when it was never granted.</summary>
    public bool Revoke(string? initiatorSessionId, string? targetSessionId)
    {
        if (string.IsNullOrEmpty(initiatorSessionId) || string.IsNullOrEmpty(targetSessionId))
        {
            return false;
        }

        lock (gate)
        {
            return grants.Remove((initiatorSessionId, targetSessionId));
        }
    }

    /// <summary>Drops every pair that names <paramref name="targetSessionId"/> (it was archived or closed); returns how many.</summary>
    public int RevokeTarget(string? targetSessionId)
    {
        if (string.IsNullOrEmpty(targetSessionId))
        {
            return 0;
        }

        lock (gate)
        {
            return RemoveWhere(grant => string.Equals(grant.TargetSessionId, targetSessionId, StringComparison.Ordinal));
        }
    }

    /// <summary>Drops every pair of one root's tree - it left <see cref="SessionStatus.Active"/>; returns how many.</summary>
    public int RevokeRoot(string? rootSessionId)
    {
        if (string.IsNullOrEmpty(rootSessionId))
        {
            return 0;
        }

        lock (gate)
        {
            return RemoveWhere(grant => string.Equals(grant.RootSessionId, rootSessionId, StringComparison.Ordinal));
        }
    }

    /// <summary>Drops every pair; the table of a released root (a restart is the other way it empties).</summary>
    public int RevokeAll()
    {
        lock (gate)
        {
            var removed = grants.Count;
            grants.Clear();
            return removed;
        }
    }

    /// <summary>The pairs <paramref name="initiatorSessionId"/> holds, newest grant last; what the composer's marks show.</summary>
    public IReadOnlyList<SessionGrant> GrantsOf(string? initiatorSessionId)
    {
        if (string.IsNullOrEmpty(initiatorSessionId))
        {
            return [];
        }

        lock (gate)
        {
            var found = new List<SessionGrant>();
            foreach (var grant in grants.Values)
            {
                if (string.Equals(grant.InitiatorSessionId, initiatorSessionId, StringComparison.Ordinal))
                {
                    found.Add(grant);
                }
            }

            return found;
        }
    }

    private int RemoveWhere(Func<SessionGrant, bool> predicate)
    {
        List<(string, string)>? doomed = null;
        foreach (var (key, grant) in grants)
        {
            if (predicate(grant))
            {
                (doomed ??= []).Add(key);
            }
        }

        if (doomed is null)
        {
            return 0;
        }

        foreach (var key in doomed)
        {
            grants.Remove(key);
        }

        return doomed.Count;
    }
}

/// <summary>
/// The app's side of a delivery (forge-core "授权通信" / "回程回执"): everything Core cannot do itself. The app owns the
/// session views, the transcript, the git checkpoints and the root's own turns, so Core asks through this seam instead of
/// growing a second copy of any of them.
/// <para>Implemented by the window; every member may be called from a worker thread and must marshal itself.</para>
/// </summary>
public interface ISessionMessageEndpoint
{
    /// <summary>
    /// Whether <paramref name="sessionId"/> is busy right now (a turn running, queued work): the tool then answers
    /// "已排队" instead of "已投递". Cheap and lock-free enough to call from the tool.
    /// </summary>
    bool IsBusy(string sessionId);

    /// <summary>
    /// Runs one turn of <paramref name="request"/>'s session through the app's live view of it - its transcript, its stop
    /// button and its own checkpoint numbering - waiting until that session is free instead of refusing. Null means the app
    /// has no live view of that session: for a root Core then has nowhere to run the turn (the root's turns are the app's
    /// alone), and for a user-driven session Core drives it itself with <see cref="AgentHost.RunUserChildTurnAsync"/>.
    /// </summary>
    Task<SessionTurnOutcome?> RunSessionTurnAsync(SessionTurnRequest request, CancellationToken ct);

    /// <summary>
    /// Opens the checkpoint of one turn of a user-driven session before Core drives it
    /// (<see cref="AgentHost.RunUserChildTurnAsync"/>), and answers the turn number it got, exactly like the session's own
    /// tab numbers its turns.
    /// </summary>
    Task<int> BeginSessionTurnAsync(string sessionId, string workingDirectory, CancellationToken ct);

    /// <summary>Closes the checkpoint opened by <see cref="BeginSessionTurnAsync"/>, on success, failure and stop alike.</summary>
    Task EndSessionTurnAsync(string sessionId, int turn, string workingDirectory, CancellationToken ct);

    /// <summary>
    /// The change line of one turn, read from <c>turn_checkpoints</c> (files, added and removed lines); null when the session
    /// recorded no checkpoint for it (a directory that is not a repository, or a write that failed).
    /// </summary>
    Task<SessionTurnChanges?> ReadChangesAsync(string sessionId, int turn, CancellationToken ct);

    /// <summary>
    /// Hands one receipt to the session that asked for it. Never starts a turn: when that session is running a turn the app
    /// puts the receipt at the next tool-batch boundary, otherwise it lands in the transcript as an unread message.
    /// </summary>
    void DeliverReceipt(string initiatorSessionId, string text);
}

/// <summary>One message handed to a target session.</summary>
public sealed record SessionTurnRequest(string SessionId, string Message, string FromSessionId, string FromName);

/// <summary>How a target's turn ended, with the text the receipt's report line carries and the turn number its change line reads.</summary>
public sealed record SessionTurnOutcome(string Report, SessionReplyStatus Status, string? Reason, int Turn);

/// <summary>One turn's change line: files touched and the added/removed line counts.</summary>
public readonly record struct SessionTurnChanges(int Files, long Added, long Removed);

/// <summary>
/// The mailbox of one root session's tree (forge-core "授权通信"): the authorization table, the per-target dispatch queues and
/// the reply receipts. <c>message_session</c> is pure async - <see cref="SendAsync"/> only enqueues and returns the dispatch
/// number - and a delivery's receipt is written back to the initiator when the target's turn ended, whatever its outcome.
/// <para>The tool, the authorization table and the receipt producer live here; running the target's turn is
/// <see cref="ISessionMessageEndpoint"/>'s for a root (the app starts those) and <see cref="AgentHost.RunUserChildTurnAsync"/>'s
/// for a user-driven session (Core runs those on the same host, with the app's checkpoint numbering).</para>
/// </summary>
public sealed class SessionMessenger
{
    private const int TaskLabelLimit = 80;

    private readonly Func<SessionAnchor?> anchor;
    private readonly SubAgentScheduler scheduler;
    private readonly ISessionStore? store;
    private readonly IArtifactStore? artifacts;
    private readonly Func<ISessionMessageEndpoint?> endpoint;
    private readonly Func<AgentInstance, ChatMessage, CancellationToken, IAsyncEnumerable<AgentEvent>> runUserChildTurn;
    private readonly SessionAuthorizationTable grants = new();
    private readonly Dictionary<string, Mailbox> mailboxes = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private long dispatchSequence;

    internal SessionMessenger(
        Func<SessionAnchor?> anchor,
        SubAgentScheduler scheduler,
        ISessionStore? store,
        IArtifactStore? artifacts,
        Func<ISessionMessageEndpoint?> endpoint,
        Func<AgentInstance, ChatMessage, CancellationToken, IAsyncEnumerable<AgentEvent>> runUserChildTurn)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(runUserChildTurn);
        this.anchor = anchor;
        this.scheduler = scheduler;
        this.store = store;
        this.artifacts = artifacts;
        this.endpoint = endpoint;
        this.runUserChildTurn = runUserChildTurn;
    }

    /// <summary>The grant table of this root: written by the app's <c>@</c> mentions, read by the tool and by the input marks.</summary>
    public SessionAuthorizationTable Authorizations => grants;

    /// <summary>
    /// The user picked <paramref name="targetSessionId"/> in the composer's <c>@</c> picker of
    /// <paramref name="initiatorSessionId"/>: that pair is now authorized until the target ends, the root leaves
    /// <see cref="SessionStatus.Active"/> or the user revokes it.
    /// </summary>
    /// <returns>The root the pair belongs to, or null when the initiator is neither this root nor one of its user sessions.</returns>
    public string? Grant(string initiatorSessionId, string targetSessionId, string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initiatorSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSessionId);
        var current = anchor() ?? throw new InvalidOperationException("This host has no root session yet, so it can grant nothing.");
        var root = string.Equals(initiatorSessionId, current.SessionId, StringComparison.Ordinal)
            ? current.SessionId
            : scheduler.TryGetUserSession(initiatorSessionId, out var child) ? current.SessionId : null;
        if (root is null)
        {
            // Not a session of this tree: a stale tab of another root, or a model-spawned child that has no voice here.
            return null;
        }

        var grant = new SessionGrant(root, initiatorSessionId, targetSessionId, targetName ?? string.Empty);
        grants.Grant(grant);
        return root;
    }

    /// <summary>Every grant of this root's tree; the app drops them all when the root leaves Active.</summary>
    public int RevokeRoot(string? rootSessionId) => grants.RevokeRoot(rootSessionId);

    /// <summary>
    /// <c>message_session</c>'s body: resolve the target by name within this root, check the user's grant, make sure the
    /// target can run a turn, then enqueue. Never waits for the target: the answer says "已投递" (it starts now) or
    /// "已排队" (it waits behind work the target already has), and in both cases that there is no result yet.
    /// </summary>
    public async Task<ToolResult> SendAsync(ToolContext context, string? targetName, string? message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = message ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return Error("message_session needs a target: the session's name as the tree and the @ picker show it.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Error("message_session needs a message: what the target should read as its next user message.");
        }

        var current = anchor();
        if (current is null)
        {
            return Error("This host has no root session yet, so there is no session to deliver to.");
        }

        var initiator = context.SessionId;
        var initiatorIsRoot = string.Equals(initiator, current.SessionId, StringComparison.Ordinal);
        if (!initiatorIsRoot && !scheduler.TryGetUserSession(initiator, out _))
        {
            return Error("Only the root session and the agent sessions the user opened under it can deliver messages.");
        }

        var root = await ReadSessionAsync(current.SessionId, ct).ConfigureAwait(false);
        if (root is { Status: not SessionStatus.Active })
        {
            grants.RevokeRoot(current.SessionId);
            return Error("The root session is no longer active, so its sessions cannot deliver to each other.");
        }

        var resolved = await ResolveAsync(current.SessionId, initiatorIsRoot, initiator, targetName.Trim(), ct).ConfigureAwait(false);
        if (resolved.Error is { } problem)
        {
            if (resolved.Invalidated is { } gone)
            {
                grants.RevokeTarget(gone);
            }

            return Error(problem);
        }

        var target = resolved.Target!;
        if (!grants.IsGranted(initiator, target.SessionId))
        {
            return Error(
                $"No authorization to deliver to '{target.Name}'. The user has to pick that session in the composer's @ picker "
                + "of this session first; a typed @name is not an authorization, and neither is a message from the model.");
        }

        if (!target.IsRoot)
        {
            var child = await ReadSessionAsync(target.SessionId, ct).ConfigureAwait(false);
            if (child is { Status: not SessionStatus.Active })
            {
                grants.RevokeTarget(target.SessionId);
                return Error($"The session '{target.Name}' is no longer active, so it cannot take a message.");
            }

            if (child is null)
            {
                grants.RevokeTarget(target.SessionId);
                return Error($"The session '{target.Name}' no longer exists, so it cannot take a message.");
            }

            if (!scheduler.TryGetUserSession(target.SessionId, out _))
            {
                return Error($"The session '{target.Name}' is not loaded in this process, so it cannot run a turn right now.");
            }
        }

        var endpointNow = endpoint();
        if (endpointNow is null)
        {
            return Error("This host is not connected to the app's sessions, so nothing can be delivered.");
        }

        var id = Interlocked.Increment(ref dispatchSequence);
        var delivery = new PendingDelivery(
            id,
            initiator,
            target.SessionId,
            target.Name,
            text,
            ShortLabel(text));
        bool queued;
        bool start;
        Mailbox box;
        lock (gate)
        {
            box = mailboxFor(target.SessionId);
            // What the target already owes: its own mailbox work, or a turn this host does not queue (the user's own turn
            // on the root, or one the user is driving in the session's tab).
            queued = box.Running
                     || box.Queue.Count > 0
                     || (target.IsRoot ? endpointNow.IsBusy(target.SessionId) : IsUserSessionRunning(target.SessionId));
            box.Queue.Enqueue(delivery);
            start = !box.Draining;
            if (start)
            {
                box.Running = true;
                box.Draining = true;
            }
        }

        if (start)
        {
            _ = Task.Run(() => DrainAsync(box), CancellationToken.None);
        }

        return new ToolResult(
            queued
                ? string.Format(CultureInfo.InvariantCulture, "已排队 #{0}（未完成，你现在没有结果）", id)
                : string.Format(CultureInfo.InvariantCulture, "已投递 #{0}（未完成，你现在没有结果）", id));
    }

    /// <summary>Resolves a target name to the session it names within <paramref name="rootSessionId"/>; errors are model text.</summary>
    private async Task<Resolved> ResolveAsync(
        string rootSessionId,
        bool initiatorIsRoot,
        string initiatorSessionId,
        string name,
        CancellationToken ct)
    {
        if (!initiatorIsRoot)
        {
            // A user-driven session talks back to its own root, and to nothing else.
            var root = await ReadSessionAsync(rootSessionId, ct).ConfigureAwait(false);
            var rootName = NameOf(root, rootSessionId);
            return string.Equals(name, rootName, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, rootSessionId, StringComparison.Ordinal)
                ? new Resolved(new Target(rootSessionId, rootName, IsRoot: true), null, null)
                : new Resolved(null, $"This session can only deliver to its root session '{rootName}', not to '{name}'.", null);
        }

        var catalog = SessionCatalogs.From(store);
        if (catalog is null || store is null)
        {
            return new Resolved(null, "This session's store cannot list child sessions, so no target can be resolved.", null);
        }

        var children = await catalog.QueryAsync(new SessionQuery { ParentSessionId = rootSessionId }, ct).ConfigureAwait(false);
        List<Target> matches = [];
        string? invalidated = null;
        foreach (var child in children)
        {
            if (!string.Equals(child.ChildDriver, StorageSchema.ChildDriverUser, StringComparison.Ordinal))
            {
                continue;
            }

            var childName = NameOf(child, child.Id);
            if (!string.Equals(childName, name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(child.Id, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (child.Status != SessionStatus.Active)
            {
                // The target was archived or closed: the grant it had is dead with it.
                invalidated = child.Id;
                continue;
            }

            matches.Add(new Target(child.Id, childName, IsRoot: false));
        }

        return matches.Count switch
        {
            1 => new Resolved(matches[0], null, null),
            0 => invalidated is { } gone
                ? new Resolved(null, $"The session '{name}' is no longer active, so it cannot take a message.", gone)
                : new Resolved(null, $"There is no agent session named '{name}' under this root session.", null),
            _ => new Resolved(
                null,
                $"More than one agent session is named '{name}' under this root session, so the target is ambiguous; the user has to rename one of them.",
                null),
        };
    }

    private async Task<SessionInfo?> ReadSessionAsync(string sessionId, CancellationToken ct)
    {
        if (store is null)
        {
            return null;
        }

        try
        {
            return await store.GetSessionAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A store that cannot answer is treated like a session that is not there: the caller says so and revokes the grant.
            return null;
        }
    }

    private static string NameOf(SessionInfo? session, string fallback) =>
        string.IsNullOrWhiteSpace(session?.Title) ? fallback : session!.Title;

    private Mailbox mailboxFor(string targetSessionId) =>
        mailboxes.TryGetValue(targetSessionId, out var box) ? box : mailboxes[targetSessionId] = new Mailbox();

    /// <summary>One target's queue: deliveries run one at a time, in arrival order, so a target never runs two of them at once.</summary>
    private sealed class Mailbox
    {
        public Queue<PendingDelivery> Queue { get; } = new();

        /// <summary>The worker has not drained the queue to empty yet (a delivery is waiting or running).</summary>
        public bool Running { get; set; }

        /// <summary>A worker task was started for this mailbox; exactly one runs at a time.</summary>
        public bool Draining { get; set; }
    }

    private sealed record Target(string SessionId, string Name, bool IsRoot);

    private sealed record Resolved(Target? Target, string? Error, string? Invalidated);

    private sealed record PendingDelivery(
        long DispatchId,
        string InitiatorSessionId,
        string TargetSessionId,
        string TargetName,
        string Message,
        string TaskLabel);

    /// <summary>Whether a user-driven session is inside a turn right now; false when this host does not hold it.</summary>
    private bool IsUserSessionRunning(string sessionId) =>
        scheduler.TryGetUserSession(sessionId, out var child) && child.IsRunning;

    /// <summary>Takes deliveries one at a time until the queue is empty, then releases the mailbox.</summary>
    private async Task DrainAsync(Mailbox box)
    {
        while (true)
        {
            PendingDelivery delivery;
            lock (gate)
            {
                if (box.Queue.Count == 0)
                {
                    box.Running = false;
                    box.Draining = false;
                    return;
                }

                delivery = box.Queue.Dequeue();
            }

            try
            {
                await DeliverAsync(delivery).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A delivery that blew up still owes the initiator a receipt: the catch above only covers the turn itself,
                // so a broken seam ends in the same place as a failed turn instead of a silent hole.
                Deliver(delivery.InitiatorSessionId, Receipt(delivery, new SessionTurnOutcome(
                    string.Empty,
                    SessionReplyStatus.Failed,
                    exception.Message,
                    0), null));
            }
        }
    }

    private async Task DeliverAsync(PendingDelivery delivery)
    {
        var outcome = await RunTurnAsync(delivery).ConfigureAwait(false);
        SessionTurnChanges? changes = null;
        if (outcome.Turn > 0 && endpoint() is { } seam)
        {
            try
            {
                changes = await seam.ReadChangesAsync(delivery.TargetSessionId, outcome.Turn, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.TraceWarning($"message_session: change line of turn {outcome.Turn} not read: {exception.Message}");
            }
        }

        Deliver(delivery.InitiatorSessionId, await BuildReceiptAsync(delivery, outcome, changes).ConfigureAwait(false));
    }

    /// <summary>
    /// Runs the target's turn. A session the app has a live view for runs there - the transcript shows it, its stop button
    /// reaches it and its own numbering owns the checkpoint. A user-driven session without a view is driven here, on the one
    /// host that holds it (<see cref="AgentHost.RunUserChildTurnAsync"/>), with the app's checkpoint numbering around it. A
    /// root without a view cannot run at all: Core never starts a root's turn.
    /// </summary>
    private async Task<SessionTurnOutcome> RunTurnAsync(PendingDelivery delivery)
    {
        var seam = endpoint();
        if (seam is null)
        {
            return new SessionTurnOutcome(string.Empty, SessionReplyStatus.Failed, "the app's sessions are not connected", 0);
        }

        var request = new SessionTurnRequest(delivery.TargetSessionId, delivery.Message, delivery.InitiatorSessionId, delivery.TargetName);
        var isRoot = string.Equals(delivery.TargetSessionId, anchor()?.SessionId, StringComparison.Ordinal);
        try
        {
            if (await seam.RunSessionTurnAsync(request, CancellationToken.None).ConfigureAwait(false) is { } throughTheView)
            {
                return throughTheView;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new SessionTurnOutcome(string.Empty, SessionReplyStatus.Failed, exception.Message, 0);
        }

        if (isRoot)
        {
            // The root's turns are the app's to start: without its view there is nowhere to run one, and starting one here
            // is exactly the unattended loop this feature must not have.
            return new SessionTurnOutcome(string.Empty, SessionReplyStatus.Failed, "the root session is not open", 0);
        }

        if (!scheduler.TryGetUserSession(delivery.TargetSessionId, out var child))
        {
            return new SessionTurnOutcome(string.Empty, SessionReplyStatus.Failed, "the session is no longer loaded", 0);
        }

        await WaitUntilIdleAsync(child).ConfigureAwait(false);
        var turn = 0;
        try
        {
            turn = await seam.BeginSessionTurnAsync(child.SessionId, child.WorkingDirectory, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"message_session: checkpoint of '{child.SessionId}' not opened: {exception.Message}");
        }

        AgentSubAgentFinished? finished = null;
        try
        {
            await foreach (var value in runUserChildTurn(child, ChatMessage.User(delivery.Message), CancellationToken.None)
                               .ConfigureAwait(false))
            {
                if (value is AgentSubAgentFinished ended)
                {
                    finished = ended;
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            finished ??= null;
            Trace.TraceWarning($"message_session: the turn of '{child.SessionId}' failed: {exception.Message}");
            if (turn > 0)
            {
                await EndTurnAsync(seam, child, turn).ConfigureAwait(false);
            }

            return new SessionTurnOutcome(string.Empty, SessionReplyStatus.Failed, exception.Message, turn);
        }

        if (turn > 0)
        {
            await EndTurnAsync(seam, child, turn).ConfigureAwait(false);
        }

        var (status, reason) = finished switch
        {
            { Status: SubAgentStatus.Completed } => (SessionReplyStatus.Completed, (string?)null),
            { Status: SubAgentStatus.Stopped } => (SessionReplyStatus.Stopped, finished.Error),
            { Status: SubAgentStatus.Failed } => (SessionReplyStatus.Failed, finished.Error),
            // A run that emitted no terminal event still ended: without one there is no failure to report, and reporting a
            // failure would be a guess. The report is the instance's last committed answer.
            _ => (SessionReplyStatus.Completed, (string?)null),
        };
        return new SessionTurnOutcome(child.PartialText, status, reason, turn);
    }

    private static async Task EndTurnAsync(ISessionMessageEndpoint seam, AgentInstance child, int turn)
    {
        try
        {
            await seam.EndSessionTurnAsync(child.SessionId, turn, child.WorkingDirectory, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"message_session: checkpoint {turn} of '{child.SessionId}' not closed: {exception.Message}");
        }
    }

    /// <summary>
    /// Waits until the instance holds no run: a session the user is driving keeps its turn, and the delivery starts right
    /// after it instead of failing ("目标忙则排队").
    /// </summary>
    private static async Task WaitUntilIdleAsync(AgentInstance child)
    {
        while (child.IsRunning)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<string> BuildReceiptAsync(PendingDelivery delivery, SessionTurnOutcome outcome, SessionTurnChanges? changes)
    {
        var files = changes?.Files ?? 0;
        var added = changes?.Added ?? 0;
        var removed = changes?.Removed ?? 0;
        var report = outcome.Report ?? string.Empty;
        try
        {
            return SessionReplies.Build(
                delivery.DispatchId.ToString(CultureInfo.InvariantCulture),
                delivery.TargetName,
                outcome.Status,
                outcome.Reason,
                delivery.TaskLabel,
                files,
                added,
                removed,
                report);
        }
        catch (ArgumentException)
        {
            // The report is past the caps: the copy has to exist first, so the part that was cut stays readable. Without an
            // artifact store there is nowhere to put it, and a receipt without the pointer still beats no receipt at all.
            var artifactId = await StoreReportAsync(delivery, report).ConfigureAwait(false);
            return artifactId is { Length: > 0 }
                ? SessionReplies.Build(
                    delivery.DispatchId.ToString(CultureInfo.InvariantCulture),
                    delivery.TargetName,
                    outcome.Status,
                    outcome.Reason,
                    delivery.TaskLabel,
                    files,
                    added,
                    removed,
                    report,
                    artifactId)
                : SessionReplies.Build(
                    delivery.DispatchId.ToString(CultureInfo.InvariantCulture),
                    delivery.TargetName,
                    outcome.Status,
                    outcome.Reason,
                    delivery.TaskLabel,
                    files,
                    added,
                    removed,
                    Fit(report));
        }
    }

    private async Task<string?> StoreReportAsync(PendingDelivery delivery, string report)
    {
        if (artifacts is null || string.IsNullOrWhiteSpace(report))
        {
            return null;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(report);
            var info = await artifacts.PutAsync(
                    delivery.InitiatorSessionId,
                    string.Concat("session-reply-", delivery.DispatchId.ToString(CultureInfo.InvariantCulture), ".md"),
                    bytes,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return info.Id;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"message_session: the report of #{delivery.DispatchId} was not stored: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// The report cut to the caps by hand, for the one case the artifact store cannot serve: the last line says the rest is
    /// gone, because a receipt that silently lost half its report reads like a short answer.
    /// </summary>
    private static string Fit(string report)
    {
        var builder = new StringBuilder(Math.Min(report.Length, SessionReplies.ReportCharLimit));
        var used = 0;
        var lines = 0;
        foreach (var line in report.AsSpan().EnumerateLines())
        {
            if (lines == SessionReplies.ReportLineLimit)
            {
                break;
            }

            var separator = lines > 0 ? 1 : 0;
            if (used + separator + line.Length > SessionReplies.ReportCharLimit)
            {
                break;
            }

            if (separator == 1)
            {
                builder.Append('\n');
                used++;
            }

            builder.Append(line);
            used += line.Length;
            lines++;
        }

        return builder.Append("\n(报告超出上限，转存失败，已截断)").ToString();
    }

    private void Deliver(string initiatorSessionId, string receipt)
    {
        if (endpoint() is not { } seam)
        {
            return;
        }

        try
        {
            seam.DeliverReceipt(initiatorSessionId, receipt);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"message_session: receipt not delivered to '{initiatorSessionId}': {exception.Message}");
        }
    }

    /// <summary>The receipt for a delivery whose turn could not even be reported; shares the shape of a real one.</summary>
    private static string Receipt(PendingDelivery delivery, SessionTurnOutcome outcome, SessionTurnChanges? changes) =>
        SessionReplies.Build(
            delivery.DispatchId.ToString(CultureInfo.InvariantCulture),
            delivery.TargetName,
            outcome.Status,
            outcome.Reason,
            delivery.TaskLabel,
            changes?.Files ?? 0,
            changes?.Added ?? 0,
            changes?.Removed ?? 0,
            outcome.Report ?? string.Empty);

    /// <summary>The short task label the receipt header carries: the message's first line, bounded by <see cref="SessionReplies.Build"/>.</summary>
    private static string ShortLabel(string message)
    {
        var line = message;
        var newline = line.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
        {
            line = line[..newline];
        }

        line = line.Trim();
        return line.Length <= TaskLabelLimit ? line : string.Concat(line.AsSpan(0, TaskLabelLimit), "…");
    }

    private static ToolResult Error(string text) => new(text, IsError: true);
}

/// <summary>
/// <c>message_session</c> (forge-core "授权通信"): pure async delivery from a root session to one of the agent sessions the
/// user opened under it, or from one of those back to its root. The tool is always in the tool array of those two surfaces -
/// it costs a constant number of prefix bytes and its answer depends on the user's grants, not on the array's shape - and it
/// never runs the target inline: it enqueues, says "已投递"/"已排队" and the reply arrives as a receipt.
/// </summary>
public sealed class MessageSessionTool : ITool
{
    public const string Name = SessionReplies.ToolName;

    private const string Description =
        "Deliver a message to another session of this tree: from the root session to one of the agent sessions the user opened under it, "
        + "or from such a session back to its root. The target is named the way the tree and the @ picker show it. Delivery is async: this "
        + "returns as soon as the message is queued and never carries the answer. The target runs one turn with the message as its next user "
        + "message and then writes a receipt back to you (status, the files it changed and its full report), which you see at your next tool "
        + "batch or that lands in the transcript while you are idle. Only the sessions the user picked in the composer's @ picker may be "
        + "addressed; a name without such an authorization is an error.";

    private readonly SessionMessenger messenger;

    public MessageSessionTool(SessionMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(messenger);
        this.messenger = messenger;
        Definition = new ToolDefinition(Name, Description, SessionReplies.InputSchemaJson);
    }

    public ToolDefinition Definition { get; }

    /// <summary>
    /// It makes another session work, so it is declared a write; the app's approval rules let the delivery itself pass without
    /// a card (the user's @ pick is the authorization), while the target turn's own tool calls keep asking as usual.
    /// </summary>
    public ToolSideEffect SideEffect => ToolSideEffect.Write;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        string? target;
        string? message;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ToolResult("message_session takes an object with 'target' and 'message'.", IsError: true);
            }

            target = String(document.RootElement, "target");
            message = String(document.RootElement, "message");
        }
        catch (JsonException exception)
        {
            return new ToolResult($"message_session could not read its arguments: {exception.Message}", IsError: true);
        }

        if (target is null || message is null)
        {
            return new ToolResult("message_session needs both 'target' and 'message', as strings.", IsError: true);
        }

        return await messenger.SendAsync(context, target, message, ct).ConfigureAwait(false);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>What the messenger needs from the host's root: the session it belongs to and the directory it works in.</summary>
internal sealed record SessionAnchor(string SessionId, string WorkingDirectory);
