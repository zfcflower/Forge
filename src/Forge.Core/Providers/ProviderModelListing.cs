using System.Globalization;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// What one provider's <c>/models</c> endpoint said: the model ids it serves, in the order the server listed them, and the
/// context window and prices it reported for whichever of them it knows. Metadata only holds models the server actually
/// described — a model with no reported window and no reported price simply has no entry.
/// </summary>
/// <remarks>
/// Both halves are read-only and are handed to the shell as-is, so a provider refresh can replace its whole view without
/// mutating what a running session already holds.
/// </remarks>
public sealed record ProviderModelList(
    IReadOnlyList<string> Ids,
    IReadOnlyDictionary<string, ModelMetadata> Metadata)
{
    /// <summary>No model reported: what a provider that does not support listing, or that returned none, means.</summary>
    public static ProviderModelList Empty { get; } = new([], new Dictionary<string, ModelMetadata>(StringComparer.Ordinal));
}

/// <summary>
/// Reads a provider's <c>GET {baseUrl}/models</c> listing. Discovery is the only way to learn the model ids a
/// gateway serves beyond the seeds a preset carries, so this is the one place where the app describes someone else's API.
/// </summary>
/// <remarks>
/// <para>
/// Authentication is the caller's job: it passes the headers it wants (an <c>Authorization</c> bearer, a gateway's own
/// key header) because the shape of the credential differs per provider and this class has no business knowing it.
/// </para>
/// <para>
/// The response shape is OpenAI's <c>{"data":[{"id":…}]}</c>. OpenRouter and several gateways add a context window
/// (<c>context_length</c>, <c>max_context_length</c>, or <c>top_provider.context_length</c>) and per-<em>token</em> prices
/// under <c>pricing</c>; the prices are scaled to the per-million unit the rest of the app uses. A provider that reports
/// neither is not an error — the model is simply listed with no metadata.
/// </para>
/// </remarks>
public static class ProviderModelListing
{
    /// <summary>Model discovery runs off the turn's critical path; after this long the caller is better off with the seeds.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The page size asked of a protocol whose listing paginates by default. Anthropic answers twenty rows a page, so a
    /// caller that does not say otherwise would silently lose every model past the twentieth.
    /// </summary>
    private const string WholeCatalogQuery = "limit=1000";

    /// <summary>
    /// Where one provider's model list is read from. A protocol whose message path carries a version segment lists under
    /// that same segment — Anthropic's is <c>/v1/models</c>, and its base therefore carries no version of its own — and it
    /// gets the whole catalogue in one page; every other protocol lists at its own base and answers unpaginated.
    /// </summary>
    /// <param name="protocol">The provider's wire protocol.</param>
    /// <param name="baseUrl">The provider's API root, with or without a trailing slash.</param>
    public static (string Root, string? Query) ListRequest(ProviderProtocol protocol, string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (protocol != ProviderProtocol.Anthropic)
        {
            return (trimmed, null);
        }

        // A hand-edited base that already ends with the version segment is not doubled.
        var root = trimmed.EndsWith("/v1", StringComparison.Ordinal) ? trimmed : trimmed + "/v1";
        return (root, WholeCatalogQuery);
    }

    /// <summary>The one message the caller distinguishes from a transport failure, so it names the missing array.</summary>
    private const string MissingDataMessage = "The model list response must contain a 'data' array.";

    /// <summary>Fetches and parses one provider's model list.</summary>
    /// <param name="baseUrl">The provider's API root, with or without a trailing slash; <c>/models</c> is appended.</param>
    /// <param name="headers">Headers to send (an existing header of the same name is replaced); may be null. A value that needs
    /// <c>{session}</c> is left out: a listing belongs to no session.</param>
    /// <param name="http">The client to use; its own timeout should be <see cref="Timeout.InfiniteTimeSpan"/> since the call enforces its own.</param>
    /// <param name="ct">Cancelling it cancels the call and is not reported as a timeout.</param>
    /// <param name="query">Query string for the listing (already encoded, without the <c>?</c>); <c>null</c> asks for no
    /// query at all. <see cref="ListRequest"/> says what each protocol needs.</param>
    /// <exception cref="TimeoutException">The call did not finish within twenty seconds; the caller may retry.</exception>
    /// <exception cref="JsonException">The body is not JSON or has no <c>data</c> array.</exception>
    public static async Task<ProviderModelList> FetchAsync(
        string baseUrl,
        IReadOnlyDictionary<string, string>? headers,
        HttpClient http,
        CancellationToken ct,
        string? query = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(baseUrl);

        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("The base URL is empty.", nameof(baseUrl));
        }

        var target = query is null ? trimmed + "/models" : trimmed + "/models?" + query;
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(target, UriKind.Absolute));
        if (headers is { Count: > 0 })
        {
            ProviderHeaders.Apply(message, headers, sessionId: null);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using var response = await http
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            return Read(document.RootElement);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out while fetching the provider model list.", ex);
        }
    }

    private static ProviderModelList Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException(MissingDataMessage);
        }

        var ids = new List<string>(data.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal);

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || idElement.GetString() is not { } id)
            {
                continue;
            }

            var name = id.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (seen.Add(name))
            {
                ids.Add(name);
            }

            if (ReadMetadata(item) is { IsKnown: true } reported)
            {
                metadata[name] = reported;
            }
        }

        return new ProviderModelList(ids, metadata);
    }

    /// <summary>
    /// The metadata a provider reported for one model. Only fields that are actually present are read, because the shape
    /// differs per gateway; anything absent stays unknown instead of becoming a zero.
    /// </summary>
    private static ModelMetadata? ReadMetadata(JsonElement item)
    {
        var window = PositiveInt(item, "context_length")
            ?? PositiveInt(item, "max_context_length")
            ?? (item.TryGetProperty("top_provider", out var top) && top.ValueKind == JsonValueKind.Object
                ? PositiveInt(top, "context_length")
                : null);

        ModelPricing? pricing = null;
        if (item.TryGetProperty("pricing", out var prices) && prices.ValueKind == JsonValueKind.Object)
        {
            var input = PerMillion(prices, "prompt");
            var output = PerMillion(prices, "completion");
            if (input is not null || output is not null)
            {
                pricing = new ModelPricing(
                    input ?? 0,
                    output ?? 0,
                    PerMillion(prices, "input_cache_read") ?? 0,
                    PerMillion(prices, "input_cache_write") ?? 0);
            }
        }

        if (window is null && pricing is null)
        {
            return null;
        }

        return new ModelMetadata(window, pricing);
    }

    private static int? PositiveInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) && number > 0 => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0 => parsed,
            _ => null,
        };
    }

    /// <summary>Providers quote prices per token; the app displays and sums per one million tokens.</summary>
    private static double? PerMillion(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        var perToken = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => double.NaN,
        };

        return double.IsFinite(perToken) && perToken >= 0 ? perToken * 1_000_000d : null;
    }
}
