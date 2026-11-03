using Forge.Ipc.Tests.Support;
using Xunit;

namespace Forge.Ipc.Tests;

public sealed class IpcMessagesTests
{
    [Fact]
    public void Request_factory_sets_id_operation_and_arguments()
    {
        var message = IpcMessage.Request(9, "op", Json.Parse("""{"a":1}"""));

        Assert.Equal(9, message.Id);
        Assert.Equal("op", message.Operation);
        Assert.Equal("""{"a":1}""", message.Arguments!.Value.GetRawText());
        Assert.Equal(IpcProtocol.Version, message.Version);
        Assert.False(message.IsNotification);
        Assert.False(message.IsResponse);
    }

    [Fact]
    public void Notification_factory_has_zero_id_and_is_not_a_response()
    {
        var message = IpcMessage.Notification("tick");

        Assert.Equal(0, message.Id);
        Assert.True(message.IsNotification);
        Assert.False(message.IsResponse);
        Assert.Null(message.Arguments);
    }

    [Fact]
    public void Result_factory_is_a_response_and_not_a_notification()
    {
        var message = IpcMessage.ResultMessage(4, Json.Parse("true"));

        Assert.Equal(4, message.Id);
        Assert.True(message.IsResponse);
        Assert.False(message.IsNotification);
    }

    [Fact]
    public void Error_factory_is_a_response_without_a_result()
    {
        var message = IpcMessage.ErrorMessage(4, new IpcError("c", "m"));

        Assert.True(message.IsResponse);
        Assert.False(message.IsNotification);
        Assert.Null(message.Result);
        Assert.Equal("c", message.Error!.Code);
    }

    [Fact]
    public void Protocol_constants_match_the_spec()
    {
        Assert.Equal(1, IpcProtocol.Version);
        Assert.Equal(16 * 1024 * 1024, IpcProtocol.MaxFrameBytes);
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
        var ex = new IpcVersionMismatchException(2, 1);

        Assert.Equal(2, ex.Received);
        Assert.Equal(1, ex.Expected);
        Assert.Contains("received 2, expected 1", ex.Message, StringComparison.Ordinal);
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
