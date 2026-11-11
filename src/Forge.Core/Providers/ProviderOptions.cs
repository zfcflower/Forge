namespace Forge.Core.Providers;

/// <summary>
/// Per-provider wire switches that are not part of <see cref="Forge.Abstractions.ProviderConfig"/>. Every member
/// defaults to the behaviour Forge had before the option existed.
/// </summary>
public sealed record ProviderOptions
{
    public static ProviderOptions Default { get; } = new();

    /// <summary>
    /// OpenAI-compatible only: send <c>prompt_cache_key</c> = <see cref="Forge.Abstractions.ChatRequest.CacheRoutingKey"/>.
    /// Off by default because strict endpoints may reject unknown fields; never sent to DeepSeek whatever the setting.
    /// </summary>
    public bool SendPromptCacheKey { get; init; }
}
