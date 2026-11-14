using System.Collections.ObjectModel;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Providers;

namespace Forge.App.Settings;

/// <summary>
/// The <c>provider</c> section: the connected providers, the custom entry, and the built-in vendors with their two row
/// actions. It owns no window: the view's code-behind opens the dialogs and calls back into <see cref="SaveAsync"/>, so the
/// page model stays testable without an Avalonia application.
/// </summary>
internal sealed class ProvidersSettingsViewModel : NotifyingObject, IDisposable, IReloadableSettingsSection
{
    private readonly ProviderServices services;
    private readonly AppPreferences? preferences;
    private readonly AgentDefaultsStore agentDefaults;
    private readonly ModelCatalogRefreshService? modelCatalogRefresh;
    private readonly Action<Action> post;
    private readonly Forge.App.Prompts.PromptTemplateService promptTemplates;
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private bool saving;
    private string? problem;
    private string? agentsProblem;
    private string? catalogProblem;
    private string defaultContextWindowText = string.Empty;
    private string catalogSourceUrlText = string.Empty;
    private bool showsNeedProviderBanner;
    private bool disposed;

    public ProvidersSettingsViewModel(
        ProviderServices services,
        AgentDefaultsStore? agentDefaults = null,
        ModelCatalogRefreshService? modelCatalogRefresh = null,
        Action<Action>? post = null,
        Forge.App.Prompts.PromptTemplateService? promptTemplates = null,
        CompactionSettingsStore? compaction = null,
        AppPreferences? preferences = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;
        this.preferences = preferences;
        Compaction = new CompactionSettingsViewModel(compaction ?? new CompactionSettingsStore(services.Store.SettingsPath));
        Usage = new UsageLogSettingsViewModel(services.UsageLog, () => services.UsageRecorder.Directory);
        this.agentDefaults = agentDefaults ?? new AgentDefaultsStore(services.Store.SettingsPath);
        this.modelCatalogRefresh = modelCatalogRefresh;
        // The settings page is global, so it lists the user agents of ~/.forge/agents; project agents belong to a session.
        this.promptTemplates = promptTemplates ?? new Forge.App.Prompts.PromptTemplateService(AgentRuntimeServices.UserDirectory());
        this.post = post ?? (static action => action());
        services.Changed += OnProvidersChanged;
        // A save on the prompts page runs on the thread pool; the agent rows are rebuilt on the UI thread.
        this.promptTemplates.Changed += OnPromptTemplatesChanged;
        if (preferences is not null)
        {
            preferences.PropertyChanged += OnPreferencesChanged;
        }
        // The catalog is written off the UI thread (the daily refresh, the startup catch-up), so its notifications come back
        // through the injected post, exactly like the window's other background sources of change.
        services.ModelCatalog.Changed += OnCatalogChanged;
        if (this.modelCatalogRefresh is { } refresh)
        {
            refresh.Changed += OnCatalogChanged;
        }

        Loc.Instance.LanguageChanged += OnLanguageChanged;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsSaving);
        UpdateCatalogCommand = new AsyncRelayCommand(UpdateCatalogAsync, () => modelCatalogRefresh is { IsBusy: false });
    }

    /// <summary>The provider services, so the view can build the editor, login and metadata windows from the same store.</summary>
    internal ProviderServices Services => services;

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>The compaction block under the default context window: thresholds relative to each model's effective window.</summary>
    public CompactionSettingsViewModel Compaction { get; }

    /// <summary>The usage-log switch: on writes one JSONL line per model request under the data root.</summary>
    public UsageLogSettingsViewModel Usage { get; }

    /// <summary>The connected providers, in stored order.</summary>
    public ObservableCollection<ProviderRowViewModel> Rows { get; } = [];

    /// <summary>The built-in vendors, one row each; their buttons depend on what the vendor offers.</summary>
    public ObservableCollection<BuiltInProviderRowViewModel> BuiltInRows { get; } = [];

    /// <summary>The default provider's id, so a row can show which one new sessions start on.</summary>
    public string? DefaultId => services.Catalog.Default?.Id;

    /// <summary>The top banner: shown while no connected provider can run an agent.</summary>
    public bool ShowsNeedProviderBanner
    {
        get => showsNeedProviderBanner;
        private set => Set(ref showsNeedProviderBanner, value);
    }

    public string BannerText => Loc.Instance[ProviderStrings.BannerNeedProviderKey];

    public string ConnectedTitle => Loc.Instance[ProviderStrings.SectionConnectedKey];

    public string AddServerText => Loc.Instance[ProviderStrings.SectionAddServerKey];

    public string CustomTitle => Loc.Instance[ProviderStrings.SectionCustomTitleKey];

    public string CustomHint => Loc.Instance[ProviderStrings.SectionCustomHintKey];

    public string BuiltInTitle => Loc.Instance[ProviderStrings.SectionBuiltInKey];

    public string BuiltInHint => Loc.Instance[ProviderStrings.SectionBuiltInHintKey];

    public string RefreshText => Loc.Instance[ProviderStrings.SectionRefreshKey];

    public string DefaultContextWindowLabel => Loc.Instance[ProviderStrings.DefaultContextWindowKey];

    public string DefaultContextWindowHint => Loc.Instance[ProviderStrings.DefaultContextWindowHintKey];

    /// <summary>The built-in agent defaults at the bottom of the page: main plus the four built-in kinds.</summary>
    public ObservableCollection<AgentDefaultsRowViewModel> AgentRows { get; } = [];

    public string AgentsTitle => Loc.Instance[ProviderStrings.AgentsTitleKey];

    public string AgentsHint => Loc.Instance[ProviderStrings.AgentsHintKey];

    /// <summary>The two column headings of the agent rows.</summary>
    public string AgentsModelHeader => Loc.Instance[ProviderStrings.AgentsModelKey];

    public string AgentsThinkingHeader => Loc.Instance[ProviderStrings.AgentsThinkingKey];

    /// <summary>Why the last agent-defaults write was refused; null when it succeeded or nothing was written yet.</summary>
    public string? AgentsProblem
    {
        get => agentsProblem;
        private set => SetAndNotify(ref agentsProblem, value, nameof(HasAgentsProblem));
    }

    public bool HasAgentsProblem => AgentsProblem is { Length: > 0 };

    /// <summary>
    /// The global context window, as text while it is being typed: a token count, or a share of each model's own window
    /// such as <c>50%</c>. Empty means "use each model's catalog value"; the commit parses it and re-saves the section, so
    /// an invalid entry is reported instead of clearing the setting.
    /// </summary>
    public string DefaultContextWindowText
    {
        get => defaultContextWindowText;
        set => Set(ref defaultContextWindowText, value ?? string.Empty);
    }

    // ---------------------------------------------------------------- model catalog row
    public string CatalogLabel => ModelCatalogStrings.Label;

    /// <summary>The catalog button's label: "update now", or "updating…" while a refresh runs.</summary>
    public string CatalogActionText => IsCatalogUpdating ? ModelCatalogStrings.UpdatingText : ModelCatalogStrings.UpdateText;

    public string CatalogSourceLabel => ModelCatalogStrings.SourceLabel;

    public string CatalogSourceHint => ModelCatalogStrings.SourceHint;

    /// <summary>The catalog address, as typed; committed on focus loss.</summary>
    public string CatalogSourceUrl
    {
        get => catalogSourceUrlText;
        set => Set(ref catalogSourceUrlText, value ?? string.Empty);
    }

    /// <summary>When the cached catalog was fetched and how much it holds, or "not updated yet".</summary>
    public string CatalogStatusText
    {
        get
        {
            if (services.ModelCatalog.View.FetchedAt is not { } fetched)
            {
                return ModelCatalogStrings.NeverText;
            }

            var text = ModelCatalogStrings.UpdatedAt(fetched);
            var view = services.ModelCatalog.View;
            return view.ModelCount == 0 ? text : text + " · " + ModelCatalogStrings.Counts(view.ProviderCount, view.ModelCount);
        }
    }

    /// <summary>True while a refresh runs (the button says so and disables itself).</summary>
    public bool IsCatalogUpdating => modelCatalogRefresh?.IsBusy == true;

    /// <summary>Why the last catalog update failed, or a rejected address; null when there is nothing to say.</summary>
    public string? CatalogProblem
    {
        get => catalogProblem;
        private set => SetAndNotify(ref catalogProblem, value, nameof(HasCatalogProblem));
    }

    public bool HasCatalogProblem => CatalogProblem is { Length: > 0 };

    /// <summary>Asks the catalog source for a fresh snapshot now; skipped when today's is already on disk.</summary>
    public AsyncRelayCommand UpdateCatalogCommand { get; }

    /// <summary>Why the list is incomplete (a damaged settings file or secret store), or <c>null</c> when it is fine.</summary>
    public string? Problem
    {
        get => problem;
        private set => Set(ref problem, value);
    }

    public bool HasProblem => Problem is { Length: > 0 };

    public bool IsSaving
    {
        get => saving;
        private set => Set(ref saving, value);
    }

    /// <summary>Rebuilds the rows from the live catalogue; called when the section is shown and after every save.</summary>
    public void Reload()
    {
        Rows.Clear();
        var catalog = services.Catalog;
        foreach (var provider in catalog.Providers)
        {
            Rows.Add(new ProviderRowViewModel(
                provider,
                string.Equals(provider.Id, catalog.Default?.Id, StringComparison.Ordinal),
                ttl => services.SaveCacheTtlAsync(provider.Id, ttl)));
        }

        BuiltInRows.Clear();
        foreach (var preset in ProviderPresets.All)
        {
            BuiltInRows.Add(new BuiltInProviderRowViewModel(preset));
        }

        // A damaged secret store is a different problem from "no provider configured": it says the keys cannot be read,
        // which is exactly the case where a row must not claim a missing key.
        DefaultContextWindowText = catalog.DefaultContextWindow.ToString();
        CatalogSourceUrl = catalog.ModelCatalogSourceUrl;
        Problem = services.Secrets.Problem ?? (catalog.IsConfigured ? null : catalog.StatusText.Resolve());
        ShowsNeedProviderBanner = !Rows.Any(row => row.IsReady);
        RebuildAgentRows();
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(DefaultId));
    }

    /// <summary>
    /// Writes the edited list: replaces the definition with the same id, appends a new one, or (when
    /// <paramref name="removedId"/> is set) drops it. Failure leaves the file and the live catalogue untouched and surfaces as
    /// the section's problem line.
    /// </summary>
    public async Task<bool> SaveAsync(
        ProviderDefinition? edited,
        string? removedId = null,
        string? makeDefaultId = null,
        CancellationToken cancellationToken = default)
    {
        var current = services.Catalog.Providers.Select(provider => provider.Definition).ToList();
        if (removedId is not null)
        {
            current.RemoveAll(definition => string.Equals(definition.Id, removedId, StringComparison.Ordinal));
        }

        if (edited is not null)
        {
            var index = current.FindIndex(definition => string.Equals(definition.Id, edited.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                current[index] = edited;
            }
            else
            {
                current.Add(edited);
            }
        }

        var defaultId = makeDefaultId ?? DefaultId;
        if (defaultId is not null && current.TrueForAll(definition => !string.Equals(definition.Id, defaultId, StringComparison.Ordinal)))
        {
            defaultId = current.Count > 0 ? current[0].Id : null;
        }

        IsSaving = true;
        try
        {
            await services.SaveAsync(current, defaultId, ParseDefaultContextWindow(), cancellationToken: cancellationToken).ConfigureAwait(true);
            Problem = services.Secrets.Problem;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Problem = exception.Message;
            return false;
        }
        finally
        {
            IsSaving = false;
            OnPropertyChanged(nameof(HasProblem));
        }
    }

    /// <summary>Removes a connected provider; the caller has already asked the user twice.</summary>
    public Task<bool> DeleteAsync(ProviderRowViewModel row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        return SaveAsync(edited: null, removedId: row.Id, cancellationToken: cancellationToken);
    }

    /// <summary>Re-reads <c>settings.json</c> and rebuilds the rows: a hand edit or a key exported since startup shows up.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await services.ReloadAsync(cancellationToken).ConfigureAwait(true);
        agentDefaults.Reload();
        Compaction.Reload();
        Usage.Reload();
        Reload();
    }

    /// <summary>
    /// Commits the global context window typed in the section. An entry that is neither empty, a positive whole number nor
    /// a share such as <c>50%</c> is reported and not saved — clearing the setting has to be an explicit empty box, not a
    /// typo.
    /// </summary>
    public async Task CommitDefaultContextWindowAsync(CancellationToken cancellationToken = default)
    {
        if (!TryParseDefaultContextWindow(out _))
        {
            Problem = Loc.Instance[ProviderStrings.DefaultContextWindowInvalidKey];
            OnPropertyChanged(nameof(HasProblem));
            return;
        }

        await SaveAsync(edited: null, cancellationToken: cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Commits the catalog address typed in the section. An address that is not an absolute http(s) URL is reported and not
    /// saved; a changed one re-fetches in the background at once (regardless of today's date, because the snapshot on disk
    /// belongs to the old source) and the old snapshot keeps resolving until the new source answers.
    /// </summary>
    public async Task CommitCatalogSourceAsync(CancellationToken cancellationToken = default)
    {
        var text = CatalogSourceUrl.Trim();
        var valid = text.Length == 0
                    || (Uri.TryCreate(text, UriKind.Absolute, out var uri)
                        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
        if (!valid)
        {
            CatalogProblem = ModelCatalogStrings.SourceInvalid;
            return;
        }

        var changed = !string.Equals(text, services.Catalog.ModelCatalogSourceUrl, StringComparison.Ordinal);
        var current = services.Catalog.Providers.Select(provider => provider.Definition).ToList();
        IsSaving = true;
        try
        {
            await services
                .SaveAsync(current, DefaultId, ParseDefaultContextWindow(), text, cancellationToken)
                .ConfigureAwait(true);
            CatalogProblem = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CatalogProblem = exception.Message;
            return;
        }
        finally
        {
            IsSaving = false;
        }

        Reload();
        if (changed && modelCatalogRefresh is not null)
        {
            _ = modelCatalogRefresh.RefreshAsync(ModelCatalogRefreshMode.Scheduled, CancellationToken.None);
        }
    }

    private async Task UpdateCatalogAsync(CancellationToken cancellationToken)
    {
        if (modelCatalogRefresh is null)
        {
            return;
        }

        CatalogProblem = null;
        var result = await modelCatalogRefresh.RefreshAsync(ModelCatalogRefreshMode.Manual, cancellationToken).ConfigureAwait(true);
        if (result.Outcome == ModelCatalogRefreshOutcome.Failed)
        {
            CatalogProblem = ModelCatalogStrings.Summary(result);
        }

        OnCatalogChanged(this, EventArgs.Empty);
    }

    private bool TryParseDefaultContextWindow(out ContextWindowDefault window) =>
        ContextWindowDefault.TryParse(DefaultContextWindowText, out window);

    private ContextWindowDefault ParseDefaultContextWindow() =>
        TryParseDefaultContextWindow(out var window) ? window : services.Catalog.DefaultContextWindow;

    /// <summary>Makes one provider the default new sessions start on.</summary>
    public Task MakeDefaultAsync(ProviderRowViewModel row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        return SaveAsync(edited: null, makeDefaultId: row.Provider.Id, cancellationToken: cancellationToken);
    }

    /// <summary>The models the provider currently offers, for the row's "N models" button.</summary>
    public static string ModelsText(ConnectedProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var count = provider.Models.Count;
        return count == 0
            ? Loc.Instance[ProviderStrings.RowModelsNoneKey]
            : Loc.Instance.Format(ProviderStrings.RowModelsKey, TextFormat.Number(count));
    }

    public void Dispose()
    {
        disposed = true;
        services.Changed -= OnProvidersChanged;
        promptTemplates.Changed -= OnPromptTemplatesChanged;
        if (preferences is not null)
        {
            preferences.PropertyChanged -= OnPreferencesChanged;
        }

        services.ModelCatalog.Changed -= OnCatalogChanged;
        if (modelCatalogRefresh is { } refresh)
        {
            refresh.Changed -= OnCatalogChanged;
        }

        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        Compaction.Dispose();
        Usage.Dispose();
    }

    /// <summary>The catalog or a refresh changed (from either thread): re-read the row through the injected post.</summary>
    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        post(() =>
        {
            if (disposed)
            {
                return;
            }

            OnPropertyChanged(nameof(CatalogStatusText));
            OnPropertyChanged(nameof(IsCatalogUpdating));
            OnPropertyChanged(nameof(CatalogActionText));
            OnPropertyChanged(nameof(CatalogProblem));
            OnPropertyChanged(nameof(HasCatalogProblem));
            UpdateCatalogCommand.NotifyCanExecuteChanged();
        });
    }

    private void OnProvidersChanged(object? sender, EventArgs e)
    {
        if (Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        Reload();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(ConnectedTitle));
        OnPropertyChanged(nameof(AddServerText));
        OnPropertyChanged(nameof(CustomTitle));
        OnPropertyChanged(nameof(CustomHint));
        OnPropertyChanged(nameof(BuiltInTitle));
        OnPropertyChanged(nameof(BuiltInHint));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(DefaultContextWindowLabel));
        OnPropertyChanged(nameof(DefaultContextWindowHint));
        OnPropertyChanged(nameof(CatalogLabel));
        OnPropertyChanged(nameof(CatalogActionText));
        OnPropertyChanged(nameof(CatalogSourceLabel));
        OnPropertyChanged(nameof(CatalogSourceHint));
        OnPropertyChanged(nameof(CatalogStatusText));
        OnPropertyChanged(nameof(AgentsTitle));
        OnPropertyChanged(nameof(AgentsHint));
        OnPropertyChanged(nameof(AgentsModelHeader));
        OnPropertyChanged(nameof(AgentsThinkingHeader));
        Reload();
    }

    /// <summary>A model hidden or shown on the visibility page must reach the agent-defaults combos that are already built.</summary>
    private void OnPreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppPreferences.ModelsHidden))
        {
            post(() =>
            {
                if (!disposed)
                {
                    RebuildAgentRows();
                }
            });
        }
    }

    private void OnPromptTemplatesChanged() => post(() =>
    {
        if (!disposed)
        {
            RebuildAgentRows();
        }
    });

    /// <summary>Rebuilds the agent rows from the store and the live catalogue, keeping each stored choice selected.</summary>
    private void RebuildAgentRows()
    {
        AgentRows.Clear();
        var catalog = services.Catalog;
        var notSet = Loc.Instance[ProviderStrings.AgentsNotSetKey];
        foreach (var agentId in AgentIds.All)
        {
            AgentRows.Add(new AgentDefaultsRowViewModel(
                agentId,
                AgentLabel(agentId),
                agentDefaults.Get(agentId),
                catalog,
                notSet,
                changed: OnAgentChanged,
                preferences: preferences));
        }

        // User agents follow the built-ins. Their label is the agent's own name: it is the user's content, not localized.
        foreach (var (agentId, name) in UserAgents())
        {
            AgentRows.Add(new AgentDefaultsRowViewModel(
                agentId,
                name,
                agentDefaults.Get(agentId),
                catalog,
                notSet,
                changed: OnAgentChanged,
                preferences: preferences));
        }
    }

    /// <summary>The agents of the global template scope that are not one of the fixed rows, as id and name; empty when unreadable.</summary>
    private List<(string Id, string Name)> UserAgents()
    {
        var found = new List<(string, string)>();
        try
        {
            var seen = new HashSet<string>(AgentIds.All, StringComparer.Ordinal);
            foreach (var agent in promptTemplates.Resolve(workspace: null).ResolvedAgents)
            {
                if (seen.Add(agent.Id))
                {
                    found.Add((agent.Id, agent.Definition.Name));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable agents directory leaves the page with the fixed rows.
        }

        return found;
    }

    private static string AgentLabel(string agentId) => Loc.Instance[agentId switch
    {
        AgentIds.Main => ProviderStrings.AgentsMainKey,
        AgentIds.Scout => ProviderStrings.AgentsScoutKey,
        AgentIds.Worker => ProviderStrings.AgentsWorkerKey,
        AgentIds.Plan => ProviderStrings.AgentsPlanKey,
        _ => ProviderStrings.AgentsReviewerKey,
    }];

    private void OnAgentChanged(AgentDefaultsRowViewModel row) => _ = SaveAgentAsync(row);

    /// <summary>
    /// Writes one changed row through the store on the pool. The store takes the same settings mutex as the provider save
    /// and only replaces the <c>agents</c> node, so a failure here cannot lose the provider section, and the refusal is shown
    /// under the section instead of throwing through the binding.
    /// </summary>
    private async Task SaveAgentAsync(AgentDefaultsRowViewModel row)
    {
        var value = row.Current;
        try
        {
            await Task.Run(() => agentDefaults.Save(row.AgentId, value)).ConfigureAwait(true);
            if (!disposed)
            {
                AgentsProblem = null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (!disposed)
            {
                AgentsProblem = Loc.Instance.Format(ProviderStrings.AgentsSaveFailedKey, exception.Message);
            }
        }
    }
}

/// <summary>
/// One built-in vendor row: its name and the two actions it offers. A vendor offers "登录" only when it has a sign-in this app
/// can perform, and "API 密钥" only when it takes a key, so a row never shows a button that cannot work.
/// </summary>
internal sealed class BuiltInProviderRowViewModel
{
    public BuiltInProviderRowViewModel(ProviderPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        Preset = preset;
        // A row offers 登录 only when the dialog has a flow for it, and every way in the preset advertises now has one:
        // Claude reuses the local CLI, Copilot, Kimi For Coding and xAI run a device code, Meta and ChatGPT sign in to their
        // own plan through their own device flow, and OpenRouter runs its browser sign-in for an ordinary key.
        ShowsLogin = preset.Auth.HasFlag(ProviderAuthOptions.ClaudeSubscription) ||
                     preset.Auth.HasFlag(ProviderAuthOptions.GitHubCopilot) ||
                     preset.Auth.HasFlag(ProviderAuthOptions.KimiCode) ||
                     preset.Auth.HasFlag(ProviderAuthOptions.XAiSubscription) ||
                     preset.Auth.HasFlag(ProviderAuthOptions.MetaSubscription) ||
                     preset.Auth.HasFlag(ProviderAuthOptions.ChatGptSubscription) ||
                     preset.Auth.HasFlag(ProviderAuthOptions.OpenRouterOAuth);
        ShowsApiKey = preset.Auth.HasFlag(ProviderAuthOptions.ApiKey);
    }

    public ProviderPreset Preset { get; }

    public string Name => Preset.DisplayName;

    public bool ShowsLogin { get; }

    public bool ShowsApiKey { get; }

    public string LoginText => Localization.Loc.Instance[ProviderStrings.LoginButtonKey];

    public string ApiKeyText => Localization.Loc.Instance[ProviderStrings.ApiKeyButtonKey];
}

/// <summary>One connected provider as the section lists it: name, credential line, status, and the row actions.</summary>
internal sealed class ProviderRowViewModel : ObservableObject
{
    private bool deleteArmed;

    /// <param name="cacheTtlChanged">
    /// Saves a new cache lifetime for this provider; null when the row is built without a catalogue behind it (a design-time or test
    /// instance), in which case the control still shows the current value but does nothing.
    /// </param>
    public ProviderRowViewModel(ConnectedProvider provider, bool isDefault, Func<string?, Task>? cacheTtlChanged = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Provider = provider;
        IsDefault = isDefault;
        this.cacheTtlChanged = cacheTtlChanged;
        cacheTtl = provider.Definition.PromptCacheTtl;
        Status = provider.Status;
        ModelsLabel = ProvidersSettingsViewModel.ModelsText(provider);
        CredentialText = CredentialLine(provider, Status);
    }

    private readonly Func<string?, Task>? cacheTtlChanged;
    private string? cacheTtl;

    public ConnectedProvider Provider { get; }

    /// <summary>Whether this vendor has a cache lifetime to set; the selector is hidden when it does not.</summary>
    public bool SupportsCacheTtl => Provider.Definition.SupportsPromptCacheTtl;

    /// <summary>The three choices, in the order the control shows them; <c>auto</c> is the default and the product's recommendation.</summary>
    public IReadOnlyList<string> CacheTtlOptions { get; } =
    [
        Localization.Loc.Instance[ProviderStrings.CacheTtlAutoKey],
        Localization.Loc.Instance[ProviderStrings.CacheTtlShortKey],
        Localization.Loc.Instance[ProviderStrings.CacheTtlLongKey],
    ];

    /// <summary>The hover line: the two prices and what <c>auto</c> actually does.</summary>
    public string CacheTtlTooltip => Localization.Loc.Instance[ProviderStrings.CacheTtlTooltipKey];

    /// <summary>The choice shown in the control, as one of <see cref="CacheTtlOptions"/>.</summary>
    public string SelectedCacheTtl
    {
        get => cacheTtl switch
        {
            "5m" => CacheTtlOptions[1],
            "1h" => CacheTtlOptions[2],
            _ => CacheTtlOptions[0],
        };
        set
        {
            var index = CacheTtlOptions.ToList().IndexOf(value ?? string.Empty);
            var next = index switch
            {
                1 => "5m",
                2 => "1h",
                _ => null,
            };
            if (string.Equals(cacheTtl, next, StringComparison.Ordinal))
            {
                return;
            }

            cacheTtl = next;
            OnPropertyChanged(nameof(SelectedCacheTtl));
            if (cacheTtlChanged is { } save)
            {
                _ = save(next);
            }
        }
    }

    public string Id => Provider.Id;

    public string Name => Provider.DisplayName;

    public ProviderStatus Status { get; }

    /// <summary>The credential line on the right of the row, the same shape the reference UI uses.</summary>
    public string CredentialText { get; }

    /// <summary>Text of the eye button: "29 models".</summary>
    public string ModelsLabel { get; }

    public bool IsDefault { get; }

    public bool IsReady => Status.Kind == ProviderStatusKind.Ready;

    public bool NeedsKey => Status.Kind == ProviderStatusKind.NeedsKey;

    public bool NeedsSignIn => Status.Kind == ProviderStatusKind.NeedsSignIn;

    public bool HasError => Status.Kind == ProviderStatusKind.Error;

    public string StatusText => Status.Detail.Resolve();

    public string StatusTooltip => Status.Kind switch
    {
        ProviderStatusKind.Ready => Localization.Loc.Instance[ProviderStrings.RowStatusReadyKey],
        ProviderStatusKind.NeedsKey => Localization.Loc.Instance[ProviderStrings.RowStatusNeedsKeyKey],
        ProviderStatusKind.NeedsSignIn => Localization.Loc.Instance[ProviderStrings.RowStatusNeedsSignInKey],
        _ => Localization.Loc.Instance[ProviderStrings.RowStatusErrorKey],
    };

    public string EditText => Localization.Loc.Instance[ProviderStrings.RowEditKey];

    public string DefaultText => Localization.Loc.Instance[ProviderStrings.RowDefaultKey];

    public string MakeDefaultText => Localization.Loc.Instance[ProviderStrings.RowMakeDefaultKey];

    /// <summary>"删除" first; after one click the same button reads "再次点击删除" until the second click or a reload.</summary>
    public string DeleteText => Localization.Loc.Instance[deleteArmed ? ProviderStrings.RowDeleteArmedKey : ProviderStrings.RowDeleteKey];

    /// <summary>
    /// The first click arms the delete, the second confirms it. Returns <c>true</c> only for the confirming click, so a stray
    /// click on a row that holds a credential cannot remove it.
    /// </summary>
    public bool ConfirmDelete()
    {
        if (deleteArmed)
        {
            return true;
        }

        deleteArmed = true;
        OnPropertyChanged(nameof(DeleteText));
        return false;
    }

    /// <summary>
    /// The sentence next to the name: where the key comes from, or which sign-in is in use. It never contains a key, only
    /// its variable name and the store's own state.
    /// </summary>
    private static string CredentialLine(ConnectedProvider provider, ProviderStatus status)
    {
        var loc = Localization.Loc.Instance;
        return provider.CredentialKind switch
        {
            ProviderCredentialKind.ApiKey when provider.HasStoredKey && !provider.KeyComesFromEnvironment =>
                loc.Format(ProviderStrings.StoredKeyReadyKey, ProviderStrings.ManagedText),
            ProviderCredentialKind.ApiKey when provider.KeyComesFromEnvironment =>
                loc.Format(
                    ProviderStrings.ReadyEnvironmentKey,
                    provider.EnvironmentVariable ?? string.Empty,
                    ProviderStrings.ManagedText),
            ProviderCredentialKind.ApiKey => loc.Format(
                ProviderStrings.NeedsKeyVariableKey,
                provider.EnvironmentVariable ?? string.Empty),
            ProviderCredentialKind.ClaudeSubscription => status.Detail.Resolve(),
            ProviderCredentialKind.GitHubCopilot => status.Detail.Resolve(),
            _ => status.Detail.Resolve(),
        };
    }
}
