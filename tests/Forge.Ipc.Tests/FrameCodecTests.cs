using System.Buffers;
using System.Buffers.Binary;
using Forge.Ipc.Tests.Support;
using Xunit;

namespace Forge.Ipc.Tests;

public sealed class FrameCodecTests
{
    private static readonly EchoArgs Sample = new("héllo", 7);

    private static byte[] Body(byte[] prefixed) => prefixed[TestFrames.HeaderBytes..];

    [Fact]
    public void Encode_prefixes_the_body_with_its_little_endian_length()
    {
        var frame = FrameCodec.Default.EncodeToArray(IpcHeader.Request(5, "echo"), Sample);

        Assert.Equal(frame.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(frame));
    }

    [Fact]
    public void Request_round_trips_header_and_payload()
    {
        var decoded = FrameCodec.Default.Decode(TestFrames.Request(5, "echo", Sample));

        Assert.Equal(IpcProtocol.Version, decoded.Version);
        Assert.Equal(5, decoded.Id);
        Assert.Equal("echo", decoded.Operation);
        Assert.False(decoded.IsNotification);
        Assert.False(decoded.IsResponse);
        Assert.Null(decoded.Error);
        Assert.True(decoded.HasPayload);
        Assert.Equal(Sample, decoded.Read<EchoArgs>());
    }

    [Fact]
    public void Notification_round_trips_with_zero_id()
    {
        var decoded = FrameCodec.Default.Decode(TestFrames.Notification("tick", Sample));

        Assert.Equal(0, decoded.Id);
        Assert.True(decoded.IsNotification);
        Assert.False(decoded.IsResponse);
        Assert.Equal(Sample, decoded.Read<EchoArgs>());
    }

    [Fact]
    public void Result_round_trips_as_a_response_with_its_payload()
    {
        var decoded = FrameCodec.Default.Decode(TestFrames.Result(3, new EchoResult("a", 1, true)));

        Assert.Equal(3, decoded.Id);
        Assert.True(decoded.IsResponse);
        Assert.False(decoded.IsNotification);
        Assert.Null(decoded.Error);
        Assert.Equal(new EchoResult("a", 1, true), decoded.Read<EchoResult>());
    }

    [Fact]
    public void Header_only_frame_round_trips_without_a_payload()
    {
        var decoded = FrameCodec.Default.Decode(TestFrames.HeaderOnly(IpcHeader.Request(8, "bare")));

        Assert.Equal(8, decoded.Id);
        Assert.Equal("bare", decoded.Operation);
        Assert.False(decoded.HasPayload);
        var ex = Assert.Throws<IpcProtocolException>(() => decoded.Read<EchoArgs>());
        Assert.Contains("no payload", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_round_trips_code_message_and_data_without_a_payload()
    {
        var decoded = FrameCodec.Default.Decode(TestFrames.Failure(9, new IpcError("denied", "not allowed", """{"k":1}""")));

        Assert.Equal(9, decoded.Id);
        Assert.True(decoded.IsResponse);
        Assert.False(decoded.HasPayload);
        Assert.Equal("denied", decoded.Error!.Code);
        Assert.Equal("not allowed", decoded.Error.Message);
        Assert.Equal("""{"k":1}""", decoded.Error.Data);
    }

    [Fact]
    public void Failure_without_data_decodes_with_null_data()
    {
        var decoded = FrameCodec.Default.Decode(TestFrames.Failure(2, new IpcError("c", "m")));

        Assert.Null(decoded.Error!.Data);
    }

    [Fact]
    public void Decode_strips_the_optional_length_prefix()
    {
        var prefixed = TestFrames.Request(4, "x", Sample);

        var withPrefix = FrameCodec.Default.Decode(prefixed);
        var bare = FrameCodec.Default.Decode(Body(prefixed));

        Assert.Equal("x", withPrefix.Operation);
        Assert.Equal(4, withPrefix.Id);
        Assert.Equal(bare.Operation, withPrefix.Operation);
        Assert.Equal(bare.Id, withPrefix.Id);
        Assert.Equal(Sample, withPrefix.Read<EchoArgs>());
        Assert.Equal(Sample, bare.Read<EchoArgs>());
        Assert.Equal(bare.Payload.ToArray(), withPrefix.Payload.ToArray());
    }

    [Fact]
    public void Encode_to_array_matches_the_pooled_encoding()
    {
        var header = IpcHeader.Request(1, "op");
        using var pooled = FrameCodec.Default.Encode(header, Sample);

        Assert.Equal(pooled.WrittenMemory.ToArray(), FrameCodec.Default.EncodeToArray(header, Sample));
    }

    [Fact]
    public void Decode_reports_a_foreign_wire_version_without_rejecting_it()
    {
        var header = IpcHeader.Request(4, "x") with { Version = 99 };
        var decoded = FrameCodec.Default.Decode(FrameCodec.Default.EncodeToArray(header, Sample));

        Assert.Equal(99, decoded.Version);
        Assert.NotEqual(IpcProtocol.Version, decoded.Version);
        Assert.Equal(4, decoded.Id);
    }

    [Fact]
    public void Decode_rejects_an_empty_frame()
    {
        Assert.Throws<IpcProtocolException>(() => FrameCodec.Default.Decode(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Decode_rejects_a_zero_length_prefix_without_a_body()
    {
        Assert.Throws<IpcProtocolException>(() => FrameCodec.Default.Decode(TestFrames.Header(0)));
    }

    [Fact]
    public void Decode_rejects_a_body_above_the_limit()
    {
        var codec = new FrameCodec(64);
        var body = Body(FrameCodec.Default.EncodeToArray(IpcHeader.Request(1, "op"), new EchoArgs(TestFrames.Padded(80), 1)));

        Assert.Throws<IpcProtocolException>(() => codec.Decode(body));
    }

    [Fact]
    public void Decode_accepts_a_body_exactly_at_the_limit()
    {
        var bodyBytes = Body(TestFrames.Request(1, "op", Sample));
        var codec = new FrameCodec(bodyBytes.Length);

        Assert.Equal("op", codec.Decode(bodyBytes).Operation);
    }

    [Fact]
    public void Encode_rejects_a_body_above_the_limit()
    {
        var codec = new FrameCodec(64);

        Assert.Throws<ArgumentException>(() => codec.EncodeToArray(IpcHeader.Request(1, "op"), new EchoArgs(TestFrames.Padded(80), 1)));
    }

    [Fact]
    public void Decode_rejects_a_truncated_header()
    {
        var body = Body(TestFrames.HeaderOnly(IpcHeader.Request(300, "some.operation")));

        for (var cut = 1; cut < body.Length; cut++)
        {
            var truncated = body[..cut];
            Assert.Throws<IpcProtocolException>(() => FrameCodec.Default.Decode(truncated));
        }
    }

    [Fact]
    public void Decode_rejects_garbage_bytes()
    {
        var ex = Assert.Throws<IpcProtocolException>(() => FrameCodec.Default.Decode(new byte[] { 0xC1, 0xC1, 0xC1 }));

        Assert.Contains("header", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_rejects_a_header_that_is_not_an_array()
    {
        Assert.Throws<IpcProtocolException>(() => FrameCodec.Default.Decode(IpcSerializer.Serialize("not a header")));
    }

    [Fact]
    public void Decode_rejects_a_header_with_a_field_of_the_wrong_type()
    {
        var bad = IpcSerializer.Serialize(new WrongTypedHeader("version", 1, "op", false, null));

        Assert.Throws<IpcProtocolException>(() => FrameCodec.Default.Decode(bad));
    }

    [Fact]
    public void Read_rejects_a_payload_of_the_wrong_type()
    {
        var decoded = FrameCodec.Default.Decode(TestFrames.Request(1, "op", "just a string"));

        var ex = Assert.Throws<IpcProtocolException>(() => decoded.Read<EchoArgs>());
        Assert.Contains("'op'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(EchoArgs), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_rejects_a_truncated_payload()
    {
        var frame = Body(TestFrames.Request(1, "op", Sample));
        var decoded = FrameCodec.Default.Decode(frame[..^2]);

        Assert.True(decoded.HasPayload);
        Assert.Throws<IpcProtocolException>(() => decoded.Read<EchoArgs>());
    }

    [Fact]
    public void Read_rejects_a_nil_payload()
    {
        var decoded = FrameCodec.Default.Decode(FrameCodec.Default.EncodeToArray<EchoArgs?>(IpcHeader.Request(1, "op"), null));

        Assert.True(decoded.HasPayload);
        var ex = Assert.Throws<IpcProtocolException>(() => decoded.Read<EchoArgs>());
        Assert.Contains("nil", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Primitive_and_empty_payloads_round_trip()
    {
        Assert.Equal(42L, FrameCodec.Default.Decode(TestFrames.Notification("n", 42L)).Read<long>());
        Assert.Equal("hi", FrameCodec.Default.Decode(TestFrames.Notification("n", "hi")).Read<string>());
        Assert.Equal(IpcEmpty.Instance, FrameCodec.Default.Decode(TestFrames.Notification("n", IpcEmpty.Instance)).Read<IpcEmpty>());
    }

    [Fact]
    public void Byte_payloads_travel_as_binary_and_stay_exact()
    {
        var bytes = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();

        var decoded = FrameCodec.Default.Decode(TestFrames.Request(1, "bin", bytes));

        Assert.Equal(bytes, decoded.Read<byte[]>());
        Assert.True(decoded.Payload.Length < bytes.Length + 8, "bytes must not be expanded (no base64).");
    }

    [Fact]
    public void Constructor_rejects_a_limit_smaller_than_the_length_prefix()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameCodec(3));
    }

    [Fact]
    public void Default_codec_uses_the_protocol_frame_limit()
    {
        Assert.Equal(IpcProtocol.MaxFrameBytes, FrameCodec.Default.MaxFrameBytes);
        Assert.Equal(16 * 1024 * 1024, FrameCodec.Default.MaxFrameBytes);
    }

    [Fact]
    public void Frame_buffer_grows_past_its_initial_capacity_and_keeps_the_bytes()
    {
        using var buffer = new IpcFrameBuffer();
        var expected = new byte[10_000];
        for (var i = 0; i < expected.Length; i++)
        {
            expected[i] = (byte)(i * 31);
        }

        var offset = 0;
        while (offset < expected.Length)
        {
            var chunk = Math.Min(97, expected.Length - offset);
            var span = buffer.GetSpan(chunk);
            expected.AsSpan(offset, chunk).CopyTo(span);
            buffer.Advance(chunk);
            offset += chunk;
        }

        Assert.Equal(expected.Length, buffer.WrittenCount);
        Assert.Equal(expected, buffer.WrittenMemory.ToArray());
    }

    [Fact]
    public void Frame_buffer_honours_a_large_size_hint_in_one_step()
    {
        using var buffer = new IpcFrameBuffer();

        var memory = buffer.GetMemory(100_000);

        Assert.True(memory.Length >= 100_000);
    }

    [Fact]
    public void Frame_buffer_rejects_advancing_past_the_end_or_by_a_negative_count()
    {
        using var buffer = new IpcFrameBuffer();

        Assert.Throws<InvalidOperationException>(() => buffer.Advance(1_000_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Advance(-1));
    }

    [Fact]
    public void Frame_buffer_dispose_is_idempotent_and_makes_the_buffer_unusable()
    {
        var buffer = new IpcFrameBuffer();
        buffer.GetSpan(4)[0] = 1;
        buffer.Advance(1);

        buffer.Dispose();
        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => buffer.WrittenMemory);
        Assert.Throws<ObjectDisposedException>(() => buffer.GetMemory());
        Assert.Throws<ObjectDisposedException>(() => buffer.Advance(0));
    }

    [Fact]
    public void Frame_buffer_dispose_does_not_return_the_same_array_to_the_pool_twice()
    {
        var buffer = new IpcFrameBuffer();
        buffer.Dispose();
        buffer.Dispose();

        var first = ArrayPool<byte>.Shared.Rent(256);
        var second = ArrayPool<byte>.Shared.Rent(256);
        try
        {
            Assert.NotSame(first, second);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(first);
            ArrayPool<byte>.Shared.Return(second);
        }
    }
}
