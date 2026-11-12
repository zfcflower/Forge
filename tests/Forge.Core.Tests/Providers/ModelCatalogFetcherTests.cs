using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// Pins the catalog fetch: a conditional GET (the stored ETag goes back as <c>If-None-Match</c>, a 304 means "keep what you
/// have"), a fetch timestamp the caller controls, and failures as messages rather than exceptions.
/// </summary>
public sealed class ModelCatalogFetcherTests
{
    private const string Payload = """
    {"anthropic":{"id":"anthropic","models":{"claude-sonnet-4-5":{"limit":{"context":200000,"output":64000}}}}}
    """;

    private static readonly DateTimeOffset Now = new(2026, 11, 12, 5, 0, 0, TimeSpan.Zero);

    private static HttpResponseMessage Json(string body, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    [Fact]
    public async Task A_fresh_catalog_becomes_a_snapshot_stamped_with_the_callers_clock()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => Json(Payload, "\"v1\""));

        var result = await ModelCatalogFetcher.FetchAsync(
            handler.CreateClient(),
            "https://models.dev/api.json",
            etag: null,
            Now,
            ct: CancellationToken.None);

        Assert.Equal(ModelCatalogFetchStatus.Updated, result.Status);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(Now, result.Snapshot!.FetchedAt);
        Assert.Equal("\"v1\"", result.Snapshot.ETag);
        Assert.Equal(200_000, result.Snapshot.Providers["anthropic"]["claude-sonnet-4-5"].ContextWindow);
        Assert.Equal("application/json", handler.Last.Header("Accept"));
    }

    [Fact]
    public async Task The_stored_etag_is_sent_back_and_a_304_keeps_the_snapshot_on_disk()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => new HttpResponseMessage(HttpStatusCode.NotModified));

        var result = await ModelCatalogFetcher.FetchAsync(
            handler.CreateClient(),
            "https://models.dev/api.json",
            "\"v1\"",
            Now,
            ct: CancellationToken.None);

        Assert.Equal(ModelCatalogFetchStatus.NotModified, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Equal("\"v1\"", handler.Last.Header("If-None-Match"));
    }

    [Fact]
    public async Task No_validator_means_no_conditional_header()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => Json(Payload));

        await ModelCatalogFetcher.FetchAsync(
            handler.CreateClient(),
            "https://models.dev/api.json",
            etag: null,
            Now,
            ct: CancellationToken.None);

        Assert.Null(handler.Last.Header("If-None-Match"));
    }

    [Theory]
    [InlineData("https://models.dev/missing", HttpStatusCode.NotFound, "404")]
    [InlineData("https://models.dev/broken", HttpStatusCode.InternalServerError, "500")]
    public async Task A_failed_status_is_reported_with_its_code(string url, HttpStatusCode status, string expected)
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(status);

        var result = await ModelCatalogFetcher.FetchAsync(
            handler.CreateClient(),
            url,
            etag: null,
            Now,
            ct: CancellationToken.None);

        Assert.Equal(ModelCatalogFetchStatus.Failed, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Contains(expected, result.Message!);
    }

    [Fact]
    public async Task A_body_that_is_not_the_catalog_shape_fails_without_throwing()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => Json("{\"unexpected\":true}"));

        var result = await ModelCatalogFetcher.FetchAsync(
            handler.CreateClient(),
            "https://models.dev/api.json",
            etag: null,
            Now,
            ct: CancellationToken.None);

        Assert.Equal(ModelCatalogFetchStatus.Failed, result.Status);
        Assert.Contains("no models", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("models.dev/api.json")]
    [InlineData("file:///tmp/models.json")]
    public async Task An_address_that_is_not_an_absolute_http_url_is_refused_before_any_request(string url)
    {
        var handler = new FakeHttpMessageHandler();

        var result = await ModelCatalogFetcher.FetchAsync(
            handler.CreateClient(),
            url,
            etag: null,
            Now,
            ct: CancellationToken.None);

        Assert.Equal(ModelCatalogFetchStatus.Failed, result.Status);
        Assert.Contains("http", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_refused_without_content_length()
    {
        // A chunked answer (no Content-Length) must be bounded while reading, not after it has become one big array.
        var handler = new FakeHttpMessageHandler().Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ChunkedContent(Encoding.UTF8.GetBytes(Payload + Payload + Payload)),
        });

        var result = await ModelCatalogFetcher.FetchAsync(
            handler.CreateClient(),
            "https://models.dev/api.json",
            etag: null,
            Now,
            ct: CancellationToken.None,
            maxBytes: 64);

        Assert.Equal(ModelCatalogFetchStatus.Failed, result.Status);
        Assert.Contains("larger than", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_transport_error_is_a_message()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => throw new HttpRequestException("connection refused"));

        var result = await ModelCatalogFetcher.FetchAsync(
            handler.CreateClient(),
            "https://models.dev/api.json",
            etag: null,
            Now,
            ct: CancellationToken.None);

        Assert.Equal(ModelCatalogFetchStatus.Failed, result.Status);
        Assert.Contains("connection refused", result.Message!);
    }

    /// <summary>A body with no declared length, like a chunked or streaming answer.</summary>
    private sealed class ChunkedContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(body, 0, body.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
