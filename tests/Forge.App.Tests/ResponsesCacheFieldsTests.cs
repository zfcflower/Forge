using System.Net;
using System.Text;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests;

public sealed class ResponsesCacheFieldsTests : IDisposable
{
    private const string Completed =
        "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n";

    private readonly string id = "resp-" + Guid.NewGuid().ToString("n");

    public void Dispose() => ProviderDegradations.Clear();

    private sealed class Handler(Func<int, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            return answer(Bodies.Count, body);
        }
    }

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK) { Content = new StringContent(Completed, Encoding.UTF8, "text/event-stream") };

    private static HttpResponseMessage Refuse(string field) =>
        new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent($$$"""{"error":{"message":"Unknown parameter: '{{{field}}}'."}}""", Encoding.UTF8, "application/json"),
        };

    private OpenAiResponsesProvider Provider(Handler handler, string baseUrl, bool key, bool retention) =>
        new(
            new ProviderConfig(id, "openai-responses", baseUrl, "k"),
            new HttpClient(handler),
            options: new ProviderOptions { SendPromptCacheKey = key, SendPromptCacheRetention = retention });

    private static ChatRequest Request(string? ttl = null) =>
        new("sys", [], [ChatMessage.User("hi")], new ModelSettings("m"), ttl is null ? null : new CacheHint(true, Ttl: ttl))
        {
            CacheRoutingKey = "root-1",
        };

    private static async Task<List<ChatStreamEvent>> Drain(OpenAiResponsesProvider provider, ChatRequest request)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var evt in provider.StreamAsync(request, CancellationToken.None))
        {
            events.Add(evt);
        }

        return events;
    }

    [Fact]
    public async Task Key_and_retention_are_sent_only_when_the_options_allow_them()
    {
        var on = new Handler((_, _) => Ok());
        await Drain(Provider(on, "https://api.openai.com/v1", key: true, retention: true), Request("5m"));
        Assert.Contains("\"prompt_cache_key\":\"root-1\"", on.Bodies[0]);
        Assert.Contains("\"prompt_cache_retention\":\"in_memory\"", on.Bodies[0]);

        var off = new Handler((_, _) => Ok());
        await Drain(Provider(off, "https://api.openai.com/v1", key: false, retention: false), Request());
        Assert.DoesNotContain("prompt_cache", off.Bodies[0]);
    }

    [Fact]
    public async Task Nothing_cache_shaped_goes_to_deepseek()
    {
        var handler = new Handler((_, _) => Ok());
        await Drain(Provider(handler, "https://api.deepseek.com", key: true, retention: true), Request());
        Assert.DoesNotContain("prompt_cache", handler.Bodies[0]);
    }

    [Fact]
    public async Task A_gateway_that_refuses_the_cache_fields_gets_one_resend_without_them_and_is_remembered()
    {
        var handler = new Handler((n, _) => n == 1 ? Refuse("prompt_cache_key") : Ok());
        var provider = Provider(handler, "https://gateway.example/v1", key: true, retention: true);

        var events = await Drain(provider, Request());

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("prompt_cache_key", handler.Bodies[0]);
        Assert.DoesNotContain("prompt_cache", handler.Bodies[1]);
        Assert.Contains(events, e => e is MessageCompleted { Notice: not null });

        // A new provider for the same endpoint starts out without the fields: the refusal is remembered for the process.
        var next = new Handler((_, _) => Ok());
        await Drain(Provider(next, "https://gateway.example/v1", key: true, retention: true), Request());
        Assert.DoesNotContain("prompt_cache", next.Bodies[0]);
    }

    [Fact]
    public async Task A_400_that_is_not_about_the_cache_fields_is_not_retried()
    {
        var handler = new Handler((_, _) => Refuse("temperature"));
        var provider = Provider(handler, "https://gateway.example/v1", key: true, retention: false);

        await Assert.ThrowsAsync<ProviderException>(() => Drain(provider, Request()));

        Assert.Single(handler.Bodies);
    }
}
