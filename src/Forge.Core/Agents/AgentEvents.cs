using System.Threading.Channels;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>Destination for the events of one agent run; the host backs it with a channel.</summary>
public interface IAgentEventSink
{
    void Emit(AgentEvent value);
}

/// <summary>A user interjection (see <see cref="AgentInstance.Interject"/>) was handed to the agent as a user message.</summary>
public sealed record AgentInterjected(string AgentId, string Text, DateTimeOffset At) : AgentEvent(AgentId);

internal sealed class ChannelEventSink(ChannelWriter<AgentEvent> writer) : IAgentEventSink
{
    public void Emit(AgentEvent value) => writer.TryWrite(value);
}

/// <summary>
/// Ambient pointer to the run currently on this execution context, so tools such as
/// <see cref="SpawnAgentTool"/> can reach the event sink, parent id and parallelism slot of the calling
/// agent.
/// </summary>
internal sealed class AgentScope
{
    private static readonly AsyncLocal<AgentScope?> Slot = new();

    private AgentScope(IAgentEventSink sink, string agentId, AgentSlot slot)
    {
        Sink = sink;
        AgentId = agentId;
        Permit = slot;
    }

    public IAgentEventSink Sink { get; }

    public string AgentId { get; }

    public AgentSlot Permit { get; }

    public static AgentScope? Current => Slot.Value;

    public static IDisposable Enter(IAgentEventSink sink, string agentId, AgentSlot slot)
    {
        var previous = Slot.Value;
        Slot.Value = new AgentScope(sink, agentId, slot);
        return new Restore(previous);
    }

    private sealed class Restore(AgentScope? previous) : IDisposable
    {
        public void Dispose() => Slot.Value = previous;
    }
}
