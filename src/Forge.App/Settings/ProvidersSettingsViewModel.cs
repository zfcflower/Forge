using System.Collections.ObjectModel;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Providers;

namespace Forge.App.Settings;

/// <summary>
/// The <c>provider</c> section: what is connected, what each one can do, and the two actions per row — edit it, or open its
/// model metadata. It owns no window: the view's code-behind opens the dialogs and calls back into
/// <see cref="SaveAsync"/>, so the page model stays testable without an Avalonia application.
/// </summary>
internal sealed class ProvidersSettingsViewModel : ObservableObject, IDisposable, IReloadableSettingsSection
{
    private readonly ProviderServices services;
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private bool saving;
    private string? problem;
    private string defaultContextWindowText = string.Empty;

    public ProvidersSettingsViewModel(ProviderServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;
        services.Changed += OnProvidersChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsSaving);
    }

    /// <summary>The provider services, so the view can build the editor and metadata windows from the same store.</summary>
    internal ProviderServices Services => services;

    public AsyncRelayCommand RefreshCommand { get; }

    public ObservableCollection<ProviderRowViewModel> Rows { get; } = [];

    /// <summary>The default provider's id, so a row can show which one new sessions start on.</summary>
    public string? DefaultId => services.Catalog.Default?.Id;

    public string Intro => Loc.Instance[ProviderStrings.SectionIntroKey];

    public string ConnectedTitle => Loc.Instance[ProviderStrings.SectionConnectedKey];

    public string AddServerText => Loc.Instance[ProviderStrings.SectionAddServerKey];

    public string CustomTitle => Loc.Instance[ProviderStrings.SectionCustomTitleKey];

    public string CustomHint => Loc.Instance[ProviderStrings.SectionCustomHintKey];

    public string RefreshText => Loc.Instance[ProviderStrings.SectionRefreshKey];

    public string DefaultContextWindowLabel => Loc.Instance[ProviderStrings.DefaultContextWindowKey];

    public string DefaultContextWindowHint => Loc.Instance[ProviderStrings.DefaultContextWindowHintKey];

    /// <summary>
    /// The global context window, as text while it is being typed. Empty means "use each model's catalog value"; the
    /// commit parses it and re-saves the section, so an invalid entry is reported instead of clearing the setting.
    /// </summary>
    public string DefaultContextWindowText
    {
        get => defaultContextWindowText;
        set => Set(ref defaultContextWindowText, value ?? string.Empty);
    }

    /// <summary>Why the list is empty or incomplete (a damaged settings file), or <c>null</c> when it is fine.</summary>
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
            Rows.Add(new ProviderRowViewModel(provider, string.Equals(provider.Id, catalog.Default?.Id, StringComparison.Ordinal)));
        }

        // A damaged secret store is a different problem from "no provider configured": it says the keys cannot be read,
        // which is exactly the case where a row must not claim a missing key.
        DefaultContextWindowText = catalog.DefaultContextWindow?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                  ?? string.Empty;
        Problem = services.Secrets.Problem ?? (catalog.IsConfigured ? null : catalog.StatusText.Resolve());
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
            await services.SaveAsync(current, defaultId, ParseDefaultContextWindow(), cancellationToken).ConfigureAwait(true);
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

    /// <summary>
    /// Stores one typed key for an already-saved definition, on the pool. Kept separate from <see cref="SaveAsync"/> so the
    /// settings write is the thing that can fail: a refused write never replaces the key an existing provider still uses.
    /// </summary>
    public async Task SetSecretAsync(ProviderDefinition definition, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.SecretReference))
        {
            return;
        }

        var reference = definition.SecretReference;
        try
        {
            await Task.Run(() => services.Secrets.Set(reference, secret), cancellationToken).ConfigureAwait(true);
            Problem = services.Secrets.Problem;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Problem = exception.Message;
        }

        OnPropertyChanged(nameof(HasProblem));
    }

    /// <summary>The "clear key" half of <see cref="SetSecretAsync"/>.</summary>
    public async Task ClearSecretAsync(ProviderDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.SecretReference))
        {
            return;
        }

        var reference = definition.SecretReference;
        try
        {
            await Task.Run(() => services.Secrets.Remove(reference), cancellationToken).ConfigureAwait(true);
            Problem = services.Secrets.Problem;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Problem = exception.Message;
        }

        OnPropertyChanged(nameof(HasProblem));
    }

    /// <summary>Re-reads <c>settings.json</c> and rebuilds the rows: a hand edit or a key exported since startup shows up.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await services.ReloadAsync(cancellationToken).ConfigureAwait(true);
        Reload();
    }

    /// <summary>
    /// Commits the global context window typed in the section. An entry that is neither empty nor a positive number is
    /// reported and not saved — clearing the setting has to be an explicit empty box, not a typo.
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

    private bool TryParseDefaultContextWindow(out int? window)
    {
        var text = DefaultContextWindowText.Trim();
        if (text.Length == 0)
        {
            window = null;
            return true;
        }

        if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            window = parsed;
            return true;
        }

        window = null;
        return false;
    }

    private int? ParseDefaultContextWindow() => TryParseDefaultContextWindow(out var window) ? window : services.Catalog.DefaultContextWindow;

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
        services.Changed -= OnProvidersChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
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

        OnPropertyChanged(nameof(Intro));
        OnPropertyChanged(nameof(ConnectedTitle));
        OnPropertyChanged(nameof(AddServerText));
        OnPropertyChanged(nameof(CustomTitle));
        OnPropertyChanged(nameof(CustomHint));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(DefaultContextWindowLabel));
        OnPropertyChanged(nameof(DefaultContextWindowHint));
        Reload();
    }
}

/// <summary>One connected provider as the section lists it: name, credential line, status and the two row actions.</summary>
internal sealed class ProviderRowViewModel : ObservableObject
{
    public ProviderRowViewModel(ConnectedProvider provider, bool isDefault)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Provider = provider;
        IsDefault = isDefault;
        Status = provider.Status;
        ModelsLabel = ProvidersSettingsViewModel.ModelsText(provider);
        CredentialText = CredentialLine(provider, Status);
    }

    public ConnectedProvider Provider { get; }

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
