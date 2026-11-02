using System.Net;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class ProviderErrorRegressionTests
{
    private const int ErrorBodyCap = 4096;

    [Fact]
    public async Task Error_response_and_its_content_are_disposed_when_the_body_read_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var body = new ScriptedStream([], hangWhenExhausted: true, onRead: () => cts.Cancel());
        var handler = new FakeHttpMessageHandler().Enqueue(() => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StreamContent(body),
        });
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CollectAsync(TestData.Request(), cts.Token));

        Assert.True(body.IsDisposed);
    }

    [Fact]
    public async Task Error_body_read_is_bounded_so_a_large_error_page_is_not_fully_downloaded()
    {
        var body = new ScriptedStream(
            Enumerable.Range(0, 32).Select(_ => new byte[64 * 1024]),
            hangWhenExhausted: false);
        var handler = new FakeHttpMessageHandler().Enqueue(() => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StreamContent(body),
        });
        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));

        Assert.Equal(502, exception.StatusCode);
        Assert.True(exception.Retryable);
        Assert.True(body.BytesRead <= ErrorBodyCap, $"read {body.BytesRead} bytes");
        Assert.True(body.IsDisposed);
        Assert.True(exception.Message.Length <= ErrorBodyCap + 128, $"message length {exception.Message.Length}");
    }
}
