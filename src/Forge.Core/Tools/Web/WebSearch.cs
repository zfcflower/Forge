using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Tools.Web;

public sealed record WebSearchResult(string Title, string Url, string Snippet);

/// <summary>A search failure with a message fit for the model (never contains the API key).</summary>
public sealed class WebSearchException(string message) : Exception(message);

public interface IWebSearchBackend
{
    string Id { get; }

    Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct);
}

/// <summary>Shared request/response plumbing of the three backends.</summary>
internal static class SearchHttp
{
    public static async Task<JsonDocument> SendAsync(HttpClient http, HttpRequestMessage request, string label, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new WebSearchException($"{label} request failed: {exception.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new WebSearchException($"{label} returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd() + ".");
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                throw new WebSearchException($"{label} returned a response that is not valid JSON.");
            }
        }
    }

    public static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    public static List<WebSearchResult> Collect(JsonElement array, string snippetField, int count)
    {
        var list = new List<WebSearchResult>();
        if (array.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in array.EnumerateArray())
        {
            var url = Str(item, "url");
            if (url.Length == 0)
            {
                continue;
            }

            list.Add(new WebSearchResult(
                HtmlToText.ToPlain(Str(item, "title")),
                url,
                HtmlToText.ToPlain(Str(item, snippetField))));
            if (list.Count >= count)
            {
                break;
            }
        }

        return list;
    }
}

/// <summary>Brave Search API: GET with the key in <c>X-Subscription-Token</c>.</summary>
public sealed class BraveSearchBackend(HttpClient http, string apiKey, string endpoint = "https://api.search.brave.com/res/v1/web/search") : IWebSearchBackend
{
    public string Id => "brave";

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}?q={Uri.EscapeDataString(query)}&count={count}");
        request.Headers.TryAddWithoutValidation("X-Subscription-Token", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        using var doc = await SearchHttp.SendAsync(http, request, "Brave Search", ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("web", out var web) && web.TryGetProperty("results", out var results)
            ? SearchHttp.Collect(results, "description", count)
            : [];
    }
}

/// <summary>
/// Tavily: <c>POST /search</c>. Authentication is isolated in <see cref="Authorize"/>: Tavily's current API takes the key as
/// a Bearer token (older versions took <c>api_key</c> in the body).
/// </summary>
public sealed class TavilySearchBackend(HttpClient http, string apiKey, string endpoint = "https://api.tavily.com/search") : IWebSearchBackend
{
    public string Id => "tavily";

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        Authorize(request);
        var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("query", query);
            writer.WriteNumber("max_results", count);
            writer.WriteString("search_depth", "basic");
            writer.WriteEndObject();
        }

        body.Position = 0;
        request.Content = new StreamContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var doc = await SearchHttp.SendAsync(http, request, "Tavily", ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("results", out var results)
            ? SearchHttp.Collect(results, "content", count)
            : [];
    }

    private void Authorize(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
}

/// <summary>SearXNG: <c>{baseUrl}/search?q=..&amp;format=json</c>, no key. The instance must enable the json format.</summary>
public sealed class SearxngSearchBackend(HttpClient http, string baseUrl) : IWebSearchBackend
{
    public string Id => "searxng";

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct)
    {
        var root = baseUrl.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{root}/search?q={Uri.EscapeDataString(query)}&format=json");
        request.Headers.Accept.ParseAdd("application/json");
        using var doc = await SearchHttp.SendAsync(http, request, "SearXNG", ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("results", out var results)
            ? SearchHttp.Collect(results, "content", count)
            : [];
    }
}

/// <summary><c>web_search</c>: a numbered list of results from the configured backend.</summary>
public sealed class WebSearchTool : ITool
{
    public const string Name = "web_search";

    private const string SchemaJson =
        """{"type":"object","properties":{"query":{"type":"string"},"count":{"type":"integer","minimum":1,"maximum":10,"description":"Number of results, default 5."}},"required":["query"],"additionalProperties":false}""";

    private const string Description =
        "Search the web and return a numbered list of results (title, URL, snippet). Follow up with web_fetch to read a result. If it reports that no search backend is configured, tell the user instead of retrying.";

    /// <summary>What a call returns when no backend is configured (the model is told where the user sets one).</summary>
    public const string NotConfiguredMessage =
        "no search backend is configured. Ask the user to choose one (Brave, Tavily or SearXNG) in Settings -> Web tools.";

    private readonly Func<IWebSearchBackend?> backend;
    private readonly TimeSpan timeout;

    /// <summary>A tool bound to one backend for its whole life.</summary>
    public WebSearchTool(IWebSearchBackend backend, TimeSpan? timeout = null)
        : this(Bind(backend), timeout)
    {
    }

    /// <summary>
    /// A tool that asks <paramref name="backend"/> for the backend on every call, so a settings change (backend, key, URL)
    /// applies to the next search of every running session; null means none is configured and the call fails with
    /// <see cref="NotConfiguredMessage"/>.
    /// </summary>
    public WebSearchTool(Func<IWebSearchBackend?> backend, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        this.backend = backend;
        this.timeout = timeout ?? TimeSpan.FromSeconds(30);
        Definition = new ToolDefinition(Name, Description, SchemaJson);
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        string? query;
        var count = 5;
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            query = doc.RootElement.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null;
            if (doc.RootElement.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n))
            {
                count = Math.Clamp(n, 1, 10);
            }
        }
        catch (JsonException)
        {
            return Error("the arguments are not valid JSON.");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return Error("query must not be empty.");
        }

        if (backend() is not { } current)
        {
            return Error(NotConfiguredMessage);
        }

        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        IReadOnlyList<WebSearchResult> results;
        try
        {
            results = await current.SearchAsync(query, count, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Error($"timed out after {timeout.TotalSeconds:0} seconds.");
        }
        catch (WebSearchException exception)
        {
            return Error(exception.Message);
        }
        catch (HttpRequestException exception)
        {
            return Error("request failed: " + exception.Message);
        }

        return new ToolResult(Format(results));
    }

    /// <summary>The numbered list: <c>1. title — url</c> then the snippet on an indented line.</summary>
    public static string Format(IReadOnlyList<WebSearchResult> results)
    {
        if (results.Count == 0)
        {
            return "No results.";
        }

        var sb = new StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.Append(i + 1).Append(". ").Append(r.Title).Append(" — ").Append(r.Url).Append('\n');
            if (r.Snippet.Length > 0)
            {
                sb.Append("   ").Append(r.Snippet).Append('\n');
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static Func<IWebSearchBackend?> Bind(IWebSearchBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        return () => backend;
    }

    private static ToolResult Error(string message) => new($"{Name} failed: {message}", IsError: true);
}
