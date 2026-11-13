using Forge.Ipc.Tests.Support;
using Xunit;

namespace Forge.Ipc.Tests;

public sealed class IpcMessagesTests
{
    [Fact]
    public void Request_header_sets_version_id_and_operation()
    {
        var header = IpcHeader.Request(9, "op");

        Assert.Equal(IpcProtocol.Version, header.Version);
        Assert.Equal(9, header.Id);
        Assert.Equal("op", header.Operation);
        Assert.False(header.IsResponse);
        Assert.Null(header.Error);
    }

    [Fact]
    public void Notification_header_has_zero_id_and_is_not_a_response()
    {
        var message = new IpcMessage(IpcHeader.Notification("tick"), ReadOnlyMemory<byte>.Empty);

        Assert.Equal(0, message.Id);
        Assert.True(message.IsNotification);
        Assert.False(message.IsResponse);
        Assert.False(message.HasPayload);
    }

    [Fact]
    public void Result_header_is_a_response_and_not_a_notification_even_with_zero_id()
    {
        var message = new IpcMessage(IpcHeader.Result(4), ReadOnlyMemory<byte>.Empty);

        Assert.Equal(4, message.Id);
        Assert.True(message.IsResponse);
        Assert.False(message.IsNotification);
        Assert.Null(message.Error);
        Assert.Equal("", message.Operation);
        Assert.False(new IpcMessage(IpcHeader.Result(0), ReadOnlyMemory<byte>.Empty).IsNotification);
    }

    [Fact]
    public void Failure_header_is_a_response_carrying_the_error()
    {
        var message = new IpcMessage(IpcHeader.Failure(4, new IpcError("c", "m")), ReadOnlyMemory<byte>.Empty);

        Assert.True(message.IsResponse);
        Assert.False(message.IsNotification);
        Assert.Equal("c", message.Error!.Code);
        Assert.Equal("m", message.Error.Message);
    }

    [Fact]
    public void Message_with_a_null_operation_reports_an_empty_operation()
    {
        var message = new IpcMessage(new IpcHeader(IpcProtocol.Version, 1, null!, false, null), ReadOnlyMemory<byte>.Empty);

        Assert.Equal("", message.Operation);
    }

    [Fact]
    public void Message_read_decodes_its_payload_with_the_serializer()
    {
        var message = new IpcMessage(IpcHeader.Request(1, "op"), IpcSerializer.Serialize(new EchoArgs("a", 2)));

        Assert.True(message.HasPayload);
        Assert.Equal(new EchoArgs("a", 2), message.Read<EchoArgs>());
    }

    [Fact]
    public void Serializer_rejects_an_empty_payload_naming_the_operation()
    {
        var ex = Assert.Throws<IpcProtocolException>(() => IpcSerializer.Deserialize<EchoArgs>(ReadOnlyMemory<byte>.Empty, "lookup"));

        Assert.Contains("'lookup'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_payload_is_an_empty_msgpack_array()
    {
        Assert.Equal(new byte[] { 0x90 }, IpcSerializer.Serialize(IpcEmpty.Instance));
        Assert.Equal(IpcEmpty.Instance, IpcSerializer.Deserialize<IpcEmpty>(new byte[] { 0x90 }, "op"));
    }

    [Fact]
    public void Error_with_and_without_data_round_trips()
    {
        var full = IpcSerializer.Deserialize<IpcError>(IpcSerializer.Serialize(new IpcError("c", "m", "{}")), "op");
        var bare = IpcSerializer.Deserialize<IpcError>(IpcSerializer.Serialize(new IpcError("c", "m")), "op");

        Assert.Equal(new IpcError("c", "m", "{}"), full);
        Assert.Null(bare.Data);
    }

    [Fact]
    public void Protocol_constants_match_the_spec()
    {
        Assert.Equal(2, IpcProtocol.Version);
        Assert.Equal(16 * 1024 * 1024, IpcProtocol.MaxFrameBytes);
    }

    [Fact]
    public void Operation_names_are_stable()
    {
        Assert.Equal("cancel", IpcOperations.Cancel);
        Assert.Equal("ping", IpcOperations.Ping);
    }

    [Fact]
    public void Pipe_options_defaults_match_the_spec_limits()
    {
        var options = new IpcPipeOptions();

        Assert.Equal(16 * 1024 * 1024, options.MaxFrameBytes);
        Assert.Equal(64L * 1024 * 1024, options.MaxResynchronizeBytes);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ConnectTimeout);
        Assert.Equal(64 * 1024, options.ReadBufferBytes);
        Assert.Equal(64 * 1024, options.WriteBufferBytes);
        Assert.Equal(1024, options.FrameQueueCapacity);
        Assert.Equal(32 * 1024 * 1024, options.FrameQueueBytes);
        Assert.Equal(TimeSpan.FromSeconds(5), options.StopTimeout);
    }

    [Fact]
    public void Peer_options_default_to_120_second_deadline_and_unlimited_outstanding()
    {
        var options = new IpcPeerOptions();

        Assert.Equal(TimeSpan.FromSeconds(120), options.DefaultTimeout);
        Assert.Equal(0, options.MaxOutstandingRequests);
        Assert.Null(options.Codec);
    }

    [Fact]
    public void Timeout_exception_exposes_operation_and_timeout()
    {
        var ex = new IpcTimeoutException("slow", TimeSpan.FromMilliseconds(150));

        Assert.IsAssignableFrom<TimeoutException>(ex);
        Assert.Equal("slow", ex.Operation);
        Assert.Equal(TimeSpan.FromMilliseconds(150), ex.Timeout);
        Assert.Contains("'slow'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("150 ms", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_exception_exposes_operation_code_and_data()
    {
        var ex = new IpcRemoteException("lookup", "not_found", "no such key", """{"key":7}""");

        Assert.Equal("lookup", ex.Operation);
        Assert.Equal("not_found", ex.Code);
        Assert.Equal("""{"key":7}""", ex.DataJson);
        Assert.Equal("IPC remote error 'not_found' from 'lookup': no such key", ex.Message);
    }

    [Fact]
    public void Version_mismatch_exception_exposes_both_versions()
    {
        var ex = new IpcVersionMismatchException(3, 2);

        Assert.Equal(3, ex.Received);
        Assert.Equal(2, ex.Expected);
        Assert.Contains("received 3, expected 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_closed_exception_has_default_message_and_keeps_inner()
    {
        var inner = new IOException("reset");
        var ex = new IpcConnectionClosedException(inner: inner);

        Assert.IsAssignableFrom<IOException>(ex);
        Assert.Contains("closed", ex.Message, StringComparison.Ordinal);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void Protocol_exception_is_an_invalid_operation_exception()
    {
        Assert.IsAssignableFrom<InvalidOperationException>(new IpcProtocolException("x"));
    }
}
