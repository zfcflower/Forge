using System.Collections.ObjectModel;
using System.ComponentModel;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.ViewModels;

namespace Forge.App.Settings;

/// <summary>
/// One row of the <c>models-visibility</c> section: a model a connected provider offers and the switch that hides it. The
/// switch writes through to <see cref="AppPreferences.ModelsHidden"/>, so every picker reads the same set; the same model
/// served by two providers has two rows and both follow the one flag.
/// </summary>
internal sealed class ModelVisibilityRowViewModel : ObservableObject
{
    private readonly AppPreferences preferences;
    private bool isVisible;

    public ModelVisibilityRowViewModel(string name, string providerId, string providerName, AppPreferences preferences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(providerName);
        ArgumentNullException.ThrowIfNull(preferences);
        Name = name;
        ProviderId = providerId;
        ProviderName = providerName;
        this.preferences = preferences;
        isVisible = !preferences.IsModelHidden(name);
    }

    /// <summary>The model id as the provider lists it; the row label, never translated.</summary>
    public string Name { get; }

    public string ProviderId { get; }

    /// <summary>The provider serving this row, shown after the model name so two providers' rows are told apart.</summary>
    public string ProviderName { get; }

    /// <summary>The switch: on means the pickers offer this model.</summary>
    public bool IsVisible
    {
        get => isVisible;
        set
        {
            if (Set(ref isVisible, value))
            {
                preferences.SetModelHidden(Name, !value);
            }
        }
    }

    /// <summary>
    /// Takes a change made elsewhere (another provider's row for the same model, or a direct preference edit) without
    /// writing it back, so the section's own preferences subscription cannot loop through the setter.
    /// </summary>
    internal void Sync(bool visible) => Set(ref isVisible, visible, nameof(IsVisible));
}

/// <summary>
/// The <c>models-visibility</c> section: every model of every connected provider with one switch that hides it from the
/// session composer's picker and the built-in agents' default-model combo, plus a search box that only filters this page.
/// <para>The rows are built once per <see cref="Reload"/> (the settings page calls it when the section is selected, never on
/// a timer) in a single pass over the catalogue; the search re-fills the bound <see cref="Rows"/> from that same list, so a
/// keystroke allocates nothing and never re-reads the catalogue. Hiding is a write to
/// <see cref="AppPreferences.ModelsHidden"/> — the page keeps no state of its own.</para>
/// </summary>
internal sealed class ModelsVisibilitySectionViewModel : NotifyingObject, IDisposable, IReloadableSettingsSection
{
    private readonly ProviderServices services;
    private readonly AppPreferences preferences;

    /// <summary>Every row of the last <see cref="Reload"/>, in catalogue order; the search filters this list.</summary>
    private readonly List<ModelVisibilityRowViewModel> allRows = [];

    /// <summary>Reused by the filter: one list per section, not one per keystroke.</summary>
    private readonly List<ModelVisibilityRowViewModel> visibleRows = [];

    /// <summary>Reused by <see cref="RefreshHiddenCount"/> so counting distinct hidden names allocates nothing.</summary>
    private readonly HashSet<string> hiddenNames = new(StringComparer.Ordinal);

    private string searchText = string.Empty;
    private bool hasProviders;
    private int hiddenCount;
    private bool disposed;

    public ModelsVisibilitySectionViewModel(ProviderServices services, AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(preferences);
        this.services = services;
        this.preferences = preferences;
        preferences.PropertyChanged += OnPreferencesChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        ShowAllCommand = new RelayCommand(() => preferences.ModelsHidden = [], () => preferences.ModelsHidden.Count > 0);
    }

    /// <summary>
    /// Clears the whole hidden set, including ids no provider lists any more (a model that left the catalogue, a provider
    /// that is offline): such an id has no row to switch back on, so this is the one way to take it out.
    /// </summary>
    public RelayCommand ShowAllCommand { get; }

    public string ShowAllText => ModelsVisibilityStrings.ShowAll;

    /// <summary>The rows the search currently matches; a hidden model is still listed here, that is how it is unhidden.</summary>
    public ObservableCollection<ModelVisibilityRowViewModel> Rows { get; } = [];

    public string Hint => ModelsVisibilityStrings.Hint;

    public string SearchPlaceholder => ModelsVisibilityStrings.SearchPlaceholder;

    /// <summary>Heading and accessible name of the switch column.</summary>
    public string VisibilityColumn => ModelsVisibilityStrings.VisibilityColumn;

    /// <summary>The search box: filters this page only, so it is not a preference and lives nowhere else.</summary>
    public string SearchText
    {
        get => searchText;
        set
        {
            if (Set(ref searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>How many of the listed models are switched off, and the sentence that says it. A model two providers
    /// both serve is one hidden model, not two, which is what the preference holds.</summary>
    public int HiddenCount => hiddenCount;

    public string HiddenCountText => ModelsVisibilityStrings.HiddenCount(hiddenCount);

    /// <summary>True while a provider is connected; false shows <see cref="EmptyText"/> instead of the list.</summary>
    public bool HasProviders
    {
        get => hasProviders;
        private set => SetAndNotify(ref hasProviders, value, nameof(ShowsEmpty));
    }

    public bool ShowsEmpty => !hasProviders;

    /// <summary>What the section says while no provider is connected (its models would be the only thing to list).</summary>
    public string EmptyText => ModelsVisibilityStrings.Empty;

    /// <summary>
    /// Re-reads the live catalogue: one row per model per provider, in catalogue order. Idempotent — the settings page calls
    /// it every time the section is selected, and a second call over the same catalogue produces the same rows.
    /// </summary>
    public void Reload()
    {
        allRows.Clear();
        var catalog = services.Catalog;
        foreach (var provider in catalog.Providers)
        {
            var providerId = provider.Id;
            var providerName = provider.DisplayName;
            foreach (var model in provider.Models)
            {
                allRows.Add(new ModelVisibilityRowViewModel(model, providerId, providerName, preferences));
            }
        }

        HasProviders = catalog.Providers.Count > 0;
        ApplyFilter();
        RefreshHiddenCount();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        preferences.PropertyChanged -= OnPreferencesChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>Refills <see cref="Rows"/> from the built rows; the collection is only touched when the match set moved.</summary>
    private void ApplyFilter()
    {
        var query = searchText.Trim();
        visibleRows.Clear();
        foreach (var row in allRows)
        {
            if (query.Length == 0 ||
                row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.ProviderName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                visibleRows.Add(row);
            }
        }

        if (SameAsVisible())
        {
            return;
        }

        Rows.Clear();
        foreach (var row in visibleRows)
        {
            Rows.Add(row);
        }
    }

    private bool SameAsVisible()
    {
        if (Rows.Count != visibleRows.Count)
        {
            return false;
        }

        for (var index = 0; index < visibleRows.Count; index++)
        {
            if (!ReferenceEquals(Rows[index], visibleRows[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A visible/hidden flag moved — here or in another surface. The rows have no subscription of their own: one pass over
    /// the built rows keeps the duplicated rows of a shared model name in step, and the count follows.
    /// </summary>
    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (disposed || !string.Equals(e.PropertyName, nameof(AppPreferences.ModelsHidden), StringComparison.Ordinal))
        {
            return;
        }

        foreach (var row in allRows)
        {
            row.Sync(!preferences.IsModelHidden(row.Name));
        }

        RefreshHiddenCount();
        ShowAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Recounts the distinct hidden model names of the listed rows; the name set is reused, so a switch allocates nothing.</summary>
    private void RefreshHiddenCount()
    {
        hiddenNames.Clear();
        foreach (var row in allRows)
        {
            if (!row.IsVisible)
            {
                hiddenNames.Add(row.Name);
            }
        }

        if (hiddenCount == hiddenNames.Count)
        {
            return;
        }

        hiddenCount = hiddenNames.Count;
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HiddenCountText));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(VisibilityColumn));
        OnPropertyChanged(nameof(HiddenCountText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ShowAllText));
    }
}
