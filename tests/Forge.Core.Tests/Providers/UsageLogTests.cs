using System.Net;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// Pins what the usage log receives: one record per HTTP attempt, with the shape the request claimed and the numbers the response
/// reported — including the attempts that failed, which are exactly the ones a later analysis needs.
/// </summary>
[Collection("anthropic-wire")]
public sealed class UsageLogTests : IDisposable
{
    private sealed class Recorder : IUsageRecorder
    {
        public List<UsageRecord> Records { get; } = [];

        public void Record(UsageRecord record) => Records.Add(record);
    }

    public UsageLogTests() => AnthropicProvider.ForgetDegradations();

    public void Dispose() => AnthropicProvider.ForgetDegradations();

    private static ProviderConfig Subscription() => TestData.AnthropicConfig() with
    {
        ApiKey = "oat-token",
        AuthStyle = ProviderAuthStyle.BearerOnly,
        SystemPromptPreamble = AnthropicWire.Identity,
        Wire = new ClaudeWire("2.1.318", "da9", "sdk-cli", AnthropicWire.Betas),
    };

    private static string Answer() => string.Concat(
        TestData.WithEvent("message_start", """{"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":11,"cache_read_input_tokens":100,"output_tokens":1}}}"""),
        TestData.WithEvent("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}"""),
        TestData.WithEvent("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}"""),
        TestData.WithEvent("message_stop", """{"type":"message_stop"}"""));

    [Fact]
    public async Task A_completed_request_is_recorded_with_its_shape_and_numbers()
    {
        var recorder = new Recorder();
        var handler = new FakeHttpMessageHandler().Enqueue(() =>
        {
            var response = FakeHttpMessageHandler.SseResponse(Answer());
            response.Headers.TryAddWithoutValidation("request-id", "req_1");
            response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.42");
            return response;
        });
        var provider = new AnthropicProvider(
            Subscription(),
            handler.CreateClient(),
            runtimeBuild: () => "da9",
            runtimeBetas: () => AnthropicWire.Betas,
            options: new ProviderOptions { UsageRecorder = recorder });

        await provider.CollectAsync(TestData.Request(cache: new CacheHint(true, null, "1h")) with { SessionId = "session-7" });

        var record = Assert.Single(recorder.Records);
        Assert.Equal(UsageRecord.Ok, record.Outcome);
        Assert.Equal("session-7", record.SessionId);
        Assert.Equal("anthropic", record.ProviderId);
        Assert.Equal("sdk-cli", record.Entrypoint);
        Assert.Equal("1h", record.CacheTtl);
        Assert.False(record.Downgraded);
        Assert.Equal(200, record.StatusCode);
        Assert.Equal("req_1", record.RequestId);
        Assert.Equal(111, record.Usage.InputTokens);
        Assert.Equal(100, record.Usage.CacheReadTokens);
        Assert.Equal(2, record.Usage.OutputTokens);
        Assert.Equal(0.42, record.RateLimit?.FiveHourUtilization);
        Assert.True(record.ElapsedMilliseconds >= 0);
    }

    [Fact]
    public async Task A_failed_request_is_recorded_too()
    {
        var recorder = new Recorder();
        var handler = new FakeHttpMessageHandler().Enqueue(() => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom"),
        });
        var provider = new AnthropicProvider(
            Subscription(),
            handler.CreateClient(),
            runtimeBuild: () => "da9",
            runtimeBetas: () => AnthropicWire.Betas,
            options: new ProviderOptions { UsageRecorder = recorder });

        await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));

        var record = Assert.Single(recorder.Records);
        Assert.Equal(UsageRecord.Error, record.Outcome);
        Assert.Equal(500, record.StatusCode);
        Assert.Equal(TokenUsage.Empty, record.Usage);
    }

    [Fact]
    public async Task A_downgraded_retry_is_two_records_and_the_second_says_so()
    {
        var recorder = new Recorder();
        var handler = new FakeHttpMessageHandler()
            .Enqueue(() => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"anthropic-beta: unknown value"}}"""),
            })
            .EnqueueSse(Answer());
        var provider = new AnthropicProvider(
            Subscription(),
            handler.CreateClient(),
            runtimeBuild: () => "da9",
            runtimeBetas: () => AnthropicWire.Betas,
            options: new ProviderOptions { UsageRecorder = recorder });

        await provider.CollectAsync(TestData.Request());

        Assert.Equal(2, recorder.Records.Count);
        Assert.Equal(UsageRecord.Error, recorder.Records[0].Outcome);
        Assert.Equal(400, recorder.Records[0].StatusCode);
        Assert.False(recorder.Records[0].Downgraded);
        Assert.Equal(UsageRecord.Ok, recorder.Records[1].Outcome);
        Assert.True(recorder.Records[1].Downgraded);
    }

    [Fact]
    public async Task Nothing_is_recorded_when_the_log_is_off()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(Answer());
        var provider = new AnthropicProvider(
            Subscription(),
            handler.CreateClient(),
            runtimeBuild: () => "da9",
            runtimeBetas: () => AnthropicWire.Betas);

        await provider.CollectAsync(TestData.Request());

        // No recorder in the options: the request ran and nothing was written anywhere.
        Assert.True(handler.Calls == 1);
    }
}
