using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Providers;

namespace Forge.App.Providers;

/// <summary>
/// Resolves <c>spawn_agent</c>'s per-spawn model override against the live provider catalogue: matches the text the user
/// named, clamps the thinking step to what the model accepts, fills in the provider's output ceiling, and never guesses.
/// An ambiguous or unknown choice comes back as a refusal that lists the candidates, so the model can pick one next turn
/// instead of running on a config that only fails at request time.
/// </summary>
internal sealed class SpawnModelResolver(Func<ProviderCatalog> catalog)
{
    /// <summary>How many candidates a refusal shows before it says "and N more"; the model narrows with <c>models</c>.</summary>
    private const int MaxCandidates = 20;

    public SpawnModelChoice Resolve(SpawnModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (catalog() is not { } live)
        {
            return SpawnModelChoice.Refused("no provider is connected: connect one in Settings > Providers before choosing a model.");
        }

        var providers = Project(live);
        if (providers.Count == 0)
        {
            return SpawnModelChoice.Refused("no provider is connected: connect one in Settings > Providers before choosing a model.");
        }

        var modelText = Clean(request.Model);
        var providerText = Clean(request.Provider);
        var thinkingText = Clean(request.Thinking);

        if (providerText is not null && ModelResolver.MatchProviders(providers, providerText).Count == 0)
        {
            return SpawnModelChoice.Refused(UnknownProvider(providerText, providers));
        }

        var requested = ThinkingEffort.Off;
        if (thinkingText is not null && !ThinkingEfforts.TryParse(thinkingText, out requested))
        {
            return SpawnModelChoice.Refused(
                $"unknown thinking '{thinkingText}'; valid steps are off, minimal, low, medium, high, xhigh, max.");
        }

        string model;
        string? providerId;
        if (modelText is null)
        {
            if (providerText is null)
            {
                // Thinking-only: the kind keeps its model and provider (a null provider means the default one), which is
                // also what a template that pinned its own model carries.
                model = request.Current.Model;
                providerId = request.Current.ProviderId;
            }
            else
            {
                var current = ModelResolver.Resolve(request.Current.Model, providers, providerText);
                if (current.Kind != ModelResolutionKind.Unique)
                {
                    return SpawnModelChoice.Refused(current.Kind == ModelResolutionKind.Ambiguous
                        ? Candidates(request.Current.Model, current.Candidates)
                        : $"provider '{providerText}' does not list the agent's current model '{request.Current.Model}'; call models with provider=<id> to see what it serves.");
                }

                model = current.Match!.Model;
                providerId = current.Match.ProviderId;
            }

            if (model.Length == 0)
            {
                return SpawnModelChoice.Refused("this agent has no model configured to override; pass 'model' explicitly.");
            }
        }
        else
        {
            var match = ModelResolver.Resolve(modelText, providers, providerText);
            switch (match.Kind)
            {
                case ModelResolutionKind.Unique:
                    model = match.Match!.Model;
                    providerId = match.Match.ProviderId;
                    break;

                case ModelResolutionKind.Ambiguous:
                    return SpawnModelChoice.Refused(Candidates(modelText, match.Candidates));

                default:
                    return SpawnModelChoice.Refused(
                        $"unknown model '{modelText}'. Call models to list the connected models"
                        + (providerText is null ? string.Empty : $" of provider '{providerText}'")
                        + ", or pass provider to narrow the lookup.");
            }
        }

        var accepted = live.ThinkingStepsFor(model, providerId);
        var preferred = thinkingText is null ? request.Current.Thinking : requested;
        var thinking = ThinkingEfforts.Clamp(preferred, accepted);
        string? note = null;
        if (thinking != preferred)
        {
            note = thinkingText is not null
                ? $"requested {ThinkingEfforts.Name(preferred)}; {model} accepts {StepList(accepted)}; runs at {ThinkingEfforts.Name(thinking)}"
                : $"{ThinkingEfforts.Name(preferred)} is not one of {model}'s steps ({StepList(accepted)}); runs at {ThinkingEfforts.Name(thinking)}";
        }

        var owner = live.Find(providerId);
        // Only a cap the user pinned may reach the wire; the catalogue's capability feeds the compaction budget alone.
        var settings = new ModelSettings(
            model,
            thinking,
            owner?.UserMaxOutput(model),
            providerId);
        return SpawnModelChoice.Resolved(settings, note);
    }

    private static IReadOnlyList<ProviderModels> Project(ProviderCatalog live)
    {
        var providers = new List<ProviderModels>(live.Providers.Count);
        foreach (var provider in live.Providers)
        {
            providers.Add(new ProviderModels(provider.Id, provider.DisplayName, provider.Models));
        }

        return providers;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string StepList(IReadOnlyList<ThinkingEffort> steps) =>
        string.Join(", ", steps.Select(ThinkingEfforts.Name));

    private static string Candidates(string text, IReadOnlyList<ResolvedModel> candidates)
    {
        var hidden = Math.Max(0, candidates.Count - MaxCandidates);
        var shown = string.Join(" | ", candidates.Take(MaxCandidates).Select(static c => c.ProviderId + " / " + c.Model));
        return $"model '{text}' matches {candidates.Count} models: {shown}"
               + (hidden == 0 ? string.Empty : $" | and {hidden} more")
               + ". Pass provider to pick one, or call models for the full list.";
    }

    private static string UnknownProvider(string text, IReadOnlyList<ProviderModels> providers) =>
        $"unknown provider '{text}'; connected: {string.Join(", ", providers.Select(static p => p.ProviderId + " (" + p.DisplayName + ")"))}.";
}
