using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Storage;

namespace Forge.Core.Agents;

/// <summary>Reads a byte range of an artifact that the loop spilled out of the context.</summary>
public sealed class ReadArtifactTool : ITool
{
    public const string Name = "read_artifact";
    private const int DefaultLength = 20_000;

    private const string SchemaJson =
        """{"type":"object","properties":{"artifact_id":{"type":"string","description":"Artifact id reported by a truncated tool result."},"offset":{"type":"integer","description":"Byte offset to start at; default 0."},"length":{"type":"integer","description":"Bytes to read; default 20000."}},"required":["artifact_id"],"additionalProperties":false}""";

    private readonly IArtifactStore artifacts;

    public ReadArtifactTool(IArtifactStore artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        this.artifacts = artifacts;
    }

    public ToolDefinition Definition { get; } = new(Name, "Read part or all of a stored artifact by id.", SchemaJson);

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        string id;
        long offset = 0;
        int length = DefaultLength;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("artifact_id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                return new ToolResult("read_artifact requires an 'artifact_id' string.", IsError: true);
            }

            id = idElement.GetString()!;
            offset = JsonArguments.Integer(root, "offset", 0);
            length = checked((int)JsonArguments.Integer(root, "length", DefaultLength));
        }
        catch (Exception exception) when (exception is JsonException or OverflowException or InvalidOperationException)
        {
            return new ToolResult($"read_artifact arguments are invalid: {exception.Message}", IsError: true);
        }

        if (offset < 0 || length < 0)
        {
            return new ToolResult("read_artifact requires non-negative 'offset' and 'length'.", IsError: true);
        }

        try
        {
            var info = await artifacts.GetInfoAsync(id, ct).ConfigureAwait(false);
            if (info is null)
            {
                return new ToolResult($"No artifact '{id}' exists.", IsError: true);
            }

            var bytes = await artifacts.ReadAsync(id, offset, length, ct).ConfigureAwait(false);
            var builder = new StringBuilder(bytes.Length + 128);
            builder.Append($"artifact '{id}' ({info.SizeBytes} bytes) offset {offset}, {bytes.Length} bytes\n\n");
            builder.Append(Encoding.UTF8.GetString(bytes.Span));
            return new ToolResult(builder.ToString());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ToolResult($"read_artifact failed: {exception.Message}", IsError: true);
        }
    }
}

/// <summary>Full-text search over the agent's session, including messages compacted out of the context.</summary>
public sealed class SearchHistoryTool : ITool
{
    public const string Name = "search_history";
    private const int DefaultLimit = 10;
    private const int MaxLimit = 50;

    private const string SchemaJson =
        """{"type":"object","properties":{"query":{"type":"string","description":"Full-text query over the session's stored messages."},"limit":{"type":"integer","description":"Maximum hits to return; default 10, at most 50."}},"required":["query"],"additionalProperties":false}""";

    private readonly ISessionStore store;

    public SearchHistoryTool(ISessionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    public ToolDefinition Definition { get; } = new(Name, "Search earlier conversation text and tool output by query; returns message sequence numbers.", SchemaJson);

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        string query;
        int limit;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("query", out var queryElement) || queryElement.ValueKind != JsonValueKind.String)
            {
                return new ToolResult("search_history requires a 'query' string.", IsError: true);
            }

            query = queryElement.GetString()!;
            limit = (int)Math.Clamp(JsonArguments.Integer(root, "limit", DefaultLimit), 1, MaxLimit);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new ToolResult($"search_history arguments are invalid: {exception.Message}", IsError: true);
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return new ToolResult("search_history requires a non-empty 'query'.", IsError: true);
        }

        if (string.IsNullOrEmpty(context.SessionId))
        {
            return new ToolResult("This agent has no session to search.", IsError: true);
        }

        var hits = await store.SearchAsync(context.SessionId, query, limit, ct).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            return new ToolResult($"No stored message matches '{query}'.");
        }

        var builder = new StringBuilder(hits.Count * 160);
        foreach (var hit in hits)
        {
            builder.Append('#').Append(hit.Seq.ToString(CultureInfo.InvariantCulture))
                .Append(" (").Append(hit.Score.ToString("0.00", CultureInfo.InvariantCulture)).Append("): ")
                .Append(hit.Snippet).Append('\n');
        }

        return new ToolResult(builder.ToString());
    }
}

/// <summary>Reads raw stored messages back by sequence range.</summary>
public sealed class ReadHistoryTool : ITool
{
    public const string Name = "read_history";
    private const int MaxOutputChars = 200_000;

    private const string SchemaJson =
        """{"type":"object","properties":{"from_seq":{"type":"integer","description":"First sequence number to read."},"to_seq":{"type":"integer","description":"Last sequence number to read; defaults to the newest message."}},"required":["from_seq"],"additionalProperties":false}""";

    private readonly ISessionStore store;

    public ReadHistoryTool(ISessionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    public ToolDefinition Definition { get; } = new(Name, "Read stored messages by sequence range; use the sequence numbers from search_history.", SchemaJson);

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        long fromSeq;
        long? toSeq;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("from_seq", out var fromElement) || fromElement.ValueKind != JsonValueKind.Number)
            {
                return new ToolResult("read_history requires a 'from_seq' number.", IsError: true);
            }

            fromSeq = fromElement.GetInt64();
            toSeq = root.TryGetProperty("to_seq", out var toElement) && toElement.ValueKind == JsonValueKind.Number
                ? toElement.GetInt64()
                : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new ToolResult($"read_history arguments are invalid: {exception.Message}", IsError: true);
        }

        if (fromSeq < 0)
        {
            return new ToolResult("read_history requires a non-negative 'from_seq'.", IsError: true);
        }

        if (string.IsNullOrEmpty(context.SessionId))
        {
            return new ToolResult("This agent has no session to read.", IsError: true);
        }

        var builder = new StringBuilder(4096);
        var count = 0;
        await foreach (var stored in store.ReadAsync(context.SessionId, fromSeq, toSeq, ct).ConfigureAwait(false))
        {
            if (builder.Length >= MaxOutputChars)
            {
                builder.Append("\n[remaining messages omitted; narrow the range]");
                break;
            }

            builder.Append('#').Append(stored.Seq.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(MessageJson.RoleName(stored.Message.Role)).Append(": ")
                .Append(MessageJson.ExtractFullText(stored.Message)).Append("\n\n");
            count++;
        }

        return count == 0
            ? new ToolResult($"No stored messages between #{fromSeq} and #{(toSeq?.ToString(CultureInfo.InvariantCulture) ?? "end")}.")
            : new ToolResult(builder.ToString());
    }
}

internal static class JsonArguments
{
    public static long Integer(JsonElement root, string name, long fallback) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetInt64()
            : fallback;

    public static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
