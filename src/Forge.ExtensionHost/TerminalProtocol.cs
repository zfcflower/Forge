using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Forge.Ipc;

namespace Forge.ExtensionHost;

/// <summary>
/// The <c>terminal.*</c> wire protocol between the main program and the host. Property names travel camelCase and the
/// payload of <c>terminal.output</c>/<c>terminal.input</c> is base64 in the <c>data</c> field; one frame carries at most
/// <see cref="TerminalSession.ReadChunkBytes"/> decoded bytes, far below <see cref="IpcProtocol.MaxFrameBytes"/>. This
/// file is the host side of a two-file contract - the main program has its own copy in
/// <c>Forge.HostClient/TerminalProtocol.cs</c> and neither is part of the plugin SDK, because a terminal is not a plugin
/// API and must not raise the SDK version. The integration tests pin the two copies together end to end.
/// </summary>
internal static class TerminalProtocol
{
    // Requests: main program → host.
    public const string Start = "terminal.start";
    public const string Resize = "terminal.resize";
    public const string Foreground = "terminal.foreground";
    public const string Stop = "terminal.stop";

    // Notifications: main program → host.
    public const string Input = "terminal.input";
    public const string Ack = "terminal.ack";

    // Notifications: host → main program.
    public const string Output = "terminal.output";
    public const string Exit = "terminal.exit";

    /// <summary>Property names of the output frame, encoded once. A flooding terminal writes one frame per 64 KiB read,
    /// so re-escaping the same three names on every frame is the only per-frame work this saves besides the payload
    /// itself.</summary>
    private static readonly JsonEncodedText TerminalIdName = JsonEncodedText.Encode("terminalId");

    private static readonly JsonEncodedText SequenceName = JsonEncodedText.Encode("seq");

    private static readonly JsonEncodedText DataName = JsonEncodedText.Encode("data");

    private static readonly JsonEncodedText ExitCodeName = JsonEncodedText.Encode("exitCode");

    /// <summary>
    /// Reads the arguments object of an inbound terminal request through the generated metadata. A request without an
    /// arguments object is refused rather than defaulted, the same rule <see cref="HostJson.Read{T}"/> applies to the
    /// plugin operations.
    /// </summary>
    internal static T Read<T>(IpcMessage message, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (message.Arguments is not { ValueKind: JsonValueKind.Object } arguments)
        {
            throw new HostOperationException("invalid_request", $"'{message.Operation}' requires an arguments object.");
        }

        try
        {
            return arguments.Deserialize(typeInfo) ??
                throw new HostOperationException("invalid_request", $"'{message.Operation}' requires an arguments object.");
        }
        catch (JsonException exception)
        {
            throw new HostOperationException(
                "invalid_request",
                $"'{message.Operation}' has unreadable arguments: {exception.Message}");
        }
    }

    /// <summary>Serializes a small payload (a start result, an empty object) through the generated metadata.</summary>
    internal static JsonElement Serialize<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToElement(value, typeInfo);

    /// <summary>
    /// Builds one <c>terminal.output</c> frame straight from the bytes the PTY read: the base64 text is written by the
    /// UTF-8 writer, so no intermediate <see cref="string"/> is allocated for a payload that is up to 87 KiB of base64 per
    /// frame. The document is copied once into a standalone element (the caller's buffer is reused for the next frame)
    /// and the IPC transport serializes that element when it writes the pipe; both copies are bounded by the frame size.
    /// </summary>
    internal static JsonElement OutputPayload(
        ArrayBufferWriter<byte> buffer,
        string terminalId,
        long sequence,
        ReadOnlySpan<byte> data)
    {
        buffer.ResetWrittenCount();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(TerminalIdName, terminalId);
            writer.WriteNumber(SequenceName, sequence);
            writer.WriteBase64String(DataName, data);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>Builds the last message a terminal id ever gets.</summary>
    internal static JsonElement ExitPayload(string terminalId, int exitCode)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(TerminalIdName, terminalId);
            writer.WriteNumber(ExitCodeName, exitCode);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

}

// Main program → host.

/// <summary>One <c>terminal.start</c>: the shell, its arguments and the initial size of the pseudo-terminal.
/// <see cref="WorkingDirectory"/> must be an existing absolute path; an empty <see cref="Shell"/> means the platform
/// default and a null <see cref="Arguments"/> means that default shell's own default arguments.</summary>
internal sealed record TerminalStartRequest(
    string SessionId,
    string WorkingDirectory,
    int Columns,
    int Rows,
    string? Shell = null,
    IReadOnlyList<string>? Arguments = null,
    IReadOnlyDictionary<string, string>? Environment = null);

internal sealed record TerminalStartResponse(string TerminalId, int ProcessId, string Shell);

/// <summary>One <c>terminal.input</c>: base64 bytes for the pseudo-terminal's input, in the order they were sent.</summary>
internal sealed record TerminalInputNotification(string? TerminalId, string? Data);

internal sealed record TerminalResizeRequest(string? TerminalId, int Columns, int Rows);

internal sealed record TerminalForegroundRequest(string? TerminalId);

internal sealed record TerminalForegroundResponse(bool Busy);

internal sealed record TerminalStopRequest(string? TerminalId);

/// <summary>One <c>terminal.ack</c>: how many decoded output bytes the main program has consumed.</summary>
internal sealed record TerminalAckNotification(string? TerminalId, int Bytes);

// Host → main program.

internal sealed record TerminalOutputNotification(string TerminalId, long Seq, string Data);

internal sealed record TerminalExitNotification(string TerminalId, int ExitCode);

/// <summary>Generated metadata for the terminal protocol. Reflection-free, camelCase on the wire, and case-insensitive on
/// the way in so a peer that writes PascalCase still binds, exactly like <see cref="HostJson"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(TerminalStartRequest))]
[JsonSerializable(typeof(TerminalStartResponse))]
[JsonSerializable(typeof(TerminalInputNotification))]
[JsonSerializable(typeof(TerminalResizeRequest))]
[JsonSerializable(typeof(TerminalForegroundRequest))]
[JsonSerializable(typeof(TerminalForegroundResponse))]
[JsonSerializable(typeof(TerminalStopRequest))]
[JsonSerializable(typeof(TerminalAckNotification))]
[JsonSerializable(typeof(TerminalOutputNotification))]
[JsonSerializable(typeof(TerminalExitNotification))]
internal sealed partial class TerminalJsonContext : JsonSerializerContext;
