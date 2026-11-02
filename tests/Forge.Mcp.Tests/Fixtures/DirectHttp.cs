using System.Net.Http;

namespace Forge.Mcp.Tests.Fixtures;

/// <summary>Loopback-only client that ignores HTTP_PROXY, so fake endpoints are reached directly.</summary>
internal static class DirectHttp
{
    public static HttpClient Client { get; } = new(new SocketsHttpHandler { UseProxy = false, UseCookies = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
}
