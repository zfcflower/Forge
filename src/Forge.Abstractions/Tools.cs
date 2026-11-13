namespace Forge.Abstractions;

public enum ToolSideEffect { ReadOnly, Write, Execute }

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// What a tool returns: text and images in order. A text-only tool writes <c>new ToolResult("text")</c>; <see cref="Content"/> is
/// the flattened text for every consumer that reads text (an image shows as its placeholder, never as base64). Only
/// <see cref="TextBlock"/> and <see cref="ImageBlock"/> are allowed in <paramref name="Blocks"/>, and the list is treated as immutable once passed in.
/// </summary>
public sealed record ToolResult(IReadOnlyList<ContentBlock> Blocks, bool IsError = false)
{
    public ToolResult(string Content, bool IsError = false)
        : this([new TextBlock(Content)], IsError)
    {
    }

    private readonly IReadOnlyList<ContentBlock> blocks = ToolResultBlocks.Validate(Blocks);

    /// <summary>Text and image blocks only; a <c>with</c> expression is validated too.</summary>
    public IReadOnlyList<ContentBlock> Blocks
    {
        get => blocks;
        init => blocks = ToolResultBlocks.Validate(value);
    }

    /// <summary>The flattened text of <see cref="Blocks"/>; see <see cref="ToolResultBlocks.Flatten"/>.</summary>
    public string Content => ToolResultBlocks.Flatten(Blocks);

    public bool Equals(ToolResult? other) =>
        other is not null && IsError == other.IsError && ToolResultBlocks.SequenceEqual(Blocks, other.Blocks);

    public override int GetHashCode() => HashCode.Combine(IsError, ToolResultBlocks.Hash(Blocks));
}

/// <summary>Execution context handed to every tool; the workspace root bounds file access.</summary>
public sealed record ToolContext(string SessionId, string WorkingDirectory, int AgentDepth = 0)
{
    /// <summary>The calling agent instance's id; null when the caller is not an agent loop (a script, a plugin bridge).</summary>
    public string? AgentId { get; init; }

    /// <summary>The calling agent's display name (its definition name, e.g. "main", "scout"); null like <see cref="AgentId"/>.</summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// Where a long-running tool reports what it is doing: each call carries the <em>latest tail</em> of its live output (a
    /// snapshot, not a delta, so a listener may drop calls without losing state). Bounded by the producer, throttled by the
    /// producer, callable from any thread. Null when nobody watches; a tool must not require it.
    /// </summary>
    public Action<string>? Progress { get; init; }
}

/// <summary>Where a tool comes from. Declared by whoever registers the tool, never reported by the tool itself.</summary>
public enum ToolOrigin
{
    /// <summary>Ships with Forge (Core or App). The user may switch it off in settings.</summary>
    BuiltIn,

    /// <summary>A plugin tool from the extension host (including <c>run_code</c>); switched on and off per plugin.</summary>
    Extension,

    /// <summary>A tool of an MCP server; switched on and off per server.</summary>
    Mcp,
}

/// <summary>
/// Catalog entry for one tool. <paramref name="Group"/> is one of <see cref="ToolGroups"/> for a built-in tool and the
/// plugin or server name otherwise; <paramref name="Source"/> names the plugin or MCP server (null for a built-in tool).
/// <paramref name="CanDisable"/> is false only for a built-in tool another feature cannot run without.
/// </summary>
public sealed record ToolDescriptor(
    string Name,
    ToolOrigin Origin,
    string Group,
    string? Source = null,
    bool CanDisable = true);

/// <summary>Groups of built-in tools, used to lay out the settings page.</summary>
public static class ToolGroups
{
    public const string Files = "files";
    public const string Git = "git";
    public const string PullRequests = "pr";
    public const string Web = "web";
    public const string Session = "session";
    public const string Agents = "agents";
    public const string Retrieval = "retrieval";
    public const string Extensions = "extensions";
}

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
