using System.Net;
using System.Text;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;
using Xunit.Sdk;

namespace Forge.App.Tests;

/// <summary>
/// What a rejected request leaves the user holding: one sentence the server actually said, with the hints that name a fix, and
/// the body it came from kept for the phrase rules and for support. Two bodies here are pasted verbatim from this app's
/// <c>turn_metrics</c> — the OpenCode gateway's opaque 400 and its deprecated-model 410.
/// </summary>
// The degradation table is process-wide: these four classes add entries and clear the whole table in Dispose, so
// they must not run beside each other (xunit v3 interleaves them, and one class's Clear() erases another's entry
// mid-test). The collection serialises them; nothing else touches ProviderDegradations.
[Collection("provider-degradations")]
public sealed class ProviderRejectionTests : IDisposable
{
    private const string GatewayInvalidRequest =
        """{"error":{"param":null,"type":"invalid_request_error","message":"Upstream request failed: [invalid_request] the request is invalid (request_id: gw-bc59b9977860-107a68)"}}""";

    private const string DeprecatedModel =
        """{"type":"error","error":{"type":"ModelDeprecated","message":"Model longcat-2.5-free has been deprecated. Use longcat-2.5 instead."},"metadata":{"model":"longcat-2.5-free","replacement":"longcat-2.5"}}""";

    private const string PromptCacheKeyNamedInParam =
        """{"error":{"type":"invalid_request_error","param":"prompt_cache_key","message":"Unknown parameter"}}""";

    private readonly string id = "opencode";

    public void Dispose() => ProviderDegradations.Clear();

    /// <summary>Serves one canned answer per attempt and keeps every request body the adapter sent.</summary>
    private sealed class Handler(Func<int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return answer(Bodies.Count);
        }
    }

    private static HttpResponseMessage Refused(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Completed() =>
        FakeSse.Ok(FakeSse.Body(
        [
            FakeSse.Frame("""{"choices":[{"delta":{"content":"ok"}}]}"""),
            FakeSse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}]}"""),
            FakeSse.Done,
        ]));

    private OpenAiCompatibleProvider Provider(Handler handler, ProviderOptions? options = null) =>
        new(new ProviderConfig(id, "openai-compatible", "https://gateway.example/v1", "k"), new HttpClient(handler), options: options);

    private static ChatRequest Request() =>
        new("sys", [], [ChatMessage.User("hi")], new ModelSettings("glm-5.3")) { CacheRoutingKey = "root-1" };

    private static async Task<ProviderException> Refusal(OpenAiCompatibleProvider provider)
    {
        try
        {
            await foreach (var _ in provider.StreamAsync(Request(), CancellationToken.None))
            {
            }
        }
        catch (ProviderException failure)
        {
            return failure;
        }

        throw new XunitException("the request was expected to be refused");
    }

    [Fact]
    public async Task A_gateway_refusal_reaches_the_user_as_the_sentence_the_gateway_wrote()
    {
        var provider = Provider(new Handler(_ => Refused(HttpStatusCode.BadRequest, GatewayInvalidRequest)));

        var failure = await Refusal(provider);

        Assert.Equal(
            "opencode returned 400 BadRequest: Upstream request failed: [invalid_request] the request is invalid"
            + " (request_id: gw-bc59b9977860-107a68)",
            failure.Message);
        Assert.Equal(GatewayInvalidRequest, failure.Data[ProviderErrors.ErrorBodyKey]);
        Assert.False(failure.Retryable);
    }

    [Fact]
    public async Task A_deprecated_model_refusal_names_the_model_to_switch_to()
    {
        var provider = Provider(new Handler(_ => Refused(HttpStatusCode.Gone, DeprecatedModel)));

        var failure = await Refusal(provider);

        Assert.Equal(
            "opencode returned 410 Gone: Model longcat-2.5-free has been deprecated. Use longcat-2.5 instead."
            + " (type: ModelDeprecated) (use: longcat-2.5)",
            failure.Message);
    }

    [Fact]
    public async Task A_refusal_that_names_the_cache_field_only_in_param_still_degrades()
    {
        // The field is named in `param` and not in the sentence: matching the readable message alone would lose the recovery this
        // adapter already had, and a turn that used to survive would start failing on a 400.
        var handler = new Handler(attempt => attempt == 1
            ? Refused(HttpStatusCode.BadRequest, PromptCacheKeyNamedInParam)
            : Completed());
        var provider = Provider(handler, new ProviderOptions { SendPromptCacheKey = true });

        await foreach (var _ in provider.StreamAsync(Request(), CancellationToken.None))
        {
        }

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("prompt_cache_key", handler.Bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_cache_key", handler.Bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_that_is_not_json_still_arrives_on_one_line()
    {
        var provider = Provider(new Handler(_ => Refused(HttpStatusCode.BadGateway, "<html>\n  <body>bad gateway</body>\n</html>")));

        var failure = await Refusal(provider);

        Assert.Equal("opencode returned 502 BadGateway: <html> <body>bad gateway</body> </html>", failure.Message);
    }
}
