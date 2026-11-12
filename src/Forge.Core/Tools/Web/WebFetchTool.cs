using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Net;

namespace Forge.Core.Tools.Web;

/// <summary>Limits and switches of <see cref="WebFetchTool"/>.</summary>
public sealed class WebFetchOptions
{
    /// <summary>Read on every call, so a settings change applies at once. Default: private addresses are refused.</summary>
    public Func<bool> AllowPrivateNetwork { get; init; } = static () => false;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public long MaxBodyBytes { get; init; } = 5 * 1024 * 1024;

    public int MaxRedirects { get; init; } = 5;

    /// <summary>
    /// True when a request to the URL will be dialled directly by a handler whose
    /// <see cref="NetworkHandlerOptions.AddressFilter"/> already refuses private addresses (read per hop). The resolve-only
    /// pre-check is then skipped: the connect-time filter checks the very address that is dialled. Null (the default)
    /// always pre-checks, which is the only check when the request goes through a proxy.
    /// </summary>
    public Func<Uri, bool>? ConnectsDirectly { get; init; }
}

/// <summary>
/// <c>web_fetch</c>: downloads one http(s) page and returns it as readable text. Redirects are followed by hand so every
/// hop passes the same scheme and address checks. The <see cref="HttpClient"/> handed in must not follow redirects itself.
/// <para>The private-network check has two forms. On a direct connection built by <see cref="NetworkStack"/> the handler's
/// address filter vets the address actually dialled (DNS rebinding cannot slip past it) and
/// <see cref="WebFetchOptions.ConnectsDirectly"/> skips the pre-check; through a proxy, the proxy resolves the target, so
/// the tool resolves it beforehand and refuses private answers — a best effort a rebinding answer between the two lookups
/// can still evade.</para>
/// </summary>
public sealed class WebFetchTool : ITool
{
    public const string Name = "web_fetch";

    private const string SchemaJson =
        """{"type":"object","properties":{"url":{"type":"string","description":"Absolute http or https URL."},"maxChars":{"type":"integer","minimum":1,"description":"Truncate the returned text to this many characters."}},"required":["url"],"additionalProperties":false}""";

    private const string Description =
        "Fetch a web page over http or https and return its readable text (HTML is converted to Markdown-style text; JSON and plain text are returned as-is). Binary content is refused, as are loopback and private-network addresses. Use it to read documentation or a URL the user gave.";

    private readonly HttpClient http;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolve;
    private readonly WebFetchOptions options;

    static WebFetchTool() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public WebFetchTool(
        HttpClient http,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        WebFetchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
        this.resolve = resolve ?? Dns.GetHostAddressesAsync;
        this.options = options ?? new WebFetchOptions();
        Definition = new ToolDefinition(Name, Description, SchemaJson);
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        string? urlText;
        int maxChars = 0;
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            urlText = doc.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            if (doc.RootElement.TryGetProperty("maxChars", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var mc))
            {
                maxChars = Math.Max(0, mc);
            }
        }
        catch (JsonException)
        {
            return Error("the arguments are not valid JSON.");
        }

        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri))
        {
            return Error("url must be an absolute http or https URL.");
        }

        using var timeout = new CancellationTokenSource(options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await FetchAsync(uri, maxChars, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Error($"timed out after {options.Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException exception) when (NetworkAddressRefusedException.Find(exception) is { } refused)
        {
            return Error(PrivateRefusal(refused.Host, refused.Address));
        }
        catch (HttpRequestException exception)
        {
            return Error("request failed: " + exception.Message);
        }
        catch (FetchRefusedException exception)
        {
            return Error(exception.Message);
        }
    }

    private async Task<ToolResult> FetchAsync(Uri start, int maxChars, CancellationToken ct)
    {
        var current = start;
        for (var hop = 0; ; hop++)
        {
            await GuardAsync(current, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("Forge/1.0 (web_fetch)");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain,application/json;q=0.9,*/*;q=0.5");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                if (location is null)
                {
                    return Error($"HTTP {status} without a Location header.");
                }

                if (hop >= options.MaxRedirects)
                {
                    return Error($"too many redirects (more than {options.MaxRedirects}).");
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return Error($"HTTP {status} {response.ReasonPhrase}".TrimEnd() + $" for {current}.");
            }

            return await ReadResponseAsync(response, current, maxChars, ct).ConfigureAwait(false);
        }
    }

    private async Task<ToolResult> ReadResponseAsync(HttpResponseMessage response, Uri final, int maxChars, CancellationToken ct)
    {
        var headers = response.Content.Headers;
        if (headers.ContentLength is { } declared && declared > options.MaxBodyBytes)
        {
            return TooLarge();
        }

        byte[] body;
        await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            var read = await ReadLimitedAsync(stream, options.MaxBodyBytes, ct).ConfigureAwait(false);
            if (read is null)
            {
                return TooLarge();
            }

            body = read;
        }

        var mediaType = headers.ContentType?.MediaType?.ToLowerInvariant();
        var kind = Classify(mediaType, body);
        if (kind == ContentKind.Binary)
        {
            return Error($"binary content not supported ({mediaType ?? "unknown content type"}).");
        }

        var text = Decode(body, headers.ContentType?.CharSet);
        string? title = null;
        if (kind == ContentKind.Html)
        {
            text = HtmlToText.Convert(text, final, out title);
        }

        if (maxChars > 0 && text.Length > maxChars)
        {
            text = string.Concat(text.AsSpan(0, maxChars), $"\n\n[truncated: showing {maxChars} of {text.Length} characters]");
        }

        var sb = new StringBuilder();
        sb.Append("URL: ").Append(final.AbsoluteUri).Append('\n');
        if (!string.IsNullOrEmpty(title))
        {
            sb.Append("Title: ").Append(title).Append('\n');
        }

        sb.Append('\n').Append(text);
        return new ToolResult(sb.ToString());
    }

    private ToolResult TooLarge() => Error($"response is larger than the {options.MaxBodyBytes / (1024 * 1024)} MB limit.");

    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, long limit, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var output = new MemoryStream();
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (output.Length + n > limit)
                {
                    return null;
                }

                output.Write(buffer, 0, n);
            }

            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private enum ContentKind { Html, Text, Binary }

    private static ContentKind Classify(string? mediaType, byte[] body)
    {
        if (mediaType is null)
        {
            var head = body.AsSpan(0, Math.Min(body.Length, 512));
            if (head.IndexOf((byte)0) >= 0)
            {
                return ContentKind.Binary;
            }

            var sniff = Encoding.UTF8.GetString(head).TrimStart();
            return sniff.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || sniff.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                ? ContentKind.Html
                : ContentKind.Text;
        }

        if (mediaType is "text/html" or "application/xhtml+xml")
        {
            return ContentKind.Html;
        }

        if (mediaType.StartsWith("text/", StringComparison.Ordinal) ||
            mediaType is "application/json" or "application/xml" or "application/javascript" or "application/x-yaml" or "application/yaml" ||
            mediaType.EndsWith("+json", StringComparison.Ordinal) || mediaType.EndsWith("+xml", StringComparison.Ordinal))
        {
            return ContentKind.Text;
        }

        return ContentKind.Binary;
    }

    private static string Decode(byte[] body, string? charset)
    {
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"', ' '));
            }
            catch (ArgumentException)
            {
                encoding = Encoding.UTF8;
            }
        }

        return encoding.GetString(body);
    }

    private async Task GuardAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new FetchRefusedException($"only http and https URLs are supported (got '{uri.Scheme}').");
        }

        if (options.AllowPrivateNetwork() || options.ConnectsDirectly?.Invoke(uri) == true)
        {
            return;
        }

        var host = uri.DnsSafeHost;
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolve(host, ct).ConfigureAwait(false);
            }
            catch (SocketException exception)
            {
                throw new FetchRefusedException($"could not resolve host '{host}': {exception.Message}");
            }
        }

        if (addresses.Length == 0)
        {
            throw new FetchRefusedException($"could not resolve host '{host}'.");
        }

        foreach (var address in addresses)
        {
            if (IsPrivate(address))
            {
                throw new FetchRefusedException(PrivateRefusal(host, address));
            }
        }
    }

    /// <summary>Loopback, link-local, private, CGNAT, multicast and unspecified addresses (IPv4-mapped IPv6 included).</summary>
    public static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        Span<byte> b = stackalloc byte[16];
        if (!address.TryWriteBytes(b, out var length))
        {
            return true;
        }

        if (length == 4)
        {
            return b[0] == 0 || b[0] == 10 || b[0] >= 224 ||
                   (b[0] == 100 && b[1] is >= 64 and <= 127) ||
                   (b[0] == 169 && b[1] == 254) ||
                   (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                   (b[0] == 192 && b[1] == 168);
        }

        return (b[0] & 0xFE) == 0xFC ||
               (b[0] == 0xFE && (b[1] & 0xC0) is 0x80 or 0xC0) ||
               b[0] == 0xFF;
    }

    private static string PrivateRefusal(string host, IPAddress address) =>
        $"refused: host '{host}' resolves to a loopback, link-local or private address ({address}). Enable \"allow private network\" in the Web tools settings to fetch it.";

    private static ToolResult Error(string message) => new($"{Name} failed: {message}", IsError: true);

    private sealed class FetchRefusedException(string message) : Exception(message);
}
