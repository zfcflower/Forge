using System.Net;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class ProviderModelListingTests
{
    [Fact]
    public async Task Lists_ids_in_server_order_and_dedupes()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(
            HttpStatusCode.OK,
            """{"data":[{"id":"zeta"},{"id":"alpha"},{"id":"zeta"},{"id":"beta"},{"id":" alpha "},{"no_id":true},{"id":7}]}""");

        var models = await ProviderModelListing.FetchAsync(
            "https://gateway.example.test/v1",
            headers: null,
            handler.CreateClient(),
            CancellationToken.None);

        Assert.Equal(new[] { "zeta", "alpha", "beta" }, models.Ids);
        Assert.Empty(models.Metadata);
        Assert.Equal("https://gateway.example.test/v1/models", handler.Last.Uri.ToString());
    }

    [Fact]
    public async Task Scales_reported_per_token_prices_to_one_million_tokens()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(
            HttpStatusCode.OK,
            """
            {"data":[{"id":"vendor/model","context_length":200000,
              "pricing":{"prompt":"0.000003","completion":"0.000015","input_cache_read":"0.0000003","input_cache_write":"0.00000375"}}]}
            """);

        var models = await ProviderModelListing.FetchAsync("https://openrouter.ai/api/v1", null, handler.CreateClient(), CancellationToken.None);

        Assert.True(models.Metadata.TryGetValue("vendor/model", out var metadata));
        Assert.Equal(200000, metadata.ContextWindow);
        Assert.NotNull(metadata.Pricing);
        Assert.Equal(3d, metadata.Pricing!.Input, 6);
        Assert.Equal(15d, metadata.Pricing.Output, 6);
        Assert.Equal(0.3d, metadata.Pricing.CacheRead, 6);
        Assert.Equal(3.75d, metadata.Pricing.CacheWrite, 6);
    }

    [Fact]
    public async Task Reads_the_window_from_either_property_name()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(
            HttpStatusCode.OK,
            """
            {"data":[
              {"id":"long","max_context_length":"131072"},
              {"id":"nested","top_provider":{"context_length":32768}},
              {"id":"both","context_length":8000,"top_provider":{"context_length":4}},
              {"id":"nothing","owned_by":"someone"}]}
            """);

        var models = await ProviderModelListing.FetchAsync("https://api.example.test/v1", null, handler.CreateClient(), CancellationToken.None);

        Assert.Equal(131072, models.Metadata["long"].ContextWindow);
        Assert.Equal(32768, models.Metadata["nested"].ContextWindow);
        Assert.Equal(8000, models.Metadata["both"].ContextWindow);
        Assert.False(models.Metadata.ContainsKey("nothing"));
        Assert.Null(models.Metadata["long"].Pricing);
    }

    [Fact]
    public async Task Ignores_prices_that_are_not_numbers_or_are_negative()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(
            HttpStatusCode.OK,
            """{"data":[{"id":"odd","pricing":{"prompt":"free","completion":-1}}]}""");

        var models = await ProviderModelListing.FetchAsync("https://api.example.test/v1", null, handler.CreateClient(), CancellationToken.None);

        Assert.Empty(models.Metadata);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"models":[]}""")]
    [InlineData("[]")]
    public async Task A_response_without_a_data_array_is_a_json_exception(string body)
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.OK, body);

        var exception = await Assert.ThrowsAsync<JsonException>(
            () => ProviderModelListing.FetchAsync("https://api.example.test/v1", null, handler.CreateClient(), CancellationToken.None));

        Assert.Equal("The model list response must contain a 'data' array.", exception.Message);
    }

    [Fact]
    public async Task Sends_the_callers_headers_and_replaces_its_own()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.OK, """{"data":[]}""");

        var models = await ProviderModelListing.FetchAsync(
            "https://gateway.example.test/v1/",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer sk-gateway",
                ["X-Gateway-Key"] = "gw",
            },
            handler.CreateClient(),
            CancellationToken.None);

        Assert.Empty(models.Ids);
        var request = handler.Last;
        Assert.Equal("Bearer sk-gateway", request.Header("Authorization"));
        Assert.Equal("gw", request.Header("X-Gateway-Key"));
    }

    [Fact]
    public async Task A_failed_status_is_surfaced()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.Unauthorized, """{"error":"nope"}""");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => ProviderModelListing.FetchAsync("https://api.example.test/v1", null, handler.CreateClient(), CancellationToken.None));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_is_not_reported_as_a_timeout()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => throw new TaskCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProviderModelListing.FetchAsync("https://api.example.test/v1", null, handler.CreateClient(), cts.Token));
    }

    [Fact]
    public async Task An_empty_data_array_is_an_empty_list()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.OK, """{"data":[]}""");

        var models = await ProviderModelListing.FetchAsync("https://api.example.test/v1", null, handler.CreateClient(), CancellationToken.None);

        Assert.Empty(models.Ids);
        Assert.Empty(models.Metadata);
        Assert.Empty(ProviderModelList.Empty.Ids);
        Assert.Empty(ProviderModelList.Empty.Metadata);
    }
}
