using System.Text;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>One message of an instance's context; <see cref="Seq"/> is its session store sequence, 0 when unpersisted.</summary>
internal readonly record struct AgentMessage(ChatMessage Message, long Seq);

/// <summary>
/// A live agent: a definition (shared, immutable) plus the context of this instance (history, usage)
/// and the exclusive-run gate that stops two spawns from driving it at once.
/// </summary>
public sealed class AgentInstance
{
    private readonly List<AgentMessage> history = [];
    private readonly StringBuilder streaming = new();
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

    /// <summary>Control state of the run driving this instance right now; null for a root agent or a finished sub-agent.</summary>
    internal SubAgentRun? Control { get; set; }

    /// <summary>Text produced by this instance so far: the turn in progress when it is streaming, else the last finished turn.</summary>
    internal string PartialText => streaming.Length > 0 ? streaming.ToString() : lastText;

    /// <summary>Snapshot of the persisted context, for diagnostics and retrieval; the loop reads <see cref="Messages"/>.</summary>
    public IReadOnlyList<ChatMessage> History => [.. history.Select(static entry => entry.Message)];

    internal List<AgentMessage> Messages => history;

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
