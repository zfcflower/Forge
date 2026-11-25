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

    /// <summary>
    /// Closes the checkpoint opened by <see cref="BeginSessionTurnAsync"/>, on success, failure and stop alike, and reports
    /// how the turn ended: a turn Core drives itself has no other reporter, so this call is the only way its outcome reaches
    /// <c>turn_metrics</c> - a failure or a stop that never arrives here would leave the row reading as a completed turn.
    /// </summary>
    /// <param name="status">How the turn ended. Used only while closing this checkpoint: it decides the status of the turn's
    /// metrics row (completed, failed or stopped) and never the checkpoint's own write.</param>
    /// <param name="reason">The failure or stop text the receipt carries; null for a turn that went through. Recorded as the
    /// row's error, and nothing is made up for a row that has none.</param>
    /// <param name="lastSeq">The seq of the last message the delivered turn persisted, read off the child instance at the
    /// source: it is the turn's exact stream position, the one key a replay pairs the row by, and reading it here rather
    /// than after the fact is what keeps a message that lands meanwhile from taking the position.</param>
    Task EndSessionTurnAsync(
        string sessionId,
        int turn,
        SessionReplyStatus status,
        string? reason,
        string workingDirectory,
        long lastSeq,
        CancellationToken ct);

    /// <summary>
    /// The change line of one turn, read from <c>turn_checkpoints</c> (files, added and removed lines); null when the session
    /// recorded no checkpoint for it (a directory that is not a repository, or a write that failed).
    /// </summary>
    Task<SessionTurnChanges?> ReadChangesAsync(string sessionId, int turn, CancellationToken ct);

    /// <summary>
    /// Hands one receipt to the session that asked for it. Never starts a turn: when that session is running a turn the app
    /// puts the receipt at the next tool-batch boundary, otherwise it lands in the transcript as an unread message. Only called
    /// while <see cref="AcceptsReceipts"/> is true.
    /// </summary>
    void DeliverReceipt(string initiatorSessionId, string text);

    /// <summary>
    /// Whether this app can still take a receipt. False once the window is closing: the delivery is asynchronous (it posts to the
    /// UI thread), so a report handed over then would be dropped without a trace - the caller keeps it itself instead.
    /// </summary>
    bool AcceptsReceipts { get; }
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

    /// <summary>Why a delivery ended when this root's sessions were stopped before its turn started.</summary>
    private const string StoppedReason = "会话已停止";

    /// <summary>
    /// Why a delivery ended when its target never went idle inside <see cref="IdleWaitLimit"/>. The wire text of the receipt, so
    /// it stays fixed: the app's own wait for a target's tab ends with the same words (it holds the turn the mailbox waits for).
    /// </summary>
    public const string BusyTooLongReason = "目标长时间忙";

    /// <summary>
    /// How long a delivery waits for its target to go idle: far past any one turn, so only a stuck target reaches it. The app's
    /// wait for a busy tab follows the same limit - the two waits are the same wait seen from either side of the seam.
    /// </summary>
    public static readonly TimeSpan IdleWaitLimit = TimeSpan.FromMinutes(30);

    /// <summary>The pause between two looks at whether a busy target has gone idle.</summary>
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(200);

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

    /// <summary>
    /// Cancelled by <see cref="CancelPending"/> and replaced with a fresh one, so a delivery already handed to a worker learns
    /// about the root's stop while one queued behind it does not. Guarded by <see cref="gate"/>.
    /// </summary>
    private CancellationTokenSource stopGate = new();

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
    /// Stops every delivery this root's mailboxes still owe (forge-core "授权通信": the root's stop takes the sessions under it
    /// and the messages already handed to them together): the queued deliveries are dropped and the one a mailbox is running
    /// right now ends with it, each one answered by a <see cref="SessionReplyStatus.Stopped"/> receipt, and the turns in flight
    /// get a stop token, so a mailbox whose target is busy returns to the next delivery instead of waiting out
    /// <see cref="IdleWaitLimit"/>. A delivery whose turn already ended is left alone: <see cref="DeliverAsync"/> recorded its
    /// outcome and is only assembling its receipt, which the stop must not replace. Thread-safe, and it may be called from the
    /// UI thread: it takes <see cref="gate"/> for a moment and never calls the seam while holding it.
    /// </summary>
    /// <returns>How many deliveries were stopped.</returns>
    public int CancelPending(string? reason)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? StoppedReason : reason.Trim();
        List<PendingDelivery> stopped = [];
        CancellationTokenSource previous;
        lock (gate)
        {
            previous = stopGate;
            stopGate = new CancellationTokenSource();
            foreach (var box in mailboxes.Values)
            {
                while (box.Queue.Count > 0)
                {
                    stopped.Add(box.Queue.Dequeue());
                }

                // Only a delivery whose turn is still running is stopped here. One that already recorded its outcome is
                // past the turn: it is reading its change line and building its receipt right now, so its answer is the
                // one the initiator must read - a "已停止" thrown in here would be it, because Reply hands out the first
                // receipt only and the real one would be dropped.
                if (box.Current is { Outcome: null } running)
                {
                    stopped.Add(running);
                }
            }
        }

        previous.Cancel();
        foreach (var delivery in stopped)
        {
            Reply(delivery, Receipt(delivery, new SessionTurnOutcome(string.Empty, SessionReplyStatus.Stopped, text, 0), null));
        }

        return stopped.Count;
    }

    /// <summary>
    /// <c>message_session</c>'s body: resolve the target by name within this root, check the user's grant, make sure the target
    /// is still there, then enqueue. Never waits for the target: the answer says "已投递" (it starts now) or "已排队" (it waits
    /// behind work the target already has), and in both cases that there is no result yet.
    /// <para>An active user session takes the message whether or not this process has run a turn for it: the app opens the
    /// session's own tab and runs it there (<see cref="ISessionMessageEndpoint.RunSessionTurnAsync"/>), and only a host without
    /// that seam falls back to the instance this scheduler holds - which is why "not loaded" is not a reason to refuse.</para>
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
        }

        var endpointNow = endpoint();
        if (endpointNow is null)
        {
            return Error("This host is not connected to the app's sessions, so nothing can be delivered.");
        }

        // Read the busy state before taking the gate, never inside it: IsBusy marshals to the UI thread (the app's
        // implementation does) and the UI thread itself takes that same gate when the user presses stop, so a call made
        // under the lock would wedge the tool thread against the stop button. It only decides the wording below.
        var targetBusy = target.IsRoot ? endpointNow.IsBusy(target.SessionId) : IsUserSessionRunning(target.SessionId);
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
            // What the target already owes: the mailbox work read under the gate here, plus the busy reading taken above
            // (its own run or a turn this host does not queue - the user's turn on the root, or one in the session's tab).
            queued = box.Running || box.Queue.Count > 0 || targetBusy;
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

        /// <summary>The delivery the worker took out of the queue and has not finished yet; <see cref="CancelPending"/> stops it.</summary>
        public PendingDelivery? Current { get; set; }
    }

    private sealed record Target(string SessionId, string Name, bool IsRoot);

    private sealed record Resolved(Target? Target, string? Error, string? Invalidated);

    private sealed record PendingDelivery(
        long DispatchId,
        string InitiatorSessionId,
        string TargetSessionId,
        string TargetName,
        string Message,
        string TaskLabel)
    {
        /// <summary>
        /// Whether the receipt went out; read and written under the messenger's gate, because <see cref="CancelPending"/> and
        /// the worker that runs the delivery can both reach <see cref="Reply"/> for it.
        /// </summary>
        public bool Replied { get; set; }

        /// <summary>
        /// How the delivery's turn ended, recorded under the messenger's gate the moment the turn returned and before the
        /// change line and the receipt are worked out. <see cref="CancelPending"/> reads it to tell a delivery that is still
        /// running (a stop owns that one) from one that already ended and is only assembling its answer: rewriting the second
        /// one as "已停止" would drop the finished turn's receipt for good, because <see cref="Replied"/> lets the stop's line win.
        /// </summary>
        public SessionTurnOutcome? Outcome { get; set; }
    }

    /// <summary>Records how a delivery's turn ended, so <see cref="CancelPending"/> leaves a finished delivery to its worker.</summary>
    private void MarkEnded(PendingDelivery delivery, SessionTurnOutcome outcome)
    {
        lock (gate)
        {
            delivery.Outcome = outcome;
        }
    }

    /// <summary>Whether a user-driven session is inside a turn right now; false when this host does not hold it.</summary>
    private bool IsUserSessionRunning(string sessionId) =>
        scheduler.TryGetUserSession(sessionId, out var child) && child.IsRunning;

    /// <summary>Takes deliveries one at a time until the queue is empty, then releases the mailbox.</summary>
    private async Task DrainAsync(Mailbox box)
    {
        while (true)
        {
            PendingDelivery delivery;
            CancellationToken stop;
            lock (gate)
            {
                if (box.Queue.Count == 0)
                {
                    box.Running = false;
                    box.Draining = false;
                    return;
                }

                delivery = box.Queue.Dequeue();
                box.Current = delivery;
                stop = stopGate.Token;
            }

            try
            {
                await DeliverAsync(delivery, stop).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A delivery that blew up still owes the initiator a receipt: the catch above only covers the turn itself,
                // so a broken seam ends in the same place as a failed turn instead of a silent hole. Recording the outcome
                // first keeps a stop that arrives in this moment from taking the answer away from the worker.
                var failed = new SessionTurnOutcome(string.Empty, SessionReplyStatus.Failed, exception.Message, 0);
                MarkEnded(delivery, failed);
                Reply(delivery, Receipt(delivery, failed, null));
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(box.Current, delivery))
                    {
                        box.Current = null;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Runs the delivery's turn and answers it: the outcome is recorded before the change line is read and the receipt built,
    /// because everything after the turn is bookkeeping on an answer that already exists - a stop that arrives while it runs
    /// must not throw that answer away (it would be the only line the initiator ever reads).
    /// </summary>
    private async Task DeliverAsync(PendingDelivery delivery, CancellationToken stop)
    {
        var outcome = await RunTurnAsync(delivery, stop).ConfigureAwait(false);
        MarkEnded(delivery, outcome);
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

        Reply(delivery, await BuildReceiptAsync(delivery, outcome, changes).ConfigureAwait(false));
    }

    /// <summary>
    /// Runs the target's turn. A session the app has a live view for runs there - the transcript shows it, its stop button
    /// reaches it and its own numbering owns the checkpoint. A user-driven session without a view is driven here, on the one
    /// host that holds it (<see cref="AgentHost.RunUserChildTurnAsync"/>), with the app's checkpoint numbering around it. A
    /// root without a view cannot run at all: Core never starts a root's turn.
    /// </summary>
    private async Task<SessionTurnOutcome> RunTurnAsync(PendingDelivery delivery, CancellationToken stop)
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
            if (await seam.RunSessionTurnAsync(request, stop).ConfigureAwait(false) is { } throughTheView)
            {
                return throughTheView;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The root's sessions were stopped while the delivery waited for the target: the receipt says so instead of
            // reporting the cancellation as a failure of the turn.
            return new SessionTurnOutcome(string.Empty, SessionReplyStatus.Stopped, StoppedReason, 0);
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

        if (!await WaitUntilIdleAsync(child, stop).ConfigureAwait(false))
        {
            var ended = stop.IsCancellationRequested ? StoppedReason : BusyTooLongReason;
            return new SessionTurnOutcome(string.Empty, SessionReplyStatus.Stopped, ended, 0);
        }

        if (await RefuseInactiveAsync(child).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

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
            await foreach (var value in runUserChildTurn(child, ChatMessage.User(delivery.Message), stop)
                               .ConfigureAwait(false))
            {
                if (value is AgentSubAgentFinished ended)
                {
                    finished = ended;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The root's stop reached this turn: the checkpoint still closes, and the delivery ends as stopped rather than
            // as a failure of the session's work.
            if (turn > 0)
            {
                await EndTurnAsync(seam, child, turn, SessionReplyStatus.Stopped, StoppedReason).ConfigureAwait(false);
            }

            return new SessionTurnOutcome(child.PartialText, SessionReplyStatus.Stopped, StoppedReason, turn);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            finished ??= null;
            Trace.TraceWarning($"message_session: the turn of '{child.SessionId}' failed: {exception.Message}");
            if (turn > 0)
            {
                await EndTurnAsync(seam, child, turn, SessionReplyStatus.Failed, exception.Message).ConfigureAwait(false);
            }

            return new SessionTurnOutcome(string.Empty, SessionReplyStatus.Failed, exception.Message, turn);
        }

        // The outcome is read before the checkpoint closes, because closing it is also what writes the turn's metrics row:
        // a run that ended failed or stopped must not be recorded as a completed turn.
        var (status, reason) = finished switch
        {
            { Status: SubAgentStatus.Completed } => (SessionReplyStatus.Completed, (string?)null),
            { Status: SubAgentStatus.Stopped } => (SessionReplyStatus.Stopped, finished.Error),
            { Status: SubAgentStatus.Failed } => (SessionReplyStatus.Failed, finished.Error),
            // A run that emitted no terminal event still ended: without one there is no failure to report, and reporting a
            // failure would be a guess. The report is the instance's last committed answer.
            _ => (SessionReplyStatus.Completed, (string?)null),
        };

        if (turn > 0)
        {
            await EndTurnAsync(seam, child, turn, status, reason).ConfigureAwait(false);
        }

        return new SessionTurnOutcome(child.PartialText, status, reason, turn);
    }

    /// <summary>
    /// Closes the turn's checkpoint with the outcome the run actually reached: the App writes the turn's metrics row while it
    /// closes the checkpoint, so passing the status here is what keeps a failed or stopped delivery from being recorded as a
    /// completed turn. A checkpoint that cannot be closed is never a reason to report a different outcome.
    /// </summary>
    private static async Task EndTurnAsync(
        ISessionMessageEndpoint seam,
        AgentInstance child,
        int turn,
        SessionReplyStatus status,
        string? reason)
    {
        try
        {
            await seam.EndSessionTurnAsync(
                child.SessionId,
                turn,
                status,
                reason,
                child.WorkingDirectory,
                child.LastPersistedSeq,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"message_session: checkpoint {turn} of '{child.SessionId}' not closed: {exception.Message}");
        }
    }

    /// <summary>
    /// The gate a delivery Core drives itself passes right before it starts the turn (forge-core "回合前校验", the rule the
    /// root's own turn already follows): the mailbox waited for the target, so the sessions are read again - the user can take
    /// the root over or archive it, and the target can be closed, while the delivery sits in the queue. A session that left
    /// <see cref="SessionStatus.Active"/> means no turn at all: the delivery fails saying so, and the grants that can no longer
    /// hold are dropped instead of surviving into the next delivery.
    /// </summary>
    /// <returns>The refused outcome when the turn must not start, null when it may.</returns>
    private async Task<SessionTurnOutcome?> RefuseInactiveAsync(AgentInstance child)
    {
        var rootId = anchor()?.SessionId;
        var root = rootId is null ? null : await ReadSessionAsync(rootId, CancellationToken.None).ConfigureAwait(false);
        if (root is not { Status: SessionStatus.Active })
        {
            grants.RevokeRoot(rootId);
            return new SessionTurnOutcome(string.Empty, SessionReplyStatus.Failed, "会话已不再活动（根会话已离开 Active）", 0);
        }

        var target = await ReadSessionAsync(child.SessionId, CancellationToken.None).ConfigureAwait(false);
        if (target is not { Status: SessionStatus.Active })
        {
            grants.RevokeTarget(child.SessionId);
            return new SessionTurnOutcome(string.Empty, SessionReplyStatus.Failed, "会话已不再活动（目标会话已归档或关闭）", 0);
        }

        return null;
    }

    /// <summary>
    /// Waits until the instance holds no run: a session the user is driving keeps its turn, and the delivery starts right
    /// after it instead of failing ("目标忙则排队"). Bounded by <see cref="IdleWaitLimit"/> and by the root's stop, because a
    /// mailbox that never gets there would leave the initiator without the receipt the state line owes it.
    /// </summary>
    /// <returns>Whether the target went idle; false when the limit ran out or the root was stopped.</returns>
    private static async Task<bool> WaitUntilIdleAsync(AgentInstance child, CancellationToken stop)
    {
        var waited = Stopwatch.StartNew();
        while (child.IsRunning)
        {
            if (waited.Elapsed >= IdleWaitLimit)
            {
                return false;
            }

            try
            {
                await Task.Delay(IdlePollInterval, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return true;
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
                    SessionReplies.Cut(report));
        }
    }

    /// <summary>
    /// Hands one receipt to the app's delivery seam, which owns what this host cannot do: drawing it in a tab it shows, storing it
    /// when that tab is idle, marking it unread. False when no endpoint is installed (a script, a test) or when the one there refused
    /// it (a window already closing) - the caller then has to keep the message itself.
    /// </summary>
    internal bool TryDeliverReceipt(string sessionId, string text)
    {
        if (endpoint() is not { } seam || !seam.AcceptsReceipts)
        {
            return false;
        }

        seam.DeliverReceipt(sessionId, text);
        return true;
    }

    /// <summary>Whether an app is behind this host at all; false means the host itself has to keep the message.</summary>
    internal bool HasEndpoint => endpoint() is not null;

    /// <summary>
    /// The receipt text of one background child. Composing stays here (the receipt's shape and its artifact fallback live in this
    /// class); which conversation it lands in is the host's call, because only the host knows which instances it holds.
    /// </summary>
    internal Task<string> BuildBackgroundReportAsync(BackgroundChildReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return BuildBackgroundReportTextAsync(report);
    }

    /// <summary>The receipt of one background child, with the report stored as an artifact when the caps would have cut it.</summary>
    private async Task<string> BuildBackgroundReportTextAsync(BackgroundChildReport report)
    {
        var agentId = report.AgentId;
        var reportText = report.Report ?? string.Empty;
        try
        {
            return SessionReplies.BuildChildReport(report.Kind, agentId, report.Status, report.Reason, report.TaskLabel, 0, 0, 0, reportText);
        }
        catch (ArgumentException)
        {
            // Past the caps: the copy has to exist first, so the part that was cut stays readable. Without an artifact store
            // the receipt is cut instead - an incomplete receipt still beats a report nobody ever sees.
            var artifactId = await StoreChildReportAsync(report).ConfigureAwait(false);
            return SessionReplies.BuildChildReport(
                report.Kind,
                agentId,
                report.Status,
                report.Reason,
                report.TaskLabel,
                0,
                0,
                0,
                artifactId is { Length: > 0 } ? reportText : SessionReplies.Cut(reportText),
                artifactId);
        }
    }

    private async Task<string?> StoreChildReportAsync(BackgroundChildReport report)
    {
        if (artifacts is null || string.IsNullOrWhiteSpace(report.Report))
        {
            return null;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(report.Report);
            var info = await artifacts.PutAsync(
                    report.ChildSessionId.Length > 0 ? report.ChildSessionId : report.ParentSessionId,
                    "sub-agent-report.md",
                    bytes,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return info.Id;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"background sub-agent report could not be stored: {exception.Message}");
            return null;
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
    /// <summary>
    /// Writes one delivery's receipt exactly once. Two callers can get here for the same delivery - the worker that ran its
    /// turn and <see cref="CancelPending"/> - and the initiator must read one answer per dispatch number, whichever came first:
    /// a turn that ended before the stop counts (<see cref="CancelPending"/> no longer answers for a delivery that recorded its
    /// <see cref="PendingDelivery.Outcome"/>, and this first-wins check is the belt to that suspender), a stopped delivery does
    /// not owe a second line.
    /// </summary>
    private void Reply(PendingDelivery delivery, string receipt)
    {
        lock (gate)
        {
            if (delivery.Replied)
            {
                return;
            }

            delivery.Replied = true;
        }

        Deliver(delivery.InitiatorSessionId, receipt);
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
