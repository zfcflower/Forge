using System.Text;
using Forge.Abstractions;

namespace Forge.Mcp;

/// <summary>Exposes one MCP server tool as a Forge tool named <c>&lt;server&gt;__&lt;tool&gt;</c>.</summary>
public sealed class McpToolAdapter : ITool
{
    private readonly Func<string, CancellationToken, Task<McpToolCallResult>> invoke;
    private readonly int maxResultChars;

    internal McpToolAdapter(
        string serverName,
        string toolName,
        string exposedName,
        string? description,
        string inputSchemaJson,
        ToolSideEffect sideEffect,
        Func<string, CancellationToken, Task<McpToolCallResult>> invoke,
        int maxResultChars)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        ServerName = serverName;
        ToolName = toolName;
        SideEffect = sideEffect;
        this.invoke = invoke;
        this.maxResultChars = maxResultChars <= 0 ? int.MaxValue : maxResultChars;
        Definition = new ToolDefinition(exposedName, BuildDescription(serverName, description), inputSchemaJson);
    }

    public string ServerName { get; }

    /// <summary>The tool name as the MCP server knows it, without the server prefix.</summary>
    public string ToolName { get; }

    public string ExposedName => Definition.Name;

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect { get; }

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        try
        {
            var result = await invoke(call.ArgumentsJson, ct).ConfigureAwait(false);
            return new ToolResult(Truncate(result.Text), result.IsError);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (McpTimeoutException ex)
        {
            return new ToolResult($"{ServerName}: {ex.Message}", IsError: true);
        }
        catch (McpProtocolException ex)
        {
            return new ToolResult($"{ServerName}: {ex.Message}" + (ex.DataJson is null ? string.Empty : $"\n{ex.DataJson}"), IsError: true);
        }
        catch (McpException ex)
        {
            return new ToolResult($"{ServerName}: {ex.Message}", IsError: true);
        }
    }

    private string Truncate(string text)
    {
        if (text.Length <= maxResultChars)
        {
            return text;
        }

        var kept = maxResultChars;
        var builder = new StringBuilder(kept + 64);
        builder.Append(text, 0, kept);
        builder.Append("\n…[output truncated: ").Append(text.Length - kept).Append(" characters omitted]");
        return builder.ToString();
    }

    private static string BuildDescription(string serverName, string? description) =>
        string.IsNullOrWhiteSpace(description)
            ? $"[MCP server \"{serverName}\"]"
            : $"[MCP server \"{serverName}\"] {description}";
}
