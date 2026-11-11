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
    private readonly StringBuilder streaming = new();
    private readonly Lock interjectionGate = new();
    private List<string>? interjections;
    private int pendingInterjections;
    private string lastText = string.Empty;
    private int running;

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

    public TokenUsage Usage { get; internal set; } = TokenUsage.Empty;

    /// <summary>Prompt-cache bookkeeping of this instance (routing key, last fingerprint, first-request report); loop-owned.</summary>
    internal PromptCacheState CacheState { get; } = new();

    /// <summary>Control state of the run driving this instance right now; null for a root agent or a finished sub-agent.</summary>
    internal SubAgentRun? Control { get; set; }

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
    public void Interject(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (interjectionGate)
        {
            (interjections ??= []).Add(text);
            Volatile.Write(ref pendingInterjections, interjections.Count);
        }
    }

    /// <summary>Removes and returns the interjections not delivered yet, oldest first; empty when there are none.</summary>
    public IReadOnlyList<string> TakeInterjections()
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

    internal bool TryAcquire() => Interlocked.CompareExchange(ref running, 1, 0) == 0;

    internal void Release() => Volatile.Write(ref running, 0);

    internal void Add(ChatMessage message, long seq) => history.Add(new AgentMessage(message, seq));

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
        history.Clear();
        history.AddRange(messages);
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
