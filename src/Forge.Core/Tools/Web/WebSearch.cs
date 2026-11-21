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

/// <summary>Shared request/response plumbing of the backends.</summary>
internal static class SearchHttp
{
    /// <summary>The response body, or a <see cref="WebSearchException"/> naming the backend and the HTTP failure.</summary>
    public static async Task<string> SendTextAsync(HttpClient http, HttpRequestMessage request, string label, CancellationToken ct)
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

            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
    }

    public static async Task<JsonDocument> SendAsync(HttpClient http, HttpRequestMessage request, string label, CancellationToken ct)
    {
        var text = await SendTextAsync(http, request, label, ct).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new WebSearchException($"{label} returned a response that is not valid JSON.");
        }
    }

    /// <summary>A JSON body, either written as-is or the <c>data:</c> payloads of a server-sent-events reply. The answer is
    /// the first payload that parses to an object with <c>result</c> or <c>error</c>, so a keep-alive event before the
    /// answer is skipped instead of breaking the parse.</summary>
    public static JsonDocument ParseJsonOrSse(string text, string label)
    {
        if (text.TrimStart().StartsWith('{'))
        {
            return Parse(text, label);
        }

        var payloads = new List<string>();
        var current = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0)
            {
                if (current.Length > 0)
                {
                    payloads.Add(current.ToString());
                    current.Clear();
                }
            }
            else if (trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                current.Append(trimmed.AsSpan(5));
            }
        }

        if (current.Length > 0)
        {
            payloads.Add(current.ToString());
        }

        foreach (var payload in payloads)
        {
            try
            {
                var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    (doc.RootElement.TryGetProperty("result", out _) || doc.RootElement.TryGetProperty("error", out _)))
                {
                    return doc;
                }

                doc.Dispose();
            }
            catch (JsonException)
            {
                // A keep-alive or comment payload is not this call's answer.
            }
        }

        throw new WebSearchException(payloads.Count == 0
            ? $"{label} returned an empty response."
            : $"{label} returned a response that is not valid JSON.");
    }

    private static JsonDocument Parse(string json, string label)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new WebSearchException($"{label} returned a response that is not valid JSON.");
        }
    }

    public static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Up to <paramref name="count"/> items with a URL, reading <paramref name="snippetField"/> and, when that is
    /// empty, <paramref name="fallbackSnippetField"/>.</summary>
    public static List<WebSearchResult> Collect(JsonElement array, string snippetField, int count, string? fallbackSnippetField = null)
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

            var snippet = Str(item, snippetField);
            if (snippet.Length == 0 && fallbackSnippetField is not null)
            {
                snippet = Str(item, fallbackSnippetField);
            }

            list.Add(new WebSearchResult(
                HtmlToText.ToPlain(Str(item, "title")),
                url,
                HtmlToText.ToPlain(snippet)));
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

/// <summary>
/// Exa: with a key the REST <c>POST /search</c> answers clean JSON; without one the keyless public MCP endpoint answers a
/// text listing of <c>Title:/URL:/…/Highlights:</c> blocks. The keyless path is what makes Exa the backend that works with
/// nothing configured, so it must stay usable with no credential at all (it is anonymous and rate-limited).
/// </summary>
public sealed class ExaSearchBackend(HttpClient http, string? apiKey) : IWebSearchBackend
{
    public const string McpUrl = "https://mcp.exa.ai/mcp";
    public const string RestUrl = "https://api.exa.ai/search";

    /// <summary>Exa's highlights are long; the model gets at most this much of one.</summary>
    private const int SnippetLimit = 600;

    public string Id => "exa";

    public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(apiKey) ? SearchMcpAsync(query, count, ct) : SearchRestAsync(query, count, ct);

    private async Task<IReadOnlyList<WebSearchResult>> SearchRestAsync(string query, int count, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RestUrl);
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Content = JsonContent(query, count);
        using var doc = await SearchHttp.SendAsync(http, request, "Exa", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var results)
            ? SearchHttp.Collect(results, "text", count, "summary")
            : [];
    }

    private async Task<IReadOnlyList<WebSearchResult>> SearchMcpAsync(string query, int count, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, McpUrl);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Content = McpCallContent(query, count);
        var text = await SearchHttp.SendTextAsync(http, request, "Exa", ct).ConfigureAwait(false);
        using var doc = SearchHttp.ParseJsonOrSse(text, "Exa");
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new WebSearchException("Exa returned a response that is not an object.");
        }

        if (root.TryGetProperty("error", out var error))
        {
            throw new WebSearchException("Exa: " + SearchHttp.Str(error, "message"));
        }

        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            throw new WebSearchException("Exa returned a response without a result.");
        }

        if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            throw new WebSearchException("Exa: " + FirstText(result));
        }

        return ParseBlocks(FirstText(result), count);
    }

    private static string FirstText(JsonElement result) =>
        result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0
            ? SearchHttp.Str(content[0], "text")
            : string.Empty;

    /// <summary>The MCP reply: blocks of <c>Title:</c>, <c>URL:</c>, <c>Published:</c>, <c>Author:</c>, <c>Highlights:</c>,
    /// separated by a <c>---</c> line. Only the title, the URL and the highlights are kept.</summary>
    private static List<WebSearchResult> ParseBlocks(string text, int count)
    {
        var list = new List<WebSearchResult>();
        var title = string.Empty;
        var url = string.Empty;
        var snippet = new StringBuilder();
        var inHighlights = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line == "---")
            {
                Flush();
            }
            else if (line.StartsWith("Title:", StringComparison.Ordinal))
            {
                Flush();
                title = line[6..].Trim();
            }
            else if (line.StartsWith("URL:", StringComparison.Ordinal))
            {
                url = line[4..].Trim();
            }
            else if (line.Trim() == "Highlights:")
            {
                inHighlights = true;
            }
            else if (inHighlights)
            {
                snippet.Append(line).Append(' ');
            }
        }

        Flush();
        return list;

        void Flush()
        {
            if (url.Length > 0 && list.Count < count)
            {
                var body = snippet.ToString().Trim();
                list.Add(new WebSearchResult(
                    HtmlToText.ToPlain(title),
                    url,
                    body.Length > SnippetLimit ? body[..SnippetLimit] : body));
            }

            title = string.Empty;
            url = string.Empty;
            snippet.Clear();
            inHighlights = false;
        }
    }

    private static StreamContent JsonContent(string query, int count)
    {
        var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("query", query);
            writer.WriteNumber("numResults", count);
            writer.WriteStartObject("contents");
            writer.WriteStartObject("text");
            writer.WriteNumber("maxCharacters", SnippetLimit);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        body.Position = 0;
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static StreamContent McpCallContent(string query, int count)
    {
        var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", 1);
            writer.WriteString("method", "tools/call");
            writer.WriteStartObject("params");
            writer.WriteString("name", "web_search_exa");
            writer.WriteStartObject("arguments");
            writer.WriteString("query", query);
            writer.WriteString("objective", "Find the pages that best answer: " + query);

            // The tool caps the default at 5 and the model at 10; Exa accepts any positive count.
            writer.WriteNumber("numResults", count);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        body.Position = 0;
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
}

/// <summary>DeepSeek Search API: <c>POST /v1/tools/search</c> with the key as a Bearer token; the long <c>summary</c> is the
/// snippet (it is written for a model), the short <c>snippet</c> the fallback.</summary>
public sealed class DeepSeekSearchBackend(HttpClient http, string apiKey, string endpoint = "https://api.deepseek.com/v1/tools/search") : IWebSearchBackend
{
    public string Id => "deepseek";

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("query", query);
            writer.WriteNumber("count", Math.Clamp(count, 1, 50));
            writer.WriteBoolean("need_content", false);
            writer.WriteEndObject();
        }

        body.Position = 0;
        request.Content = new StreamContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var doc = await SearchHttp.SendAsync(http, request, "DeepSeek Search", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var results)
            ? SearchHttp.Collect(results, "summary", count, "snippet")
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
        "no search backend is configured. Ask the user to choose one (Exa, Tavily, DeepSeek, Brave or SearXNG) in Settings -> Web tools.";

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
