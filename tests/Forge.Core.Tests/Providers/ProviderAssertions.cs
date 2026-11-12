using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>The assertion sequences the provider tests repeat: what a refused request looks like and what a streamed
/// tool call with fragmented arguments must produce.</summary>
internal static class ProviderAssertions
{
    /// <summary>Asserts <paramref name="provider"/> rejects the default request with a <see cref="ProviderException"/>
    /// the caller must not retry, and returns it for the tests that keep asserting on the message.</summary>
    public static async Task<ProviderException> AssertRejectedAsNonRetryableAsync(IChatProvider provider)
    {
        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));
        Assert.False(exception.Retryable);
        return exception;
    }

    /// <summary>Asserts one <c>read_file</c> call (id <paramref name="callId"/>) arrived as two argument deltas and
    /// completed a tool-use turn carrying exactly that call.</summary>
    public static void AssertFragmentedReadFileCall(this List<ChatStreamEvent> events, string callId)
    {
        var started = Assert.Single(events.OfType<ToolCallStarted>());
        Assert.Equal(callId, started.Id);
        Assert.Equal("read_file", started.Name);

        var deltas = events.OfType<ToolCallArgumentsDelta>().ToList();
        Assert.Equal(2, deltas.Count);
        Assert.All(deltas, d => Assert.Equal(callId, d.Id));
        Assert.Equal("{\"path\":\"a.txt\"}", string.Concat(deltas.Select(d => d.Json)));

        var completed = events.Completed();
        Assert.Equal(StopReason.ToolUse, completed.Stop);
        Assert.Equal(
            new ToolCallBlock(callId, "read_file", "{\"path\":\"a.txt\"}"),
            Assert.Single(completed.Message.Content));
    }
}
