using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.Core.Providers;

namespace Forge.App.Providers;

/// <summary>
/// One PKCE verifier and the challenge derived from it, both base64url (RFC 7636). The verifier is the secret half of the
/// flow, so it stays out of <see cref="ToString"/>.
/// </summary>
internal sealed record OpenRouterPkce(string Verifier, string Challenge)
{
    public override string ToString() => "OpenRouterPkce(verifier=<hidden>)";
}

/// <summary>
/// OpenRouter's sign-in: a PKCE authorization-code flow in the browser whose result is an ordinary, long-lived API key.
/// </summary>
/// <remarks>
/// There is no token pair and no refresh: the code is exchanged once for the key, and from then on the app holds a plain key in
/// the secret store exactly as if the user had pasted it. The code comes back on a loopback URL, so the flow has to listen on
/// <c>127.0.0.1</c> before the browser is opened; the request is answered with a small page and the listener is closed with the
/// sign-in. Every step is a small method of its own, so the wire shapes can be exercised without binding a port.
/// </remarks>
internal sealed class OpenRouterAuthentication
{
    /// <summary>Where the user approves the sign-in; the app opens it with the callback and the challenge on the query.</summary>
    public const string AuthorizeUrl = "https://openrouter.ai/auth";

    /// <summary>Where the authorization code is traded for the key.</summary>
    public const string KeyExchangeUrl = "https://openrouter.ai/api/v1/auth/keys";

    /// <summary>The one path the app listens on; the code arrives as its <c>code</c> query parameter.</summary>
    internal const string CallbackPath = "/oauth/callback";

    /// <summary>The whole sign-in gives up here, so a browser tab left open cannot hold the dialog forever.</summary>
    internal static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    /// <summary>A stalled exchange must not outlive the dialog's patience.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Another process can take a probed port between the probe and the bind, so a few candidates are tried.</summary>
    private const int PortAttempts = 8;

    private const int MaxErrorBodyLength = 1024;

    /// <summary>
    /// The page the browser lands on. A static string on purpose: the callback runs before the UI is involved, and an asset
    /// that failed to load would leave the user looking at nothing.
    /// </summary>
    internal const string CallbackPage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Forge</title></head>" +
        "<body style=\"font-family:sans-serif;margin:3rem\"><h1>Signed in</h1>" +
        "<p>OpenRouter is connected. You can close this tab and return to Forge.</p></body></html>";

    /// <summary>The same page for a callback that carried no code, so the browser does not look like a failure to load.</summary>
    internal const string CallbackProblemPage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Forge</title></head>" +
        "<body style=\"font-family:sans-serif;margin:3rem\"><h1>Sign-in incomplete</h1>" +
        "<p>This page carried no authorization code. Close it and start the sign-in in Forge again.</p></body></html>";

    private readonly HttpClient http;
    private readonly TimeProvider time;

    /// <param name="http">Must be the same client the API calls use, so its handler pool is shared.</param>
    /// <param name="time">Clock for the five-minute limit; tests inject one instead of waiting for a real five minutes.</param>
    public OpenRouterAuthentication(HttpClient http, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Runs one whole sign-in: it starts listening, hands <paramref name="progress"/> the page to open, waits for the
    /// browser's callback and exchanges the code for the key. The key is returned, never logged.
    /// </summary>
    /// <param name="progress">
    /// Receives the authorization URL once the listener is up, so the page cannot be opened before the app can receive the
    /// answer. Invoked synchronously on the caller's thread.
    /// </param>
    public async Task<string> SignInAsync(Action<Uri>? progress, CancellationToken ct)
    {
        var pkce = CreatePkce();
        using var listener = Listen(out var port);
        progress?.Invoke(AuthorizeUri(CallbackUrl(port), pkce.Challenge));

        var code = await WaitForCodeAsync(listener, ct).ConfigureAwait(false);
        return await ExchangeAsync(code, pkce.Verifier, ct).ConfigureAwait(false);
    }

    /// <summary>A fresh verifier (32 random bytes, base64url) and the SHA-256 challenge derived from it.</summary>
    internal static OpenRouterPkce CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new OpenRouterPkce(verifier, ChallengeOf(verifier));
    }

    /// <summary><c>BASE64URL(SHA256(ASCII(verifier)))</c>, the S256 challenge of RFC 7636.</summary>
    internal static string ChallengeOf(string verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifier);
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    /// <summary>Base64 with <c>+</c>→<c>-</c>, <c>/</c>→<c>_</c> and no padding: the only alphabet PKCE allows.</summary>
    internal static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The page to open: the callback the app listens on, the challenge, and S256 as the only method it accepts.</summary>
    internal static Uri AuthorizeUri(Uri callbackUrl, string challenge)
    {
        ArgumentNullException.ThrowIfNull(callbackUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge);
        return new Uri(
            AuthorizeUrl +
            "?callback_url=" + Uri.EscapeDataString(callbackUrl.AbsoluteUri) +
            "&code_challenge=" + Uri.EscapeDataString(challenge) +
            "&code_challenge_method=S256");
    }

    /// <summary>Where the browser comes back: the loopback address the listener was bound to, always over plain http.</summary>
    internal static Uri CallbackUrl(int port) => new($"http://127.0.0.1:{port}{CallbackPath}");

    /// <summary>
    /// Trades the authorization code for the API key. A non-2xx answer carries the server's own explanation (the status plus
    /// whichever of <c>error_description</c>, <c>message</c> or <c>error</c> the body holds); a 2xx without a key is invalid.
    /// </summary>
    public async Task<string> ExchangeAsync(string code, string verifier, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifier);

        using var message = new HttpRequestMessage(HttpMethod.Post, KeyExchangeUrl)
        {
            Content = KeyRequestContent(code, verifier),
        };
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        var (status, root, body) = await SendForJsonAsync(message, "key exchange", ct).ConfigureAwait(false);
        using var document = root;

        if (status is < 200 or >= 300)
        {
            throw new ProviderException(
                $"OpenRouter: the key exchange failed with HTTP {status}{Detail(document, body)}",
                ProviderErrors.IsRetryableStatus(status),
                status);
        }

        if (Text(document!.RootElement, "key") is not { } key)
        {
            throw new ProviderException(
                "OpenRouter: the key exchange response carried no key.",
                retryable: false,
                status);
        }

        return key;
    }

    /// <summary>
    /// Binds one loopback prefix and starts listening. The port is chosen at runtime: a fixed one would collide with whatever
    /// else the user runs, and the callback URL is only known after the bind succeeded.
    /// </summary>
    private static HttpListener Listen(out int port)
    {
        for (var attempt = 0; attempt < PortAttempts; attempt++)
        {
            var candidate = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
            try
            {
                listener.Start();
                port = candidate;
                return listener;
            }
            catch (HttpListenerException)
            {
                // The port was taken between the probe and the bind; the next candidate costs nothing.
                listener.Close();
            }
        }

        throw new ProviderException(
            "OpenRouter: no free loopback port was available for the sign-in callback.",
            retryable: true);
    }

    /// <summary>A port the OS reports as free right now; the bind above is what actually decides.</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>
    /// Waits for the one callback that carries the code. A browser may ask for a second resource on the same origin, so a
    /// request that is not the callback path is answered and ignored instead of ending the flow.
    /// </summary>
    private async Task<string> WaitForCodeAsync(HttpListener listener, CancellationToken ct)
    {
        // Linked to the caller's token as well, so cancelling the sign-in unblocks the wait instead of waiting for the limit.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var expired = Task.Delay(LoginTimeout, time, limit.Token);
        try
        {
            while (true)
            {
                var context = await NextContextAsync(listener, expired, ct).ConfigureAwait(false);
                var isCallback = string.Equals(
                    context.Request.Url?.AbsolutePath,
                    CallbackPath,
                    StringComparison.OrdinalIgnoreCase);
                if (isCallback && context.Request.QueryString["code"] is { Length: > 0 } code)
                {
                    await AnswerAsync(context.Response, found: true, ct).ConfigureAwait(false);
                    return code;
                }

                await AnswerAsync(context.Response, found: false, ct).ConfigureAwait(false);
                if (!isCallback)
                {
                    continue;
                }

                throw new ProviderException(ProviderStrings.OpenRouterNoCodeText().Resolve(), retryable: true);
            }
        }
        finally
        {
            // The five-minute timer would otherwise outlive a sign-in that already finished.
            await limit.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One pending request, the caller's cancellation and the five-minute limit racing. Both endings stop the listener, so the
    /// pending request completes instead of being left on a socket; its fault is observed because nobody awaits it.
    /// </summary>
    private static async Task<HttpListenerContext> NextContextAsync(
        HttpListener listener,
        Task expired,
        CancellationToken ct)
    {
        var pending = listener.GetContextAsync();
        if (await Task.WhenAny(pending, expired).ConfigureAwait(false) == pending)
        {
            return await pending.ConfigureAwait(false);
        }

        listener.Stop();
        _ = pending.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.ExecuteSynchronously);
        ct.ThrowIfCancellationRequested();
        throw new ProviderException(ProviderStrings.OpenRouterTimedOutText().Resolve(), retryable: true);
    }

    /// <summary>Answers the browser once and closes that response; a browser that hung up already changes nothing.</summary>
    private static async Task AnswerAsync(HttpListenerResponse response, bool found, CancellationToken ct)
    {
        using (response)
        {
            var body = Encoding.UTF8.GetBytes(found ? CallbackPage : CallbackProblemPage);
            response.StatusCode = found ? (int)HttpStatusCode.OK : (int)HttpStatusCode.BadRequest;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = body.Length;
            try
            {
                await response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or HttpListenerException or ObjectDisposedException)
            {
                // The tab was closed before the page went out; the sign-in itself does not depend on it.
            }
        }
    }

    /// <summary>
    /// The exchange body. Written with a JSON writer rather than a serializer: three fixed strings need no metadata, and
    /// nothing here is under the source generator's thumb.
    /// </summary>
    private static HttpContent KeyRequestContent(string code, string verifier)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("code_verifier", verifier);
            writer.WriteString("code_challenge_method", "S256");
            writer.WriteEndObject();
        }

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>
    /// Sends one request and reads its body once. The body comes back as text plus a parsed document (null when it is not
    /// JSON), because a failure still has to report the status and what the server said, JSON or not.
    /// </summary>
    private async Task<(int Status, JsonDocument? Root, string Body)> SendForJsonAsync(
        HttpRequestMessage message,
        string operation,
        CancellationToken ct)
    {
        // The shared client has no timeout of its own: each step gets its own, so a stalled connection cannot outlive the
        // five-minute sign-in.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await http
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("OpenRouter: the sign-in request timed out.", retryable: true);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new ProviderException(
                $"OpenRouter: the sign-in request failed: {exception.Message}",
                retryable: true,
                inner: exception);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                throw new ProviderException(
                    $"OpenRouter: the {operation} response could not be read.",
                    retryable: true,
                    status,
                    exception);
            }

            if (status is >= 200 and < 300)
            {
                try
                {
                    return (status, JsonDocument.Parse(body), string.Empty);
                }
                catch (JsonException exception)
                {
                    throw new ProviderException(
                        $"OpenRouter: the {operation} response is not JSON.",
                        retryable: false,
                        status,
                        exception);
                }
            }

            // A failure body is only shown to explain a failure; a successful one is parsed and never echoed.
            return (status, TryParse(body), body.Length <= MaxErrorBodyLength ? body : body[..MaxErrorBodyLength]);
        }
    }

    private static JsonDocument? TryParse(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The tail of a failed exchange: the server's own explanation when the body carries one, otherwise what it said
    /// verbatim. The body of a success is never printed, because it holds the key.
    /// </summary>
    private static string Detail(JsonDocument? document, string body)
    {
        var element = document?.RootElement ?? default;
        var detail = Text(element, "error_description") ?? Text(element, "message") ?? ErrorText(element);
        if (detail is not null)
        {
            return ": " + detail;
        }

        return body.Length == 0 ? string.Empty : ": " + body;
    }

    /// <summary>OpenRouter's error is a string in some answers and an object with a message in others.</summary>
    private static string? ErrorText(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("error", out var error))
        {
            return null;
        }

        return error.ValueKind switch
        {
            JsonValueKind.String => error.GetString() is { Length: > 0 } text ? text : null,
            JsonValueKind.Object => Text(error, "message") ?? Text(error, "type"),
            _ => null,
        };
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
