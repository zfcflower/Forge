using System.Collections.ObjectModel;
using System.ComponentModel;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.Core.Providers;
using Forge.App.Services;
using Forge.App.Prompts;
using Forge.Core.Prompts;
using Forge.Core.Prompts.Templates;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>Why a <see cref="ComposerAgentOption"/> cannot drive an agent-session tab; <see cref="None"/> is the runnable case.</summary>
internal enum AgentUnavailableReason
{
    /// <summary>Nothing says it cannot: the kind names a tool whitelist that may drive a user session.</summary>
    None,

    /// <summary>The kind declares no tool whitelist, so which tools it would reach is unknown.</summary>
    NoTools,

    /// <summary>The whitelist names a git write or a pull request tool: only the root session may call those.</summary>
    RootOnlyTools,
}

/// <summary>One entry of the composer's agent menu: the default main agent or a built-in agent kind.</summary>
internal sealed class ComposerAgentOption : ObservableObject
{
    private bool isSelected;
    private AgentUnavailableReason unavailable;

    public ComposerAgentOption(AgentDefinition? definition)
    {
        Definition = definition;
        SelectCommand = new RelayCommand(() => Selected?.Invoke(this), () => IsAvailable);
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

    /// <summary>
    /// Whether an agent-session tab may run this kind (forge-app "Agent 会话的界面与接线"): it has to name the tools it may call
    /// and that list may hold no git write and no pull request tool. Every row is available until a kind like that is marked,
    /// so an ordinary session only ever lists runnable rows.
    /// </summary>
    public bool IsAvailable => unavailable == AgentUnavailableReason.None;

    /// <summary>Why the row is greyed, read through the table so it follows a language switch; null when it is runnable.</summary>
    public string? UnavailableReason => unavailable switch
    {
        AgentUnavailableReason.NoTools => AgentSessionStrings.AgentNoTools,
        AgentUnavailableReason.RootOnlyTools => AgentSessionStrings.AgentRootOnlyTools,
        _ => null,
    };

    /// <summary>
    /// The row's opacity: a greyed row reads as unavailable. A property rather than a style because the picker lives in the
    /// window's overlay layer, outside the toolbar's style scope.
    /// </summary>
    public double Opacity => IsAvailable ? 1 : 0.5;

    /// <summary>Marks the kind as one an agent-session tab cannot run, with the sentence that says why.</summary>
    internal void MarkUnavailable(AgentUnavailableReason reason)
    {
        if (unavailable == reason)
        {
            return;
        }

        unavailable = reason;
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(UnavailableReason));
        OnPropertyChanged(nameof(Opacity));
    }

    public RelayCommand SelectCommand { get; }

    internal event Action<ComposerAgentOption>? Selected;

    public void RefreshText()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(UnavailableReason));
    }
}

/// <summary>One row of the model popup's orchestration list: Auto, Direct, or one workflow skill.</summary>
internal sealed class ComposerOrchestrationOption : ObservableObject
{
    private readonly string skillDescription;
    private bool isSelected;

    public ComposerOrchestrationOption(OrchestrationChoice choice, string skillDescription = "")
    {
        Choice = choice;
        this.skillDescription = skillDescription;
        SelectCommand = new RelayCommand(() => Selected?.Invoke(this));
    }

    public OrchestrationChoice Choice { get; }

    public string Name => Choice.Mode switch
    {
        OrchestrationMode.Direct => SessionStrings.ComposerOrchestrationDirect,
        OrchestrationMode.Workflow => Choice.Skill,
        _ => SessionStrings.ComposerOrchestrationAuto,
    };

    public string Description => Choice.Mode switch
    {
        OrchestrationMode.Direct => SessionStrings.ComposerOrchestrationDirectDescription,
        OrchestrationMode.Workflow => skillDescription,
        _ => SessionStrings.ComposerOrchestrationAutoDescription,
    };

    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }

    public RelayCommand SelectCommand { get; }

    internal event Action<ComposerOrchestrationOption>? Selected;

    public void RefreshText()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
    }
}

/// <summary>One row of the model picker's flat row list: a provider group's header, the pinned section's header, or a model.</summary>
internal interface IComposerModelRow;

/// <summary>One row of the model picker.</summary>
internal sealed class ComposerModelOption : ObservableObject, IComposerModelRow
{
    private readonly Func<string>? priceSource;
    private bool isPinned;
    private bool isSelected;
    private bool isDefaultModel;

    public ComposerModelOption(string name, string providerId, string providerName, bool pinned, Func<string>? priceSource = null)
    {
        Name = name;
        ProviderId = providerId;
        ProviderName = providerName;
        this.priceSource = priceSource;
        isPinned = pinned;
        SelectCommand = new RelayCommand(() => Selected?.Invoke(this));
        TogglePinCommand = new RelayCommand(() => PinToggled?.Invoke(this));
    }

    public string Name { get; }

    /// <summary>The provider that serves this model, so picking a row also picks where it runs.</summary>
    public string ProviderId { get; }

    public string ProviderName { get; }

    /// <summary>
    /// The price line on the right of the row: "每百万 $0.15 / $0.6 · 1M" (input / output per million tokens, then the context
    /// window). Read from the catalogue on each request rather than captured once: the models.dev snapshot lands on the pool
    /// after the rows exist, so a value taken at build time would stay blank until the picker was rebuilt.
    /// </summary>
    public string PriceText => priceSource?.Invoke() ?? string.Empty;

    public bool HasPriceText => PriceText.Length > 0;

    /// <summary>Re-reads the price line after the catalogue's metadata changed underneath it.</summary>
    public void RefreshPrice()
    {
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(HasPriceText));
    }

    /// <summary>True on the model a new session starts with (the one "设为默认" stored), which is what the icon marks.</summary>
    public bool IsDefaultModel
    {
        get => isDefaultModel;
        set => Set(ref isDefaultModel, value);
    }

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

/// <summary>
/// A header row of the model picker: one provider's collapsed/expanded group, or the pinned section's caption (which has no
/// chevron, no count and no toggle).
/// </summary>
internal sealed class ComposerModelGroupOption : ObservableObject, IComposerModelRow
{
    private bool isExpanded;
    private int count;

    public ComposerModelGroupOption(string name, bool isSection)
    {
        Name = name;
        IsSection = isSection;
        ToggleCommand = new RelayCommand(
            () => Toggled?.Invoke(this),
            () => !isSection);
    }

    public string Name { get; }

    /// <summary>How many models the group lists right now (after hiding, before the search filter); zero on the pinned caption.</summary>
    public int Count
    {
        get => count;
        set => Set(ref count, value);
    }

    /// <summary>The pinned section's caption, not a provider group.</summary>
    public bool IsSection { get; }

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }

    public RelayCommand ToggleCommand { get; }

    internal event Action<ComposerModelGroupOption>? Toggled;
}

/// <summary>One bar of the composer's effort readout: the accepted steps of the current model, filled up to the chosen one.</summary>
internal sealed class ComposerEffortBarViewModel : NotifyingObject
{
    private bool isActive;

    public ComposerEffortBarViewModel(double height, bool isActive)
    {
        Height = height;
        this.isActive = isActive;
    }

    /// <summary>The bar's height: a ramp from the lowest accepted step to the highest.</summary>
    public double Height { get; }

    public bool IsActive
    {
        get => isActive;
        set => SetAndNotify(ref isActive, value, nameof(Opacity));
    }

    public double Opacity => isActive ? 1 : 0.3;
}

/// <summary>
/// One label under the effort track: the accepted step's name and whether it is the chosen one. Where the label sits is the
/// panel's business (<see cref="Views.EffortTickPanel"/>), not the item's.
/// </summary>
internal sealed class ComposerEffortTickViewModel : NotifyingObject
{
    private readonly ThinkingEffort step;
    private bool isSelected;

    public ComposerEffortTickViewModel(ThinkingEffort step)
    {
        this.step = step;
        Name = SessionStrings.ComposerEffortTickName(step);
    }

    public string Name { get; private set; }

    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }

    /// <summary>Re-reads the step's name after a language switch; the selected step is unchanged by one.</summary>
    public void RefreshText()
    {
        var name = SessionStrings.ComposerEffortTickName(step);
        if (!string.Equals(name, Name, StringComparison.Ordinal))
        {
            Name = name;
            OnPropertyChanged(nameof(Name));
        }
    }
}

/// <summary>A message waiting above the input: queued for after the running turn, or interjected and not yet delivered.</summary>
internal sealed class QueuedMessageViewModel : ObservableObject
{
    public QueuedMessageViewModel(
        string text,
        bool isInterjection,
        Action<QueuedMessageViewModel> remove,
        IReadOnlyList<ContentBlock>? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(remove);
        Text = text;
        Attachments = attachments ?? [];
        IsInterjection = isInterjection;
        RemoveCommand = new RelayCommand(() => remove(this), () => !IsInterjection);
    }

    public QueuedMessageViewModel(ComposerPrompt prompt, bool isInterjection, Action<QueuedMessageViewModel> remove)
        : this(prompt.Text, isInterjection, remove, prompt.Attachments)
    {
        Orchestration = prompt.Orchestration;
    }

    public string Text { get; }

    /// <summary>The attachments of the message (images, files, inlined text files); they travel with it through the queue.</summary>
    public IReadOnlyList<ContentBlock> Attachments { get; }

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>"Images ×2" when only images are attached, "Attachments ×3" otherwise; empty without attachments.</summary>
    public string AttachmentsLabel =>
        Attachments.Count == 0 ? string.Empty
        : Attachments.All(static block => block is ImageBlock) ? ImageStrings.Count(Attachments.Count)
        : AttachmentStrings.Count(Attachments.Count);

    /// <summary>The message as it will be sent.</summary>
    public ComposerPrompt Prompt => new(Text, Attachments, Orchestration);

    /// <summary>The orchestration the message was composed with; it is applied when the message is finally sent.</summary>
    public OrchestrationChoice Orchestration { get; }

    /// <summary>Single-line preview for the chip.</summary>
    public string Preview => Text.ReplaceLineEndings(" ");

    /// <summary>Handed to the running agent already; it can no longer be taken back.</summary>
    public bool IsInterjection { get; }

    public bool CanRemove => !IsInterjection;

    public string Label => IsInterjection ? SessionStrings.ComposerInterjecting : SessionStrings.ComposerQueued;

    public RelayCommand RemoveCommand { get; }

    public void RefreshText()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(AttachmentsLabel));
    }
}

/// <summary>
/// A message the user composed: its text (possibly empty when something is attached) and its attachments - images,
/// <see cref="FileBlock"/>s and text files inlined as <see cref="TextBlock"/>s, in sending order. It is what the queue, an
/// interjection, a redirect and a refused turn carry, so attachments are never dropped on the way.
/// </summary>
internal sealed record ComposerPrompt(string Text, IReadOnlyList<ContentBlock> Attachments, OrchestrationChoice Orchestration = default)
{
    public static ComposerPrompt FromText(string text) => new(text, []);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && Attachments.Count == 0;
}

/// <summary>
/// The unsent composer state a replacing view takes over ("continue"): the draft text, the queued messages and the
/// attachments of the draft.
/// </summary>
internal sealed record ComposerCarry(string Draft, IReadOnlyList<ComposerPrompt> Queued, IReadOnlyList<ContentBlock> DraftAttachments);

/// <summary>One row of the <c>/</c> or <c>@</c> picker.</summary>
internal sealed class ComposerSuggestionItem : ObservableObject
{
    private readonly string unavailableKey;
    private bool isSelected;

    /// <param name="unavailableKey">The key of the sentence that says why this row cannot be picked; empty when it can.</param>
    public ComposerSuggestionItem(string value, string title, string detail, string badge, string unavailableKey = "")
    {
        Value = value;
        Title = title;
        Detail = detail;
        Badge = badge;
        this.unavailableKey = unavailableKey ?? string.Empty;
    }

    /// <summary>What is inserted after the trigger character.</summary>
    public string Value { get; }

    public string Title { get; }

    public string Detail { get; }

    public bool HasDetail => Detail.Length > 0;

    public string Badge { get; }

    public bool HasBadge => Badge.Length > 0;

    /// <summary>False for a row this tab cannot use (a skill an agent session may not run); it stays listed so it can be read.</summary>
    public bool IsAvailable => unavailableKey.Length == 0;

    /// <summary>Why the row cannot be picked, resolved on read so it follows the language; null when it can be.</summary>
    public string? UnavailableReason => unavailableKey.Length == 0 ? null : Loc.Instance[unavailableKey];

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
/// <para>The model list skips every model <see cref="AppPreferences.ModelsHidden"/> hides; the model the session is on
/// stays listed (it is the state, not a choice), and a hidden flag raised by the settings page re-filters this list without
/// a restart. Without preferences (a standalone composer) nothing is filtered.</para>
/// </summary>
internal sealed partial class ComposerViewModel : NotifyingObject, IDisposable
{
    /// <summary>
    /// The model id a resolved agent kind carries when its template names no model: the runtime substitutes the
    /// <c>settings.json</c> default for it (see <c>SessionRuntime.ResolveAgentDefaults</c>), so the chip keeps its own model.
    /// </summary>
    private const string ConfiguredModelPlaceholder = "default";

    private readonly Action<AgentDefinition?> apply;
    private readonly ComposerPreferences preferences;
    private readonly AppPreferences? appPreferences;
    private readonly string workspace;
    private readonly ProviderServices services;
    private ProviderCatalog catalog;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> fileSource;
    private readonly Func<IReadOnlyList<SlashEntry>> skillSource;
    private readonly Func<PromptTemplateSet> templateSource;
    private PromptTemplateSet? templates;
    private readonly Func<CancellationToken, Task> modelRefresh;
    private readonly Func<string?, string?, IReadOnlyList<ThinkingEffort>> thinkingLevels;
    private readonly List<ComposerModelOption> allModels = [];

    /// <summary>Every agent row in menu order - the main agent first - of which <see cref="Agents"/> lists the visible ones.</summary>
    private readonly List<ComposerAgentOption> allAgents;

    /// <summary>One provider's header plus the models it lists, in catalogue order; the pinned caption lives apart from these.</summary>
    private readonly List<ComposerModelProviderGroup> providerGroups = [];
    private readonly ComposerModelGroupOption pinnedSection = new(SessionStrings.ComposerPinnedSection, isSection: true);

    /// <summary>Reused by <see cref="FilterModels"/>: a keystroke must not allocate a list of the whole catalogue.</summary>
    private readonly List<ComposerModelOption> visiblePinned = [];
    private readonly List<ComposerModelOption> visibleGroupModels = [];
    private readonly List<IComposerModelRow> visibleRows = [];
    private IReadOnlyList<ThinkingEffort> effortSteps = [ThinkingEffort.Off];
    private string providerId = string.Empty;
    private ComposerAgentOption selectedAgent;

    /// <summary>The tab is an agent session; see <see cref="IsAgentSession"/>.</summary>
    private bool agentSession;
    private List<ComposerOrchestrationOption>? orchestrations;
    private ComposerOrchestrationOption? selectedOrchestration;
    private string model = string.Empty;
    private ThinkingEffort effort;
    // The thumb's continuous position while it is being dragged, and whether the step is being read from that position rather
    // than written to it (so a drag can move the thumb without the step setter pulling it back onto a notch).
    private double effortPosition;
    private bool draggingPosition;
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
        Func<CancellationToken, Task>? modelRefresh = null,
        Func<string?, string?, IReadOnlyList<ThinkingEffort>>? thinkingLevels = null,
        Func<PromptTemplateSet>? templateSource = null,
        IImageCodec? imageCodec = null,
        Func<string?, string?, ModelInputs?>? imageInputs = null,
        Func<string?, string, bool>? wireCarries = null,
        INotifications? notifications = null,
        string? notificationScope = null,
        AppPreferences? appPreferences = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(preferences);
        services = providers;
        catalog = providers.Catalog;
        this.apply = apply;
        this.preferences = preferences;
        this.appPreferences = appPreferences;
        // The hidden set lives in the preferences, which are edited on the settings page while a composer stays open: the
        // composer only re-filters, it never rebuilds the rows, so a hidden flag costs one property notification.
        if (appPreferences is not null)
        {
            appPreferences.PropertyChanged += OnAppPreferencesChanged;
        }

        // The models.dev snapshot and the provider model lists land on the pool after the rows exist: their prices show up when
        // the catalogue says so, without rebuilding the list (which would drop the group the user just opened).
        services.ModelCatalog.Changed += OnModelCatalogChanged;

        this.workspace = workspace ?? string.Empty;
        this.fileSource = fileSource ?? ListWorkspaceFilesAsync;
        this.skillSource = skillSource ?? DiscoverSkills;
        this.templateSource = templateSource ??
                              (() => new PromptTemplateService(AgentRuntimeServices.UserDirectory()).Resolve(this.workspace));
        // The picker's 刷新 re-fetches every provider that can list models; the refresh itself is injectable.
        this.modelRefresh = modelRefresh ?? RefreshAllProvidersAsync;
        this.thinkingLevels = thinkingLevels ?? ((modelId, provider) => services.Catalog.ThinkingStepsFor(modelId, provider));
        this.imageCodec = imageCodec ?? AvaloniaImageCodec.Instance;
        this.imageInputs = imageInputs ??
                           ((modelId, provider) => services.Catalog.MetadataFor(modelId, provider).Capabilities?.Inputs);
        this.wireCarries = wireCarries ?? DefaultWireCarries;
        this.notifications = notifications;
        this.notificationScope = notificationScope ?? string.Empty;
        Attachments = [];
        Files = [];

        var agents = new List<ComposerAgentOption> { new(null) };
        // Built-ins (possibly overridden by a file) first, then the user's own agents.
        agents.AddRange(ResolveTemplates().Agents.Select(definition => new ComposerAgentOption(definition)));
        foreach (var agent in agents)
        {
            agent.Selected += OnAgentSelected;
        }

        allAgents = agents;
        Agents.ReplaceAll(allAgents);
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

        RefreshEffortSteps();
        Suggestions = [];
        Models = [];
        RebuildModels();
        SaveDefaultsCommand = new RelayCommand(SaveDefaults, () => catalog.IsConfigured);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, onError: OnRefreshFailed);
        Apply();
    }

    /// <summary>Ends the preferences subscription; the session that owns the composer calls this from its own dispose.</summary>
    public void Dispose()
    {
        if (appPreferences is not null)
        {
            appPreferences.PropertyChanged -= OnAppPreferencesChanged;
        }

        services.ModelCatalog.Changed -= OnModelCatalogChanged;
    }

    /// <summary>
    /// The rows the agent chip lists: every kind for an ordinary session; an agent-session tab leaves the main agent out (the
    /// tab already hangs under one) and keeps the kinds it cannot run, greyed with the reason.
    /// </summary>
    public RangeObservableCollection<ComposerAgentOption> Agents { get; } = [];

    /// <summary>
    /// The tab is an agent session (forge-app "Agent 会话的界面与接线"): the agent chip offers only the kinds that may drive one,
    /// the model chip follows the selected kind's own model, the orchestration chip is gone (every message runs directly), and
    /// a skill whose <c>allowed-tools</c> need a git write or a pull request tool is greyed here. Set once by the view that
    /// hosts the session; an ordinary session leaves it false and every chip behaves as before.
    /// </summary>
    public bool IsAgentSession
    {
        get => agentSession;
        set
        {
            if (agentSession == value)
            {
                return;
            }

            agentSession = value;
            OnPropertyChanged();
            // A kind an agent-session tab cannot drive is greyed there rather than hidden, so the reason can be read; an
            // ordinary session lists the same kinds it always did, all of them pickable.
            foreach (var agent in allAgents)
            {
                agent.MarkUnavailable(agentSession ? UnavailableReasonFor(agent.Definition) : AgentUnavailableReason.None);
            }

            Agents.ReplaceAll(agentSession ? [.. allAgents.Where(static agent => !agent.IsDefault)] : allAgents);
            if (agentSession)
            {
                // The tab starts on the implementing agent (forge-app: Worker is the default kind); a session without a
                // runnable Worker falls back to the first kind it can run.
                SelectedAgent =
                    Agents.FirstOrDefault(static agent => agent.IsAvailable && string.Equals(agent.Key, AgentSessionKinds.Default, StringComparison.Ordinal))
                    ?? Agents.FirstOrDefault(static agent => agent.IsAvailable)
                    ?? selectedAgent;
            }
            else
            {
                SelectedAgent = allAgents[0];
            }

            // A '/' popup already open on this tab lists rows that just became greyed.
            if (IsSuggestionOpen)
            {
                UpdateSuggestions(lastText, lastCaret);
            }
        }
    }

    /// <summary>
    /// Why a kind cannot drive an agent-session tab: it names no tool whitelist, or the whitelist holds a tool only the root
    /// session may call. <see cref="AgentUnavailableReason.None"/> for the kinds the shared rule lets through.
    /// </summary>
    private static AgentUnavailableReason UnavailableReasonFor(AgentDefinition? kind)
    {
        if (kind is null)
        {
            return AgentUnavailableReason.None;
        }

        if (SessionWorkspaceViewModel.CanBeUserSession(kind))
        {
            return AgentUnavailableReason.None;
        }

        return kind.ToolNames is { Count: > 0 } ? AgentUnavailableReason.RootOnlyTools : AgentUnavailableReason.NoTools;
    }

    /// <summary>Auto, Direct and the built-in workflow skills: how the next message is orchestrated.</summary>
    public IReadOnlyList<ComposerOrchestrationOption> Orchestrations
    {
        get
        {
            EnsureOrchestrations();
            return orchestrations!;
        }
    }

    /// <summary>The choice for the next message; it returns to Auto once a message has been sent (<see cref="TakeOrchestration"/>).</summary>
    public ComposerOrchestrationOption SelectedOrchestration
    {
        get
        {
            EnsureOrchestrations();
            return selectedOrchestration!;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            EnsureOrchestrations();
            if (ReferenceEquals(selectedOrchestration, value))
            {
                return;
            }

            selectedOrchestration!.IsSelected = false;
            selectedOrchestration = value;
            value.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OrchestrationLabel));
            OnPropertyChanged(nameof(HasOrchestrationOverride));
        }
    }

    /// <summary>The chip text for a non-Auto choice.</summary>
    public string OrchestrationLabel => SelectedOrchestration.Name;

    public bool HasOrchestrationOverride => SelectedOrchestration.Choice.Mode != OrchestrationMode.Auto;

    /// <summary>The choice for the message being sent now; the selection goes back to Auto.</summary>
    public OrchestrationChoice TakeOrchestration()
    {
        // An agent-session tab has no orchestration to pick (the chip is hidden): every message it sends runs directly.
        if (agentSession)
        {
            return OrchestrationChoice.Direct;
        }

        if (selectedOrchestration is not { } current)
        {
            return OrchestrationChoice.Auto;
        }

        var choice = current.Choice;
        if (choice.Mode != OrchestrationMode.Auto)
        {
            SelectedOrchestration = orchestrations![0];
        }

        return choice;
    }

    private void EnsureOrchestrations()
    {
        if (orchestrations is not null)
        {
            return;
        }

        // Auto and Direct need nothing from disk, so the popup opens at once; the skills folder scan (disk) follows off the
        // UI thread and adds the workflow rows when it is done. An agent-session tab never picks one, so it does not scan.
        InstallOrchestrations([]);
        if (!agentSession)
        {
            _ = LoadWorkflowsAsync();
        }
    }

    private async Task LoadWorkflowsAsync()
    {
        var found = await Task.Run(LoadSkills).ConfigureAwait(true);
        var workflows = new List<ComposerOrchestrationOption>();
        // Every skill that declares `workflow: true` (FORGE's own and the user's or workspace's); the router and the
        // plan panel's todos rule do not, and a router a user copied over does not become a pick.
        foreach (var skill in found)
        {
            if (skill.IsSkill && skill.IsWorkflow &&
                !string.Equals(skill.Name, OrchestrationPrompt.RouterSkillName, StringComparison.OrdinalIgnoreCase))
            {
                workflows.Add(new ComposerOrchestrationOption(OrchestrationChoice.ForWorkflow(skill.Name), skill.Description));
            }
        }

        if (workflows.Count > 0)
        {
            InstallOrchestrations(workflows);
        }
    }

    /// <summary>Replaces the list with Auto, Direct and <paramref name="workflows"/>, keeping the current pick where it still exists.</summary>
    private void InstallOrchestrations(List<ComposerOrchestrationOption> workflows)
    {
        var list = new List<ComposerOrchestrationOption>(2 + workflows.Count)
        {
            new(OrchestrationChoice.Auto),
            new(OrchestrationChoice.Direct),
        };
        list.AddRange(workflows);
        var keep = selectedOrchestration?.Choice ?? OrchestrationChoice.Auto;
        foreach (var option in list)
        {
            option.Selected += OnOrchestrationSelected;
        }

        var first = orchestrations is null;
        orchestrations = list;
        selectedOrchestration = list.FirstOrDefault(option => option.Choice == keep) ?? list[0];
        selectedOrchestration.IsSelected = true;
        if (!first)
        {
            OnPropertyChanged(nameof(Orchestrations));
            OnPropertyChanged(nameof(SelectedOrchestration));
            OnPropertyChanged(nameof(OrchestrationLabel));
            OnPropertyChanged(nameof(HasOrchestrationOverride));
        }
    }

    private void OnOrchestrationSelected(ComposerOrchestrationOption option) => SelectedOrchestration = option;

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
            FollowAgentModel(value.Definition);
            Apply();
        }
    }

    public string AgentLabel => selectedAgent.Name;

    public bool HasModel => catalog.IsConfigured;

    /// <summary>The visible model rows: the search filter applied, pinned ones first.</summary>
    public RangeObservableCollection<IComposerModelRow> Models { get; }

    public bool HasNoModels { get; private set; } = true;


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

            MarkSelectedOption();
            OnPropertyChanged(nameof(ModelLabel));
            RefreshEffortSteps();
            Apply();
        }
    }

    /// <summary>The provider of the current model; the session readout prices tokens with it.</summary>
    public string ProviderId => providerId;

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
            // The dial never offers a step the model does not accept, and a value set from code is clamped the same way.
            var accepted = ThinkingEfforts.Clamp(value, effortSteps);
            if (!Set(ref effort, accepted))
            {
                return;
            }

            UpdateEffortBars();
            UpdateEffortTicks();
            OnPropertyChanged(nameof(EffortName));
            Apply();
            // A step chosen from anywhere but the dial pulls the thumb onto its notch; during a drag the thumb stays where the
            // pointer put it and only the step follows (see EffortValue).
            if (!draggingPosition)
            {
                SetEffortPosition(ThinkingEfforts.IndexOf(effort, effortSteps));
            }
        }
    }

    /// <summary>The steps the current model accepts, ascending and always starting with off.</summary>
    public IReadOnlyList<ThinkingEffort> EffortSteps => effortSteps;

    /// <summary>The slider's top position: the last accepted step's index.</summary>
    public double EffortMaximum => Math.Max(0, effortSteps.Count - 1);

    /// <summary>
    /// The dial's thumb position in accepted-step indices: continuous while the pointer drags, so a four-step model still gives
    /// a thumb that follows the pointer, while the step itself changes only when the thumb crosses a notch's midpoint
    /// (never the enum value, so a model with gaps still lands on a step it takes). <see cref="SnapEffort"/> pulls the thumb
    /// onto that step when the drag ends.
    /// </summary>
    public double EffortValue
    {
        get => effortPosition;
        set
        {
            if (effortSteps.Count == 0 || !double.IsFinite(value))
            {
                return;
            }

            var position = Math.Clamp(value, 0, EffortMaximum);
            if (!Set(ref effortPosition, position))
            {
                return;
            }

            var index = Math.Clamp((int)Math.Round(position, MidpointRounding.AwayFromZero), 0, effortSteps.Count - 1);
            draggingPosition = true;
            try
            {
                Effort = effortSteps[index];
            }
            finally
            {
                draggingPosition = false;
            }
        }
    }

    /// <summary>Puts the thumb back on the step it landed on; the view calls this when the pointer lets go of the dial.</summary>
    public void SnapEffort() => SetEffortPosition(ThinkingEfforts.IndexOf(effort, effortSteps));

    private void SetEffortPosition(double position)
    {
        if (!double.IsFinite(position) || position == effortPosition)
        {
            return;
        }

        effortPosition = position;
        OnPropertyChanged(nameof(EffortValue));
    }

    public string EffortName => SessionStrings.ComposerEffortName(effort);

    /// <summary>The small bars on the model chip: one per accepted step, lit up to the chosen one.</summary>
    public ObservableCollection<ComposerEffortBarViewModel> EffortBars { get; } = [];

    /// <summary>The labels under the effort track in the model picker: one per accepted step, the chosen one highlighted.</summary>
    public ObservableCollection<ComposerEffortTickViewModel> EffortTicks { get; } = [];

    /// <summary>A short confirmation under the model list ("saved as defaults"); empty when there is none.</summary>
    public string? Status
    {
        get => status;
        private set => SetAndNotify(ref status, value, nameof(HasStatus));
    }

    public bool HasStatus => !string.IsNullOrEmpty(status);

    public RelayCommand SaveDefaultsCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Status = null;
        skills = null;
        files = null;
        if (orchestrations is not null)
        {
            // The skills may have changed on disk: rescan (off the UI thread) and keep the current pick where it still exists.
            _ = LoadWorkflowsAsync();
        }

        await modelRefresh(cancellationToken).ConfigureAwait(true);
        catalog = services.Catalog;
        RebuildModels();
        RefreshEffortSteps();
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
            effort == provider.Definition.Thinking &&
            string.Equals(provider.Id, catalog.Default?.Id, StringComparison.Ordinal))
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
        var previous = token;
        token = ComposerSuggestions.FindToken(lastText, caret);
        if (token.Kind == ComposerTokenKind.Slash && (previous.Kind != ComposerTokenKind.Slash || previous.Start != token.Start))
        {
            // A new slash token: pick up commands edited since the popup last opened.
            templates = null;
        }

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
                foreach (var entry in ComposerSuggestions.FilterSlash(SlashEntries(), token.Query))
                {
                    // An agent-session tab has no git write and no pr_* tool: a skill whose `allowed-tools` need one is greyed
                    // here, so the reason shows up before a workflow is run halfway into a tool the session does not have.
                    var needsRootSession = agentSession && entry.RequiresUnavailable(hasGitWrite: false, hasPrTools: false);
                    items.Add(new ComposerSuggestionItem(
                        entry.Name,
                        "/" + entry.Name,
                        entry.Description,
                        entry.IsSkill ? SessionStrings.ComposerSkill : SessionStrings.ComposerCommand,
                        needsRootSession ? AgentSessionStrings.SkillNeedsRootKey : string.Empty));
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

        Select(FirstAvailable(items));
        IsSuggestionOpen = items.Count > 0;
    }

    /// <summary>The row the picker highlights when it opens, or -1: a greyed row is listed but never preselected.</summary>
    private static int FirstAvailable(List<ComposerSuggestionItem> items)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].IsAvailable)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Moves the highlighted row by <paramref name="delta"/>, wrapping around and skipping greyed rows.</summary>
    public void MoveSelection(int delta)
    {
        if (Suggestions.Count == 0)
        {
            return;
        }

        var count = Suggestions.Count;
        var index = selectedSuggestion < 0 ? (delta < 0 ? 0 : -1) : selectedSuggestion;
        for (var step = 0; step < count; step++)
        {
            index = ((index + delta) % count + count) % count;
            if (Suggestions[index].IsAvailable)
            {
                Select(index);
                return;
            }
        }
    }

    /// <summary>
    /// The draft with the highlighted (or the given) entry inserted in place of the token, plus the new caret; null when
    /// the picker is closed. Closes the picker.
    /// </summary>
    public (string Text, int Caret)? Accept(ComposerSuggestionItem? item = null)
    {
        item ??= selectedSuggestion >= 0 && selectedSuggestion < Suggestions.Count ? Suggestions[selectedSuggestion] : null;
        if (!IsSuggestionOpen || item is null || !item.IsAvailable || token.Kind == ComposerTokenKind.None)
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

        if (orchestrations is not null)
        {
            foreach (var option in orchestrations)
            {
                option.RefreshText();
            }

            OnPropertyChanged(nameof(OrchestrationLabel));
        }

        OnPropertyChanged(nameof(AgentLabel));
        OnPropertyChanged(nameof(EffortName));
        foreach (var tick in EffortTicks)
        {
            tick.RefreshText();
        }

        RefreshImageText();
        // The row labels, the price line and the pinned caption are localised, so the picker's rows are rebuilt for the new language.
        RebuildModels();
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

    /// <summary>
    /// The text to send for <paramref name="text"/>: the command body with arguments filled in when it is
    /// <c>/name args</c> for a known command, otherwise the text unchanged.
    /// </summary>
    public string ExpandCommand(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return ResolveTemplates().TryExpandCommand(text, out var expanded) ? expanded : text;
    }

    /// <summary>Commands first, then skills; a skill sharing a command's name is hidden.</summary>
    private List<SlashEntry> SlashEntries()
    {
        var commands = ResolveTemplates().Commands;
        var entries = new List<SlashEntry>(commands.Count + (skills?.Count ?? 0) + 1);
        var names = new HashSet<string>(StringComparer.Ordinal);

        // Built-in: /compact is handled by the session (manual compaction), never sent; it shadows a same-named template.
        const string compact = "compact";
        names.Add(compact);
        entries.Add(new SlashEntry(compact, CompactionStrings.CommandDescription, IsSkill: false));
        foreach (var command in commands)
        {
            if (!names.Add(command.Name))
            {
                continue;
            }

            var description = string.IsNullOrWhiteSpace(command.ArgumentHint)
                ? command.Description
                : command.ArgumentHint + "  " + command.Description;
            entries.Add(new SlashEntry(command.Name, description, IsSkill: false));
        }

        foreach (var skill in skills ?? [])
        {
            if (!names.Contains(skill.Name))
            {
                entries.Add(skill);
            }
        }

        return entries;
    }

    // Same approach as skill discovery: a small synchronous read of a few files, kept off the per-keystroke path by the cache.
    private PromptTemplateSet ResolveTemplates()
    {
        if (templates is { } cached)
        {
            return cached;
        }

        try
        {
            templates = templateSource();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            templates = PromptTemplateSet.BuiltIn();
        }

        return templates;
    }

    private IReadOnlyList<SlashEntry> DiscoverSkills() =>
        SkillCatalog.Discover(workspace, AgentRuntimeServices.UserDirectory(), AgentRuntimeServices.ProductSkillsDirectory);

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
        if (SwitchModel(option.Name, option.ProviderId))
        {
            Apply();
        }
    }

    /// <summary>
    /// Points the model chip at another model of (possibly) another provider; false when neither moved. Two providers can
    /// serve a model with the same id: the name alone would not change, but the selection did, so the rows follow it anyway.
    /// </summary>
    private bool SwitchModel(string name, string provider)
    {
        var providerChanged = !string.Equals(providerId, provider, StringComparison.Ordinal);
        providerId = provider;
        if (!Set(ref model, name) && !providerChanged)
        {
            return false;
        }

        MarkSelectedOption();
        // The current model is exempt from hiding, so a hidden model the session just left has to drop out of the list now.
        FilterModels();
        OnPropertyChanged(nameof(ModelLabel));
        RefreshEffortSteps();
        return true;
    }

    /// <summary>
    /// Moves the model chip onto the model the kind is configured with, on an agent-session tab (forge-app "Agent 会话的界面与
    /// 接线"). A kind that names no model of its own - the <c>default</c> placeholder every built-in kind uses, which the
    /// runtime fills from <c>settings.json</c> - leaves the session's model alone.
    /// </summary>
    private void FollowAgentModel(AgentDefinition? kind)
    {
        if (!agentSession || kind is null || !catalog.IsConfigured)
        {
            return;
        }

        var modelName = kind.Model.Model;
        if (string.IsNullOrWhiteSpace(modelName) || string.Equals(modelName, ConfiguredModelPlaceholder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SwitchModel(modelName, ResolveProviderFor(modelName) ?? providerId);
    }

    /// <summary>
    /// Re-reads the current model's accepted steps and clamps the chosen effort to them. Called whenever the model or the
    /// catalogue changes: the dial must never keep a step the new model rejects.
    /// </summary>
    private void RefreshEffortSteps()
    {
        var accepted = thinkingLevels(model, providerId);
        effortSteps = accepted is { Count: > 0 } ? accepted : [ThinkingEffort.Off];
        effort = ThinkingEfforts.Clamp(effort, effortSteps);
        effortPosition = ThinkingEfforts.IndexOf(effort, effortSteps);
        RebuildEffortBars();
        RebuildEffortTicks();
        UpdateEffortBars();
        UpdateEffortTicks();
        RefreshImageGate();
        OnPropertyChanged(nameof(EffortSteps));
        OnPropertyChanged(nameof(EffortMaximum));
        OnPropertyChanged(nameof(EffortValue));
        OnPropertyChanged(nameof(EffortName));
    }

    private void RebuildEffortBars()
    {
        EffortBars.Clear();
        for (var index = 0; index < effortSteps.Count; index++)
        {
            var height = effortSteps.Count == 1 ? 10 : 4 + (6.0 * index / (effortSteps.Count - 1));
            EffortBars.Add(new ComposerEffortBarViewModel(height, isActive: false));
        }
    }

    private void UpdateEffortBars()
    {
        var selected = ThinkingEfforts.IndexOf(effort, effortSteps);
        for (var index = 0; index < EffortBars.Count; index++)
        {
            EffortBars[index].IsActive = index <= selected;
        }
    }

    private void RebuildEffortTicks()
    {
        EffortTicks.Clear();
        foreach (var step in effortSteps)
        {
            EffortTicks.Add(new ComposerEffortTickViewModel(step));
        }
    }

    private void UpdateEffortTicks()
    {
        var selected = ThinkingEfforts.IndexOf(effort, effortSteps);
        for (var index = 0; index < EffortTicks.Count; index++)
        {
            EffortTicks[index].IsSelected = index == selected;
        }
    }

    /// <summary>Puts the check mark on the one row that matches both the model and its provider.</summary>
    private void MarkSelectedOption()
    {
        foreach (var option in allModels)
        {
            option.IsSelected = string.Equals(option.Name, model, StringComparison.Ordinal) &&
                                (providerId.Length == 0 || string.Equals(option.ProviderId, providerId, StringComparison.Ordinal));
        }
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

        foreach (var group in providerGroups)
        {
            group.Header.Toggled -= OnGroupToggled;
        }

        allModels.Clear();
        providerGroups.Clear();
        if (catalog.IsConfigured)
        {
            foreach (var provider in catalog.Providers)
            {
                var group = new ComposerModelProviderGroup(provider.DisplayName);
                group.Header.Toggled += OnGroupToggled;
                foreach (var name in provider.Models)
                {
                    var option = new ComposerModelOption(
                        name,
                        provider.Id,
                        provider.DisplayName,
                        preferences.IsPinned(name),
                        () => SessionStrings.ComposerModelMeta(catalog.MetadataFor(name, provider.Id)))
                    {
                        IsDefaultModel = string.Equals(name, preferences.DefaultModel, StringComparison.Ordinal),
                        IsSelected = string.Equals(name, model, StringComparison.Ordinal) &&
                                     string.Equals(provider.Id, providerId, StringComparison.Ordinal),
                    };
                    option.Selected += OnModelSelected;
                    option.PinToggled += OnPinToggled;
                    allModels.Add(option);
                    group.Models.Add(option);
                }

                providerGroups.Add(group);
            }

            // A pinned model that no connected provider lists any more still shows, so a pin is not silently lost.
            foreach (var pinned in preferences.PinnedModels)
            {
                if (!allModels.Exists(option => string.Equals(option.Name, pinned, StringComparison.Ordinal)))
                {
                    var option = new ComposerModelOption(pinned, providerId, string.Empty, pinned: true)
                    {
                        IsSelected = string.Equals(pinned, model, StringComparison.Ordinal),
                    };
                    option.Selected += OnModelSelected;
                    option.PinToggled += OnPinToggled;
                    allModels.Add(option);
                }
            }
        }

        // The group holding the session's own model opens; the rest start collapsed, which is the picker's default shape.
        foreach (var group in providerGroups)
        {
            group.Header.IsExpanded = group.Models.Exists(IsCurrentModel);
        }

        FilterModels();
    }

    private void OnGroupToggled(ComposerModelGroupOption header)
    {
        header.IsExpanded = !header.IsExpanded;
        FilterModels();
    }

    /// <summary>
    /// Builds the picker's flat row list: the pinned section first (its rows stay at the top, out of the provider groups), then
    /// one header per provider with that provider's surviving models under it. A search overrides the collapsed state — typing
    /// is a request to see matches, not to click a group open afterwards — and drops the groups that matched nothing.
    /// </summary>
    private void FilterModels()
    {
        var query = modelSearch.Trim();
        var searching = query.Length > 0;

        visiblePinned.Clear();
        foreach (var option in allModels)
        {
            if (option.IsPinned && Shows(option, query))
            {
                visiblePinned.Add(option);
            }
        }

        visibleRows.Clear();
        if (visiblePinned.Count > 0)
        {
            pinnedSection.Count = visiblePinned.Count;
            visibleRows.Add(pinnedSection);
            foreach (var option in visiblePinned)
            {
                visibleRows.Add(option);
            }
        }

        var modelsShowing = 0;
        foreach (var group in providerGroups)
        {
            visibleGroupModels.Clear();
            foreach (var option in group.Models)
            {
                // A pinned model is listed once, in the pinned section, so the group is what is left of it.
                if (!option.IsPinned && Shows(option, query))
                {
                    visibleGroupModels.Add(option);
                }
            }

            group.Header.Count = visibleGroupModels.Count;
            if (visibleGroupModels.Count == 0)
            {
                continue;
            }

            modelsShowing += visibleGroupModels.Count;
            visibleRows.Add(group.Header);
            if (searching || group.Header.IsExpanded)
            {
                foreach (var option in visibleGroupModels)
                {
                    visibleRows.Add(option);
                }
            }
        }

        modelsShowing += visiblePinned.Count;
        if (SameAsVisible())
        {
            return;
        }

        // One Reset instead of ~180 Add notifications: the picker rebuilds its rows once.
        Models.ReplaceAll(visibleRows);

        if (HasNoModels != (modelsShowing == 0))
        {
            HasNoModels = modelsShowing == 0;
            OnPropertyChanged(nameof(HasNoModels));
        }
    }

    /// <summary>One provider's header and the models it lists; <see cref="FilterModels"/> folds them into the row list.</summary>
    private sealed class ComposerModelProviderGroup(string name)
    {
        public ComposerModelGroupOption Header { get; } = new(name, isSection: false);

        public List<ComposerModelOption> Models { get; } = [];
    }

    /// <summary>
    /// Whether one row survives the search box and the hidden set. The current model and a pinned one are never filtered
    /// out: hiding is for the long tail of the catalogue, and a pin is the user saying "keep this one in reach".
    /// </summary>
    private bool Shows(ComposerModelOption option, string query)
    {
        if (appPreferences?.IsModelHidden(option.Name) == true && !IsCurrentModel(option) && !option.IsPinned)
        {
            return false;
        }

        return query.Length == 0 ||
               option.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               option.ProviderName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The row the session runs on: hiding a model never takes the current state out of the picker.</summary>
    private bool IsCurrentModel(ComposerModelOption option) =>
        string.Equals(option.Name, model, StringComparison.Ordinal) &&
        (providerId.Length == 0 || string.Equals(option.ProviderId, providerId, StringComparison.Ordinal));

    private bool SameAsVisible()
    {
        if (Models.Count != visibleRows.Count)
        {
            return false;
        }

        for (var index = 0; index < visibleRows.Count; index++)
        {
            if (!ReferenceEquals(Models[index], visibleRows[index]))
            {
                return false;
            }
        }

        return true;
    }

    private void OnModelCatalogChanged(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                foreach (var option in allModels)
                {
                    option.RefreshPrice();
                }
            },
            Avalonia.Threading.DispatcherPriority.Background);

    /// <summary>A hidden flag moved on the settings page: re-filter in place, the rows themselves are unchanged.</summary>
    private void OnAppPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(AppPreferences.ModelsHidden), StringComparison.Ordinal))
        {
            FilterModels();
        }
    }

    private void SaveDefaults()
    {
        preferences.SetDefaults(selectedAgent.IsDefault ? null : selectedAgent.Key, model, effort);
        // The icon marks the model a new session starts with, which just changed.
        foreach (var option in allModels)
        {
            option.IsDefaultModel = string.Equals(option.Name, model, StringComparison.Ordinal);
        }

        Status = SessionStrings.ComposerDefaultsSaved;
    }

    /// <summary>
    /// The picker's refresh: asks every connected provider that can list models for its current list. One provider failing
    /// does not stop the others; the first failure is reported (and announced, like any other model-fetch failure), and the
    /// lists that did come back are already in the cache.
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
            OnRefreshFailed(failure);
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

    private void OnRefreshFailed(Exception exception)
    {
        var failure = SessionStrings.ComposerModelFetchFailed(exception.Message);
        Status = failure;
        NotificationCenter.Current?.Notify(
            NotificationLevel.Warning,
            NotificationsStrings.ProviderMissingTitle,
            LocText.Literal(failure),
            "model-fetch");
    }

    private void Apply() => apply(CurrentDefinition());
}
