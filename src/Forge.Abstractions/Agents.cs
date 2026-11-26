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
/// <summary>
/// The agent has committed to a call: emitted before the approval gate, so a card exists while a call still waits for the
/// user. <see cref="AwaitingApproval"/> says which half it is in; a call that had to ask carries <see cref="AgentToolRunning"/>
/// once the gate lets it through.
/// </summary>
public sealed record AgentToolStarted(string AgentId, ToolCall Call, bool AwaitingApproval = false) : AgentEvent(AgentId);

/// <summary>
/// The approval gate let a call through and it is about to be invoked. Emitted only for a call that started with
/// <see cref="AgentToolStarted.AwaitingApproval"/>, so a caller can flip its card from "waiting for you" to "running".
/// </summary>
public sealed record AgentToolRunning(string AgentId, ToolCall Call) : AgentEvent(AgentId);

public sealed record AgentToolFinished(string AgentId, ToolCall Call, ToolResult Result) : AgentEvent(AgentId);

/// <summary>
/// A running tool's live output, as the latest bounded tail (a snapshot, not a delta). Emitted between
/// <see cref="AgentToolStarted"/> and <see cref="AgentToolFinished"/> for the call, at most a few times per second, by tools
/// that stream (the shell tool). Not persisted: the finished result carries the full output.
/// </summary>
public sealed record AgentToolProgress(string AgentId, ToolCall Call, string Tail) : AgentEvent(AgentId);
public sealed record AgentApprovalDenied(string AgentId, ToolCall Call) : AgentEvent(AgentId);
public sealed record AgentCompacted(string AgentId, int TokensBefore, int TokensAfter) : AgentEvent(AgentId);
/// <summary>Emitted by the parent when it spawns or resumes a child, before the child waits for a run slot.</summary>
/// <param name="ProviderId">Which connected provider serves the child's model; null means the default one, the same
/// resolution <c>ModelSettings.ProviderId</c> carries. The App's per-child readings price and match against it.</param>
public sealed record AgentSubAgentStarted(
    string AgentId,
    string ChildId,
    string Kind,
    string Task,
    string Model,
    string ChildSessionId,
    DateTimeOffset At,
    string? ProviderId = null) : AgentEvent(AgentId);
/// <param name="StreamElapsed">How long the model spent streaming this turn's answers, first output increment to
/// last, accumulated over every call the turn made (an instance-cumulative counter exactly like
/// <paramref name="Usage"/>, so the turn's own share is the difference against the turn-start snapshot). Zero for a
/// turn that streamed nothing; never "unknown" — a missing measurement is an adapter defect.</param>
/// <param name="StartedAt">When the turn began running — after its run slot was acquired, the same moment the strip's
/// running marker counts from. Their difference is the turn's wall clock, the number the turn's divider shows.</param>
/// <param name="EndedAt">When the turn's loop finished.</param>
/// <param name="LastSeq">The seq of the last message this turn persisted in its session store, 0 when no store is
/// attached. The turn's exact stream position — an immutable join key that cannot drift the way a counted turn number
/// does (stored user messages exist that never opened a live turn: receipts, interjections), so a reopened transcript
/// pairs this turn's metrics row with the card that spans this seq.</param>
public sealed record AgentTurnCompleted(
    string AgentId,
    TokenUsage Usage,
    StopReason Stop,
    TimeSpan StreamElapsed = default,
    DateTimeOffset StartedAt = default,
    DateTimeOffset EndedAt = default,
    long LastSeq = 0) : AgentEvent(AgentId);


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

/// <summary>
/// The turn's next request is waiting on the provider's local rate-limit budget before it can be sent. Informational: shown
/// as a quiet line on the running turn, never an error; not persisted, and unlike <see cref="AgentRateLimitUpdated"/> it
/// describes the local budget, not the account's spending windows.
/// </summary>
public sealed record AgentBudgetWait(string AgentId, string ProviderId, TimeSpan Wait) : AgentEvent(AgentId);

/// <summary>
/// The turn's last request failed before producing anything and is being sent again after <paramref name="Delay"/>, which is
/// the only explanation the user gets for a turn that looks stuck: without it a retried 429 and a hung request are the same
/// picture. Informational like <see cref="AgentBudgetWait"/> — a quiet line on the running turn, never an error, never
/// persisted — but it describes the endpoint refusing us, not our own budget, and <paramref name="Attempt"/> of
/// <paramref name="Total"/> says how much of the ladder is left.
/// </summary>
public sealed record AgentRetryWait(
    string AgentId,
    string ProviderId,
    TimeSpan Delay,
    int Attempt,
    int Total,
    RetryReason Reason) : AgentEvent(AgentId);

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
