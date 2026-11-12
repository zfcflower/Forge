using System.Collections.ObjectModel;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Providers;

namespace Forge.App.Settings;

/// <summary>One choice of the model combo: a provider and model, or the empty "not set" row that inherits the rule.</summary>
internal sealed record AgentModelChoice(string? ProviderId, string Model, string Display)
{
    /// <summary>True for the "not set" row: the agent then follows the Worker, or the first connected model.</summary>
    public bool IsInherited => Model.Length == 0;
}

/// <summary>One choice of the thinking combo: an accepted step and its localized name.</summary>
internal sealed record AgentThinkingChoice(ThinkingEffort Effort, string Name);

/// <summary>
/// One row of the settings page's built-in agent defaults: the agent's name, the model it runs on and how hard it thinks.
/// It owns the two combos' state and the clamp between them: picking a model that accepts fewer steps lowers the thinking
/// choice instead of leaving an unsupported one. Every user change is handed to the page, which writes settings.json.
/// </summary>
internal sealed class AgentDefaultsRowViewModel : ObservableObject
{
    private readonly Dictionary<string, string> providerNames = new(StringComparer.Ordinal);
    private readonly Func<string?, string?, IReadOnlyList<ThinkingEffort>> levels;
    private readonly Action<AgentDefaultsRowViewModel> changed;
    private AgentModelChoice selectedModel;
    private AgentThinkingChoice selectedThinking = new(ThinkingEffort.Off, string.Empty);
    private bool updating;

    public AgentDefaultsRowViewModel(
        string agentId,
        string name,
        AgentDefault? stored,
        ProviderCatalog catalog,
        string notSetText,
        Func<string?, string?, IReadOnlyList<ThinkingEffort>>? levels = null,
        Action<AgentDefaultsRowViewModel>? changed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(catalog);
        AgentId = agentId;
        Name = name;
        this.levels = levels ?? ((modelId, providerId) => catalog.ThinkingStepsFor(modelId, providerId));
        this.changed = changed ?? (_ => { });

        var choices = new List<AgentModelChoice> { new(null, string.Empty, notSetText) };
        foreach (var provider in catalog.Providers)
        {
            providerNames[provider.Id] = provider.DisplayName;
            foreach (var model in provider.Models)
            {
                choices.Add(new AgentModelChoice(provider.Id, model, provider.DisplayName + " · " + model));
            }
        }

        var match = Match(stored, choices);
        if (match is not null && !choices.Contains(match))
        {
            // A stored model no provider lists any more: append it so the combo shows the value the agent actually runs on
            // instead of falling back to the blank "not set" row, and the user can still pick it or replace it.
            choices.Add(match);
        }

        Models = choices;
        selectedModel = match ?? choices[0];
        RefreshThinking(stored?.Thinking ?? ThinkingEffort.Off);
    }

    public string AgentId { get; }

    /// <summary>The localized row label (Main, Scout, Worker, Plan, Reviewer).</summary>
    public string Name { get; }

    /// <summary>
    /// The "not set" row first, then every model of every connected provider as "provider · model", and last the stored model
    /// when no provider lists it any more so the combo still shows what the agent runs on.
    /// </summary>
    public IReadOnlyList<AgentModelChoice> Models { get; }

    public ObservableCollection<AgentThinkingChoice> ThinkingChoices { get; } = [];

    public AgentModelChoice SelectedModel
    {
        get => selectedModel;
        set
        {
            if (updating || value is null || !Set(ref selectedModel, value))
            {
                return;
            }

            RefreshThinking(selectedThinking.Effort);
            changed(this);
        }
    }

    public AgentThinkingChoice SelectedThinking
    {
        get => selectedThinking;
        set
        {
            if (updating || value is null || selectedModel.IsInherited || !Set(ref selectedThinking, value))
            {
                return;
            }

            changed(this);
        }
    }

    /// <summary>An inherited row has no model to hang steps on, so its thinking combo is disabled.</summary>
    public bool CanChooseThinking => !selectedModel.IsInherited;

    /// <summary>The entry to persist: <c>null</c> for "not set" (inherit), otherwise the chosen model and step.</summary>
    public AgentDefault? Current =>
        selectedModel.IsInherited ? null : new AgentDefault(selectedModel.ProviderId, selectedModel.Model, selectedThinking.Effort);

    /// <summary>
    /// Rebuilds the thinking choices for the selected model and keeps the nearest accepted step, never one the model rejects.
    /// </summary>
    private void RefreshThinking(ThinkingEffort preferred)
    {
        updating = true;
        try
        {
            var steps = levels(selectedModel.IsInherited ? null : selectedModel.Model, selectedModel.ProviderId);
            if (steps is not { Count: > 0 })
            {
                steps = [ThinkingEffort.Off];
            }

            var clamped = ThinkingEfforts.Clamp(preferred, steps);
            ThinkingChoices.Clear();
            foreach (var step in steps)
            {
                ThinkingChoices.Add(new AgentThinkingChoice(step, SessionStrings.ComposerEffortName(step)));
            }

            selectedThinking = ThinkingChoices.FirstOrDefault(choice => choice.Effort == clamped) ?? ThinkingChoices[0];
            OnPropertyChanged(nameof(ThinkingChoices));
            OnPropertyChanged(nameof(SelectedThinking));
            OnPropertyChanged(nameof(CanChooseThinking));
        }
        finally
        {
            updating = false;
        }
    }

    /// <summary>The stored entry as one of the combo's rows; a model no provider lists any more gets its own row.</summary>
    private AgentModelChoice? Match(AgentDefault? stored, List<AgentModelChoice> listed)
    {
        if (stored is null || stored.Model.Length == 0)
        {
            return null;
        }

        foreach (var choice in listed)
        {
            if (choice.IsInherited || !string.Equals(choice.Model, stored.Model, StringComparison.Ordinal))
            {
                continue;
            }

            if (stored.ProviderId is null || string.Equals(choice.ProviderId, stored.ProviderId, StringComparison.Ordinal))
            {
                return choice;
            }
        }

        var providerName = stored.ProviderId is { } id && providerNames.TryGetValue(id, out var name) ? name : stored.ProviderId;
        var display = string.IsNullOrEmpty(providerName) ? stored.Model : providerName + " · " + stored.Model;
        return new AgentModelChoice(stored.ProviderId, stored.Model, display);
    }
}
