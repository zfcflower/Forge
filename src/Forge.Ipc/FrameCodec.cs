using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Forge.Ipc;

/// <summary>Encodes <see cref="IpcMessage"/> as a 4-byte little-endian length header followed by the UTF-8 JSON body.</summary>
public sealed class FrameCodec : IJsonFrameCodec
{
    public static FrameCodec Default { get; } = new();

    public FrameCodec()
        : this(IpcProtocol.MaxFrameBytes)
    {
    }

    public FrameCodec(int maxFrameBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameBytes, 4);
        MaxFrameBytes = maxFrameBytes;
    }

    public int MaxFrameBytes { get; }

    public ReadOnlyMemory<byte> Serialize(IpcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, IpcWireJsonContext.Wire.IpcMessage);
        if (body.Length > MaxFrameBytes)
        {
            throw new ArgumentException(
                $"Frame of {body.Length} bytes exceeds the {MaxFrameBytes}-byte limit.", nameof(message));
        }

        var frame = new byte[FrameExtractor.HeaderBytes + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame.AsSpan(FrameExtractor.HeaderBytes));
        return frame;
    }

    public IpcMessage Deserialize(ReadOnlyMemory<byte> frame)
    {
        var body = frame.Span;
        if (body.Length > FrameExtractor.HeaderBytes &&
            BinaryPrimitives.ReadInt32LittleEndian(body) == body.Length - FrameExtractor.HeaderBytes)
        {
            body = body[FrameExtractor.HeaderBytes..];
        }

        if (body.IsEmpty)
        {
            throw new IpcProtocolException("An IPC frame must not be empty.");
        }

        if (body.Length > MaxFrameBytes)
        {
            throw new IpcProtocolException(
                $"Frame of {body.Length} bytes exceeds the {MaxFrameBytes}-byte limit.");
        }

        try
        {
            return JsonSerializer.Deserialize(body, IpcJsonContext.Default.IpcMessage)
                ?? throw new IpcProtocolException("The IPC frame decoded to null.");
        }
        catch (JsonException ex)
        {
            throw new IpcProtocolException("The IPC frame is not a valid message.", ex);
        }
    }
}

/// <summary>Operation names the peer handles itself or emits on its own.</summary>
public static class IpcOperations
{
    public const string Cancel = "cancel";
    public const string Ping = "ping";
    public const string RequestIdArgument = "id";
}

/// <summary>Source-generated result types available to <see cref="IIpcPeer.SendRequestAsync{T}"/> plus a registry for others.</summary>
public static class IpcJsonTypes
{
    private static readonly Lock Gate = new();
    private static JsonSerializerContext[] contexts = [IpcBuiltInJsonContext.Default];

    /// <summary>Adds a context so its types can be used as typed request results; later registrations win.</summary>
    public static void Register(JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (Gate)
        {
            if (!contexts.Contains(context))
            {
                contexts = [context, .. contexts];
            }
        }
    }

    public static JsonTypeInfo? Find(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        JsonSerializerContext[] snapshot;
        lock (Gate)
        {
            snapshot = contexts;
        }

        foreach (var context in snapshot)
        {
            var info = context.GetTypeInfo(type);
            if (info is not null)
            {
                return info;
            }
        }

        return null;
    }

    public static T DeserializeResult<T>(JsonElement element, string operation)
        where T : notnull
    {
        if (typeof(T) == typeof(JsonElement))
        {
            return (T)(object)element.Clone();
        }

        if (Find(typeof(T)) is not JsonTypeInfo<T> info)
        {
            throw new IpcProtocolException(
                $"Type '{typeof(T).FullName}' is not registered for IPC results; register a JsonSerializerContext that covers it.");
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize(element, info);
        }
        catch (JsonException ex)
        {
            throw new IpcProtocolException($"The result of '{operation}' is not a '{typeof(T).Name}'.", ex);
        }

        return value ?? throw new IpcProtocolException($"The result of '{operation}' was null.");
    }

    public static JsonElement EmptyObject { get; } = CreateEmptyObject();

    private static JsonElement CreateEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    public static JsonElement FromId(long id)
    {
        var buffer = new ArrayBufferWriter<byte>(32);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber(IpcOperations.RequestIdArgument, id);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}

[JsonSerializable(typeof(IpcMessage))]
[JsonSerializable(typeof(IpcError))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(List<JsonElement>))]
internal partial class IpcBuiltInJsonContext : JsonSerializerContext
{
}
