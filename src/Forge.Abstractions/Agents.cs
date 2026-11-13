namespace Forge.Abstractions;

/// <summary>Reusable agent kind. Instances of one definition share its stable prompt prefix.</summary>
public sealed record AgentDefinition(
    string Name,
    string Description,
    string Prompt,
    ModelSettings Model,
    IReadOnlyList<string>? ToolNames = null);

public abstract record AgentEvent(string AgentId);
public sealed record AgentTextDelta(string AgentId, string Text) : AgentEvent(AgentId);
public sealed record AgentThinkingDelta(string AgentId, string Text) : AgentEvent(AgentId);
public sealed record AgentToolStarted(string AgentId, ToolCall Call) : AgentEvent(AgentId);
public sealed record AgentToolFinished(string AgentId, ToolCall Call, ToolResult Result) : AgentEvent(AgentId);
public sealed record AgentApprovalDenied(string AgentId, ToolCall Call) : AgentEvent(AgentId);
public sealed record AgentCompacted(string AgentId, int TokensBefore, int TokensAfter) : AgentEvent(AgentId);
/// <summary>Emitted by the parent when it spawns or resumes a child, before the child waits for a run slot.</summary>
public sealed record AgentSubAgentStarted(
    string AgentId,
    string ChildId,
    string Kind,
    string Task,
    string Model,
    string ChildSessionId,
    DateTimeOffset At) : AgentEvent(AgentId);
public sealed record AgentTurnCompleted(string AgentId, TokenUsage Usage, StopReason Stop) : AgentEvent(AgentId);

/// <summary>
/// A note about how the turn went that is not a failure: the request had to be downgraded, for one. Shown as a toast, never as an
/// error and never as something that stops the turn.
/// </summary>
public sealed record AgentNotice(string AgentId, string Text) : AgentEvent(AgentId);

/// <summary>
/// The account's usage windows after a model response (successful or rejected). Carries no session cost: it is the
/// provider's own meter, which is what tells the user a window is filling <em>before</em> a request is refused.
/// </summary>
public sealed record AgentRateLimitUpdated(string AgentId, ProviderRateLimit RateLimit) : AgentEvent(AgentId);
public sealed record AgentFailed(string AgentId, string Error) : AgentEvent(AgentId);

/// <summary>Lifecycle of one sub-agent run as shown to the user; terminal values never change again for that run.</summary>
public enum SubAgentStatus { Queued, Running, Completed, Stopped, Failed }

/// <summary>The child is waiting for a free run slot. <see cref="AgentEvent.AgentId"/> is the child.</summary>
public sealed record AgentSubAgentQueued(string AgentId, DateTimeOffset At) : AgentEvent(AgentId);

/// <summary>The child acquired a run slot and is working. <see cref="AgentEvent.AgentId"/> is the child.</summary>
public sealed record AgentSubAgentRunning(string AgentId, DateTimeOffset At) : AgentEvent(AgentId);

/// <summary>
/// A steering instruction was injected at a tool-batch boundary; <paramref name="DroppedToolCalls"/> calls of the
/// interrupted batch were skipped and answered as cancelled. <see cref="AgentEvent.AgentId"/> is the child.
/// </summary>
public sealed record AgentSubAgentSteered(string AgentId, string Instruction, int DroppedToolCalls, DateTimeOffset At) : AgentEvent(AgentId);

/// <summary>A follow-up instruction was queued; <paramref name="Pending"/> counts follow-ups not yet delivered.</summary>
public sealed record AgentSubAgentFollowUpQueued(string AgentId, string Instruction, int Pending, DateTimeOffset At) : AgentEvent(AgentId);

/// <summary>Terminal event of one sub-agent run. <see cref="AgentEvent.AgentId"/> is the child.</summary>
public sealed record AgentSubAgentFinished(
    string AgentId,
    SubAgentStatus Status,
    TokenUsage Usage,
    TimeSpan Elapsed,
    string? Error,
    DateTimeOffset At) : AgentEvent(AgentId);

public enum SubAgentControlResult { Accepted, NotFound, AlreadyFinished }

/// <summary>
/// Control surface over the live sub-agents of one host, used by the parent agent's tools and by the UI stop button.
/// Steer: delivered at the next tool-batch boundary, the rest of that batch is skipped. FollowUp: delivered after the
/// current task ends instead of finishing. Stop: cancels the child now; its partial text is returned to the parent.
/// A queued child accepts all three: steer and follow-up are appended to its task, stop finishes it without running.
/// </summary>
public interface ISubAgentControl
{
    SubAgentControlResult Steer(string agentId, string instruction);

    SubAgentControlResult FollowUp(string agentId, string instruction);

    SubAgentControlResult Stop(string agentId, string? reason);
}
