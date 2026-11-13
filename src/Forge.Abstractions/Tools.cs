namespace Forge.Abstractions;

public enum ToolSideEffect { ReadOnly, Write, Execute }

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// What a tool returns: a list of content blocks. Text is the default and the only shape a text-only tool ever builds; an
/// image block is how a tool that produced a picture (a screenshot, an MCP server's <c>image</c> content) hands it over
/// instead of describing it. <see cref="Content"/> is the text view of the whole payload — placeholders included, never the
/// base64 itself — and <see cref="Text"/> is the tool's own words without them.
/// </summary>
/// <remarks>
/// <see cref="Blocks"/> is copied on construction and read-only afterwards, so the property can be kept and re-read on any
/// thread. The derived members are computed once here rather than per read: the pipeline asks for <see cref="Content"/> in
/// several places per tool call (storage, search text, provider wire, UI).
/// </remarks>
public sealed record ToolResult
{
    /// <summary>A text-only result, the shape of every tool that returns words.</summary>
    public ToolResult(string Content, bool IsError = false)
        : this([new TextBlock(Content)], IsError)
    {
    }

    public ToolResult(IReadOnlyList<ContentBlock> Blocks, bool IsError = false)
    {
        ArgumentNullException.ThrowIfNull(Blocks);
        this.Blocks = Blocks.Count == 0 ? [] : [.. Blocks];
        Text = ToolPayload.Text(this.Blocks);
        Content = ToolPayload.HasNonText(this.Blocks) ? ToolPayload.WithPlaceholders(this.Blocks) : Text;
        Images = ToolPayload.Images(this.Blocks);
        this.IsError = IsError;
    }

    /// <summary>The blocks the tool returned, in order; empty when it returned nothing.</summary>
    public IReadOnlyList<ContentBlock> Blocks { get; }

    /// <summary>The tool's own text, one text block per line, with nothing standing in for the blocks that carry no text.</summary>
    public string Text { get; }

    /// <summary>
    /// The text view of the whole result: <see cref="Text"/> with a placeholder for each block that carries no text, such as
    /// <c>[image: image/png]</c>. This is what a transcript, the search index and a provider that is not sending the images
    /// itself shows; it is never the base64.
    /// </summary>
    public string Content { get; }

    /// <summary>The images of this result, in order; empty for a text-only tool.</summary>
    public IReadOnlyList<ImageBlock> Images { get; }

    public bool IsError { get; }
}

/// <summary>Execution context handed to every tool; the workspace root bounds file access.</summary>
public sealed record ToolContext(string SessionId, string WorkingDirectory, int AgentDepth = 0)
{
    /// <summary>The calling agent instance's id; null when the caller is not an agent loop (a script, a plugin bridge).</summary>
    public string? AgentId { get; init; }

    /// <summary>The calling agent's display name (its definition name, e.g. "main", "scout"); null like <see cref="AgentId"/>.</summary>
    public string? AgentName { get; init; }
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
