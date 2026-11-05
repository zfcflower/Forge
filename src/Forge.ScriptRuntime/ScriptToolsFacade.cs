using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Plugin.Sdk;

namespace Forge.Scripting;

/// <summary>
/// The tool surface one script run sees. Every call goes to the main program through <see cref="IHostFacade"/>; this
/// module resolves no tool in-process and never decides approval. The names a script may call come from this run's
/// <see cref="ToolDeclaration"/> metadata only, and calls are bounded by a per-run concurrency limit and truncated to
/// the configured size.
/// </summary>
public sealed class ToolsFacade
{
    /// <summary>The script's own tool on the main side; a script can never call it (no recursion).</summary>
    public const string ReservedToolName = "run_code";

    private const int ListedNames = 40;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly IHostFacade host;
    private readonly HashSet<string> callable;
    private readonly SemaphoreSlim slots;
    private readonly int maxResultChars;
    private readonly CancellationToken ct;

    internal ToolsFacade(
        IHostFacade host,
        IReadOnlyList<ToolDeclaration> tools,
        int maxConcurrency,
        int maxResultChars,
        CancellationToken ct)
    {
        this.host = host;
        this.maxResultChars = maxResultChars;
        this.ct = ct;
        slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        callable = new HashSet<string>(tools.Count, StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (!string.Equals(tool.Name, ReservedToolName, StringComparison.Ordinal))
            {
                callable.Add(tool.Name);
            }
        }
    }

    public Task<string> Call(string toolName) => CallJson(toolName, "{}");

    public Task<string> Call(string toolName, object? arguments)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        return CallJson(toolName, SerializeArguments(toolName, arguments));
    }

    public Task<T> Call<T>(string toolName) => CallJson<T>(toolName, "{}");

    public Task<T> Call<T>(string toolName, object? arguments)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        return CallJson<T>(toolName, SerializeArguments(toolName, arguments));
    }

    /// <summary>Invokes a main-program tool by name and returns its text result; the arguments must be a JSON object.</summary>
    public async Task<string> CallJson(string toolName, string? argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(toolName);

        if (string.Equals(toolName, ReservedToolName, StringComparison.Ordinal))
        {
            throw new ToolCallException(
                toolName,
                $"{ReservedToolName} cannot call itself; call the underlying tools directly.",
                denied: true);
        }

        if (!callable.Contains(toolName))
        {
            throw new ToolCallException(
                toolName,
                $"Unknown tool '{toolName}'. Available tools: {AvailableNames()}.");
        }

        var arguments = ParseArguments(toolName, argumentsJson);
        ct.ThrowIfCancellationRequested();
        await slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            HostToolResult result;
            try
            {
                result = await host.CallToolAsync(toolName, arguments, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ToolCallException(toolName, $"The call to '{toolName}' failed in the host: {ex.GetType().Name}: {ex.Message}");
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

    private static JsonElement ParseArguments(string toolName, string? argumentsJson)
    {
        var json = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ToolCallException(toolName, $"Arguments for '{toolName}' must be a JSON object.");
            }

            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ToolCallException(toolName, $"Arguments for '{toolName}' are not valid JSON: {ex.Message}");
        }
    }

    private string AvailableNames()
    {
        var names = new StringBuilder(128);
        var sorted = new List<string>(callable);
        sorted.Sort(StringComparer.Ordinal);
        var shown = 0;
        foreach (var name in sorted)
        {
            if (shown >= ListedNames)
            {
                names.Append(", ...");
                break;
            }

            if (shown > 0)
            {
                names.Append(", ");
            }

            names.Append(name);
            shown++;
        }

        return names.Length == 0 ? "(none)" : names.ToString();
    }

    private static string SerializeArguments(string toolName, object? arguments)
    {
        try
        {
            return arguments switch
            {
                null => "{}",
                string text => string.IsNullOrWhiteSpace(text) ? "{}" : text,
                JsonElement element => element.GetRawText(),
                JsonDocument document => document.RootElement.GetRawText(),
                JsonNode node => node.ToJsonString(),
                var other => JsonSerializer.Serialize(other, other.GetType(), JsonOptions),
            };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ToolCallException(
                toolName,
                string.Create(CultureInfo.InvariantCulture, $"arguments for '{toolName}' cannot be serialized to JSON: {ex.Message}"));
        }
    }
}
