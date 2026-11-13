using System.Buffers;
using System.Buffers.Binary;
using MessagePack;

namespace Forge.Ipc;

/// <summary>
/// Encodes a frame as a 4-byte little-endian body length followed by the body: one MessagePack <see cref="IpcHeader"/>
/// and, for requests, notifications and successful responses, one MessagePack payload value written straight from the
/// typed object. Encoding writes into a pooled <see cref="IpcFrameBuffer"/>, so a send allocates no frame array.
/// </summary>
public sealed class FrameCodec
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

    /// <summary>Encodes a header and its payload; the caller disposes the buffer once the transport has copied it.</summary>
    public IpcFrameBuffer Encode<T>(in IpcHeader header, T payload)
    {
        var buffer = new IpcFrameBuffer();
        try
        {
            buffer.Advance(FrameExtractor.HeaderBytes);
            IpcSerializer.Serialize(buffer, header);
            IpcSerializer.Serialize(buffer, payload);
            Seal(buffer);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>Encodes a header followed by payload bytes the caller already encoded (a relayed frame, or a protocol
    /// test that deliberately builds a value no formatter would produce). The bytes are appended verbatim.</summary>
    internal IpcFrameBuffer EncodeRaw(in IpcHeader header, ReadOnlyMemory<byte> payload)
    {
        var buffer = new IpcFrameBuffer();
        try
        {
            buffer.Advance(FrameExtractor.HeaderBytes);
            IpcSerializer.Serialize(buffer, header);
            if (!payload.IsEmpty)
            {
                payload.Span.CopyTo(buffer.GetSpan(payload.Length));
                buffer.Advance(payload.Length);
            }

            Seal(buffer);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>Encodes a header without a payload (a failed response).</summary>
    public IpcFrameBuffer Encode(in IpcHeader header)
    {
        var buffer = new IpcFrameBuffer();
        try
        {
            buffer.Advance(FrameExtractor.HeaderBytes);
            IpcSerializer.Serialize(buffer, header);
            Seal(buffer);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>Encodes into a standalone array, for tests and tools that keep the frame.</summary>
    public byte[] EncodeToArray<T>(in IpcHeader header, T payload)
    {
        using var buffer = Encode(header, payload);
        return buffer.WrittenMemory.ToArray();
    }

    /// <summary>Decodes a frame body (with or without its length prefix). The returned message's payload is a slice
    /// of <paramref name="frame"/>, which the transport hands over and never reuses.</summary>
    public IpcMessage Decode(ReadOnlyMemory<byte> frame)
    {
        var body = frame;
        if (body.Length > FrameExtractor.HeaderBytes &&
            BinaryPrimitives.ReadInt32LittleEndian(body.Span) == body.Length - FrameExtractor.HeaderBytes)
        {
            body = body[FrameExtractor.HeaderBytes..];
        }

        if (body.IsEmpty)
        {
            throw new IpcProtocolException("An IPC frame must not be empty.");
        }

        if (body.Length > MaxFrameBytes)
        {
            throw new IpcProtocolException($"Frame of {body.Length} bytes exceeds the {MaxFrameBytes}-byte limit.");
        }

        IpcHeader header;
        int consumed;
        try
        {
            var reader = new MessagePackReader(body.Span);
            reader.Skip();
            consumed = checked((int)reader.BytesConsumed);
            header = MessagePackSerializer.Deserialize<IpcHeader>(body.Span[..consumed], IpcSerializer.Options);
        }
        catch (Exception ex) when (ex is MessagePackSerializationException or EndOfStreamException or FormatException or OverflowException or InvalidOperationException or ArgumentException)
        {
            throw new IpcProtocolException("The IPC frame does not start with a valid header.", ex);
        }

        return new IpcMessage(header, body[consumed..]);
    }

    private void Seal(IpcFrameBuffer buffer)
    {
        var length = buffer.WrittenCount - FrameExtractor.HeaderBytes;
        if (length > MaxFrameBytes)
        {
            throw new ArgumentException($"Frame of {length} bytes exceeds the {MaxFrameBytes}-byte limit.");
        }

        BinaryPrimitives.WriteInt32LittleEndian(buffer.WrittenSpanForPrefix, length);
    }
}

/// <summary>A growable frame writer backed by <see cref="ArrayPool{T}.Shared"/>; dispose returns the array.</summary>
public sealed class IpcFrameBuffer : IBufferWriter<byte>, IDisposable
{
    private const int InitialCapacity = 256;

    private byte[]? array = ArrayPool<byte>.Shared.Rent(InitialCapacity);
    private int written;

    public int WrittenCount => written;

    public ReadOnlyMemory<byte> WrittenMemory => Array.AsMemory(0, written);

    internal Span<byte> WrittenSpanForPrefix => Array.AsSpan(0, FrameExtractor.HeaderBytes);

    private byte[] Array => array ?? throw new ObjectDisposedException(nameof(IpcFrameBuffer));

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (written + count > Array.Length)
        {
            throw new InvalidOperationException("Advanced past the end of the frame buffer.");
        }

        written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return Array.AsMemory(written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return Array.AsSpan(written);
    }

    public void Dispose()
    {
        var rented = Interlocked.Exchange(ref array, null);
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void Ensure(int sizeHint)
    {
        var current = Array;
        var needed = written + Math.Max(sizeHint, 1);
        if (needed <= current.Length)
        {
            return;
        }

        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(needed, current.Length * 2));
        current.AsSpan(0, written).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(current);
        array = grown;
    }
}

/// <summary>Operation names the peer handles itself or emits on its own.</summary>
public static class IpcOperations
{
    /// <summary>Notification whose payload is the <see cref="long"/> id of the request to cancel.</summary>
    public const string Cancel = "cancel";

    /// <summary>Request answered by the peer itself with <see cref="IpcEmpty"/>.</summary>
    public const string Ping = "ping";
}
