using System.Collections.ObjectModel;
using System.Globalization;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Providers;

namespace Forge.App.Settings;

/// <summary>
/// The model metadata window: one row per model the provider offers, with the context window and the four prices, editable
/// and written back to <c>settings.json</c>'s <c>modelMeta</c> override. Empty means unknown — it is stored as an absent
/// field, never as zero, because a zero window or price would put a fake "% used" or "$0.00" on every session.
/// </summary>
internal sealed class ModelMetadataViewModel : NotifyingObject, IDisposable
{
    private readonly ProviderServices services;
    private readonly string providerId;
    private readonly bool billsCacheWrite;
    private string? status;
    private bool isBusy;
    private int uiThread = Environment.CurrentManagedThreadId;

    public ModelMetadataViewModel(ProviderServices services, ConnectedProvider provider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(provider);
        this.services = services;
        providerId = provider.Id;
        ProviderName = provider.DisplayName;
        // The fourth price is a real Anthropic number (cache writes cost 1.25x/2x input); for the others it is always zero,
        // so the column would be an empty box pretending to be a missing value.
        billsCacheWrite = provider.Protocol == ProviderProtocol.Anthropic ||
                          provider.Definition.Overrides.Values.Any(static metadata => metadata.Pricing is { CacheWrite: > 0 });
        Reload(provider);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync(), () => !IsBusy);
        ResetCommand = new RelayCommand(Reset);
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public string ProviderName { get; }

    public string Title => Loc.Instance.Format(ProviderStrings.MetadataTitleKey, ProviderName);

    public string Intro => Loc.Instance[ProviderStrings.MetadataIntroKey];

    public string ModelHeader => Loc.Instance[ProviderStrings.MetadataModelKey];

    /// <summary>The column of what the model accepts (input kinds and reasoning), which is read-only.</summary>
    public string CapabilitiesHeader => ProviderStrings.MetadataCapabilities;

    public string ContextHeader => Loc.Instance[ProviderStrings.MetadataContextKey];

    public string InputHeader => Loc.Instance[ProviderStrings.MetadataInputKey];

    public string OutputHeader => Loc.Instance[ProviderStrings.MetadataOutputKey];

    public string CacheReadHeader => Loc.Instance[ProviderStrings.MetadataCacheReadKey];

    public string CacheWriteHeader => Loc.Instance[ProviderStrings.MetadataCacheWriteKey];

    /// <summary>Whether the cache-write price column is shown at all (protocol-billed or already set on a model).</summary>
    public bool ShowsCacheWriteColumn => billsCacheWrite ||
                                        Rows.Any(static row => row.ToSection()?.CacheWrite is > 0);

    public string PerMillion => Loc.Instance[ProviderStrings.MetadataPerMillionKey];

    public string SaveText => Loc.Instance[ProviderStrings.MetadataSaveKey];

    public string RefreshText => Loc.Instance[ProviderStrings.MetadataRefreshKey];

    public string ResetText => Loc.Instance[ProviderStrings.MetadataResetKey];

    public string CloseText => Loc.Instance[ProviderStrings.MetadataCloseKey];

    public string EmptyText => Loc.Instance[ProviderStrings.MetadataEmptyKey];

    public ObservableCollection<ModelMetadataRowViewModel> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public string? Status
    {
        get => status;
        private set => SetAndNotify(ref status, value, nameof(HasStatus));
    }

    public bool HasStatus => Status is { Length: > 0 };

    /// <summary>True while a refresh or a save is in flight; the footer disables its buttons then.</summary>
    public bool IsBusy => isBusy;

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand ResetCommand { get; }

    /// <summary>Raised when the window should close itself.</summary>
    public event EventHandler? CloseRequested;

    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private bool ShowsCacheWriteFor(ModelMetadata metadata) =>
        billsCacheWrite || metadata.Pricing is { CacheWrite: > 0 };

    public void Reload(ConnectedProvider provider)
    {
        Rows.Clear();
        foreach (var model in provider.Models)
        {
            // The editable cells start from the user's override, but the read-only capability column is what the chain
            // resolved (catalog → provider → override): an override never carries capabilities, so reading them from it
            // would leave the column blank for every model the catalog knows.
            var overridden = provider.Definition.Overrides.TryGetValue(model, out var stored)
                ? stored
                : ModelMetadata.Unknown;
            var resolved = provider.MetadataFor(model);
            Rows.Add(new ModelMetadataRowViewModel(
                model,
                overridden,
                ShowsCacheWriteFor(overridden),
                resolved.Capabilities));
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowsCacheWriteColumn));
    }

    public void Dispose() => Loc.Instance.LanguageChanged -= OnLanguageChanged;

    /// <summary>Builds the override map the editor's rows describe; a row with every field empty is left out.</summary>
    internal static Dictionary<string, ModelMetadataSection> ToOverrides(IEnumerable<ModelMetadataRowViewModel> rows)
    {
        var overrides = new Dictionary<string, ModelMetadataSection>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var section = row.ToSection();
            if (section is not null)
            {
                overrides[row.Model] = section;
            }
        }

        return overrides;
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var provider = services.Catalog.Find(providerId);
        if (provider is null)
        {
            return;
        }

        isBusy = true;
        OnPropertyChanged(nameof(IsBusy));
        try
        {
            var list = await services.RefreshModelsAsync(provider).ConfigureAwait(true);
            if (Environment.CurrentManagedThreadId != uiThread)
            {
                return;
            }

            Rows.Clear();
            foreach (var model in list.Ids)
            {
                var metadata = provider.Definition.Overrides.TryGetValue(model, out var overridden)
                    ? overridden
                    : provider.MetadataFor(model);
                Rows.Add(new ModelMetadataRowViewModel(model, metadata, ShowsCacheWriteFor(metadata)));
            }

            OnPropertyChanged(nameof(HasRows));
            Status = Loc.Instance.Format(ProviderStrings.EditorModelsFetchedKey, TextFormat.Number(list.Ids.Count));
        }
        catch (Exception exception) when (exception is ProviderException or HttpRequestException or IOException or TimeoutException)
        {
            Status = exception.Message;
        }
        finally
        {
            isBusy = false;
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    private async Task SaveAsync()
    {
        var provider = services.Catalog.Find(providerId);
        if (provider is null)
        {
            return;
        }

        var overrides = ToOverrides(Rows);
        var edited = provider.Definition with
        {
            Overrides = ProviderDefinition.FromMetadataDocument(overrides),
        };
        var definitions = services.Catalog.Providers
            .Select(existing => string.Equals(existing.Id, providerId, StringComparison.Ordinal) ? edited : existing.Definition)
            .ToList();

        isBusy = true;
        OnPropertyChanged(nameof(IsBusy));
        try
        {
            await services.SaveAsync(definitions, services.Catalog.Default?.Id, services.Catalog.DefaultContextWindow)
                .ConfigureAwait(true);
            Status = Loc.Instance[ProviderStrings.MetadataSavedKey];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = exception.Message;
        }
        finally
        {
            isBusy = false;
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    private void Reset()
    {
        var provider = services.Catalog.Find(providerId);
        if (provider is null)
        {
            return;
        }

        Rows.Clear();
        foreach (var model in provider.Models)
        {
            Rows.Add(new ModelMetadataRowViewModel(model, provider.MetadataFor(model)));
        }

        OnPropertyChanged(nameof(HasRows));
        Status = Loc.Instance[ProviderStrings.MetadataResetKey];
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        foreach (var name in new[]
                 {
                     nameof(Title), nameof(Intro), nameof(ModelHeader), nameof(CapabilitiesHeader), nameof(ContextHeader), nameof(InputHeader),
                     nameof(OutputHeader), nameof(CacheReadHeader), nameof(CacheWriteHeader), nameof(PerMillion),
                     nameof(SaveText), nameof(RefreshText), nameof(ResetText), nameof(CloseText), nameof(EmptyText),
                 })
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>One model's editable metadata. Numbers stay strings while editing so a half-typed value is not rounded away.</summary>
internal sealed class ModelMetadataRowViewModel : ObservableObject
{
    private string contextWindow;
    private string input;
    private string output;
    private string cacheRead;
    private string cacheWrite;

    public ModelMetadataRowViewModel(
        string model,
        ModelMetadata metadata,
        bool showCacheWrite = true,
        ModelCapabilities? capabilities = null)
    {
        Model = model;
        ShowCacheWrite = showCacheWrite;
        Capabilities = ProviderStrings.CapabilitiesText(capabilities ?? metadata.Capabilities);
        contextWindow = Format(metadata.ContextWindow);
        input = Format(metadata.Pricing?.Input);
        output = Format(metadata.Pricing?.Output);
        cacheRead = Format(metadata.Pricing?.CacheRead);
        cacheWrite = Format(metadata.Pricing?.CacheWrite);
    }

    public string Model { get; }

    /// <summary>The read-only capability summary ("text·image · reasoning × 4"); empty when nothing described the model.</summary>
    public string Capabilities { get; }

    /// <summary>Whether this row shows the cache-write price; hidden for providers that never bill it.</summary>
    public bool ShowCacheWrite { get; }

    public string ContextWindow
    {
        get => contextWindow;
        set => Set(ref contextWindow, value ?? string.Empty);
    }

    public string Input
    {
        get => input;
        set => Set(ref input, value ?? string.Empty);
    }

    public string Output
    {
        get => output;
        set => Set(ref output, value ?? string.Empty);
    }

    public string CacheRead
    {
        get => cacheRead;
        set => Set(ref cacheRead, value ?? string.Empty);
    }

    public string CacheWrite
    {
        get => cacheWrite;
        set => Set(ref cacheWrite, value ?? string.Empty);
    }

    /// <summary>The row as stored metadata; <c>null</c> when every field is empty (nothing is known, nothing is written).</summary>
    public ModelMetadataSection? ToSection()
    {
        var window = ParsePositive(ContextWindow);
        var inputPrice = ParsePrice(Input);
        var outputPrice = ParsePrice(Output);
        var cacheReadPrice = ParsePrice(CacheRead);
        var cacheWritePrice = ParsePrice(CacheWrite);
        if (window is null && inputPrice is null && outputPrice is null && cacheReadPrice is null && cacheWritePrice is null)
        {
            return null;
        }

        return new ModelMetadataSection
        {
            ContextWindow = window,
            Input = inputPrice,
            Output = outputPrice,
            CacheRead = cacheReadPrice,
            CacheWrite = cacheWritePrice,
        };
    }

    private static string Format(int? value) => value is > 0 ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static string Format(double? value) => value is not null
        ? value.Value.ToString("0.######", CultureInfo.InvariantCulture)
        : string.Empty;

    private static int? ParsePositive(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

    private static double? ParsePrice(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
}
