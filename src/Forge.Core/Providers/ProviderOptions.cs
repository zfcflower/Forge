using Forge.Abstractions;

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

    /// <summary>
    /// OpenAI-compatible, OpenAI only: send <c>prompt_cache_retention</c> (<c>in_memory</c> for the short lifetime, <c>24h</c> for the
    /// long one). Off by default: the values depend on the model, and a model that does not know the field answers 400.
    /// </summary>
    public bool SendPromptCacheRetention { get; init; }

    /// <summary>
    /// OpenAI-compatible, xAI only: send the conversation id header (<c>x-grok-conv-id</c>) that raises the automatic cache's hit rate.
    /// </summary>
    public bool SendConversationCacheHeader { get; init; }

    /// <summary>
    /// OpenAI-compatible, GitHub Copilot only: send <c>X-Interaction-Id</c>, the header its API keys the prompt cache on. Without it
    /// the cache barely hits for the Claude models Copilot serves.
    /// </summary>
    public bool SendInteractionId { get; init; }

    /// <summary>
    /// Where every model request is recorded, when the user turned the usage log on. Null means recording is off, which is the default:
    /// the log is for diagnosis and analysis, so the user opts in and it never runs behind their back.
    /// </summary>
    public IUsageRecorder? UsageRecorder { get; init; }
}
