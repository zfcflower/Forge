namespace Forge.Core.Providers;

/// <summary>
/// Builds the <see cref="HttpClient"/> the streaming providers expect: the whole-response timeout is
/// disabled so it cannot cut a long body mid-stream; liveness is enforced per stream read through
/// <see cref="HttpChatProvider.IdleTimeout"/> instead.
/// </summary>
public static class ProviderHttpClient
{
    public static HttpClient Create(HttpMessageHandler? handler = null) =>
        new(handler ?? new HttpClientHandler()) { Timeout = Timeout.InfiniteTimeSpan };
}
