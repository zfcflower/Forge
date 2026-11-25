using System.Text;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// One message of an instance's context; <see cref="Seq"/> is its session store sequence, 0 when unpersisted.
/// <see cref="Inherited"/> marks the prefix taken over from an ancestor session by full inheritance: its
/// <see cref="Seq"/> belongs to that ancestor, and compaction keeps the whole inherited part as it is.
/// </summary>
internal readonly record struct AgentMessage(ChatMessage Message, long Seq, bool Inherited = false);

/// <summary>
/// A live agent: a definition (shared, immutable) plus the context of this instance (history, usage)
/// and the exclusive-run gate that stops two spawns from driving it at once.
/// </summary>
public sealed class AgentInstance
{
    private readonly List<AgentMessage> history = [];
    private readonly Lock historyGate = new();
    private readonly StringBuilder streaming = new();
    private readonly Lock interjectionGate = new();
    private List<Interjection>? interjections;
    private int pendingInterjections;
    private string lastText = string.Empty;
    private int running;
    private DateTimeOffset? lastRequestAt;
    private string? cacheTtlInUse;

    /// <summary>
    /// The two cumulative counters as one immutable value: the run driving this instance moves them on its own thread while
    /// the shell reads them for the usage bar, so they are published and read as a whole (see <see cref="ReadCounters"/>).
    /// </summary>
    private volatile Counters counters = new(TokenUsage.Empty, TimeSpan.Zero);

    /// <summary>
    /// The gap since this instance's previous model request, then records this one. The cache-lifetime policy reads the gap; the
    /// state belongs to the instance because what it measures is the pause between two turns of one agent, not of one request.
    /// </summary>
    internal TimeSpan? TakeRequestGap(DateTimeOffset now)
    {
        var previous = lastRequestAt;
        lastRequestAt = now;
        return previous is { } at ? now - at : null;
    }

    /// <summary>Moves this instance to the hour-long cache for good; never reversed, since reversing it would force a write.</summary>
    internal void PromoteCacheTtl() => cacheTtlInUse = "1h";

    /// <summary>The lifetime the last request asked for, reported to the shell so the strip can show which one is in force.</summary>
    internal string? CacheTtlInUse => cacheTtlInUse;

    internal void NoteCacheTtl(string ttl) => cacheTtlInUse = ttl;

    internal AgentInstance(
        string id,
        AgentDefinition definition,
        string sessionId,
        string workingDirectory,
        int depth,
        ComposedPrompt prompt)
    {
        Id = id;
        Definition = definition;
        SessionId = sessionId;
        WorkingDirectory = workingDirectory;
        Depth = depth;
        Prompt = prompt;
    }

    public string Id { get; }

    public AgentDefinition Definition { get; }

    public string SessionId { get; }

    public string WorkingDirectory { get; }

    public int Depth { get; }

    public ComposedPrompt Prompt { get; }

    public TokenUsage Usage
    {
        get => counters.Usage;
        internal set
        {
            // One read of the pair, republished with the new half: a run moves these on its own thread, so a setter never reads
            // the pair twice and never loses what the other half says.
            var read = counters;
            counters = new Counters(value, read.Stream);
        }
    }

    /// <summary>
    /// The files this instance has read, which <c>write</c> consults before it replaces an existing file (see
    /// <see cref="IFileVersionLedger"/>): in memory, cleared when the instance goes away, and one table per instance
    /// rather than per session id - a rebuilt agent has not seen what its predecessor read.
    /// </summary>
    public IFileVersionLedger FileVersions { get; } = new FileVersionLedger();

    /// <summary>
    /// How long the model spent streaming this instance's answers so far: the sum of every completed call's
    /// first-output-increment-to-stream-end measurement (<see cref="MessageCompleted.StreamElapsed"/>), an
    /// instance-cumulative counter exactly like <see cref="Usage"/> - a turn's own share is the difference against
    /// its start snapshot. An attempt that failed or was cancelled before its completion carries no measurement and
    /// adds nothing, which is why this never needs an "unknown" fallback.
    /// </summary>
    public TimeSpan StreamElapsed
    {
        get => counters.Stream;
        internal set
        {
            var read = counters;
            counters = new Counters(read.Usage, value);
        }
    }

    /// <summary>
    /// Both cumulative counters as one reading. The run moves them on its own thread, so a reader elsewhere must not see a
    /// token count of one request beside the streaming time of another, nor a half-written <see cref="TimeSpan"/>: the pair
    /// travels as one reference, and one read of it is a point in time this instance really had.
    /// </summary>
    public (TokenUsage Usage, TimeSpan StreamElapsed) ReadCounters()
    {
        var read = counters;
        return (read.Usage, read.Stream);
    }

    /// <summary>Prompt-cache bookkeeping of this instance (routing key, last fingerprint, first-request report); loop-owned.</summary>
    internal PromptCacheState CacheState { get; } = new();

    /// <summary>Usage and hysteresis bookkeeping for automatic compaction; loop-owned.</summary>
    internal CompactionState Compaction { get; } = new();

    /// <summary>Control state of the run driving this instance right now; null for a root agent or a finished sub-agent.</summary>
    internal SubAgentRun? Control { get; set; }

    /// <summary>
    /// A run is driving this instance right now. Roots have no <see cref="Control"/> and a finished sub-agent has none either,
    /// so this answers for the instances that carry a run - in practice the user-driven sessions, whose turn a mailbox delivery
    /// has to wait for (<see cref="SessionMessenger"/>).
    /// </summary>
    internal bool IsRunning => Control is not null;

    /// <summary>
    /// True for a session the user opened under a root (forge-core "Agent 会话"): such a session gets the session-messaging
    /// tool, while a child the model spawned with <c>spawn_agent</c> does not (depth 2 inherits no authorization either).
    /// </summary>
    internal bool IsUserSession { get; init; }

    /// <summary>Text produced by this instance so far: the turn in progress when it is streaming, else the last finished turn.</summary>
    internal string PartialText => streaming.Length > 0 ? streaming.ToString() : lastText;

    /// <summary>Snapshot of the persisted context, for diagnostics and retrieval; the loop reads <see cref="Messages"/>.</summary>
    public IReadOnlyList<ChatMessage> History => [.. history.Select(static entry => entry.Message)];

    internal List<AgentMessage> Messages => history;

    /// <summary>True while at least one interjection waits for the next tool-batch boundary.</summary>
    public bool HasInterjections => Volatile.Read(ref pendingInterjections) > 0;

    /// <summary>
    /// Queues a user message for the run driving this instance. The loop delivers it at the next tool-batch boundary, after
    /// the batch's results, without dropping any call; a run that ends without another batch leaves it pending, and the
    /// host takes it back with <see cref="TakeInterjections"/> (typically to send it as the next turn). Thread-safe.
    /// </summary>
    /// <returns>
    /// The queued message, so the caller can take it back with <see cref="Withdraw"/> while it is still waiting (the user
    /// withdraws an interjection to edit it).
    /// </returns>
    public Interjection Interject(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return Enqueue(new Interjection([new TextBlock(text)]));
    }

    /// <summary>
    /// Queues a user message with attachments: typed text as <see cref="TextBlock"/>s, attachments as
    /// <see cref="ImageBlock"/>, <see cref="FileBlock"/> or an inlined text file (<see cref="Storage.TextAttachment"/>), in
    /// the order they should reach the model. It must carry typed text or at least one attachment; other block kinds are
    /// refused. Delivered like <see cref="Interject(string)"/>.
    /// </summary>
    public Interjection Interject(IReadOnlyList<ContentBlock> content) => Enqueue(Interjection.Create(content));

    /// <summary>
    /// Takes back one interjection that has not reached the model yet, so the user can edit what they sent while the turn
    /// runs on. False once the loop has taken them (all pending ones leave together at a tool-batch boundary): the message is
    /// part of the history then and cannot be undone. Thread-safe.
    /// </summary>
    public bool Withdraw(Interjection interjection)
    {
        ArgumentNullException.ThrowIfNull(interjection);
        lock (interjectionGate)
        {
            if (interjections is not { } pending)
            {
                return false;
            }

            var index = pending.IndexOf(interjection);
            if (index < 0)
            {
                return false;
            }

            pending.RemoveAt(index);
            Volatile.Write(ref pendingInterjections, pending.Count);
            return true;
        }
    }

    /// <summary>Removes and returns the interjections not delivered yet, oldest first, with their blocks; empty when none.</summary>
    public IReadOnlyList<Interjection> TakeInterjections()
    {
        if (Volatile.Read(ref pendingInterjections) == 0)
        {
            return [];
        }

        lock (interjectionGate)
        {
            var taken = interjections;
            interjections = null;
            Volatile.Write(ref pendingInterjections, 0);
            return taken is null ? [] : taken;
        }
    }

    /// <summary>
    /// Puts an interjection this instance had already handed over back into its inbox: the caller took it and could not keep it
    /// (a store write failed), so it waits for the next tool-batch boundary instead of being lost. Thread-safe.
    /// </summary>
    internal void Requeue(Interjection interjection) => Enqueue(interjection);

    private Interjection Enqueue(Interjection interjection)
    {
        lock (interjectionGate)
        {
            (interjections ??= []).Add(interjection);
            Volatile.Write(ref pendingInterjections, interjections.Count);
            return interjection;
        }
    }

    internal bool TryAcquire() => Interlocked.CompareExchange(ref running, 1, 0) == 0;

    internal void Release() => Volatile.Write(ref running, 0);

    /// <summary>
    /// A run holds this instance right now, read from the gate itself rather than from <see cref="Control"/>: the control
    /// object is attached a moment later (after the run is created and the text reset), so a scheduler picking a reuse
    /// candidate from the control would briefly see an instance that is already running as idle - and scan its history
    /// while the new run appends to it.
    /// </summary>
    internal bool IsAcquired => Volatile.Read(ref running) != 0;

    internal void Add(ChatMessage message, long seq)
    {
        // Guarded: a delivery can append to an idle instance while a spawn reads its history to judge reuse, and the two
        // must not see each other's half-written list.
        lock (historyGate)
        {
            history.Add(new AgentMessage(message, seq));
        }
    }

    /// <summary>
    /// A point-in-time copy of the context, for a reader outside the run that owns the instance (the reuse scan): it walks a
    /// snapshot instead of the live list, so a delivery appending meanwhile cannot make the walk throw.
    /// </summary>
    internal AgentMessage[] SnapshotHistory()
    {
        lock (historyGate)
        {
            return [.. history];
        }
    }

    /// <summary>
    /// The seq of the last message this instance persisted in its own session, 0 when nothing did or the newest entry
    /// was inherited from an ancestor (an inherited seq names the ancestor's session, not this one). The run-end event
    /// and the shell's counters read this: it is the turn's exact stream position, the one key a replay pairs the
    /// turn's metrics row by.
    /// </summary>
    public long LastPersistedSeq => history is [.., var last] && !last.Inherited && last.Seq > 0 ? last.Seq : 0;

    /// <summary>Bumped whenever the history is replaced rather than appended to, so recorded positions stop matching.</summary>
    internal int HistoryVersion { get; private set; }

    internal void BeginText() => streaming.Clear();

    internal void AppendText(string text) => streaming.Append(text);

    internal void CommitText(string text)
    {
        lastText = text;
        streaming.Clear();
    }

    internal void ResetText()
    {
        lastText = string.Empty;
        streaming.Clear();
    }

    internal void ReplaceHistory(IEnumerable<AgentMessage> messages)
    {
        lock (historyGate)
        {
            history.Clear();
            history.AddRange(messages);
            HistoryVersion++;
        }
    }

    /// <summary>One instance's cumulative counters, replaced as a whole: an instance's usage and streaming time never mix two requests.</summary>
    private sealed record Counters(TokenUsage Usage, TimeSpan Stream);
}

/// <summary>
/// Per-instance compaction bookkeeping, touched only by the run that holds the instance (or a manual compaction, which
/// the host runs only while no turn does).
/// </summary>
internal sealed class CompactionState
{
    /// <summary>Model the recorded usage belongs to (provider and model id); null when nothing was recorded.</summary>
    public string? ModelKey { get; set; }

    /// <summary>Real prompt size of the last request (input tokens, which include cached ones), 0 when unknown.</summary>
    public int LastPromptTokens { get; set; }

    /// <summary>How many messages the history held when that request was built.</summary>
    public int MessagesAtRequest { get; set; }

    /// <summary><see cref="AgentInstance.HistoryVersion"/> at that request.</summary>
    public int HistoryVersionAtRequest { get; set; }

    /// <summary>Own-content estimate after the last fruitless attempt; automatic attempts wait for growth past it.</summary>
    public int? FruitlessBaseline { get; set; }

    public int FruitlessAttempts { get; set; }

    /// <summary>True once automatic compaction gave up, until a manual compaction, another model, or a context reduction that brings the instance back under the line.</summary>
    public bool Stalled { get; set; }

    /// <summary>Why <see cref="Stalled"/> is set. Only <see cref="CompactionStallReason.Irreducible"/> can lift by itself (a higher line or a smaller tool set).</summary>
    public CompactionStallReason StallReason { get; set; }

    /// <summary>The line <see cref="CompactionStallReason.Irreducible"/> was declared against.</summary>
    public int IrreducibleLine { get; set; }

    /// <summary>The tool-definition tokens at that moment; a smaller set is a reason to look again.</summary>
    public int IrreducibleToolTokens { get; set; }

    /// <summary>Published tool-set version <see cref="ToolTokens"/> was counted for; -1 before the first count.</summary>
    public int ToolTokensVersion { get; set; } = -1;

    /// <summary>Estimated tokens of the tool definitions this instance is offered at <see cref="ToolTokensVersion"/>.</summary>
    public int ToolTokens { get; set; }

    public void ResetHysteresis()
    {
        FruitlessBaseline = null;
        FruitlessAttempts = 0;
        Stalled = false;
        StallReason = CompactionStallReason.Ineffective;
        IrreducibleLine = 0;
        IrreducibleToolTokens = 0;
    }

    public void ForgetUsage()
    {
        LastPromptTokens = 0;
        MessagesAtRequest = 0;
    }
}

/// <summary>Per-instance prompt-cache state, touched only by the run that holds the instance.</summary>
internal sealed class PromptCacheState
{
    /// <summary>True once the session row was read (successfully or not); the values below come from it.</summary>
    public bool Resolved { get; set; }

    /// <summary><see cref="ChatRequest.CacheRoutingKey"/>: the session's lineage root, else its own id.</summary>
    public string? RoutingKey { get; set; }

    public SessionOrigin? Origin { get; set; }

    /// <summary>Tool registry the fingerprint was last computed for; the same reference means the same definitions.</summary>
    public object? FingerprintTools { get; set; }

    public string? Fingerprint { get; set; }

    public bool FirstRequestReported { get; set; }
}
