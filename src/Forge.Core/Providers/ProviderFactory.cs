using Forge.Abstractions;

namespace Forge.Core.Providers;

public static class ProviderFactory
{
    public const string OpenAiCompatibleKind = "openai-compatible";

    public const string AnthropicKind = "anthropic";

    public static IChatProvider Create(ProviderConfig config, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(httpClient);

        if (httpClient.Timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentException(
                $"The HttpClient timeout ({httpClient.Timeout}) also covers the streamed response body and would abort long streams without a retry. "
                    + $"Use {nameof(ProviderHttpClient)}.{nameof(ProviderHttpClient.Create)}() (Timeout.InfiniteTimeSpan) and rely on the provider idle timeout instead.",
                nameof(httpClient));
        }

        return config.Kind?.Trim().ToLowerInvariant() switch
        {
            OpenAiCompatibleKind => new OpenAiCompatibleProvider(config, httpClient),
            AnthropicKind => new AnthropicProvider(config, httpClient),
            _ => throw new ArgumentException(
                $"Unsupported provider kind '{config.Kind}'; expected '{OpenAiCompatibleKind}' or '{AnthropicKind}'.",
                nameof(config)),
        };
    }
}
