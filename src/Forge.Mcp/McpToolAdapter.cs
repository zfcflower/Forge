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
            return new ToolResult(BlocksOf(result), result.IsError);
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

    /// <summary>
    /// The blocks the model gets: one text block holding the flattened result — with the placeholder of every image
    /// that also travels as a real <see cref="ImageBlock"/> left out, so the model is never told about the same
    /// picture twice — then one <see cref="ImageBlock"/> per MCP image content whose base64 was kept, up to
    /// <see cref="McpContentFlattener.MaxKeptImageBytesPerResult"/> in total. A result with no kept image is the same
    /// single <see cref="TextBlock"/> as before, byte for byte.
    /// </summary>
    private IReadOnlyList<ContentBlock> BlocksOf(McpToolCallResult result)
    {
        var contents = result.Content;
        if (contents is not { Count: > 0 })
        {
            return [new TextBlock(Truncate(result.Text))];
        }

        var images = new List<ImageBlock>();
        var text = new StringBuilder(256);
        long kept = 0;
        foreach (var content in contents)
        {
            // Within the per-result budget an image travels as itself; past it, as its placeholder line like any other block.
            if (content.Data is not null &&
                kept + (content.SizeBytes ?? 0) <= McpContentFlattener.MaxKeptImageBytesPerResult)
            {
                kept += content.SizeBytes ?? 0;
                images.Add(new ImageBlock(content.MimeType!, content.Data));
                continue;
            }

            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(content.Text);
        }

        if (images.Count == 0)
        {
            return [new TextBlock(Truncate(result.Text))];
        }

        var blocks = new List<ContentBlock>(images.Count + 1);
        if (text.Length > 0)
        {
            blocks.Add(new TextBlock(Truncate(text.ToString())));
        }

        blocks.AddRange(images);
        return blocks;
    }

    private static string BuildDescription(string serverName, string? description) =>
        string.IsNullOrWhiteSpace(description)
            ? $"[MCP server \"{serverName}\"]"
            : $"[MCP server \"{serverName}\"] {description}";
}
