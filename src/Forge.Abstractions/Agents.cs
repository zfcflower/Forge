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
public sealed record AgentSubAgentStarted(string AgentId, string ChildId, string Kind) : AgentEvent(AgentId);
public sealed record AgentTurnCompleted(string AgentId, TokenUsage Usage, StopReason Stop) : AgentEvent(AgentId);
public sealed record AgentFailed(string AgentId, string Error) : AgentEvent(AgentId);
