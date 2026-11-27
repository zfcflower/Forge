using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// Reads a range of the session's managed context back: the same per-turn entries the request carries, fetched on demand
/// instead of being kept in every prompt. This is the counterpart of a folded block's <c>retrieve:</c> line - the model
/// names the turn range and gets those turns' entries, not a summary of them.
/// </summary>
public sealed class ReadContextTool : ITool
{
    public const string Name = "read_context";

    private const int MaxTurns = 50;

    private const int MaxOutputChars = 200_000;

    private const string SchemaJson =
        """{"type":"object","properties":{"from_turn":{"type":"integer","description":"First turn to read: the number a turn's receipt line gives as turn=."},"to_turn":{"type":"integer","description":"Last turn to read; omit to read up to the newest finished turn. At most 50 turns per call."}},"required":["from_turn"],"additionalProperties":false}""";

    private readonly ITurnLedgerStore store;

    public ReadContextTool(ITurnLedgerStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    public ToolDefinition Definition { get; } = new(
        Name,
        "Read a turn of this session back in full: its task and the answer it produced. The turn number is the turn= of " +
        "the receipt line under an answer; a [compacted: …] block names the turns it stands in for.",
        SchemaJson);

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        long fromTurn;
        long? toTurn;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("from_turn", out var from) || from.ValueKind != JsonValueKind.Number)
            {
                return new ToolResult("read_context requires a 'from_turn' number.", IsError: true);
            }

            fromTurn = from.GetInt64();
            toTurn = root.TryGetProperty("to_turn", out var to) && to.ValueKind == JsonValueKind.Number ? to.GetInt64() : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new ToolResult($"read_context arguments are invalid: {exception.Message}", IsError: true);
        }

        if (fromTurn <= 0 || (toTurn is { } end && end < fromTurn))
        {
            return new ToolResult("read_context needs a positive 'from_turn' and a 'to_turn' at or after it.", IsError: true);
        }

        if (string.IsNullOrEmpty(context.SessionId))
        {
            return new ToolResult("This agent has no session to read.", IsError: true);
        }

        var rows = await store.ListAsync(context.SessionId, ct).ConfigureAwait(false);
        var builder = new StringBuilder(4096);
        var count = 0;
        foreach (var row in rows)
        {
            if (row.TurnSequence < fromTurn || (toTurn is { } limit && row.TurnSequence > limit))
            {
                continue;
            }

            if (count == MaxTurns || builder.Length >= MaxOutputChars)
            {
                builder.Append("\n[more turns in this range; narrow it]");
                break;
            }

            builder.Append(ManagedContext.RenderEntry(row)).Append("\n\n");
            count++;
        }

        return count == 0
            ? new ToolResult(
                $"No finished turn in #{fromTurn.ToString(CultureInfo.InvariantCulture)}"
                + (toTurn is { } last ? $"-#{last.ToString(CultureInfo.InvariantCulture)}" : " onwards")
                + " has an entry.")
            : new ToolResult(builder.ToString());
    }
}

/// <summary>
/// The harness's own receipt of one finished turn, callable but never offered (a dispatch-only tool, see
/// <c>ToolRegistry</c>). Every finished turn's exchange already ends with such a receipt, so a model that copies the call out
/// of its own history reaches this instead of an "unknown tool" answer: it gets the real numbers of that turn - the same JSON
/// the exchange carries - and can go on to <c>read_context</c> or <c>read_history</c> with them.
/// </summary>
public sealed class EvidenceReceiptTool : ITool
{
    public const string Name = ManagedContext.ReceiptToolName;

    private const string SchemaJson =
        """{"type":"object","properties":{"turn":{"type":"integer","description":"The turn whose receipt to read; omit for the newest finished turn."}},"additionalProperties":false}""";

    private readonly ITurnLedgerStore store;

    public EvidenceReceiptTool(ITurnLedgerStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <summary>Never sent: the tool is dispatch-only, so its text costs the prompt nothing.</summary>
    public ToolDefinition Definition { get; } = new(
        Name,
        "The harness's receipt of one finished turn: its turn number, seq range and the files it read and changed.",
        SchemaJson);

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(context.SessionId))
        {
            return new ToolResult("This agent has no session to read a receipt from.", IsError: true);
        }

        long? turn;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var root = document.RootElement;
            turn = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("turn", out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt64()
                : null;
        }
        catch (JsonException exception)
        {
            return new ToolResult($"evidence_receipt arguments are invalid: {exception.Message}", IsError: true);
        }

        if (turn is { } wanted)
        {
            var one = await store.GetAsync(context.SessionId, wanted, ct).ConfigureAwait(false);
            return one is null
                ? new ToolResult($"No finished turn #{wanted.ToString(CultureInfo.InvariantCulture)} in this session.", IsError: true)
                : new ToolResult(ManagedContext.RenderReceipt(one));
        }

        var rows = await store.ListAsync(context.SessionId, ct).ConfigureAwait(false);
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            if (rows[index].Status != TurnLedgerStatus.Pending)
            {
                return new ToolResult(ManagedContext.RenderReceipt(rows[index]));
            }
        }

        return new ToolResult("No finished turn yet.", IsError: true);
    }
}

/// <summary>
/// Full-text search over the session's finished turns - the managed context's own index, so a hit names a turn whose entry
/// can be read back with <see cref="ReadContextTool"/> and whose raw messages with <c>read_history</c>.
/// </summary>
public sealed class SearchContextTool : ITool
{
    public const string Name = "search_context";

    private const int DefaultLimit = 10;

    private const int MaxLimit = 50;

    private const string SchemaJson =
        """{"type":"object","properties":{"query":{"type":"string","description":"Full-text query over the finished turns' task and answer."},"limit":{"type":"integer","description":"Maximum hits to return; default 10, at most 50."}},"required":["query"],"additionalProperties":false}""";

    private readonly ITurnLedgerStore store;

    public SearchContextTool(ITurnLedgerStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    public ToolDefinition Definition { get; } = new(
        Name,
        "Search what earlier turns of this session concluded: each hit names a turn, its seq range and the matching text. " +
        "Read the entry with read_context, the raw messages with read_history.",
        SchemaJson);

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
                return new ToolResult("search_context requires a 'query' string.", IsError: true);
            }

            query = queryElement.GetString()!;
            limit = (int)Math.Clamp(
                root.TryGetProperty("limit", out var limitElement) && limitElement.ValueKind == JsonValueKind.Number
                    ? limitElement.GetInt64()
                    : DefaultLimit,
                1,
                MaxLimit);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new ToolResult($"search_context arguments are invalid: {exception.Message}", IsError: true);
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return new ToolResult("search_context requires a non-empty 'query'.", IsError: true);
        }

        if (string.IsNullOrEmpty(context.SessionId))
        {
            return new ToolResult("This agent has no session to search.", IsError: true);
        }

        var hits = await store.SearchAsync(context.SessionId, query, limit, ct).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            return new ToolResult($"No finished turn matches '{query}'.");
        }

        var builder = new StringBuilder(hits.Count * 160);
        foreach (var hit in hits)
        {
            builder.Append("turn ").Append(hit.TurnSequence.ToString(CultureInfo.InvariantCulture))
                .Append(" (").Append(hit.Score.ToString("0.00", CultureInfo.InvariantCulture)).Append("): ")
                .Append(hit.Snippet).Append('\n');
        }

        return new ToolResult(builder.ToString());
    }
}
