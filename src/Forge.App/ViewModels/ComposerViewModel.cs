using System.Collections.ObjectModel;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.Core.Providers;
using Forge.App.Services;
using Forge.Core.Prompts;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>One entry of the composer's agent menu: the default main agent or a built-in agent kind.</summary>
internal sealed class ComposerAgentOption : ObservableObject
{
    private bool isSelected;

    public ComposerAgentOption(AgentDefinition? definition)
    {
        Definition = definition;
        SelectCommand = new RelayCommand(() => Selected?.Invoke(this));
    }

    /// <summary>The built-in kind, or null for the default main agent.</summary>
    public AgentDefinition? Definition { get; }

    public bool IsDefault => Definition is null;

    /// <summary>The stable key stored as a default for new sessions.</summary>
    public string Key => Definition?.Name ?? "default";

    public string Name => Definition?.Name ?? SessionStrings.ComposerAgentDefault;

    public string Description => Definition?.Description ?? SessionStrings.ComposerAgentDefaultDescription;

    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }

    public RelayCommand SelectCommand { get; }

    internal event Action<ComposerAgentOption>? Selected;

    public void RefreshText()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
    }
}

/// <summary>One row of the model picker.</summary>
internal sealed class ComposerModelOption : ObservableObject
{
    private bool isPinned;
    private bool isSelected;

    public ComposerModelOption(string name, string providerId, string providerName, bool pinned)
    {
        Name = name;
        ProviderId = providerId;
        ProviderName = providerName;
        isPinned = pinned;
        SelectCommand = new RelayCommand(() => Selected?.Invoke(this));
        TogglePinCommand = new RelayCommand(() => PinToggled?.Invoke(this));
    }

    public string Name { get; }

    /// <summary>The provider that serves this model, so picking a row also picks where it runs.</summary>
    public string ProviderId { get; }

    public string ProviderName { get; }

    /// <summary>The provider name shown after the model when more than one provider is connected; empty otherwise.</summary>
    public string Caption => ShowProviderCaption ? ProviderName : string.Empty;

    /// <summary>Set by the composer: a single-provider setup does not repeat the provider on every row.</summary>
    public bool ShowProviderCaption { get; init; }

    public bool IsPinned
    {
        get => isPinned;
        set => Set(ref isPinned, value);
    }

    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }

    public RelayCommand SelectCommand { get; }

    public RelayCommand TogglePinCommand { get; }

    internal event Action<ComposerModelOption>? Selected;

    internal event Action<ComposerModelOption>? PinToggled;
}

/// <summary>A message waiting above the input: queued for after the running turn, or interjected and not yet delivered.</summary>
internal sealed class QueuedMessageViewModel : ObservableObject
{
    public QueuedMessageViewModel(string text, bool isInterjection, Action<QueuedMessageViewModel> remove)
    {
        ArgumentNullException.ThrowIfNull(remove);
        Text = text;
        IsInterjection = isInterjection;
        RemoveCommand = new RelayCommand(() => remove(this), () => !IsInterjection);
    }

    public string Text { get; }

    /// <summary>Single-line preview for the chip.</summary>
    public string Preview => Text.ReplaceLineEndings(" ");

    /// <summary>Handed to the running agent already; it can no longer be taken back.</summary>
    public bool IsInterjection { get; }

    public bool CanRemove => !IsInterjection;

    public string Label => IsInterjection ? SessionStrings.ComposerInterjecting : SessionStrings.ComposerQueued;

    public RelayCommand RemoveCommand { get; }

    public void RefreshText() => OnPropertyChanged(nameof(Label));
}

/// <summary>One row of the <c>/</c> or <c>@</c> picker.</summary>
internal sealed class ComposerSuggestionItem : ObservableObject
{
    private bool isSelected;

    public ComposerSuggestionItem(string value, string title, string detail, string badge)
    {
        Value = value;
        Title = title;
        Detail = detail;
        Badge = badge;
    }

    /// <summary>What is inserted after the trigger character.</summary>
    public string Value { get; }

    public string Title { get; }

    public string Detail { get; }

    public bool HasDetail => Detail.Length > 0;

    public string Badge { get; }

    public bool HasBadge => Badge.Length > 0;

    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }
}

/// <summary>
/// The chat composer's chips and pickers: which agent kind and which model and effort the session's root agent runs
/// with, and the <c>/</c> (commands and skills) and <c>@</c> (workspace files) suggestion lists. A change of agent,
/// model or effort is handed to <paramref name="apply"/> as the root definition for the next turn; the default agent with
/// the configured model and effort hands over null, so a session nobody touched runs exactly as before.
/// </summary>
internal sealed class ComposerViewModel : ObservableObject
{
    /// <summary>The app's own slash commands; none exist yet, skills fill the list.</summary>
    internal static readonly IReadOnlyList<SlashEntry> Commands = [];

    private readonly Action<AgentDefinition?> apply;
    private readonly ComposerPreferences preferences;
    private readonly string workspace;
    private readonly ProviderServices services;
    private readonly ProviderCatalog catalog;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> fileSource;
    private readonly Func<IReadOnlyList<SlashEntry>> skillSource;
    private readonly Func<CancellationToken, Task>? modelRefresh;
    private readonly List<ComposerModelOption> allModels = [];
    private string providerId = string.Empty;
    private ComposerAgentOption selectedAgent;
    private string model = string.Empty;
    private ThinkingEffort effort;
    private string modelSearch = string.Empty;
    private string? status;
    private IReadOnlyList<SlashEntry>? skills;
    private IReadOnlyList<string>? files;
    private Task? filesLoading;
    private ComposerToken token = ComposerToken.None;
    private string lastText = string.Empty;
    private int lastCaret;
    private int selectedSuggestion = -1;
    private bool isSuggestionOpen;

    /// <summary>The token Escape closed; it stays closed until the caret leaves it.</summary>
    private ComposerToken dismissed = ComposerToken.None;

    public ComposerViewModel(
        ProviderServices providers,
        Action<AgentDefinition?> apply,
        ComposerPreferences preferences,
        string workspace,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? fileSource = null,
        Func<IReadOnlyList<SlashEntry>>? skillSource = null,
        Func<CancellationToken, Task>? modelRefresh = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(preferences);
        services = providers;
        catalog = providers.Catalog;
        this.apply = apply;
        this.preferences = preferences;
        this.workspace = workspace ?? string.Empty;
        this.fileSource = fileSource ?? ListWorkspaceFilesAsync;
        this.skillSource = skillSource ?? DiscoverSkills;
        this.modelRefresh = modelRefresh;

        var agents = new List<ComposerAgentOption> { new(null) };
        agents.AddRange(BuiltInAgents.All.Select(definition => new ComposerAgentOption(definition)));
        foreach (var agent in agents)
        {
            agent.Selected += OnAgentSelected;
        }

        Agents = agents;
        selectedAgent = agents.FirstOrDefault(agent => string.Equals(agent.Key, preferences.DefaultAgent, StringComparison.Ordinal)) ??
                        agents[0];
        selectedAgent.IsSelected = true;

        var defaultProvider = catalog.Default;
        providerId = defaultProvider?.Id ?? string.Empty;
        model = defaultProvider?.DefaultModel ?? string.Empty;
        effort = defaultProvider?.Definition.Thinking ?? ThinkingEffort.Off;
        if (defaultProvider is not null)
        {
            if (!string.IsNullOrWhiteSpace(preferences.DefaultModel))
            {
                model = preferences.DefaultModel!;
                providerId = ResolveProviderFor(model) ?? providerId;
            }

            effort = preferences.DefaultEffort ?? effort;
        }

        Suggestions = [];
        Models = [];
        RebuildModels();
        SaveDefaultsCommand = new RelayCommand(SaveDefaults, () => catalog.IsConfigured);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, onError: OnRefreshFailed);
        Apply();
    }

    public IReadOnlyList<ComposerAgentOption> Agents { get; }

    public ComposerAgentOption SelectedAgent
    {
        get => selectedAgent;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(selectedAgent, value))
            {
                return;
            }

            selectedAgent.IsSelected = false;
            selectedAgent = value;
            selectedAgent.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AgentLabel));
            Apply();
        }
    }

    public string AgentLabel => selectedAgent.Name;

    public bool HasModel => catalog.IsConfigured;

    /// <summary>The visible model rows: the search filter applied, pinned ones first.</summary>
    public ObservableCollection<ComposerModelOption> Models { get; }

    public bool HasNoModels => Models.Count == 0;

    public string Model
    {
        get => model;
        set
        {
            value = (value ?? string.Empty).Trim();
            if (!catalog.IsConfigured || value.Length == 0 || !Set(ref model, value))
            {
                return;
            }

            foreach (var option in allModels)
            {
                option.IsSelected = string.Equals(option.Name, model, StringComparison.Ordinal) &&
                                    (providerId.Length == 0 || string.Equals(option.ProviderId, providerId, StringComparison.Ordinal));
            }

            OnPropertyChanged(nameof(ModelLabel));
            Apply();
        }
    }

    public string ModelLabel => model.Length == 0 ? "—" : model;

    public string ModelSearch
    {
        get => modelSearch;
        set
        {
            if (Set(ref modelSearch, value ?? string.Empty))
            {
                FilterModels();
            }
        }
    }

    public ThinkingEffort Effort
    {
        get => effort;
        set
        {
            if (!Set(ref effort, value))
            {
                return;
            }

            OnPropertyChanged(nameof(EffortValue));
            OnPropertyChanged(nameof(EffortName));
            OnPropertyChanged(nameof(EffortBar1));
            OnPropertyChanged(nameof(EffortBar2));
            OnPropertyChanged(nameof(EffortBar3));
            Apply();
        }
    }

    /// <summary>The slider's position, 0 (off) to 3 (high).</summary>
    public double EffortValue
    {
        get => (double)effort;
        set => Effort = (ThinkingEffort)Math.Clamp((int)Math.Round(value), (int)ThinkingEffort.Off, (int)ThinkingEffort.High);
    }

    public string EffortName => SessionStrings.ComposerEffortName(effort);

    public double EffortBar1 => effort >= ThinkingEffort.Low ? 1 : 0.3;

    public double EffortBar2 => effort >= ThinkingEffort.Medium ? 1 : 0.3;

    public double EffortBar3 => effort >= ThinkingEffort.High ? 1 : 0.3;

    /// <summary>A short confirmation under the model list ("saved as defaults"); empty when there is none.</summary>
    public string? Status
    {
        get => status;
        private set
        {
            if (Set(ref status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(status);

    public RelayCommand SaveDefaultsCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Status = null;
        skills = null;
        files = null;
        if (modelRefresh is null)
        {
            RebuildModels();
            return;
        }

        await modelRefresh(cancellationToken).ConfigureAwait(true);
        RebuildModels();
        Status = SessionStrings.ComposerModelsFetched(allModels.Count);
    }

    /// <summary>The definition the root agent should run with, or null for the untouched default.</summary>
    public AgentDefinition? CurrentDefinition()
    {
        var provider = catalog.Find(providerId);
        if (provider is null)
        {
            return null;
        }

        if (selectedAgent.IsDefault &&
            string.Equals(model, provider.DefaultModel, StringComparison.Ordinal) &&
            effort == provider.Definition.Thinking)
        {
            return null;
        }

        var settings = new ModelSettings(model, effort, provider.Definition.MaxOutputTokens, provider.Id);
        return selectedAgent.Definition is { } definition
            ? definition with { Model = settings }
            : new AgentDefinition("Root", "Main coding agent.", string.Empty, settings);
    }

    // ---------------------------------------------------------------- suggestions

    public ObservableCollection<ComposerSuggestionItem> Suggestions { get; }

    public bool IsSuggestionOpen
    {
        get => isSuggestionOpen;
        private set => Set(ref isSuggestionOpen, value);
    }

    public ComposerTokenKind SuggestionKind => token.Kind;

    public int SelectedSuggestionIndex => selectedSuggestion;

    /// <summary>Re-reads the token under the caret and refills the picker; called on every text or caret change.</summary>
    public void UpdateSuggestions(string? text, int caret)
    {
        lastText = text ?? string.Empty;
        lastCaret = caret;
        token = ComposerSuggestions.FindToken(lastText, caret);
        if (token.Kind == ComposerTokenKind.None || token.Start != dismissed.Start || token.Kind != dismissed.Kind)
        {
            dismissed = ComposerToken.None;
        }
        else
        {
            CloseSuggestions();
            return;
        }

        OnPropertyChanged(nameof(SuggestionKind));
        var items = new List<ComposerSuggestionItem>();
        switch (token.Kind)
        {
            case ComposerTokenKind.Slash:
                skills ??= LoadSkills();
                foreach (var entry in ComposerSuggestions.FilterSlash(Commands.Concat(skills), token.Query))
                {
                    items.Add(new ComposerSuggestionItem(
                        entry.Name,
                        "/" + entry.Name,
                        entry.Description,
                        entry.IsSkill ? SessionStrings.ComposerSkill : SessionStrings.ComposerCommand));
                }

                break;
            case ComposerTokenKind.Mention:
                if (files is null)
                {
                    filesLoading ??= LoadFilesAsync();
                    if (files is not null)
                    {
                        // The list was already at hand and the load has refilled the picker.
                        return;
                    }

                    break;
                }

                foreach (var path in ComposerSuggestions.FilterFiles(files, token.Query))
                {
                    var slash = path.LastIndexOf('/');
                    items.Add(new ComposerSuggestionItem(
                        path,
                        slash < 0 ? path : path[(slash + 1)..],
                        slash < 0 ? string.Empty : path[..slash],
                        string.Empty));
                }

                break;
        }

        Suggestions.Clear();
        foreach (var item in items)
        {
            Suggestions.Add(item);
        }

        Select(items.Count > 0 ? 0 : -1);
        IsSuggestionOpen = items.Count > 0;
    }

    /// <summary>Moves the highlighted row by <paramref name="delta"/>, wrapping around.</summary>
    public void MoveSelection(int delta)
    {
        if (Suggestions.Count == 0)
        {
            return;
        }

        var next = ((selectedSuggestion < 0 ? 0 : selectedSuggestion + delta) % Suggestions.Count + Suggestions.Count) % Suggestions.Count;
        Select(next);
    }

    /// <summary>
    /// The draft with the highlighted (or the given) entry inserted in place of the token, plus the new caret; null when
    /// the picker is closed. Closes the picker.
    /// </summary>
    public (string Text, int Caret)? Accept(ComposerSuggestionItem? item = null)
    {
        item ??= selectedSuggestion >= 0 && selectedSuggestion < Suggestions.Count ? Suggestions[selectedSuggestion] : null;
        if (!IsSuggestionOpen || item is null || token.Kind == ComposerTokenKind.None)
        {
            return null;
        }

        var result = ComposerSuggestions.Apply(lastText, token, item.Value);
        CloseSuggestions();
        return result;
    }

    /// <summary>Closes the picker; <paramref name="dismissed"/> (Escape) keeps it closed while the caret stays in the token.</summary>
    public void CloseSuggestions(bool dismissed = false)
    {
        if (dismissed)
        {
            this.dismissed = token;
        }

        token = ComposerToken.None;
        Suggestions.Clear();
        selectedSuggestion = -1;
        IsSuggestionOpen = false;
    }

    public void RefreshText()
    {
        foreach (var agent in Agents)
        {
            agent.RefreshText();
        }

        OnPropertyChanged(nameof(AgentLabel));
        OnPropertyChanged(nameof(EffortName));
        Status = null;
    }

    private void Select(int index)
    {
        selectedSuggestion = index;
        for (var i = 0; i < Suggestions.Count; i++)
        {
            Suggestions[i].IsSelected = i == index;
        }

        OnPropertyChanged(nameof(SelectedSuggestionIndex));
    }

    private IReadOnlyList<SlashEntry> DiscoverSkills() => SkillCatalog.Discover(workspace, AgentRuntimeServices.UserDirectory());

    private IReadOnlyList<SlashEntry> LoadSkills()
    {
        try
        {
            return skillSource();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private async Task LoadFilesAsync()
    {
        IReadOnlyList<string> loaded;
        try
        {
            loaded = await fileSource(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                                              ArgumentException or GitException)
        {
            loaded = [];
        }

        files = loaded;
        filesLoading = null;
        if (token.Kind == ComposerTokenKind.Mention)
        {
            UpdateSuggestions(lastText, lastCaret);
        }
    }

    private async Task<IReadOnlyList<string>> ListWorkspaceFilesAsync(CancellationToken ct)
    {
        if (workspace.Length == 0)
        {
            return [];
        }

        var index = await Task.Run(() => FileIndex.BuildAsync(workspace, new GitCli(), ct), ct).ConfigureAwait(false);
        return index.Files;
    }

    // ---------------------------------------------------------------- agent, model and effort

    private void OnAgentSelected(ComposerAgentOption option) => SelectedAgent = option;

    private void OnModelSelected(ComposerModelOption option)
    {
        providerId = option.ProviderId;
        Model = option.Name;
    }

    private void OnPinToggled(ComposerModelOption option)
    {
        option.IsPinned = !option.IsPinned;
        preferences.SetPinned(option.Name, option.IsPinned);
        FilterModels();
    }

    private void RebuildModels()
    {
        foreach (var option in allModels)
        {
            option.Selected -= OnModelSelected;
            option.PinToggled -= OnPinToggled;
        }

        allModels.Clear();
        if (catalog.IsConfigured)
        {
            var showProvider = catalog.Providers.Count > 1;
            foreach (var provider in catalog.Providers)
            {
                foreach (var name in provider.Models)
                {
                    var option = new ComposerModelOption(name, provider.Id, provider.DisplayName, preferences.IsPinned(name))
                    {
                        ShowProviderCaption = showProvider,
                        IsSelected = string.Equals(name, model, StringComparison.Ordinal) &&
                                     string.Equals(provider.Id, providerId, StringComparison.Ordinal),
                    };
                    option.Selected += OnModelSelected;
                    option.PinToggled += OnPinToggled;
                    allModels.Add(option);
                }
            }

            // A pinned model that no connected provider lists any more still shows, so a pin is not silently lost.
            foreach (var pinned in preferences.PinnedModels)
            {
                if (!allModels.Exists(option => string.Equals(option.Name, pinned, StringComparison.Ordinal)))
                {
                    var option = new ComposerModelOption(pinned, providerId, string.Empty, pinned: true)
                    {
                        ShowProviderCaption = false,
                        IsSelected = string.Equals(pinned, model, StringComparison.Ordinal),
                    };
                    option.Selected += OnModelSelected;
                    option.PinToggled += OnPinToggled;
                    allModels.Add(option);
                }
            }
        }

        FilterModels();
    }

    private void FilterModels()
    {
        var query = modelSearch.Trim();
        var visible = allModels
            .Where(option => query.Length == 0 ||
                             option.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             option.ProviderName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(option => option.IsPinned)
            .ToList();
        Models.Clear();
        foreach (var option in visible)
        {
            Models.Add(option);
        }

        OnPropertyChanged(nameof(HasNoModels));
    }

    private void SaveDefaults()
    {
        preferences.SetDefaults(selectedAgent.IsDefault ? null : selectedAgent.Key, model, effort);
        Status = SessionStrings.ComposerDefaultsSaved;
    }

    /// <summary>
    /// The picker's refresh: asks every connected provider that can list models for its current list. One provider failing
    /// does not stop the others; the first failure is reported, and the lists that did come back are already in the cache.
    /// </summary>
    private async Task RefreshAllProvidersAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        foreach (var provider in catalog.Providers)
        {
            if (!provider.Definition.Preset.SupportsModelListing)
            {
                continue;
            }

            try
            {
                await services.RefreshModelsAsync(provider, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is ProviderException or HttpRequestException or TimeoutException)
            {
                failure ??= exception;
            }
        }

        if (failure is not null)
        {
            Status = SessionStrings.ComposerModelFetchFailed(failure.Message);
        }
    }

    /// <summary>The first connected provider that offers this model, for a default chosen before the picker was built.</summary>
    private string? ResolveProviderFor(string modelName)
    {
        foreach (var provider in catalog.Providers)
        {
            if (provider.Models.Contains(modelName, StringComparer.Ordinal))
            {
                return provider.Id;
            }
        }

        return null;
    }

    private void OnRefreshFailed(Exception exception) => Status = SessionStrings.ComposerModelFetchFailed(exception.Message);

    private void Apply() => apply(CurrentDefinition());
}
