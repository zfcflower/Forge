using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Abstractions;

namespace Forge.CodeMode;

/// <summary>
/// The tool surface a script sees. Every call is approval-checked, bounded by a shared concurrency limit,
/// truncated to the configured size, and reported back as either text or <see cref="ToolCallException"/>.
/// </summary>
public sealed class ToolsFacade
{
    private const int ListedNames = 40;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly IToolRegistry registry;
    private readonly ToolContext context;
    private readonly Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>> approval;
    private readonly SemaphoreSlim slots;
    private readonly string selfName;
    private readonly int maxResultChars;
    private readonly CancellationToken ct;
    private int calls;

    internal ToolsFacade(
        IToolRegistry registry,
        ToolContext context,
        Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>> approval,
        string selfName,
        int maxConcurrency,
        int maxResultChars,
        CancellationToken ct)
    {
        this.registry = registry;
        this.context = context;
        this.approval = approval;
        this.selfName = selfName;
        this.maxResultChars = maxResultChars;
        this.ct = ct;
        slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public Task<string> Call(string toolName) => CallJson(toolName, "{}");

    public Task<string> Call(string toolName, object? arguments)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        string json;
        try
        {
            json = SerializeArguments(arguments);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ToolCallException(toolName, $"arguments for '{toolName}' cannot be serialized to JSON: {ex.Message}");
        }

        return CallJson(toolName, json);
    }

    public Task<T> Call<T>(string toolName) => CallJson<T>(toolName, "{}");

    public Task<T> Call<T>(string toolName, object? arguments)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        string json;
        try
        {
            json = SerializeArguments(arguments);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ToolCallException(toolName, $"arguments for '{toolName}' cannot be serialized to JSON: {ex.Message}");
        }

        return CallJson<T>(toolName, json);
    }

    /// <summary>Invokes a tool by name and returns its text result; <paramref name="argumentsJson"/> must be a JSON object.</summary>
    public async Task<string> CallJson(string toolName, string? argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        var arguments = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
        if (string.Equals(toolName, selfName, StringComparison.Ordinal))
        {
            throw new ToolCallException(toolName, $"{selfName} cannot call itself; call the underlying tools directly.", denied: true);
        }

        if (!registry.TryGet(toolName, out var tool))
        {
            throw new ToolCallException(toolName, $"Unknown tool '{toolName}'. Available tools: {AvailableNames()}.");
        }

        ValidateArguments(toolName, arguments);
        ct.ThrowIfCancellationRequested();
        await slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var request = new ApprovalRequest(toolName, arguments, tool.SideEffect, context.WorkingDirectory);
            var decision = await approval(request, ct).ConfigureAwait(false);
            if (decision != ApprovalDecision.Allow)
            {
                throw new ToolCallException(toolName, $"The user or the approval policy denied the inner call to '{toolName}'.", denied: true);
            }

            var call = new ToolCall(NextCallId(), toolName, arguments);
            ToolResult result;
            try
            {
                result = await tool.InvokeAsync(call, context, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ToolCallException(toolName, $"Tool '{toolName}' threw {ex.GetType().Name}: {ex.Message}");
            }

            var content = TextBounds.Truncate(result.Content ?? string.Empty, maxResultChars, "tool result");
            if (result.IsError)
            {
                throw new ToolCallException(toolName, content);
            }

            return content;
        }
        finally
        {
            slots.Release();
        }
    }

    public async Task<T> CallJson<T>(string toolName, string? argumentsJson)
    {
        var text = await CallJson(toolName, argumentsJson).ConfigureAwait(false);
        if (typeof(T) == typeof(string))
        {
            return (T)(object)text;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ToolCallException(
                toolName,
                $"The result of '{toolName}' is not JSON that fits {typeof(T).Name}: {ex.Message}. Use CallJson and parse it yourself.");
        }
    }

    public JsonElement Json(string json) => Json<JsonElement>(json);

    public T Json<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            if (typeof(T) == typeof(JsonElement))
            {
                using var document = JsonDocument.Parse(json);
                return (T)(object)document.RootElement.Clone();
            }

            return JsonSerializer.Deserialize<T>(json, JsonOptions)!;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ToolCallException("json", $"Text is not JSON that fits {typeof(T).Name}: {ex.Message}");
        }
    }

    private void ValidateArguments(string toolName, string arguments)
    {
        try
        {
            using var _ = JsonDocument.Parse(arguments);
        }
        catch (JsonException ex)
        {
            throw new ToolCallException(toolName, $"Arguments for '{toolName}' are not valid JSON: {ex.Message}");
        }
    }

    private string NextCallId() =>
        string.Concat(selfName, "-", Interlocked.Increment(ref calls).ToString(CultureInfo.InvariantCulture));

    private string AvailableNames()
    {
        var names = new StringBuilder(128);
        var shown = 0;
        foreach (var tool in registry.Tools)
        {
            if (shown >= ListedNames)
            {
                names.Append(", ...");
                break;
            }

            if (string.Equals(tool.Definition.Name, selfName, StringComparison.Ordinal))
            {
                continue;
            }

            if (shown > 0)
            {
                names.Append(", ");
            }

            names.Append(tool.Definition.Name);
            shown++;
        }

        return names.Length == 0 ? "(none)" : names.ToString();
    }

    private static string SerializeArguments(object? arguments) => arguments switch
    {
        null => "{}",
        string text => string.IsNullOrWhiteSpace(text) ? "{}" : text,
        JsonElement element => element.GetRawText(),
        JsonDocument document => document.RootElement.GetRawText(),
        JsonNode node => node.ToJsonString(),
        var other => JsonSerializer.Serialize(other, other.GetType(), JsonOptions),
    };
}
