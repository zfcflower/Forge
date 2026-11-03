using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Forge.Ipc.Tests.Support;
using Xunit;

namespace Forge.Ipc.Tests;

public sealed class FrameCodecTests
{
    [Fact]
    public void Serialize_prefixes_the_body_with_its_little_endian_length()
    {
        var frame = FrameCodec.Default.Serialize(IpcMessage.Notification("tick")).ToArray();

        Assert.Equal(frame.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(frame));
        Assert.Equal("""{"v":1,"id":0,"op":"tick"}""", Encoding.UTF8.GetString(frame, 4, frame.Length - 4));
    }

    [Fact]
    public void Notification_wire_text_is_exact_and_omits_computed_properties()
    {
        Assert.Equal("""{"v":1,"id":0,"op":"tick"}""", Wire.BodyText(IpcMessage.Notification("tick")));
    }

    [Fact]
    public void Request_wire_text_carries_arguments_verbatim()
    {
        var text = Wire.BodyText(IpcMessage.Request(5, "echo", Json.Parse("""{ "z" : 1.50, "a" : "\u00e9" }""")));

        Assert.Equal("""{"v":1,"id":5,"op":"echo","args":{ "z" : 1.50, "a" : "\u00e9" }}""", text);
    }

    [Fact]
    public void Result_wire_text_carries_result_verbatim_and_no_computed_fields()
    {
        var text = Wire.BodyText(IpcMessage.ResultMessage(3, Json.Parse("""[1, 2.0,"x"]""")));

        Assert.Equal("""{"v":1,"id":3,"op":"","result":[1, 2.0,"x"]}""", text);
        Assert.DoesNotContain("IsResponse", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IsNotification", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_round_trips_with_arguments_byte_exact()
    {
        const string args = """{ "z" : 1.50, "a" : "\u00e9", "n" : [1,2] }""";
        var decoded = FrameCodec.Default.Deserialize(FrameCodec.Default.Serialize(IpcMessage.Request(5, "echo", Json.Parse(args))));

        Assert.Equal(5, decoded.Id);
        Assert.Equal("echo", decoded.Operation);
        Assert.Equal(args, decoded.Arguments!.Value.GetRawText());
        Assert.False(decoded.IsNotification);
        Assert.False(decoded.IsResponse);
    }

    [Fact]
    public void Notification_round_trips_with_zero_id()
    {
        var decoded = FrameCodec.Default.Deserialize(FrameCodec.Default.Serialize(IpcMessage.Notification("tick", Json.Parse("""{"n":1}"""))));

        Assert.Equal(0, decoded.Id);
        Assert.True(decoded.IsNotification);
        Assert.False(decoded.IsResponse);
        Assert.Equal("""{"n":1}""", decoded.Arguments!.Value.GetRawText());
    }

    [Fact]
    public void Result_round_trips_as_a_response_with_its_bytes_intact()
    {
        var decoded = FrameCodec.Default.Deserialize(FrameCodec.Default.Serialize(IpcMessage.ResultMessage(3, Json.Parse("""[1, 2.0,"x"]"""))));

        Assert.Equal(3, decoded.Id);
        Assert.True(decoded.IsResponse);
        Assert.False(decoded.IsNotification);
        Assert.Null(decoded.Error);
        Assert.Equal("""[1, 2.0,"x"]""", decoded.Result!.Value.GetRawText());
    }

    [Fact]
    public void Error_round_trips_code_message_and_data()
    {
        var decoded = FrameCodec.Default.Deserialize(FrameCodec.Default.Serialize(
            IpcMessage.ErrorMessage(9, new IpcError("denied", "not allowed", """{"k":1}"""))));

        Assert.Equal(9, decoded.Id);
        Assert.True(decoded.IsResponse);
        Assert.Null(decoded.Result);
        Assert.Equal("denied", decoded.Error!.Code);
        Assert.Equal("not allowed", decoded.Error.Message);
        Assert.Equal("""{"k":1}""", decoded.Error.Data);
    }

    [Fact]
    public void Error_without_data_decodes_with_null_data()
    {
        var decoded = FrameCodec.Default.Deserialize(FrameCodec.Default.Serialize(IpcMessage.ErrorMessage(2, new IpcError("c", "m"))));

        Assert.Null(decoded.Error!.Data);
    }

    [Fact]
    public void Deserialize_accepts_a_body_with_or_without_its_length_prefix()
    {
        var body = Encoding.UTF8.GetBytes("""{"v":1,"id":4,"op":"x"}""");

        var bare = FrameCodec.Default.Deserialize(body);
        var prefixed = FrameCodec.Default.Deserialize(TestFrames.Prefix(body));

        Assert.Equal("x", bare.Operation);
        Assert.Equal(4, bare.Id);
        Assert.Equal(bare.Operation, prefixed.Operation);
        Assert.Equal(bare.Id, prefixed.Id);
    }

    [Fact]
    public void Deserialize_ignores_unknown_properties_and_reports_the_wire_version()
    {
        var decoded = FrameCodec.Default.Deserialize(Encoding.UTF8.GetBytes("""{"v":2,"id":4,"op":"x","extra":{"a":1}}"""));

        Assert.Equal(2, decoded.Version);
        Assert.Equal(4, decoded.Id);
        Assert.Equal("x", decoded.Operation);
    }

    [Fact]
    public void Deserialize_rejects_an_empty_frame()
    {
        Assert.Throws<IpcProtocolException>(() => FrameCodec.Default.Deserialize(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Deserialize_rejects_malformed_json_with_the_json_exception_as_inner()
    {
        var ex = Assert.Throws<IpcProtocolException>(() => FrameCodec.Default.Deserialize(Encoding.UTF8.GetBytes("not json")));

        Assert.IsType<JsonException>(ex.InnerException);
    }

    [Fact]
    public void Deserialize_rejects_a_frame_that_decodes_to_null()
    {
        var ex = Assert.Throws<IpcProtocolException>(() => FrameCodec.Default.Deserialize(Encoding.UTF8.GetBytes("null")));

        Assert.Contains("null", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_rejects_a_body_above_the_limit()
    {
        var codec = new FrameCodec(64);
        var body = Encoding.UTF8.GetBytes($$"""{"v":1,"id":1,"op":"{{TestFrames.Padded(80)}}"}""");

        Assert.Throws<IpcProtocolException>(() => codec.Deserialize(body));
    }

    [Fact]
    public void Serialize_rejects_a_body_above_the_limit()
    {
        var codec = new FrameCodec(64);
        var message = IpcMessage.Request(1, "op", Json.Parse($"\"{TestFrames.Padded(80)}\""));

        Assert.Throws<ArgumentException>(() => codec.Serialize(message));
    }

    [Fact]
    public void Constructor_rejects_a_limit_smaller_than_the_length_header()
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
    public void Serialize_rejects_a_null_message()
    {
        Assert.Throws<ArgumentNullException>(() => FrameCodec.Default.Serialize(null!));
    }
}
