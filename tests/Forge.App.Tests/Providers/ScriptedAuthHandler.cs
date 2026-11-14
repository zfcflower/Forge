using System.Net;
using System.Text;

namespace Forge.App.Tests.Providers;

/// <summary>
/// The HTTP double the provider sign-in flows run against: a queue of canned answers that repeats its last entry once the
/// queue is exhausted, plus a record of every request that reached it. Each provider keeps its own record type with the
/// parts of the wire shape its tests assert.
/// </summary>
internal abstract class ScriptedAuthHandler<TRequest> : HttpMessageHandler
    where TRequest : class
{
    private readonly List<(HttpStatusCode Status, string Body)> queued = [];

    public List<TRequest> Requests { get; } = [];

    /// <summary>Runs just before the answer goes back, so a test can change the store while a request is on the wire.</summary>
    public Action<TRequest>? BeforeResponding { get; set; }

    /// <summary>When set, every answer waits for this task, so a test can keep a request on the wire as long as it needs.</summary>
    public Task? Hold { get; set; }

    public ScriptedAuthHandler<TRequest> Then(HttpStatusCode status, string body)
    {
        queued.Add((status, body));
        return this;
    }

    /// <summary>What to remember about one request.</summary>
    protected abstract TRequest Record(HttpRequestMessage request, string body);

    /// <summary>The media type of the answer; a ChatGPT poll answer pulled off the event stream is not JSON.</summary>
    protected virtual string ContentTypeFor(string body) => "application/json";

    protected sealed override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(Record(request, body));
        BeforeResponding?.Invoke(Requests[^1]);
        if (Hold is { } hold)
        {
            await hold.ConfigureAwait(false);
        }

        var (status, responseBody) = queued[Math.Min(Requests.Count - 1, queued.Count - 1)];
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, ContentTypeFor(responseBody)),
        };
    }
}
