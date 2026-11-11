using Forge.Abstractions;

namespace Forge.Core.Providers.Internal;

/// <summary>
/// One step around a provider endpoint: it wraps the provider and returns a provider that behaves the same except for the
/// concern it owns (retry, statistics, a future cache). This is the shape microsoft/agent-framework composes chat clients
/// with (<c>ChatClientBuilder.Use(...)</c> in `Microsoft.Extensions.AI`): the endpoint stays free of cross-cutting policy,
/// and the policy is a decorator that only sees the unified streaming contract.
/// <para>Provenance: the middleware shape is adapted from that design; no upstream file is copied, so the notices file
/// records the influence rather than a license obligation.</para>
/// </summary>
public interface IChatProviderMiddleware
{
    IChatProvider Apply(IChatProvider next);
}

/// <summary>
/// Composes middleware around an endpoint. Order is declared outside-in — the first middleware is the outermost, which is
/// where retry belongs: it must see the failure of everything inside it.
/// </summary>
public static class ChatProviderPipeline
{
    public static IChatProvider Build(IChatProvider endpoint, params ReadOnlySpan<IChatProviderMiddleware> middleware)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        for (var index = middleware.Length - 1; index >= 0; index--)
        {
            endpoint = middleware[index].Apply(endpoint);
        }

        return endpoint;
    }
}

/// <summary>
/// Retries a stream that fails before its first event. The first event is the commit point: once the model has produced
/// anything, replaying the request would duplicate output and re-run tools, so a later failure is surfaced instead. The
/// retry loop itself lives in <see cref="RetryingChatProvider"/>; this only places it in the pipeline.
/// </summary>
public sealed class RetryMiddleware : IChatProviderMiddleware
{
    public IChatProvider Apply(IChatProvider next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return new RetryingChatProvider(next);
    }
}
