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
/// <para>
/// It also charges the provider's local budget, through the limiter it was given: the shell passes
/// <see cref="ProviderRateLimiter.Shared"/> (the one the user's numbers are published into) explicitly, and a null limiter
/// means no budget at all, so a unit test never inherits process-wide state. Retry is the outermost middleware and therefore the only place that sees every
/// attempt (including the retries it decides on), which is exactly where a per-request budget belongs; putting it inside the
/// endpoint would charge once for a request that was sent three times.
/// </para>
/// </summary>
public sealed class RetryMiddleware : IChatProviderMiddleware
{
    private readonly ProviderRateLimiter? rateLimiter;

    /// <param name="rateLimiter">The budget to charge, or null for none (a test that builds a provider without a shell).</param>
    public RetryMiddleware(ProviderRateLimiter? rateLimiter = null) => this.rateLimiter = rateLimiter;

    public IChatProvider Apply(IChatProvider next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return new RetryingChatProvider(next, rateLimiter: rateLimiter);
    }
}
