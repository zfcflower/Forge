using MessagePack;

namespace Forge.Ipc;

/// <summary>IPC protocol constants shared by the AOT main program, the Extension Host and Workers.</summary>
public static class IpcProtocol
{
    /// <summary>2 = MessagePack frames (header + typed payload); 1 was the retired JSON envelope.</summary>
    public const int Version = 2;

    /// <summary>Maximum frame body size in bytes; larger frames are rejected before deserialization.</summary>
    public const int MaxFrameBytes = 16 * 1024 * 1024;
}

/// <summary>A protocol error carried in a response header.</summary>
[MessagePackObject]
public sealed partial record IpcError(
    [property: Key(0)] string Code,
    [property: Key(1)] string Message,
    [property: Key(2)] string? Data = null);

/// <summary>The payload of an operation that carries no data (an empty MessagePack array on the wire).</summary>
[MessagePackObject]
public sealed partial record IpcEmpty
{
    public static IpcEmpty Instance { get; } = new();
}

/// <summary>
/// The fixed part of every frame. A frame body is this header followed by at most one MessagePack payload value:
/// the arguments of a request or notification, or the result of a successful response. A response sets
/// <see cref="IsResponse"/>; a failed response carries <see cref="Error"/> and no payload. <see cref="Id"/> == 0 marks
/// a notification.
/// </summary>
[MessagePackObject]
public readonly partial record struct IpcHeader(
    [property: Key(0)] int Version,
    [property: Key(1)] long Id,
    [property: Key(2)] string Operation,
    [property: Key(3)] bool IsResponse,
    [property: Key(4)] IpcError? Error)
{
    public static IpcHeader Request(long id, string operation) => new(IpcProtocol.Version, id, operation, false, null);

    public static IpcHeader Notification(string operation) => new(IpcProtocol.Version, 0, operation, false, null);

    public static IpcHeader Result(long id) => new(IpcProtocol.Version, id, "", true, null);

    public static IpcHeader Failure(long id, IpcError error) => new(IpcProtocol.Version, id, "", true, error);
}

/// <summary>One decoded inbound frame: the header plus the still-encoded payload, decoded on demand by the consumer
/// into the type the operation documents.</summary>
public sealed class IpcMessage(IpcHeader header, ReadOnlyMemory<byte> payload)
{
    public IpcHeader Header { get; } = header;

    public int Version => Header.Version;

    public long Id => Header.Id;

    public string Operation => Header.Operation ?? "";

    public IpcError? Error => Header.Error;

    public bool IsResponse => Header.IsResponse;

    public bool IsNotification => Id == 0 && !IsResponse;

    /// <summary>The raw MessagePack payload: arguments for a request/notification, the result for a response.</summary>
    public ReadOnlyMemory<byte> Payload { get; } = payload;

    public bool HasPayload => !Payload.IsEmpty;

    /// <summary>Decodes the payload; a missing or mismatched payload is an <see cref="IpcProtocolException"/>.</summary>
    public T Read<T>() => IpcSerializer.Deserialize<T>(Payload, Operation);
}

/// <summary>The one MessagePack configuration every IPC endpoint uses: source-generated formatters only (AOT safe).</summary>
public static class IpcSerializer
{
    public static MessagePackSerializerOptions Options { get; } = MessagePackSerializerOptions.DefaultAot;

    public static byte[] Serialize<T>(T value) => MessagePackSerializer.Serialize(value, Options);

    public static void Serialize<T>(System.Buffers.IBufferWriter<byte> writer, T value) =>
        MessagePackSerializer.Serialize(writer, value, Options);

    /// <summary>Decodes one payload; <paramref name="operation"/> only names the frame in the error message.</summary>
    public static T Deserialize<T>(ReadOnlyMemory<byte> payload, string operation)
    {
        if (payload.IsEmpty)
        {
            throw new IpcProtocolException($"'{operation}' carries no payload.");
        }

        T? value;
        try
        {
            value = MessagePackSerializer.Deserialize<T>(payload.Span, Options);
        }
        catch (Exception ex) when (ex is MessagePackSerializationException or EndOfStreamException or FormatException or OverflowException or InvalidOperationException or ArgumentException)
        {
            throw new IpcProtocolException($"The payload of '{operation}' is not a '{typeof(T).Name}'.", ex);
        }

        return value ?? throw new IpcProtocolException($"The payload of '{operation}' is nil.");
    }
}

/// <summary>A business error carried by the remote peer (its response carries an <see cref="IpcError"/>).
/// Distinct from <see cref="IpcProtocolException"/> which means the wire format itself was violated.</summary>
public sealed class IpcRemoteException(string operation, string code, string message, string? data = null)
    : Exception($"IPC remote error '{code}' from '{operation}': {message}")
{
    public string Operation { get; } = operation;
    public string Code { get; } = code;
    public string? DataJson { get; } = data;
}

/// <summary>The peer's protocol version does not match this side.</summary>
public sealed class IpcVersionMismatchException(int received, int expected)
    : Exception($"IPC protocol version mismatch: received {received}, expected {expected}.")
{
    public int Received { get; } = received;
    public int Expected { get; } = expected;
}
