using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>Smallest possible request to the same model: answers whether a spent quota is available again.</summary>
public sealed class ProviderQuotaProbe : IProviderQuotaProbe
{
    private readonly Func<ModelSettings, CancellationToken, ValueTask<IChatProvider>> _resolve;
    private readonly TimeProvider _time;

    public ProviderQuotaProbe(
        Func<ModelSettings, CancellationToken, ValueTask<IChatProvider>> resolve,
        TimeProvider? time = null)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _time = time ?? TimeProvider.System;
    }

    public async Task<bool> ProbeAsync(ModelSettings model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);

        var probeModel = model with { MaxOutputTokens = 1, Thinking = ThinkingEffort.Off };
        var provider = await _resolve(probeModel, ct).ConfigureAwait(false);
        var request = new ChatRequest(string.Empty, [], [ChatMessage.User("ping")], probeModel);
        try
        {
            await foreach (var evt in provider.StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                if (evt is MessageCompleted)
                {
                    return true;
                }
            }

            return false;
        }
        catch (ProviderException ex) when (ProviderQuota.Classify(ex, probeModel, _time.GetUtcNow()) is not null)
        {
            return false;
        }
    }
}
