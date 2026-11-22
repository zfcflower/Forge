using System.Globalization;
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
internal sealed class ProviderEditorViewModel : NotifyingObject, IDisposable
{
    /// <summary>The thinking effort a provider's definition carries when the dialog creates it; the agents override it.</summary>
    private const ThinkingEffort DefaultThinking = ThinkingEffort.Medium;

    /// <summary>Prefix of the temporary reference a test writes its typed key under; never a real provider's reference.</summary>
    private const string DraftReferencePrefix = "provider-draft:";

    /// <summary>The most a provider's reply cap may be set to; anything larger is a typo, not a window.</summary>
    private const int MaxOutputCeiling = 1_000_000;

    private readonly ProviderServices services;
    private readonly ProviderDefinition? original;
    private readonly IReadOnlyCollection<string> existingIds;
    private readonly ProviderPreset preset;
    private readonly Func<ProviderDefinition, Task<string?>>? persist;
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private string name = string.Empty;
    private string baseUrl = string.Empty;
    private bool keyIsSecret = true;
    private string keyText = string.Empty;
    private string headersText = string.Empty;
    private string maxOutputText = string.Empty;
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
    private InputError? nameError;
    private InputError? baseUrlError;
    private InputError? keyError;
    private string? headersError;
    private string? maxOutputError;

    /// <summary>
    /// Opens the dialog for a new provider (<paramref name="startPreset"/> is the built-in vendor, or the custom entry) or for an
    /// existing one (<paramref name="definition"/>). <paramref name="existingProviderIds"/> keeps a new id unique.
    /// <paramref name="persist"/> writes the definition to the settings and returns the failure to show, or <c>null</c> on
    /// success; without it a save only raises <see cref="Saved"/>.
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
        Loc.Instance.LanguageChanged += OnLanguageChanged;
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
            headersText = FormatHeaders(definition.Headers);
            maxOutputText = definition.MaxOutputTokens is { } cap
                ? cap.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
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

    public string HeadersLabel => Loc.Instance[ProviderStrings.EditorHeadersKey];

    public string HeadersHint => Loc.Instance[ProviderStrings.EditorHeadersHintKey];

    public string MaxOutputLabel => Loc.Instance[ProviderStrings.EditorMaxOutputKey];

    public string MaxOutputHint => Loc.Instance[ProviderStrings.EditorMaxOutputHintKey];

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
        set
        {
            if (Set(ref name, value ?? string.Empty))
            {
                RevalidateName();
            }
        }
    }

    /// <summary>Changing the address invalidates a passing test: the answer was for the old endpoint.</summary>
    public string BaseUrl
    {
        get => baseUrl;
        set
        {
            if (Set(ref baseUrl, value ?? string.Empty))
            {
                RevalidateBaseUrl();
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
            RevalidateKey();
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
                RevalidateKey();
                MarkUntested();
            }
        }
    }

    /// <summary>
    /// Extra request headers, one <c>Name: Value</c> per line; blank lines are ignored. A gateway may need them (OpenCode's
    /// <c>x-opencode-session: {session}</c>), so a change invalidates the last test like the URL does.
    /// </summary>
    public string HeadersText
    {
        get => headersText;
        set
        {
            if (Set(ref headersText, value ?? string.Empty))
            {
                RevalidateHeaders();
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
        private set => SetAndNotify(ref status, value, nameof(HasStatus));
    }

    public bool HasStatus => Status is { Length: > 0 };

    /// <summary>The validation, test or save failure, shown above the footer.</summary>
    public string? Error
    {
        get => error;
        private set => SetAndNotify(ref error, value, nameof(HasError));
    }

    public bool HasError => Error is { Length: > 0 };

    /// <summary>Inline messages for the fields that have a rule; a null message means the field is acceptable.</summary>
    public string? NameError => nameError is null ? null : InputValidationStrings.Describe(nameError);

    public bool HasNameError => nameError is not null;

    public string? BaseUrlError => baseUrlError is null ? null : InputValidationStrings.Describe(baseUrlError);

    public bool HasBaseUrlError => baseUrlError is not null;

    public string? KeyError => keyError is null ? null : InputValidationStrings.Describe(keyError);

    public bool HasKeyError => keyError is not null;

    public string? HeadersError => headersError;

    public bool HasHeadersError => headersError is { Length: > 0 };

    public string? MaxOutputError => maxOutputError;

    public bool HasMaxOutputError => maxOutputError is { Length: > 0 };

    /// <summary>
    /// The largest single reply this provider's models accept, in tokens; empty means unknown, which makes compaction reserve
    /// 20% of the window instead. It is also the request's <c>max_tokens</c>, so a wrong value truncates replies or wastes input.
    /// </summary>
    public string MaxOutputText
    {
        get => maxOutputText;
        set
        {
            if (Set(ref maxOutputText, value ?? string.Empty))
            {
                RevalidateMaxOutput();
            }
        }
    }

    private void RevalidateMaxOutput()
    {
        if (maxOutputError is not null)
        {
            SetMaxOutputError(DescribeMaxOutput());
        }
    }

    /// <summary>The box's error, or null when it is empty (unknown) or a positive number up to the ceiling.</summary>
    private string? DescribeMaxOutput()
    {
        var text = maxOutputText.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is > 0 and <= MaxOutputCeiling
            ? null
            : Loc.Instance.Format(ProviderStrings.EditorMaxOutputInvalidKey, MaxOutputCeiling);
    }

    private int? ParseMaxOutput() =>
        int.TryParse(maxOutputText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private void SetMaxOutputError(string? value)
    {
        if (!Set(ref maxOutputError, value))
        {
            return;
        }

        OnPropertyChanged(nameof(MaxOutputError));
        OnPropertyChanged(nameof(HasMaxOutputError));
    }

    private void RevalidateName()
    {
        if (nameError is not null)
        {
            SetError(ref nameError, InputRules.ValidateRequired(Name), nameof(NameError), nameof(HasNameError));
        }
    }

    private void RevalidateBaseUrl()
    {
        if (baseUrlError is not null)
        {
            SetError(ref baseUrlError, InputRules.ValidateAbsoluteHttpUrl(BaseUrl), nameof(BaseUrlError), nameof(HasBaseUrlError));
        }
    }

    private void RevalidateKey()
    {
        if (keyError is not null)
        {
            SetError(
                ref keyError,
                ShowsKey && !KeyIsSecret ? InputRules.ValidateEnvironmentVariable(KeyText) : null,
                nameof(KeyError),
                nameof(HasKeyError));
        }
    }

    private void RevalidateHeaders()
    {
        if (headersError is not null)
        {
            SetHeadersError(TryParseHeaders(HeadersText, out _, out var badLine)
                ? null
                : Loc.Instance.Format(ProviderStrings.EditorHeadersInvalidKey, badLine));
        }
    }

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

    private void SetHeadersError(string? value)
    {
        if (string.Equals(headersError, value, StringComparison.Ordinal))
        {
            return;
        }

        headersError = value;
        OnPropertyChanged(nameof(HeadersError));
        OnPropertyChanged(nameof(HasHeadersError));
    }

    /// <summary>
    /// Validates the form and builds the definition; <c>null</c> when something is wrong (<see cref="Error"/> says what).
    /// The key itself is never part of the definition — only the reference it is stored under.
    /// </summary>
    public ProviderDefinition? TryBuild()
    {
        var trimmedName = Name.Trim();
        var url = BaseUrl.Trim();
        var variable = KeyText.Trim();
        SetError(ref nameError, InputRules.ValidateRequired(trimmedName), nameof(NameError), nameof(HasNameError));
        SetError(ref baseUrlError, InputRules.ValidateAbsoluteHttpUrl(url), nameof(BaseUrlError), nameof(HasBaseUrlError));
        SetError(
            ref keyError,
            ShowsKey && !KeyIsSecret ? InputRules.ValidateEnvironmentVariable(variable) : null,
            nameof(KeyError),
            nameof(HasKeyError));

        var headersOk = TryParseHeaders(HeadersText, out var headers, out var badLine);
        SetHeadersError(headersOk ? null : Loc.Instance.Format(ProviderStrings.EditorHeadersInvalidKey, badLine));
        SetMaxOutputError(DescribeMaxOutput());
        if (nameError is not null || baseUrlError is not null || keyError is not null || headersError is not null || HasMaxOutputError)
        {
            Error = null;
            return null;
        }

        Error = null;
        // The id is fixed at the first build: the key is stored under it, so a later rename must not orphan that key.
        var id = original?.Id ?? (draftId ??= ProviderIdentity.Unique(ProviderIdentity.Slug(trimmedName), existingIds));
        // This dialog is the key way in, so it names that kind rather than following the preset's sign-in priority: a preset
        // that also signs in (OpenAI, GitHub Copilot) still creates a key provider here, on the key endpoint. A sign-in is the
        // login dialog's job.
        var basis = original ?? ProviderDefinition.FromPreset(preset, id, credentialKind: ProviderCredentialKind.ApiKey);
        var kind = basis.CredentialKind;
        var models = testedModels ?? original?.Models ?? basis.Models;
        var remembered = original?.DefaultModel ?? string.Empty;
        var defaultModel = remembered.Length > 0 && models.Contains(remembered, StringComparer.Ordinal)
            ? remembered
            : models.FirstOrDefault() ?? string.Empty;

        return basis with
        {
            DisplayName = trimmedName,
            BaseUrl = url,
            EnvironmentVariable = ShowsKey ? (KeyIsSecret ? null : variable) : basis.EnvironmentVariable,
            SecretReference = kind == ProviderCredentialKind.ApiKey ? "provider:" + id : basis.SecretReference,
            Models = [.. models],
            DefaultModel = defaultModel,
            Thinking = original?.Thinking ?? DefaultThinking,
            Headers = headers,
            MaxOutputTokens = ParseMaxOutput(),
        };
    }

    /// <summary>
    /// Reads the header box: each non-blank line is <c>Name: Value</c>, split at the first colon; a later line of the same name
    /// wins. A line without a colon, with an empty side or with whitespace in the name fails with its 1-based number.
    /// </summary>
    internal static bool TryParseHeaders(string text, out Dictionary<string, string> headers, out int badLine)
    {
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        badLine = 0;
        var number = 0;
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            number++;
            var line = raw.Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            var headerName = colon > 0 ? line[..colon].Trim() : default;
            var value = colon > 0 ? line[(colon + 1)..].Trim() : default;
            if (headerName.IsEmpty || value.IsEmpty || headerName.ContainsAnyWhiteSpace())
            {
                badLine = number;
                headers.Clear();
                return false;
            }

            headers[headerName.ToString()] = value.ToString();
        }

        return true;
    }

    private static string FormatHeaders(IReadOnlyDictionary<string, string> headers) =>
        string.Join(Environment.NewLine, headers.Select(pair => pair.Key + ": " + pair.Value));

    /// <summary>Detaches the language subscription; the window calls this on every close, saved or not.</summary>
    public void Dispose() => Loc.Instance.LanguageChanged -= OnLanguageChanged;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        OnPropertyChanged(nameof(NameError));
        OnPropertyChanged(nameof(HasNameError));
        OnPropertyChanged(nameof(BaseUrlError));
        OnPropertyChanged(nameof(HasBaseUrlError));
        OnPropertyChanged(nameof(KeyError));
        OnPropertyChanged(nameof(HasKeyError));
        OnPropertyChanged(nameof(MaxOutputError));
        OnPropertyChanged(nameof(HasMaxOutputError));
        if (headersError is not null)
        {
            RevalidateHeaders();
        }
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
    /// Writes the typed key under a draft reference. Empty or variable mode writes nothing; the provider's own reference
    /// (its stored key, if any) is used instead.
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
