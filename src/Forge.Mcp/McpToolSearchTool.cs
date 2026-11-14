using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Mcp;

/// <summary>
/// <c>mcp_tool_search</c>: the way back to the MCP tools the per-turn limit kept out of the model's tool list. It is offered
/// only while that limit actually hides something; a call searches every tool of every ready server (name, description and
/// server name), hands the hits to the caller's <c>pin</c> callback — which is what makes them callable from the next request
/// on — and answers with what it pinned. Read-only: searching never runs a server's tool.
/// <para>The session state (what is pinned, and the limit) belongs to the app, so this class only owns the tool's shape and the
/// text of its answer; the callback does the selection.</para>
/// </summary>
public sealed class McpToolSearchTool : ITool
{
    public const string Name = "mcp_tool_search";

    /// <summary>Results a call may ask for; above this the answer costs more than the examples it saves.</summary>
    public const int MaxResults = 20;

    private const string Description =
        "Search the tools of the configured MCP servers by words in their name, description or server name, and make the matches callable. Use it when the tool you need is not in the current tool list: the MCP tool surface is trimmed per request, so a server's tools appear only once they matched what the conversation is about. Read-only; it never runs a server tool.";

    private const string SchemaJson =
        """{"type":"object","properties":{"query":{"type":"string","description":"Words to look for in an MCP tool's name, description or server name."},"limit":{"type":"integer","minimum":1,"maximum":20,"description":"How many matches to make callable. Defaults to 5."}},"required":["query"],"additionalProperties":false}""";

    private readonly Func<string, int, IReadOnlyList<McpToolAdapter>> pin;

    /// <param name="pin">Searches every MCP tool for <c>query</c>, pins the best <c>limit</c> matches and returns them.</param>
    public McpToolSearchTool(Func<string, int, IReadOnlyList<McpToolAdapter>> pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        this.pin = pin;
        Definition = new ToolDefinition(Name, Description, SchemaJson);
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        var (query, limit) = ReadArguments(call.ArgumentsJson);
        if (query.Length == 0)
        {
            return new ValueTask<ToolResult>(new ToolResult(
                $"{Name} failed: \"query\" is required.",
                IsError: true));
        }

        IReadOnlyList<McpToolAdapter> matches;
        try
        {
            matches = pin(query, limit);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return new ValueTask<ToolResult>(new ToolResult(
                $"{Name} failed: {exception.Message}",
                IsError: true));
        }

        if (matches.Count == 0)
        {
            return new ValueTask<ToolResult>(new ToolResult(
                $"No MCP tool matches \"{query}\". The configured servers expose no such tool; ask the user whether a server is missing or disabled."));
        }

        var builder = new StringBuilder(matches.Count * 160 + 96);
        builder.Append(matches.Count)
            .Append(matches.Count == 1 ? " MCP tool is" : " MCP tools are")
            .Append(" now available and callable:\n");
        foreach (var match in matches)
        {
            builder.Append("- ").Append(match.ExposedName)
                .Append(" [").Append(match.ServerName);
            if (match.SideEffect != ToolSideEffect.ReadOnly)
            {
                builder.Append(", ").Append(match.SideEffect == ToolSideEffect.Execute ? "executes" : "writes");
                builder.Append("; needs approval");
            }

            builder.Append("]: ").Append(FirstLine(match.Definition.Description)).Append('\n');
        }

        builder.Append("Call them by the names above; they stay available while this conversation keeps using them.");
        return new ValueTask<ToolResult>(new ToolResult(builder.ToString()));
    }

    /// <summary>Reads the two arguments without reflection; a missing or malformed argument is not an exception.</summary>
    private static (string Query, int Limit) ReadArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (string.Empty, 5);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (string.Empty, 5);
            }

            var query = root.TryGetProperty("query", out var queryElement) && queryElement.ValueKind == JsonValueKind.String
                ? queryElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            var limit = 5;
            if (root.TryGetProperty("limit", out var limitElement) &&
                limitElement.ValueKind == JsonValueKind.Number &&
                limitElement.TryGetInt32(out var requested))
            {
                limit = Math.Clamp(requested, 1, MaxResults);
            }

            return (query, limit);
        }
        catch (JsonException)
        {
            return (string.Empty, 5);
        }
    }

    private static string FirstLine(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "(no description)";
        }

        var text = description.Trim();
        var end = text.IndexOfAny(['\r', '\n']);
        if (end < 0)
        {
            return text.Length <= 200 ? text : text[..199] + "…";
        }

        var line = text[..end];
        return line.Length <= 200 ? line : line[..199] + "…";
    }
}
