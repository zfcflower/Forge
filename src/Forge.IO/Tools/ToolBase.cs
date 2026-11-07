using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>An argument the model sent is missing, of the wrong type, or out of range; the message is shown to the model.</summary>
public sealed class ToolArgumentException(string message) : Exception(message)
{
}

/// <summary>
/// The shared shape of the file and shell tools: a frozen definition (the schema is a constant string so two runs send
/// byte-identical tools), argument parsing from <see cref="JsonDocument"/>, and argument failures turned into error
/// results instead of exceptions. Cancellation is the only thing that escapes.
/// </summary>
public abstract class ToolBase(string name, string description, string schema, ToolSideEffect sideEffect) : ITool
{
    public ToolDefinition Definition { get; } = new(name, description, schema);

    public ToolSideEffect SideEffect { get; } = sideEffect;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        }
        catch (JsonException exception)
        {
            return new ToolResult($"{Definition.Name} arguments are not valid JSON: {exception.Message}", IsError: true);
        }

        using (document)
        {
            try
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return new ToolResult($"{Definition.Name} arguments must be a JSON object.", IsError: true);
                }

                return await InvokeCoreAsync(document.RootElement, context, ct).ConfigureAwait(false);
            }
            catch (ToolArgumentException exception)
            {
                return new ToolResult($"{Definition.Name}: {exception.Message}", IsError: true);
            }
            catch (WorkspacePathException exception)
            {
                return new ToolResult($"{Definition.Name}: {exception.Message}", IsError: true);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or InvalidOperationException or RegexMatchTimeoutException)
            {
                return new ToolResult($"{Definition.Name} failed: {exception.Message}", IsError: true);
            }
        }
    }

    protected abstract ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, ToolContext context, CancellationToken ct);

    /// <summary>
    /// The workspace root every path in this call is bounded by, with the links of its components resolved. It is the real
    /// path because <see cref="WorkspacePath.Resolve"/> answers with the real path: mixing the two spellings would make
    /// every relative form fail when the workspace itself sits under a link (macOS <c>/var</c>, a Windows junction, a
    /// substituted drive).
    /// </summary>
    protected static string Root(ToolContext context) =>
        WorkspacePath.ResolveRealPath(
            string.IsNullOrWhiteSpace(context.WorkingDirectory) ? Directory.GetCurrentDirectory() : context.WorkingDirectory);
}

/// <summary>Reads tool arguments without reflection: every accessor names the parameter in its error message.</summary>
public static class ToolJson
{
    public static string RequiredString(JsonElement arguments, string name)
    {
        var value = OptionalString(arguments, name);
        if (string.IsNullOrEmpty(value))
        {
            throw new ToolArgumentException($"'{name}' is required.");
        }

        return value;
    }

    public static string? OptionalString(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ToolArgumentException($"'{name}' must be a string.");
        }

        return value.GetString();
    }

    public static int OptionalInt(JsonElement arguments, string name, int fallback, int minimum, int maximum)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw new ToolArgumentException($"'{name}' must be an integer.");
        }

        if (number < minimum || number > maximum)
        {
            throw new ToolArgumentException($"'{name}' must be between {minimum} and {maximum}.");
        }

        return number;
    }

    public static long OptionalLong(JsonElement arguments, string name, long fallback, long minimum, long maximum)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            throw new ToolArgumentException($"'{name}' must be an integer.");
        }

        if (number < minimum || number > maximum)
        {
            throw new ToolArgumentException($"'{name}' must be between {minimum} and {maximum}.");
        }

        return number;
    }

    public static bool OptionalBool(JsonElement arguments, string name, bool fallback)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ToolArgumentException($"'{name}' must be true or false."),
        };
    }
}
