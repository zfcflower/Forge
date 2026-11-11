using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Providers;

namespace Forge.App.Settings;

/// <summary>Where the add/edit dialog is in its test-then-save flow. Saving is only offered once a test has passed.</summary>
internal enum ProviderTestState
{
    /// <summary>Nothing tested since the last change to the URL or the key; the button tests.</summary>
    Untested,

    /// <summary>A test is running; the button is disabled.</summary>
    Testing,

    /// <summary>The last test passed; the button now saves.</summary>
    Passed,
}

/// <summary>
/// The shared add/edit dialog: name, base URL and an optional key (or the name of the environment variable it lives in).
/// It owns no window. A test writes a typed key under a <em>draft</em> reference, so the provider's real key is untouched
/// until its settings are saved; the save hands the definition to <c>persist</c> (the page's settings write) and, only when
/// that succeeds, moves the typed key under the real reference. Cancel, Esc and close remove the draft.
/// </summary>
/// <remarks>
/// Models, default model, thinking effort and output limit are not edited here: a provider's models come from its
/// <c>/models</c> answer, and the default model and effort belong to the agents, not to the provider.
/// </remarks>
internal sealed class ProviderEditorViewModel : ObservableObject
{
    /// <summary>The thinking effort a provider's definition carries when the dialog creates it; the agents override it.</summary>
    private const ThinkingEffort DefaultThinking = ThinkingEffort.Medium;

    /// <summary>Prefix of the temporary reference a test writes its typed key under; never a real provider's reference.</summary>
    private const string DraftReferencePrefix = "provider-draft:";

    private readonly ProviderServices services;
    private readonly ProviderDefinition? original;
    private readonly IReadOnlyCollection<string> existingIds;
    private readonly ProviderPreset preset;
    private readonly Func<ProviderDefinition, Task<string?>>? persist;
    private string name = string.Empty;
    private string baseUrl = string.Empty;
    private bool keyIsSecret = true;
    private string keyText = string.Empty;
    private ProviderTestState testState;
    private IReadOnlyList<string>? testedModels;
    private string? draftId;
    private string? draftReference;
    private int inputVersion;
    private bool abandoned;
    private CancellationTokenSource? testCancellation;
    private string? error;
    private string? status;
    private bool isBusy;

    /// <summary>
    /// Opens the dialog for a new provider (<paramref name="startPreset"/> is the built-in vendor, or the custom entry) or for an
    /// existing one (<paramref name="definition"/>). <paramref name="existingProviderIds"/> keeps a new id unique.
    /// <paramref name="persist"/> writes the definition to the settings and returns the failure to show, or <c>null</c> on
    /// success; without it a save only raises <see cref="Saved"/> (tests use that).
    /// </summary>
    public ProviderEditorViewModel(
        ProviderServices services,
        ProviderDefinition? definition,
        ProviderPreset? startPreset = null,
        IReadOnlyCollection<string>? existingProviderIds = null,
        Func<ProviderDefinition, Task<string?>>? persist = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;
        original = definition;
        existingIds = existingProviderIds ?? [];
        this.persist = persist;

        if (definition is null)
        {
            preset = startPreset ?? ProviderPresets.Custom;
            name = IsCustom ? string.Empty : preset.DisplayName;
            baseUrl = preset.BaseUrl;
            keyIsSecret = true;
            testState = ProviderTestState.Untested;
        }
        else
        {
            preset = ProviderPresets.FindOrDefault(definition.PresetId);
            name = definition.DisplayName;
            baseUrl = definition.BaseUrl;
            // An environment variable on the definition means the key lives there; otherwise it is the stored secret.
            keyIsSecret = definition.EnvironmentVariable is null;
            keyText = definition.EnvironmentVariable ?? string.Empty;
            testedModels = definition.Models;
            // Editing a provider that already works: saving needs no re-test until the URL or the key changes.
            testState = ProviderTestState.Passed;
        }

        TestOrSaveCommand = new AsyncRelayCommand(_ => TestOrSaveAsync(), () => !IsBusy);
        CancelCommand = new RelayCommand(() => Cancelled?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Raised when the definition has been persisted (or there is no persister); the window closes with it.</summary>
    public event EventHandler<ProviderDefinition>? Saved;

    /// <summary>Raised when the user cancels; the window closes with nothing.</summary>
    public event EventHandler? Cancelled;

    public bool IsNew => original is null;

    public bool IsCustom => preset.Id == ProviderPresets.CustomId;

    /// <summary>Only a custom provider's address is typed; a built-in one comes from its preset and is shown read-only.</summary>
    public bool CanEditBaseUrl => IsCustom;

    /// <summary>Whether the dialog asks for a key at all: a sign-in provider has none to type.</summary>
    public bool ShowsKey => (original?.CredentialKind ?? ProviderCredentialKind.ApiKey) == ProviderCredentialKind.ApiKey;

    public string Title => Loc.Instance[IsNew ? ProviderStrings.EditorNewKey : ProviderStrings.EditorEditKey];

    public string NameLabel => Loc.Instance[ProviderStrings.EditorNameKey];

    public string BaseUrlLabel => Loc.Instance[ProviderStrings.EditorBaseUrlKey];

    public string KeyLabel => Loc.Instance[ProviderStrings.EditorSecretKey];

    public string KeyAsSecretText => Loc.Instance[ProviderStrings.EditorKeyAsSecretKey];

    public string KeyVariableLabel => Loc.Instance[ProviderStrings.EditorKeyVariableKey];

    public string KeyHint => Loc.Instance[keyIsSecret ? ProviderStrings.EditorKeyHintSecretKey : ProviderStrings.EditorKeyHintVariableKey];

    public string KeyPlaceholder => HasStoredKey && keyIsSecret
        ? Loc.Instance[ProviderStrings.EditorSecretStoredKey]
        : string.Empty;

    public string CancelText => Loc.Instance[ProviderStrings.EditorCancelKey];

    public string SaveText => Loc.Instance[ProviderStrings.EditorSaveKey];

    public string TestText => Loc.Instance[ProviderStrings.EditorTestKey];

    public string TestingText => Loc.Instance[ProviderStrings.EditorTestingKey];

    /// <summary>The footer's right-hand button: the test before a pass, the save after it.</summary>
    public string TestOrSaveText => testState switch
    {
        ProviderTestState.Passed => SaveText,
        ProviderTestState.Testing => TestingText,
        _ => TestText,
    };

    public AsyncRelayCommand TestOrSaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public string Name
    {
        get => name;
        set => Set(ref name, value ?? string.Empty);
    }

    /// <summary>Changing the address invalidates a passing test: the answer was for the old endpoint.</summary>
    public string BaseUrl
    {
        get => baseUrl;
        set
        {
            if (Set(ref baseUrl, value ?? string.Empty))
            {
                MarkUntested();
            }
        }
    }

    /// <summary>
    /// True when the key box holds the key itself (stored encrypted); false when it holds an environment variable's name.
    /// Switching the mode clears the box, except that the variable name starts from the vendor's own variable.
    /// </summary>
    public bool KeyIsSecret
    {
        get => keyIsSecret;
        set
        {
            if (!Set(ref keyIsSecret, value))
            {
                return;
            }

            keyText = value ? string.Empty : preset.EnvironmentVariable ?? string.Empty;
            OnPropertyChanged(nameof(KeyText));
            OnPropertyChanged(nameof(KeyHint));
            OnPropertyChanged(nameof(KeyPlaceholder));
            MarkUntested();
        }
    }

    /// <summary>The key itself in secret mode, or the environment variable's name otherwise; both are the same box.</summary>
    public string KeyText
    {
        get => keyText;
        set
        {
            if (Set(ref keyText, value ?? string.Empty))
            {
                MarkUntested();
            }
        }
    }

    /// <summary>Whether a key is already stored for this provider (the box then keeps the old key when left empty).</summary>
    public bool HasStoredKey => original?.SecretReference is { Length: > 0 } reference && services.Secrets.Has(reference);

    public bool IsBusy => isBusy;

    public ProviderTestState TestState => testState;

    /// <summary>The line under the fields: the test's success and the model count it fetched, or nothing.</summary>
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

    public bool HasStatus => Status is { Length: > 0 };

    /// <summary>The validation, test or save failure, shown above the footer.</summary>
    public string? Error
    {
        get => error;
        private set
        {
            if (Set(ref error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error is { Length: > 0 };

    /// <summary>
    /// Validates the form and builds the definition; <c>null</c> when something is wrong (<see cref="Error"/> says what).
    /// The key itself is never part of the definition — only the reference it is stored under.
    /// </summary>
    public ProviderDefinition? TryBuild()
    {
        var trimmedName = Name.Trim();
        if (trimmedName.Length == 0)
        {
            Error = Loc.Instance[ProviderStrings.EditorNeedsNameKey];
            return null;
        }

        var url = BaseUrl.Trim();
        if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            Error = Loc.Instance[ProviderStrings.EditorNeedsBaseUrlKey];
            return null;
        }

        var variable = KeyText.Trim();
        if (ShowsKey && !KeyIsSecret && variable.Length == 0)
        {
            Error = Loc.Instance[ProviderStrings.EditorNeedsVariableKey];
            return null;
        }

        Error = null;
        // The id is fixed at the first build: the key is stored under it, so a later rename must not orphan that key.
        var id = original?.Id ?? (draftId ??= ProviderIdentity.Unique(ProviderIdentity.Slug(trimmedName), existingIds));
        var kind = original?.CredentialKind ?? ProviderCredentialKind.ApiKey;
        var models = testedModels ?? original?.Models ?? preset.SeedModels;
        var remembered = original?.DefaultModel ?? string.Empty;
        var defaultModel = remembered.Length > 0 && models.Contains(remembered, StringComparer.Ordinal)
            ? remembered
            : models.FirstOrDefault() ?? string.Empty;

        return new ProviderDefinition
        {
            Id = id,
            PresetId = original?.PresetId ?? preset.Id,
            DisplayName = trimmedName,
            Protocol = original?.Protocol ?? preset.Protocol,
            BaseUrl = url,
            CredentialKind = kind,
            EnvironmentVariable = ShowsKey ? (KeyIsSecret ? null : variable) : original?.EnvironmentVariable,
            SecretReference = kind == ProviderCredentialKind.ApiKey ? "provider:" + id : original?.SecretReference,
            Models = [.. models],
            DefaultModel = defaultModel,
            Thinking = original?.Thinking ?? DefaultThinking,
            MaxOutputTokens = original?.MaxOutputTokens,
            Overrides = original?.Overrides ?? new Dictionary<string, ModelMetadata>(StringComparer.Ordinal),
            Headers = original?.Headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            SendPromptCacheKey = original?.SendPromptCacheKey ?? false,
            PromptCacheTtl = original?.PromptCacheTtl,
        };
    }

    /// <summary>The first button: test when nothing has passed, save when the last test passed.</summary>
    internal async Task TestOrSaveAsync()
    {
        if (testState != ProviderTestState.Passed)
        {
            await TestAsync().ConfigureAwait(true);
            return;
        }

        if (TryBuild() is not { } definition)
        {
            return;
        }

        var typed = KeyText.Trim();
        isBusy = true;
        RaiseTestState();
        try
        {
            // Settings first: only a definition that was written may receive the key, so a refused write keeps the old one.
            var failure = persist is null ? null : await persist(definition).ConfigureAwait(true);
            if (failure is not null)
            {
                Error = failure;
                return;
            }

            await CommitKeyAsync(definition, typed).ConfigureAwait(true);
            Saved?.Invoke(this, definition);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = exception.Message;
        }
        finally
        {
            isBusy = false;
            RaiseTestState();
        }
    }

    /// <summary>
    /// Runs the connection test against the typed values: <c>GET /models</c> where the preset can list models, otherwise one
    /// tiny turn (Anthropic-protocol vendors have no listing this app can rely on). A pass records the fetched models. A result
    /// that arrives after the URL or the key changed, or after the dialog closed, is discarded.
    /// </summary>
    private async Task TestAsync()
    {
        if (TryBuild() is not { } definition)
        {
            return;
        }

        var version = inputVersion;
        testCancellation?.Cancel();
        testCancellation = new CancellationTokenSource();
        var token = testCancellation.Token;
        isBusy = true;
        testState = ProviderTestState.Testing;
        RaiseTestState();
        Error = null;
        Status = null;
        try
        {
            var testReference = await WriteDraftKeyAsync(definition).ConfigureAwait(true);
            var transient = services.CreateTransient(definition with { SecretReference = testReference });
            if (preset.SupportsModelListing)
            {
                var list = await services.RefreshModelsAsync(transient, token).ConfigureAwait(true);
                if (version == inputVersion && !abandoned)
                {
                    testedModels = list.Ids;
                    Status = Loc.Instance.Format(ProviderStrings.EditorTestPassedKey, TextFormat.Number(list.Ids.Count));
                    testState = ProviderTestState.Passed;
                }
            }
            else
            {
                await services.VerifyChatAsync(transient, token).ConfigureAwait(true);
                if (version == inputVersion && !abandoned)
                {
                    testedModels = definition.Models;
                    Status = Loc.Instance[ProviderStrings.EditorTestPassedNoListKey];
                    testState = ProviderTestState.Passed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Closed or superseded by a newer test: nothing to report.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (version == inputVersion && !abandoned)
            {
                Error = exception.Message;
            }
        }
        finally
        {
            if (version == inputVersion && testState == ProviderTestState.Testing)
            {
                testState = ProviderTestState.Untested;
            }

            testCancellation?.Dispose();
            testCancellation = null;
            isBusy = false;
            RaiseTestState();
            if (abandoned)
            {
                _ = RemoveDraftAsync();
            }
        }
    }

    /// <summary>
    /// Called when the dialog closes without a save (Cancel, Esc, the title-bar close button): an in-flight test is cancelled
    /// and the draft key is removed. A real key is never touched, because a draft is the only thing a test writes.
    /// </summary>
    public async Task AbandonAsync()
    {
        abandoned = true;
        try
        {
            testCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The test finished and disposed its source between the read and the cancel.
        }

        await RemoveDraftAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Writes the typed key under a draft reference for the test to read. Empty or variable mode writes nothing; the test then
    /// uses the provider's own reference (its stored key, if any).
    /// </summary>
    private async Task<string?> WriteDraftKeyAsync(ProviderDefinition definition)
    {
        var typed = KeyText.Trim();
        if (!ShowsKey || !KeyIsSecret || typed.Length == 0 || definition.SecretReference is null)
        {
            return definition.SecretReference;
        }

        var reference = DraftReferencePrefix + definition.Id;
        await Task.Run(() => services.Secrets.Set(reference, typed)).ConfigureAwait(true);
        draftReference = reference;
        return reference;
    }

    /// <summary>After a successful save: the typed key moves under the provider's real reference, and the draft is removed.</summary>
    private async Task CommitKeyAsync(ProviderDefinition saved, string typed)
    {
        if (ShowsKey && KeyIsSecret && typed.Length > 0 && saved.SecretReference is { Length: > 0 } reference)
        {
            await Task.Run(() => services.Secrets.Set(reference, typed)).ConfigureAwait(true);
        }

        await RemoveDraftAsync().ConfigureAwait(true);
    }

    private async Task RemoveDraftAsync()
    {
        if (draftReference is not { } reference)
        {
            return;
        }

        draftReference = null;
        try
        {
            await Task.Run(() => services.Secrets.Remove(reference)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = exception.Message;
        }
    }

    /// <summary>Any change to the address or the key makes the last test's answer stale, so the save is offered again only after a new test.</summary>
    private void MarkUntested()
    {
        inputVersion++;
        if (testState == ProviderTestState.Untested)
        {
            return;
        }

        testState = ProviderTestState.Untested;
        testedModels = original?.Models;
        Status = null;
        RaiseTestState();
    }

    private void RaiseTestState()
    {
        OnPropertyChanged(nameof(TestState));
        OnPropertyChanged(nameof(TestOrSaveText));
        OnPropertyChanged(nameof(IsBusy));
        TestOrSaveCommand.NotifyCanExecuteChanged();
    }
}
