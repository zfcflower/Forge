using Forge.Abstractions;

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

    /// <summary>
    /// The application's client: routed through <paramref name="network"/> (resolver and proxy read per connection) with
    /// the handler defaults — redirects followed, no decompression, 30 s connect timeout, 5 min pooled lifetime.
    /// </summary>
    public static HttpClient Create(INetworkStack network)
    {
        ArgumentNullException.ThrowIfNull(network);
        return Create(network.CreateHandler(new NetworkHandlerOptions()));
    }
}
