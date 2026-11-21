using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Providers;

namespace Forge.App.Settings;

/// <summary>
/// The model metadata window: one row per model the provider offers, with the context window, the four prices and the switch
/// that decides whether the model is offered by the pickers. Values are editable and written back to <c>settings.json</c>'s
/// <c>modelMeta</c> override; empty means unknown — it is stored as an absent field, never as zero, because a zero window or
/// price would put a fake "% used" or "$0.00" on every session.
/// <para>The visibility switch is staged like the metadata: nothing is written until Save, so a closed window leaves both
/// the model metadata and <see cref="AppPreferences.ModelsHidden"/> as they were. Saving writes the definitions first and
/// only then the hidden set, so a refused definition write never hides a model.</para>
/// </summary>
internal sealed class ModelMetadataViewModel : NotifyingObject, IDisposable
{
    private readonly ProviderServices services;
    private readonly AppPreferences? preferences;
    private readonly string providerId;
    private readonly bool billsCacheWrite;
    private string? status;
    private bool statusIsError;
    private bool isBusy;

    /// <summary>True while <see cref="AllShown"/> writes every row, so the rows' own notifications stay quiet.</summary>
    private bool bulkShown;
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    public ModelMetadataViewModel(ProviderServices services, ConnectedProvider provider, AppPreferences? preferences = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(provider);
        this.services = services;
        this.preferences = preferences;
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
        if (preferences is not null)
        {
            preferences.PropertyChanged += OnPreferencesChanged;
        }

        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public string ProviderName { get; }

    public string Title => Loc.Instance.Format(ProviderStrings.MetadataTitleKey, ProviderName);

    public string Intro => Loc.Instance[ProviderStrings.MetadataIntroKey];

    public string ModelHeader => Loc.Instance[ProviderStrings.MetadataModelKey];

    /// <summary>The column of what the model accepts (input kinds), which is read-only.</summary>
    public string CapabilitiesHeader => ProviderStrings.MetadataCapabilities;

    /// <summary>The column of the reasoning tiers the model offers, which is read-only.</summary>
    public string ReasoningHeader => ProviderStrings.MetadataReasoning;

    public string ContextHeader => Loc.Instance[ProviderStrings.MetadataContextKey];

    public string InputHeader => Loc.Instance[ProviderStrings.MetadataInputKey];

    public string OutputHeader => Loc.Instance[ProviderStrings.MetadataOutputKey];

    public string CacheReadHeader => Loc.Instance[ProviderStrings.MetadataCacheReadKey];

    public string CacheWriteHeader => Loc.Instance[ProviderStrings.MetadataCacheWriteKey];

    public string VisibilityHeader => ProviderStrings.MetadataVisibilityHeader;

    public string VisibilityTooltip => ProviderStrings.MetadataVisibilityTooltip;

    /// <summary>Whether the visibility column exists: only when the window was given the live preferences.</summary>
    public bool HasVisibilityColumn => preferences is not null;

    /// <summary>Whether the cache-write price column is shown at all (protocol-billed or already set on a model).</summary>
    public bool ShowsCacheWriteColumn => billsCacheWrite || Rows.Any(static row => row.ShowCacheWrite);

    public string PerMillion => Loc.Instance[ProviderStrings.MetadataPerMillionKey];

    public string SaveText => Loc.Instance[ProviderStrings.MetadataSaveKey];

    public string RefreshText => Loc.Instance[ProviderStrings.MetadataRefreshKey];

    public string ResetText => Loc.Instance[ProviderStrings.MetadataResetKey];

    public string CloseText => Loc.Instance[ProviderStrings.MetadataCloseKey];

    public string EmptyText => Loc.Instance[ProviderStrings.MetadataEmptyKey];

    public ObservableCollection<ModelMetadataRowViewModel> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    /// <summary>True when every row is shown; the first-column header checkbox selects or clears the whole list.</summary>
    public bool AllShown
    {
        get => Rows.Count > 0 && Rows.All(static row => row.IsShown);
        set
        {
            bulkShown = true;
            try
            {
                foreach (var row in Rows)
                {
                    row.SetShown(value);
                }
            }
            finally
            {
                bulkShown = false;
            }

            OnPropertyChanged();
        }
    }

    public string? Status => status;

    public bool HasStatus => status is { Length: > 0 };

    /// <summary>True when the footer line reports a validation/save/refresh failure, so it paints in the error colour.</summary>
    public bool StatusIsError => statusIsError;

    /// <summary>True while a refresh or a save is in flight; the footer disables its buttons then.</summary>
    public bool IsBusy => isBusy;

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand ResetCommand { get; }

    /// <summary>Raised when the window should close itself.</summary>
    public event EventHandler? CloseRequested;

    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void Reload(ConnectedProvider provider)
    {
        ReplaceRows(provider.Models.Select(model => new RowSeed(
            model,
            EditableMetadata(provider, model),
            provider.MetadataFor(model).Capabilities)));
    }

    public void Dispose()
    {
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        if (preferences is not null)
        {
            preferences.PropertyChanged -= OnPreferencesChanged;
        }

        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }
    }

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

    private static ModelMetadata EditableMetadata(ConnectedProvider provider, string model) =>
        provider.Definition.Overrides.TryGetValue(model, out var stored) ? stored : ModelMetadata.Unknown;

    private void ReplaceRows(IEnumerable<RowSeed> seeds)
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }

        Rows.Clear();
        var list = seeds as IList<RowSeed> ?? seeds.ToList();
        var showCacheWrite = billsCacheWrite || list.Any(static seed => seed.Metadata.Pricing?.CacheWrite is not null);
        foreach (var seed in list)
        {
            var row = new ModelMetadataRowViewModel(
                seed.Model,
                seed.Metadata,
                showCacheWrite: showCacheWrite,
                capabilities: seed.Capabilities,
                isShown: preferences is null || !preferences.IsModelHidden(seed.Model));
            row.PropertyChanged += OnRowPropertyChanged;
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowsCacheWriteColumn));
        OnPropertyChanged(nameof(AllShown));
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

            ReplaceRows(list.Ids.Select(model => new RowSeed(
                model,
                EditableMetadata(provider, model),
                provider.MetadataFor(model).Capabilities)));
            SetStatus(Loc.Instance.Format(ProviderStrings.EditorModelsFetchedKey, TextFormat.Number(list.Ids.Count)), isError: false);
        }
        catch (Exception exception) when (exception is ProviderException or HttpRequestException or IOException or TimeoutException)
        {
            SetStatus(exception.Message, isError: true);
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

        foreach (var row in Rows)
        {
            row.Validate();
        }

        var invalid = Rows.Count(static row => row.HasErrors);
        if (invalid > 0)
        {
            SetStatus(InputValidationStrings.SaveBlocked(invalid), isError: true);
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
            ApplyVisibility();
            SetStatus(Loc.Instance[ProviderStrings.MetadataSavedKey], isError: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus(exception.Message, isError: true);
        }
        finally
        {
            isBusy = false;
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    /// <summary>
    /// Writes this provider's rows into the shared hidden set. Names served by other providers are untouched, so a model
    /// hidden for another provider stays hidden unless that provider's window (or this one for the same name) shows it.
    /// </summary>
    private void ApplyVisibility()
    {
        if (preferences is null || Rows.Count == 0)
        {
            return;
        }

        var names = Rows.Select(static row => row.Model).ToHashSet(StringComparer.Ordinal);
        var hidden = preferences.ModelsHidden
            .Where(name => !names.Contains(name))
            .ToList();
        hidden.AddRange(Rows.Where(static row => !row.IsShown).Select(static row => row.Model));
        preferences.ModelsHidden = hidden;
    }

    private void Reset()
    {
        var provider = services.Catalog.Find(providerId);
        if (provider is null)
        {
            return;
        }

        ReplaceRows(provider.Models.Select(model => new RowSeed(
            model,
            provider.MetadataFor(model),
            provider.MetadataFor(model).Capabilities)));
        SetStatus(Loc.Instance[ProviderStrings.MetadataResetKey], isError: false);
    }

    private void SetStatus(string? value, bool isError)
    {
        status = value;
        statusIsError = isError;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(StatusIsError));
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(ModelMetadataRowViewModel.IsShown), StringComparison.Ordinal))
        {
            // A select-all runs through every row: reporting the running total would re-read the whole list per row, and the
            // view would push each intermediate value back into the header switch.
            if (!bulkShown)
            {
                OnPropertyChanged(nameof(AllShown));
            }
        }
        else if (string.Equals(e.PropertyName, nameof(ModelMetadataRowViewModel.CacheWrite), StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(ShowsCacheWriteColumn));
        }
    }

    /// <summary>A visibility save from another provider's window reaches this one without writing anything back.</summary>
    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (preferences is null ||
            !string.Equals(e.PropertyName, nameof(AppPreferences.ModelsHidden), StringComparison.Ordinal))
        {
            return;
        }

        foreach (var row in Rows)
        {
            row.SetShown(!preferences.IsModelHidden(row.Model));
        }

        OnPropertyChanged(nameof(AllShown));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        foreach (var name in new[]
                 {
                     nameof(Title), nameof(Intro), nameof(ModelHeader), nameof(CapabilitiesHeader), nameof(ReasoningHeader), nameof(ContextHeader), nameof(InputHeader),
                     nameof(OutputHeader), nameof(CacheReadHeader), nameof(CacheWriteHeader), nameof(VisibilityHeader),
                     nameof(VisibilityTooltip), nameof(PerMillion), nameof(SaveText), nameof(RefreshText), nameof(ResetText),
                     nameof(CloseText), nameof(EmptyText),
                 })
        {
            OnPropertyChanged(name);
        }

        foreach (var row in Rows)
        {
            row.RefreshText();
        }
    }

    private readonly record struct RowSeed(string Model, ModelMetadata Metadata, ModelCapabilities? Capabilities);
}

/// <summary>
/// One model's editable metadata. Each number is a <c>decimal?</c> bound to a cell's <c>Value</c>, so an empty cell is the
/// absence of a number rather than an empty string that has yet to be parsed: unknown survives as null all the way into the
/// stored section, where a 0 would have meant a free model with a zero window.
/// </summary>
internal sealed class ModelMetadataRowViewModel : ObservableObject
{
    /// <summary>
    /// The top of a cell's number line. The control refuses a typed value past its bounds, so the bound has to be the top
    /// of the range a number can have at all: an unknown price or a long context window must reach the cell's rules, not be
    /// dropped by the field. Zero is the bottom - a negative window or price is what <c>InputRules</c> rejects into a red cell.
    /// </summary>
    public const decimal MaxCellValue = decimal.MaxValue;

    private decimal? contextWindow;
    private decimal? input;
    private decimal? output;
    private decimal? cacheRead;
    private decimal? cacheWrite;
    private bool isShown;

    private readonly ModelCapabilities capabilityValue;
    private readonly int? reasoningSteps;
    private InputError? contextWindowError;
    private InputError? inputError;
    private InputError? outputError;
    private InputError? cacheReadError;
    private InputError? cacheWriteError;
    private InputError? priceSetError;

    public ModelMetadataRowViewModel(
        string model,
        ModelMetadata metadata,
        bool showCacheWrite = true,
        ModelCapabilities? capabilities = null,
        bool isShown = true)
    {
        Model = model;
        ShowCacheWrite = showCacheWrite;
        this.isShown = isShown;
        capabilityValue = capabilities ?? metadata.Capabilities ?? ModelCapabilities.Unknown;
        var inputs = capabilityValue.Inputs ?? ModelInputs.None;
        HasTextInput = (inputs & ModelInputs.Text) != 0;
        HasImageInput = (inputs & ModelInputs.Image) != 0;
        HasPdfInput = (inputs & ModelInputs.Pdf) != 0;
        HasAudioInput = (inputs & ModelInputs.Audio) != 0;
        HasVideoInput = (inputs & ModelInputs.Video) != 0;
        if (capabilityValue.ReasoningSteps is { Count: > 1 } steps)
        {
            HasReasoning = true;
            reasoningSteps = steps.Count;
            ReasoningText = string.Join(
                "/",
                steps.Where(static effort => effort != ThinkingEffort.Off).Select(SessionStrings.ComposerEffortName));
        }
        else if (capabilityValue.Reasoning == true)
        {
            HasReasoning = true;
        }
        contextWindow = metadata.ContextWindow is > 0 ? metadata.ContextWindow.Value : null;
        input = ToDecimal(metadata.Pricing?.Input);
        output = ToDecimal(metadata.Pricing?.Output);
        cacheRead = ToDecimal(metadata.Pricing?.CacheRead);
        cacheWrite = ToDecimal(metadata.Pricing?.CacheWrite);
        Validate();
    }

    public string Model { get; }

    /// <summary>The read-only capability summary, kept as the cell's tooltip and accessible name.</summary>
    public string CapabilitiesText => ProviderStrings.CapabilitiesText(capabilityValue);

    public bool HasTextInput { get; }

    public bool HasImageInput { get; }

    public bool HasPdfInput { get; }

    public bool HasAudioInput { get; }

    public bool HasVideoInput { get; }

    public bool HasReasoning { get; }

    /// <summary>The tiers this model offers ("低/中/高"), empty when only "it reasons" is known.</summary>
    public string ReasoningText { get; } = string.Empty;

    public bool HasReasoningText => ReasoningText.Length > 0;

    public string ReasoningLabel => HasReasoning ? ProviderStrings.CapabilityReasoningLabel(reasoningSteps) : string.Empty;

    public string TextCapabilityLabel => ProviderStrings.CapabilityTextLabel;

    public string ImageCapabilityLabel => ProviderStrings.CapabilityImageLabel;

    public string PdfCapabilityLabel => ProviderStrings.CapabilityPdfLabel;

    public string AudioCapabilityLabel => ProviderStrings.CapabilityAudioLabel;

    public string VideoCapabilityLabel => ProviderStrings.CapabilityVideoLabel;

    /// <summary>Whether this row shows the cache-write price; hidden for providers that never bill it.</summary>
    public bool ShowCacheWrite { get; }

    /// <summary>Staged visibility; written to <c>preferences.modelsHidden</c> only when the window saves.</summary>
    public bool IsShown
    {
        get => isShown;
        set => Set(ref isShown, value);
    }

    /// <summary>Applies a value from the preference without sounding like a user edit.</summary>
    internal void SetShown(bool value) => Set(ref isShown, value, nameof(IsShown));

    public decimal? ContextWindow
    {
        get => contextWindow;
        set
        {
            if (Set(ref contextWindow, value))
            {
                OnPropertyChanged(nameof(ContextWindowText));
                SetError(ref contextWindowError, WindowRule(contextWindow),
                    nameof(ContextWindowError), nameof(HasContextWindowError));
            }
        }
    }

    public decimal? Input
    {
        get => input;
        set
        {
            if (Set(ref input, value))
            {
                OnPropertyChanged(nameof(InputText));
                SetError(ref inputError, PriceRule(input), nameof(InputPriceError), nameof(HasInputPriceError));
                ValidatePriceSet();
            }
        }
    }

    public decimal? Output
    {
        get => output;
        set
        {
            if (Set(ref output, value))
            {
                OnPropertyChanged(nameof(OutputText));
                SetError(ref outputError, PriceRule(output), nameof(OutputPriceError), nameof(HasOutputPriceError));
                ValidatePriceSet();
            }
        }
    }

    public decimal? CacheRead
    {
        get => cacheRead;
        set
        {
            if (Set(ref cacheRead, value))
            {
                OnPropertyChanged(nameof(CacheReadText));
                SetError(ref cacheReadError, PriceRule(cacheRead), nameof(CacheReadPriceError), nameof(HasCacheReadPriceError));
                ValidatePriceSet();
            }
        }
    }

    public decimal? CacheWrite
    {
        get => cacheWrite;
        set
        {
            if (Set(ref cacheWrite, value))
            {
                OnPropertyChanged(nameof(CacheWriteText));
                SetError(ref cacheWriteError, PriceRule(cacheWrite), nameof(CacheWritePriceError), nameof(HasCacheWritePriceError));
                ValidatePriceSet();
            }
        }
    }

    /// <summary>
    /// The five numbers as a cell shows them while it is not being edited: nothing at all when the value is unknown (never a
    /// zero) and the column's own format otherwise. The cell is a plain text until the user clicks it, so this string is the
    /// resting table's whole number rendering - it is what keeps a field per cell out of the row.
    /// </summary>
    public string ContextWindowText => Display(contextWindow, "0");

    public string InputText => Display(input, "0.######");

    public string OutputText => Display(output, "0.######");

    public string CacheReadText => Display(cacheRead, "0.######");

    public string CacheWriteText => Display(cacheWrite, "0.######");

    public string? ContextWindowError => Describe(contextWindowError);

    public bool HasContextWindowError => contextWindowError is not null;

    public string? InputPriceError => Describe(inputError);

    public bool HasInputPriceError => inputError is not null;

    public string? OutputPriceError => Describe(outputError);

    public bool HasOutputPriceError => outputError is not null;

    public string? CacheReadPriceError => Describe(cacheReadError);

    public bool HasCacheReadPriceError => cacheReadError is not null;

    public string? CacheWritePriceError => Describe(cacheWriteError);

    public bool HasCacheWritePriceError => cacheWriteError is not null;

    public string? PriceSetError => Describe(priceSetError);

    public bool HasPriceSetError => priceSetError is not null;

    public bool InputPriceInvalid => HasInputPriceError || HasPriceSetError;

    public bool OutputPriceInvalid => HasOutputPriceError || HasPriceSetError;

    public bool CacheReadPriceInvalid => HasCacheReadPriceError || HasPriceSetError;

    public bool CacheWritePriceInvalid => HasCacheWritePriceError || HasPriceSetError;

    public string? InputPriceTooltip => InputPriceError ?? PriceSetError;

    public string? OutputPriceTooltip => OutputPriceError ?? PriceSetError;

    public string? CacheReadPriceTooltip => CacheReadPriceError ?? PriceSetError;

    public string? CacheWritePriceTooltip => CacheWritePriceError ?? PriceSetError;

    public bool HasErrors =>
        contextWindowError is not null ||
        priceSetError is not null ||
        inputError is not null ||
        outputError is not null ||
        cacheReadError is not null ||
        cacheWriteError is not null;

    /// <summary>Runs every field rule; called for fresh rows and again before a save so stored invalid values cannot slip through.</summary>
    internal void Validate()
    {
        SetError(ref contextWindowError, WindowRule(contextWindow), nameof(ContextWindowError), nameof(HasContextWindowError));
        SetError(ref inputError, PriceRule(input), nameof(InputPriceError), nameof(HasInputPriceError));
        SetError(ref outputError, PriceRule(output), nameof(OutputPriceError), nameof(HasOutputPriceError));
        SetError(ref cacheReadError, PriceRule(cacheRead), nameof(CacheReadPriceError), nameof(HasCacheReadPriceError));
        SetError(
            ref cacheWriteError,
            ShowCacheWrite ? PriceRule(cacheWrite) : null,
            nameof(CacheWritePriceError),
            nameof(HasCacheWritePriceError));
        ValidatePriceSet();
    }

    /// <summary>Prices are a package: one filled cell without the rest would make the converter write 0 for the gaps.</summary>
    private void ValidatePriceSet()
    {
        var hasPrice = Input is not null || Output is not null || CacheRead is not null || (ShowCacheWrite && CacheWrite is not null);
        var allPrices = Input is not null && Output is not null && CacheRead is not null && (!ShowCacheWrite || CacheWrite is not null);
        SetError(
            ref priceSetError,
            hasPrice && !allPrices ? new InputError(InputRuleKind.PriceSet) : null,
            nameof(PriceSetError),
            nameof(HasPriceSetError));
        OnPropertyChanged(nameof(InputPriceInvalid));
        OnPropertyChanged(nameof(OutputPriceInvalid));
        OnPropertyChanged(nameof(CacheReadPriceInvalid));
        OnPropertyChanged(nameof(CacheWritePriceInvalid));
        OnPropertyChanged(nameof(InputPriceTooltip));
        OnPropertyChanged(nameof(OutputPriceTooltip));
        OnPropertyChanged(nameof(CacheReadPriceTooltip));
        OnPropertyChanged(nameof(CacheWritePriceTooltip));
    }

    /// <summary>Re-reads the shared messages after a language change; the rules themselves did not change.</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(ContextWindowError));
        OnPropertyChanged(nameof(InputPriceError));
        OnPropertyChanged(nameof(OutputPriceError));
        OnPropertyChanged(nameof(CacheReadPriceError));
        OnPropertyChanged(nameof(CacheWritePriceError));
        OnPropertyChanged(nameof(PriceSetError));
        OnPropertyChanged(nameof(InputPriceInvalid));
        OnPropertyChanged(nameof(OutputPriceInvalid));
        OnPropertyChanged(nameof(CacheReadPriceInvalid));
        OnPropertyChanged(nameof(CacheWritePriceInvalid));
        OnPropertyChanged(nameof(InputPriceTooltip));
        OnPropertyChanged(nameof(OutputPriceTooltip));
        OnPropertyChanged(nameof(CacheReadPriceTooltip));
        OnPropertyChanged(nameof(CacheWritePriceTooltip));
        OnPropertyChanged(nameof(CapabilitiesText));
        OnPropertyChanged(nameof(ReasoningLabel));
        OnPropertyChanged(nameof(TextCapabilityLabel));
        OnPropertyChanged(nameof(ImageCapabilityLabel));
        OnPropertyChanged(nameof(PdfCapabilityLabel));
        OnPropertyChanged(nameof(AudioCapabilityLabel));
        OnPropertyChanged(nameof(VideoCapabilityLabel));
    }

    /// <summary>The row as stored metadata; <c>null</c> when every field is empty (nothing is known, nothing is written).</summary>
    public ModelMetadataSection? ToSection()
    {
        if (contextWindow is null && input is null && output is null && cacheRead is null && cacheWrite is null)
        {
            return null;
        }

        return new ModelMetadataSection
        {
            ContextWindow = contextWindow is { } window ? (int)window : null,
            Input = input is { } inputPrice ? (double)inputPrice : null,
            Output = output is { } outputPrice ? (double)outputPrice : null,
            CacheRead = cacheRead is { } readPrice ? (double)readPrice : null,
            CacheWrite = cacheWrite is { } writePrice ? (double)writePrice : null,
        };
    }

    private static string? Describe(InputError? error) =>
        error is null ? null : InputValidationStrings.Describe(error);

    private void SetError(ref InputError? field, InputError? value, string errorName, string hasErrorName)
    {
        if (Nullable.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(errorName);
        OnPropertyChanged(hasErrorName);
    }

    /// <summary>One number in a cell's own format, or nothing when it is unknown.</summary>
    private static string Display(decimal? value, string format) =>
        value?.ToString(format, CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// The window is a whole number of tokens: a typed "1.5" or "0" is a red cell rather than a value that would make every
    /// session's context bar read nonsense. Unknown (null) is not a failure - the cell is simply empty.
    /// </summary>
    private static InputError? WindowRule(decimal? value) =>
        value is null ? null : InputRules.ValidatePositiveInteger(NumberText(value.Value), allowEmpty: false);

    /// <summary>A price is a non-negative decimal with at most <see cref="InputRules.PriceMaxDecimals"/> fraction digits.</summary>
    private static InputError? PriceRule(decimal? value) => value is null ? null : InputRules.ValidateNonNegativeDecimal(NumberText(value.Value));

    /// <summary>
    /// A cell's number as the rules read it. Trailing zeros are dropped first, so digits the user did not mean are not counted:
    /// a window typed as 1000000.00 is a whole number, and a price of 0.1000000 has one decimal, not seven.
    /// </summary>
    private static string NumberText(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>
    /// A stored price as the cell shows it. The rounding is not cosmetic: <c>(decimal)0.3</c> is the double's long binary
    /// tail (0.2999999999999999888977697537), which the six-decimal rule would then reject as too precise. Prices live at
    /// <see cref="InputRules.PriceMaxDecimals"/> digits, so that is where the conversion lands.
    /// </summary>
    private static decimal? ToDecimal(double? value) =>
        value is { } number ? Math.Round((decimal)number, InputRules.PriceMaxDecimals) : null;
}
