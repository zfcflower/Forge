namespace Forge.Abstractions;

public enum ToolSideEffect { ReadOnly, Write, Execute }

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

public sealed record ToolResult(string Content, bool IsError = false);

/// <summary>Execution context handed to every tool; the workspace root bounds file access.</summary>
public sealed record ToolContext(string SessionId, string WorkingDirectory, int AgentDepth = 0);

public interface ITool
{
    ToolDefinition Definition { get; }
    ToolSideEffect SideEffect { get; }
    ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct);
}

public interface IToolRegistry
{
    IReadOnlyList<ITool> Tools { get; }
    bool TryGet(string name, out ITool tool);
}

public enum ApprovalMode { Allow, Ask, Deny }
public enum ApprovalDecision { Allow, Deny }

public sealed record ApprovalRequest(string ToolName, string ArgumentsJson, ToolSideEffect SideEffect, string WorkingDirectory);

/// <summary>Pure policy evaluation; configurable per tool/directory in settings.</summary>
public interface IApprovalPolicy
{
    ApprovalMode Evaluate(ApprovalRequest request);
}

/// <summary>Interactive side of approval; implemented by the UI layer.</summary>
public interface IApprovalHandler
{
    ValueTask<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct);
}
