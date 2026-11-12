using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Forge.Core.Providers;

/// <summary>What one catalog fetch produced.</summary>
public enum ModelCatalogFetchStatus
{
    /// <summary>A catalog arrived and parsed into a snapshot.</summary>
    Updated,

    /// <summary>The source answered 304: the snapshot on disk is still the newest one.</summary>
    NotModified,

    /// <summary>No catalog: transport, status, size or shape. The caller keeps whatever it already had.</summary>
    Failed,
}

/// <summary>One fetch attempt: a fresh snapshot, "unchanged", or why it failed.</summary>
public sealed record ModelCatalogFetchResult(ModelCatalogFetchStatus Status, ModelCatalogSnapshot? Snapshot, string? Message)
{
    public static ModelCatalogFetchResult Updated(ModelCatalogSnapshot snapshot) =>
        new(ModelCatalogFetchStatus.Updated, snapshot, null);

    public static ModelCatalogFetchResult NotModified() => new(ModelCatalogFetchStatus.NotModified, null, null);

    public static ModelCatalogFetchResult Failed(string message) => new(ModelCatalogFetchStatus.Failed, null, message);
}

/// <summary>
/// Fetches the model catalog and turns it into a <see cref="ModelCatalogSnapshot"/>. This is the second place in the app
/// that describes someone else's API (the provider <c>/models</c> listing is the first), and unlike that one it needs no
/// credential at all.
/// </summary>
/// <remarks>
/// <para>
/// The request is conditional: the previous <c>ETag</c> goes back as <c>If-None-Match</c>, and models.dev answers 304 with
/// no body when the catalog has not changed (it publishes <c>Cache-Control: max-age=0, must-revalidate</c> with a strong
/// ETag, so a 304 is the normal answer for a daily poll). That is what makes "run every day at 05:00" cheap even when the
/// user already refreshed by hand that morning.
/// </para>
/// <para>
/// Every failure is a message, never an exception: the daily automation turns it into a failed run, and the catalog the app
/// already had stays in place.
/// </para>
/// </remarks>
public static class ModelCatalogFetcher
{
    /// <summary>The catalog this build reads. Configurable in settings; this is the default a fresh install uses.</summary>
    public const string DefaultSourceUrl = "https://models.dev/api.json";

    /// <summary>A 6 MB download plus parsing, off the user's critical path; well under the automation engine's patience.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Fetches <paramref name="sourceUrl"/>. <paramref name="etag"/> is the validator of the snapshot already on disk
    /// (<c>null</c> for a first fetch); <paramref name="now"/> is when the download is deemed to have happened, so a caller
    /// with a test clock controls the "already updated today" test.
    /// </summary>
    /// <param name="maxBytes">
    /// The largest body this call will hold in memory; the default is <see cref="ModelCatalogParser.MaxBytes"/>. A test
    /// passes a small number to exercise the over-limit path without allocating 32 MB.
    /// </param>
    public static async Task<ModelCatalogFetchResult> FetchAsync(
        HttpClient http,
        string sourceUrl,
        string? etag,
        DateTimeOffset now,
        TimeSpan? timeout = null,
        CancellationToken ct = default,
        long maxBytes = ModelCatalogParser.MaxBytes)
    {
        ArgumentNullException.ThrowIfNull(http);

        var trimmed = (sourceUrl ?? string.Empty).Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return ModelCatalogFetchResult.Failed("The catalog address must be an absolute http or https URL.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(etag))
        {
            // TryAddWithoutValidation: a proxy may hand back a weak or oddly quoted validator, and echoing it verbatim is
            // what makes the 304 possible.
            message.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? DefaultTimeout);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ModelCatalogFetchResult.Failed("The catalog request timed out.");
        }
        catch (HttpRequestException exception)
        {
            return ModelCatalogFetchResult.Failed("The catalog request failed: " + exception.Message);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return ModelCatalogFetchResult.NotModified();
            }

            if (!response.IsSuccessStatusCode)
            {
                return ModelCatalogFetchResult.Failed(
                    "The catalog source answered " + (int)response.StatusCode + " " + response.ReasonPhrase + ".");
            }

            if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
            {
                return ModelCatalogFetchResult.Failed("The catalog response is larger than " + maxBytes + " bytes.");
            }

            byte[]? payload;
            try
            {
                payload = await ReadBoundedAsync(response.Content, maxBytes, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ModelCatalogFetchResult.Failed("The catalog download timed out.");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                return ModelCatalogFetchResult.Failed("The catalog download failed: " + exception.Message);
            }

            if (payload is null)
            {
                // A response without Content-Length (a chunked HTML error page, a streaming proxy) is bounded here rather
                // than buffered whole: the size limit has to hold before the bytes become an array.
                return ModelCatalogFetchResult.Failed("The catalog response is larger than " + maxBytes + " bytes.");
            }

            var validator = response.Headers.ETag?.ToString();
            if (!ModelCatalogParser.TryParse(payload, trimmed, now, validator, out var snapshot, out var error) || snapshot is null)
            {
                Trace.WriteLine("[model-catalog] parse failed: " + error);
                return ModelCatalogFetchResult.Failed(error ?? "The catalog response could not be read.");
            }

            return ModelCatalogFetchResult.Updated(snapshot);
        }
    }

    /// <summary>The body, or <c>null</c> when it exceeds <paramref name="maxBytes"/>.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(1024 * 1024);
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }
}
