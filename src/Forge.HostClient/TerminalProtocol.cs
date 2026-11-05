using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Abstractions;

namespace Forge.HostClient;

/// <summary>Operations of the <c>terminal.*</c> family carried on the main pipe. The Extension Host keeps its own
/// copy of this protocol (<c>Forge.ExtensionHost/TerminalProtocol.cs</c>); neither side references the other, so the
/// two definitions are pinned end to end by the integration tests instead of by a shared type.</summary>
internal static class TerminalOps
{
    /// <summary>Request: starts a pseudo-terminal.</summary>
    public const string Start = "terminal.start";

    /// <summary>Notification: input bytes for a terminal.</summary>
    public const string Input = "terminal.input";

    /// <summary>Request: changes the character-cell size of a terminal.</summary>
    public const string Resize = "terminal.resize";

    /// <summary>Request: asks whether the shell has a foreground child.</summary>
    public const string Foreground = "terminal.foreground";

    /// <summary>Request: kills a terminal's process tree; the host sends <c>terminal.exit</c> before it answers.</summary>
    public const string Stop = "terminal.stop";

    /// <summary>Notification: decoded bytes the app has consumed, which opens the host's flow-control window.</summary>
    public const string Ack = "terminal.ack";

    /// <summary>Notification: output bytes of a terminal, <c>seq</c> starting at 0 and increasing by one.</summary>
    public const string Output = "terminal.output";

    /// <summary>Notification: the last message of a terminal id.</summary>
    public const string Exit = "terminal.exit";
}

/// <summary><c>terminal.start</c> arguments. <c>shell</c>, <c>arguments</c> and <c>environment</c> are omitted when
/// the caller left them null, so the host applies its platform defaults.</summary>
internal sealed record TerminalStartArgs(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("workingDirectory")] string WorkingDirectory,
    [property: JsonPropertyName("columns")] int Columns,
    [property: JsonPropertyName("rows")] int Rows,
    [property: JsonPropertyName("shell")] string? Shell,
    [property: JsonPropertyName("arguments")] string[]? Arguments,
    [property: JsonPropertyName("environment")] Dictionary<string, string>? Environment);

/// <summary><c>terminal.start</c> result. Only <c>terminalId</c> is needed here; the process id and the resolved
/// shell are informational on the wire.</summary>
internal sealed record TerminalStartResult(
    [property: JsonPropertyName("terminalId")] string? TerminalId,
    [property: JsonPropertyName("processId")] long ProcessId,
    [property: JsonPropertyName("shell")] string? Shell);

/// <summary><c>terminal.input</c> arguments; <c>data</c> is base64 and at most 64 KiB of decoded bytes.</summary>
internal sealed record TerminalInputArgs(
    [property: JsonPropertyName("terminalId")] string TerminalId,
    [property: JsonPropertyName("data")] string Data);

internal sealed record TerminalResizeArgs(
    [property: JsonPropertyName("terminalId")] string TerminalId,
    [property: JsonPropertyName("columns")] int Columns,
    [property: JsonPropertyName("rows")] int Rows);

/// <summary>Arguments of <c>terminal.foreground</c> and <c>terminal.stop</c>.</summary>
internal sealed record TerminalIdArgs(
    [property: JsonPropertyName("terminalId")] string TerminalId);

internal sealed record TerminalBusyResult(
    [property: JsonPropertyName("busy")] bool? Busy);

internal sealed record TerminalAckArgs(
    [property: JsonPropertyName("terminalId")] string TerminalId,
    [property: JsonPropertyName("bytes")] long Bytes);

internal sealed record TerminalOutputArgs(
    [property: JsonPropertyName("terminalId")] string? TerminalId,
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("data")] byte[]? Data);

internal sealed record TerminalExitArgs(
    [property: JsonPropertyName("terminalId")] string? TerminalId,
    [property: JsonPropertyName("exitCode")] int ExitCode);

/// <summary>Source-generated metadata for the <c>terminal.*</c> payloads; field names are camelCase, which is also
/// what the explicit <see cref="JsonPropertyNameAttribute"/> annotations repeat so a policy change cannot move the
/// wire format. Null members are left out: the host reads an absent <c>shell</c>/<c>arguments</c>/<c>environment</c>
/// as "use the platform default".</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TerminalStartArgs))]
[JsonSerializable(typeof(TerminalStartResult))]
[JsonSerializable(typeof(TerminalInputArgs))]
[JsonSerializable(typeof(TerminalResizeArgs))]
[JsonSerializable(typeof(TerminalIdArgs))]
[JsonSerializable(typeof(TerminalBusyResult))]
[JsonSerializable(typeof(TerminalAckArgs))]
[JsonSerializable(typeof(TerminalOutputArgs))]
[JsonSerializable(typeof(TerminalExitArgs))]
internal sealed partial class TerminalJsonContext : JsonSerializerContext
{
}

/// <summary>Encodes and decodes the <c>terminal.*</c> payloads without reflection, so the client stays
/// Native-AOT compatible.</summary>
internal static class TerminalProtocol
{
    public static JsonElement SerializeStart(TerminalStartRequest request)
    {
        string[]? arguments = request.Arguments is { Count: > 0 } list ? [.. list] : null;
        Dictionary<string, string>? environment = null;
        if (request.Environment is { Count: > 0 } variables)
        {
            environment = new Dictionary<string, string>(variables.Count, StringComparer.Ordinal);
            foreach (var (name, value) in variables)
            {
                environment[name] = value;
            }
        }

        var payload = new TerminalStartArgs(
            request.SessionId,
            request.WorkingDirectory,
            request.Size.Columns,
            request.Size.Rows,
            string.IsNullOrWhiteSpace(request.Shell) ? null : request.Shell,
            arguments,
            environment);
        return JsonSerializer.SerializeToElement(payload, TerminalJsonContext.Default.TerminalStartArgs);
    }

    /// <summary>Reads <c>{terminalId, processId, shell}</c>; a response without a usable terminal id is unusable.</summary>
    public static bool TryReadStartResult(
        JsonElement? result, out string terminalId, out string problem)
    {
        terminalId = string.Empty;
        problem = string.Empty;
        var decoded = Deserialize(result, TerminalJsonContext.Default.TerminalStartResult);
        if (decoded?.TerminalId is not { Length: > 0 } id)
        {
            problem = "the response carries no terminal id.";
            return false;
        }

        terminalId = id;
        return true;
    }

    /// <summary>Base64 coming from a slice of the caller's buffer: a string is the only allocation, and nothing is
    /// handed to the host that outlives the call.</summary>
    public static JsonElement SerializeInput(string terminalId, ReadOnlySpan<byte> data) =>
        JsonSerializer.SerializeToElement(
            new TerminalInputArgs(terminalId, Convert.ToBase64String(data)),
            TerminalJsonContext.Default.TerminalInputArgs);

    public static JsonElement SerializeResize(string terminalId, TerminalSize size) =>
        JsonSerializer.SerializeToElement(
            new TerminalResizeArgs(terminalId, size.Columns, size.Rows),
            TerminalJsonContext.Default.TerminalResizeArgs);

    public static JsonElement SerializeTerminalId(string terminalId) =>
        JsonSerializer.SerializeToElement(
            new TerminalIdArgs(terminalId), TerminalJsonContext.Default.TerminalIdArgs);

    public static JsonElement SerializeAck(string terminalId, long bytes) =>
        JsonSerializer.SerializeToElement(
            new TerminalAckArgs(terminalId, bytes), TerminalJsonContext.Default.TerminalAckArgs);

    /// <summary>Reads <c>{terminalId, seq, data}</c>. A negative sequence number or a missing payload makes the
    /// message unusable; the caller decides whether that ends the terminal.</summary>
    public static bool TryReadOutput(
        JsonElement? payload, out string terminalId, out long seq, out byte[] data, out string problem)
    {
        terminalId = string.Empty;
        seq = 0;
        data = [];
        problem = string.Empty;
        var decoded = Deserialize(payload, TerminalJsonContext.Default.TerminalOutputArgs);
        if (decoded?.TerminalId is not { Length: > 0 } id)
        {
            problem = "the message carries no terminal id.";
            return false;
        }

        if (decoded.Seq < 0)
        {
            problem = $"the message carries a negative sequence number ({decoded.Seq}).";
            return false;
        }

        terminalId = id;
        seq = decoded.Seq;
        data = decoded.Data ?? [];
        return true;
    }

    /// <summary>Reads <c>{terminalId, exitCode}</c>.</summary>
    public static bool TryReadExit(
        JsonElement? payload, out string terminalId, out int exitCode, out string problem)
    {
        terminalId = string.Empty;
        exitCode = 0;
        problem = string.Empty;
        var decoded = Deserialize(payload, TerminalJsonContext.Default.TerminalExitArgs);
        if (decoded?.TerminalId is not { Length: > 0 } id)
        {
            problem = "the message carries no terminal id.";
            return false;
        }

        terminalId = id;
        exitCode = decoded.ExitCode;
        return true;
    }

    /// <summary>Reads the <c>{busy}</c> answer of <c>terminal.foreground</c>. A response without the field is
    /// unusable: an unreadable answer is not the same as "no foreground process".</summary>
    public static bool TryReadBusy(JsonElement? result, out bool busy, out string problem)
    {
        busy = false;
        problem = string.Empty;
        var decoded = Deserialize(result, TerminalJsonContext.Default.TerminalBusyResult);
        if (decoded?.Busy is not { } reported)
        {
            problem = "the response carries no busy flag.";
            return false;
        }

        busy = reported;
        return true;
    }

    private static T? Deserialize<T>(
        JsonElement? payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
    {
        if (payload is not { ValueKind: JsonValueKind.Object } element)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize(element, info);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
