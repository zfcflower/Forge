using System.Text.Json;
using Forge.Abstractions;

namespace Forge.App.Providers;

/// <summary>
/// The one HTTP round every provider sign-in makes: a per-step timeout over the shared client, the failure-to-
/// <see cref="ProviderException" /> mapping and a single body read. Only the provider's label and the name it gives the
/// request differ between the sign-ins, so the messages are built from those instead of being copied per class.
/// </summary>
internal static class ProviderSignInHttp
{
    /// <summary>How much of a failed response's body is repeated in the error message.</summary>
    private const int MaxErrorBodyLength = 1024;

    /// <summary>
    /// The budget of one step. The shared client has no timeout of its own, so each step gets its own and a stalled
    /// connection cannot outlive the sign-in it belongs to.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sends one request and reads its body once. The body comes back as text plus a parsed document (null when it is not
    /// JSON), because a failure still has to report the status and what the server said, JSON or not.
    /// </summary>
    /// <param name="http">The shared client; it carries no timeout, the step's budget is applied here.</param>
    /// <param name="message">The request to send; it is not disposed here.</param>
    /// <param name="provider">The provider's label as it leads every message, e.g. <c>ChatGPT</c>.</param>
    /// <param name="request">What this provider calls the sign-in request, e.g. <c>authorization</c> or <c>sign-in</c>.</param>
    /// <param name="operation">The step being performed, e.g. <c>device token</c>, for the read and parse failures.</param>
    /// <param name="ct">The caller's cancellation (the sign-in's own lifetime).</param>
    public static async Task<(int Status, JsonDocument? Root, string Body)> SendForJsonAsync(
        HttpClient http,
        HttpRequestMessage message,
        string provider,
        string request,
        string operation,
        CancellationToken ct)
    {
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
            throw new ProviderException($"{provider}: the {request} request timed out.", retryable: true);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new ProviderException(
                $"{provider}: the {request} request failed: {exception.Message}",
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
                    $"{provider}: the {operation} response could not be read.",
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
                        $"{provider}: the {operation} response is not JSON.",
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
}
