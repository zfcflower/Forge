using System.Collections.ObjectModel;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Providers;

namespace Forge.App.Settings;

/// <summary>One choice of the "authentication" picker, carrying the credential kind it selects.</summary>
internal sealed record ProviderAuthOption(ProviderCredentialKind Kind, string Label);

/// <summary>
/// The add/edit provider dialog's state. It never opens a window itself: the window calls
/// <see cref="TryBuild"/> on Save and <see cref="PersistSecretAsync"/> before the definition is handed back, so the same
/// model is testable without an Avalonia application.
/// </summary>
internal sealed class ProviderEditorViewModel : ObservableObject, IDisposable
{
    private readonly ProviderServices services;
    private readonly ProviderDefinition? original;
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private ProviderPreset? preset;
    private ProviderAuthOption? auth;
    private string name = string.Empty;
    private string baseUrl = string.Empty;
    private string environmentVariable = string.Empty;
    private string secret = string.Empty;
    private bool clearSecret;
    private string defaultModel = string.Empty;
    private string maxTokens = string.Empty;
    private string? error;
    private string? status;
    private bool makeDefault;
    private string? deviceCode;
    private string? verificationUri;
    private string? loginState;
    private CancellationTokenSource? loginCancellation;

    public ProviderEditorViewModel(ProviderServices services, ProviderDefinition? definition, bool isDefault)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;
        original = definition;
        MakeDefault = isDefault || definition is null;

        if (definition is null)
        {
            preset = ProviderPresets.All[0];
        }
        else
        {
            preset = ProviderPresets.FindOrDefault(definition.PresetId);
            name = definition.DisplayName;
            baseUrl = definition.BaseUrl;
            environmentVariable = definition.EnvironmentVariable ?? string.Empty;
            defaultModel = definition.DefaultModel;
            maxTokens = definition.MaxOutputTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            foreach (var model in definition.Models)
            {
                Models.Add(model);
            }

            SelectedThinking = ThinkingNames.FirstOrDefault(
                option => string.Equals(option, definition.Thinking.ToString(), StringComparison.OrdinalIgnoreCase)) ?? ThinkingNames[0];
            auth = AuthOptionsFor(preset).FirstOrDefault(option => option.Kind == definition.CredentialKind) ?? AuthOptions[0];
            ApplyPresetDefaults(preset, keepIdentity: true);
        }

        if (definition is null)
        {
            ApplyPresetDefaults(preset!, keepIdentity: false);
            auth = AuthOptions[0];
            SelectedThinking = ThinkingNames[0];
        }

        SaveCommand = new AsyncRelayCommand(_ => SaveAsync(), () => !IsSaving);
        RefreshModelsCommand = new AsyncRelayCommand(RefreshModelsAsync, () => !IsBusy);
        SignInCommand = new AsyncRelayCommand(_ => SignInAsync(), () => !IsBusy);
        SignOutCommand = new RelayCommand(SignOut);
        CancelCommand = new RelayCommand(() => Cancelled?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Raised with the validated definition when the user saves; the window closes with it.</summary>
    public event EventHandler<ProviderDefinition>? Saved;

    /// <summary>Raised when the user cancels or closes the dialog.</summary>
    public event EventHandler? Cancelled;

    public bool IsNew => original is null;

    public string Title => Loc.Instance[IsNew ? ProviderStrings.EditorNewKey : ProviderStrings.EditorEditKey];

    public IReadOnlyList<ProviderPreset> Presets => ProviderPresets.All;

    public ProviderPreset? SelectedPreset
    {
        get => preset;
        set
        {
            if (!Set(ref preset, value) || value is null || !IsNew)
            {
                return;
            }

            ApplyPresetDefaults(value, keepIdentity: false);
            auth = AuthOptions[0];
            OnPropertyChanged(nameof(SelectedAuth));
            OnPropertyChanged(nameof(AuthOptions));
            OnPropertyChanged(nameof(ShowsEnvironmentVariable));
            OnPropertyChanged(nameof(ShowsSecret));
            RaisePresetText();
        }
    }

    public string Name
    {
        get => name;
        set => Set(ref name, value ?? string.Empty);
    }

    public string BaseUrl
    {
        get => baseUrl;
        set => Set(ref baseUrl, value ?? string.Empty);
    }

    public IReadOnlyList<ProviderAuthOption> AuthOptions => AuthOptionsFor(preset);

    public ProviderAuthOption? SelectedAuth
    {
        get => auth;
        set
        {
            if (Set(ref auth, value))
            {
                OnPropertyChanged(nameof(ShowsEnvironmentVariable));
                OnPropertyChanged(nameof(ShowsSecret));
                OnPropertyChanged(nameof(ShowsSignIn));
                OnPropertyChanged(nameof(ShowsClaudeHint));
                OnPropertyChanged(nameof(ShowsCopilotHint));
                RaiseStatus();
            }
        }
    }

    public bool ShowsEnvironmentVariable => SelectedAuth?.Kind == ProviderCredentialKind.ApiKey;

    public bool ShowsSecret => SelectedAuth?.Kind == ProviderCredentialKind.ApiKey;

    public bool ShowsSignIn => SelectedAuth?.Kind is ProviderCredentialKind.ClaudeSubscription or ProviderCredentialKind.GitHubCopilot;

    public bool ShowsClaudeHint => SelectedAuth?.Kind == ProviderCredentialKind.ClaudeSubscription;

    public bool ShowsCopilotHint => SelectedAuth?.Kind == ProviderCredentialKind.GitHubCopilot;

    public string EnvironmentVariableLabel => Loc.Instance[ProviderStrings.EditorEnvironmentVariableKey];

    public string EnvironmentVariable
    {
        get => environmentVariable;
        set => Set(ref environmentVariable, value ?? string.Empty);
    }

    public string Secret
    {
        get => secret;
        set => Set(ref secret, value ?? string.Empty);
    }

    public bool HasStoredSecret => original?.SecretReference is { Length: > 0 } reference && services.Secrets.Has(reference);

    public string SecretHint => HasStoredSecret
        ? Loc.Instance[ProviderStrings.EditorSecretStoredKey]
        : Loc.Instance[ProviderStrings.EditorSecretPlaceholderKey];

    public bool ClearSecret
    {
        get => clearSecret;
        set => Set(ref clearSecret, value);
    }

    public ObservableCollection<string> Models { get; } = [];

    public string? SelectedModel { get; set; }

    public string DefaultModel
    {
        get => defaultModel;
        set => Set(ref defaultModel, value ?? string.Empty);
    }

    public static IReadOnlyList<string> ThinkingNames { get; } = ["Off", "Low", "Medium", "High"];

    public string SelectedThinking { get; set; } = "Off";

    public string MaxTokens
    {
        get => maxTokens;
        set => Set(ref maxTokens, value ?? string.Empty);
    }

    public bool MakeDefault
    {
        get => makeDefault;
        set => Set(ref makeDefault, value);
    }

    /// <summary>The validation failure, shown under the fields; <c>null</c> when the form is fine.</summary>
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

    /// <summary>The sign-in state line ("signed in", "not signed in", the device code).</summary>
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

    public string? DeviceCode
    {
        get => deviceCode;
        private set => Set(ref deviceCode, value);
    }

    public string? VerificationUri
    {
        get => verificationUri;
        private set => Set(ref verificationUri, value);
    }

    public string? LoginState
    {
        get => loginState;
        private set
        {
            if (Set(ref loginState, value))
            {
                OnPropertyChanged(nameof(HasLoginState));
            }
        }
    }

    public bool HasLoginState => LoginState is { Length: > 0 };

    public bool IsSaving { get; private set; }

    public bool IsBusy => isBusy;

    private bool isBusy;

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand RefreshModelsCommand { get; }

    public AsyncRelayCommand SignInCommand { get; }

    public RelayCommand SignOutCommand { get; }

    public RelayCommand CancelCommand { get; }


    /// <summary>Delete is only offered for a provider that already exists; a new one is cancelled instead.</summary>
    public bool CanDelete => !IsNew;

    public string DeleteText => Loc.Instance[ProviderStrings.EditorDeleteKey];

    public string ClearSecretText => Loc.Instance[ProviderStrings.EditorSecretClearKey];

    /// <summary>Raised when the user confirms deleting this provider (the window asks twice, then closes with this).</summary>
    public event EventHandler<string>? DeleteRequested;

    public void RequestDelete()
    {
        if (original is not null)
        {
            DeleteRequested?.Invoke(this, original.Id);
        }
    }

    public string SaveText => Loc.Instance[ProviderStrings.EditorSaveKey];

    public string CancelText => Loc.Instance[ProviderStrings.EditorCancelKey];

    public string RefreshModelsText => Loc.Instance[ProviderStrings.EditorModelsFetchKey];

    public string SignInText => Loc.Instance[ProviderStrings.EditorSignInKey];

    public string SignOutText => Loc.Instance[ProviderStrings.EditorSignOutKey];

    public string DeviceCodeText => DeviceCode is null
        ? string.Empty
        : Loc.Instance.Format(ProviderStrings.EditorDeviceCodeKey, DeviceCode);

    public string DeviceCodeHint => Loc.Instance[ProviderStrings.EditorDeviceCodeHintKey];

    public string ClaudeHint => Loc.Instance[ProviderStrings.EditorClaudeHintKey];

    public string CopilotHint => Loc.Instance[ProviderStrings.EditorCopilotHintKey];

    public string NameLabel => Loc.Instance[ProviderStrings.EditorNameKey];

    public string BaseUrlLabel => Loc.Instance[ProviderStrings.EditorBaseUrlKey];

    public string PresetLabel => Loc.Instance[ProviderStrings.EditorPresetKey];

    public string AuthLabel => Loc.Instance[ProviderStrings.EditorAuthKey];

    public string ModelsLabel => Loc.Instance[ProviderStrings.EditorModelsKey];

    public string DefaultModelLabel => Loc.Instance[ProviderStrings.EditorDefaultModelKey];

    public string ThinkingLabel => Loc.Instance[ProviderStrings.EditorThinkingKey];

    public string MaxTokensLabel => Loc.Instance[ProviderStrings.EditorMaxTokensKey];

    public string SecretLabel => Loc.Instance[ProviderStrings.EditorSecretKey];

    /// <summary>
    /// Validates the form and builds the definition; <c>null</c> when something is wrong (and <see cref="Error"/> explains
    /// what). The secret itself is not part of the definition — only its store reference.
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

        var models = Models.Where(model => !string.IsNullOrWhiteSpace(model)).Distinct(StringComparer.Ordinal).ToList();
        if (models.Count == 0 && DefaultModel.Trim().Length > 0)
        {
            models.Add(DefaultModel.Trim());
        }

        if (models.Count == 0)
        {
            Error = Loc.Instance[ProviderStrings.EditorNeedsModelKey];
            return null;
        }

        var chosenDefault = DefaultModel.Trim();
        if (chosenDefault.Length == 0)
        {
            chosenDefault = models[0];
        }
        else if (!models.Contains(chosenDefault, StringComparer.Ordinal))
        {
            models.Insert(0, chosenDefault);
        }

        var kind = SelectedAuth?.Kind ?? ProviderCredentialKind.ApiKey;
        var id = original?.Id ?? Slug(trimmedName, models);
        var reference = kind == ProviderCredentialKind.ApiKey ? "provider:" + id : original?.SecretReference;

        Error = null;
        return new ProviderDefinition
        {
            Id = id,
            PresetId = preset?.Id ?? ProviderPresets.CustomId,
            DisplayName = trimmedName,
            Protocol = preset?.Protocol ?? ProviderProtocol.OpenAiCompatible,
            BaseUrl = url,
            CredentialKind = kind,
            EnvironmentVariable = environmentVariable.Trim().Length == 0 ? null : environmentVariable.Trim(),
            SecretReference = reference,
            Models = models,
            DefaultModel = chosenDefault,
            Thinking = SelectedThinking switch
            {
                "Low" => ThinkingEffort.Low,
                "Medium" => ThinkingEffort.Medium,
                "High" => ThinkingEffort.High,
                _ => ThinkingEffort.Off,
            },
            MaxOutputTokens = int.TryParse(MaxTokens, out var max) && max > 0 ? max : null,
            Overrides = original?.Overrides ?? new Dictionary<string, ModelMetadata>(StringComparer.Ordinal),
            Headers = original?.Headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Writes the pasted or cleared key into the encrypted store. Runs on the pool: this is a file write, and the dialog is
    /// not allowed to stall the UI thread for it.
    /// </summary>
    public async Task PersistSecretAsync(ProviderDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var reference = definition.SecretReference;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return;
        }

        var pasted = Secret.Trim();
        if (pasted.Length > 0)
        {
            await Task.Run(() => services.Secrets.Set(reference, pasted), cancellationToken).ConfigureAwait(true);
            Secret = string.Empty;
        }
        else if (ClearSecret)
        {
            await Task.Run(() => services.Secrets.Remove(reference), cancellationToken).ConfigureAwait(true);
            ClearSecret = false;
        }
    }

    /// <summary>Adds a model id typed by hand (a model the server does not list yet, or a private one).</summary>
    public void AddModel(string model)
    {
        var trimmed = (model ?? string.Empty).Trim();
        if (trimmed.Length == 0 || Models.Contains(trimmed, StringComparer.Ordinal))
        {
            return;
        }

        Models.Add(trimmed);
        if (DefaultModel.Length == 0)
        {
            DefaultModel = trimmed;
        }
    }

    /// <summary>Removes one model id, moving the default off it when it was the default.</summary>
    public void RemoveModel(string model)
    {
        var trimmed = (model ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        Models.Remove(trimmed);
        if (string.Equals(DefaultModel, trimmed, StringComparison.Ordinal))
        {
            DefaultModel = Models.Count > 0 ? Models[0] : string.Empty;
        }
    }

    public void Dispose()
    {
        loginCancellation?.Cancel();
        loginCancellation?.Dispose();
        loginCancellation = null;
    }

    private void ApplyPresetDefaults(ProviderPreset value, bool keepIdentity)
    {
        if (!keepIdentity)
        {
            BaseUrl = value.BaseUrl;
            EnvironmentVariable = value.EnvironmentVariable ?? string.Empty;
            name = value.DisplayName;
            OnPropertyChanged(nameof(Name));
            Models.Clear();
            foreach (var model in value.SeedModels)
            {
                Models.Add(model);
            }

            DefaultModel = value.SeedModels.Count > 0 ? value.SeedModels[0] : string.Empty;
        }
    }

    private IReadOnlyList<ProviderAuthOption> AuthOptionsFor(ProviderPreset? value)
    {
        var options = new List<ProviderAuthOption>();
        if (value is null || value.Auth.HasFlag(ProviderAuthOptions.ApiKey))
        {
            options.Add(new ProviderAuthOption(ProviderCredentialKind.ApiKey, Loc.Instance[ProviderStrings.EditorAuthApiKeyKey]));
        }

        if (value?.Auth.HasFlag(ProviderAuthOptions.ClaudeSubscription) == true)
        {
            options.Add(new ProviderAuthOption(ProviderCredentialKind.ClaudeSubscription, Loc.Instance[ProviderStrings.EditorAuthClaudeKey]));
        }

        if (value?.Auth.HasFlag(ProviderAuthOptions.GitHubCopilot) == true)
        {
            options.Add(new ProviderAuthOption(ProviderCredentialKind.GitHubCopilot, Loc.Instance[ProviderStrings.EditorAuthCopilotKey]));
        }

        return options;
    }

    private void RaisePresetText()
    {
        OnPropertyChanged(nameof(Status));
        RaiseStatus();
    }

    private void RaiseStatus()
    {
        if (SelectedAuth?.Kind == ProviderCredentialKind.ClaudeSubscription)
        {
            Status = services.Credentials.HasClaudeLogin()
                ? Loc.Instance[ProviderStrings.EditorSignedInClaudeKey]
                : Loc.Instance[ProviderStrings.NeedsClaudeSignInKey];
        }
        else if (SelectedAuth?.Kind == ProviderCredentialKind.GitHubCopilot)
        {
            var id = original?.Id;
            Status = id is not null && services.Credentials.HasCopilotLogin(id)
                ? Loc.Instance[ProviderStrings.ReadyCopilotKey]
                : Loc.Instance[ProviderStrings.NeedsCopilotSignInKey];
        }
        else
        {
            Status = null;
        }
    }

    private async Task SaveAsync()
    {
        if (TryBuild() is not { } definition)
        {
            return;
        }

        try
        {
            await PersistSecretAsync(definition).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = exception.Message;
            return;
        }

        Saved?.Invoke(this, definition);
    }

    private async Task RefreshModelsAsync(CancellationToken cancellationToken)
    {
        if (TryBuild() is not { } definition)
        {
            return;
        }

        isBusy = true;
        OnPropertyChanged(nameof(IsBusy));
        Error = null;
        try
        {
            await PersistSecretAsync(definition).ConfigureAwait(true);
            var transient = services.CreateTransient(definition);
            var list = await services.RefreshModelsAsync(transient).ConfigureAwait(true);
            if (Environment.CurrentManagedThreadId != uiThread)
            {
                return;
            }

            foreach (var model in list.Ids)
            {
                if (!Models.Contains(model, StringComparer.Ordinal))
                {
                    Models.Add(model);
                }
            }

            if (DefaultModel.Length == 0 && list.Ids.Count > 0)
            {
                DefaultModel = list.Ids[0];
            }

            Status = Loc.Instance.Format(ProviderStrings.EditorModelsFetchedKey, TextFormat.Number(list.Ids.Count));
        }
        catch (Exception exception) when (exception is ProviderException or HttpRequestException or IOException or TimeoutException)
        {
            Error = exception.Message;
        }
        finally
        {
            isBusy = false;
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    /// <summary>
    /// Starts the GitHub device-code login and polls until the user authorizes, cancels, or the code expires. The code and
    /// link appear as soon as GitHub returns them; the browser is opened by the view, not from here.
    /// </summary>
    private async Task SignInAsync()
    {
        if (SelectedAuth?.Kind == ProviderCredentialKind.ClaudeSubscription)
        {
            // The subscription is the CLI's own login; there is nothing to authorize from here.
            RaiseStatus();
            return;
        }

        if (SelectedAuth?.Kind != ProviderCredentialKind.GitHubCopilot)
        {
            return;
        }

        var definition = TryBuild();
        if (definition is null)
        {
            return;
        }

        loginCancellation?.Cancel();
        loginCancellation?.Dispose();
        loginCancellation = new CancellationTokenSource();
        var token = loginCancellation.Token;
        isBusy = true;
        OnPropertyChanged(nameof(IsBusy));
        LoginState = Loc.Instance[ProviderStrings.EditorDeviceCodeWaitingKey];
        try
        {
            var code = await services.Credentials.StartCopilotLoginAsync(host: null, token).ConfigureAwait(true);
            DeviceCode = code.UserCode;
            VerificationUri = code.VerificationUri;
            OnPropertyChanged(nameof(DeviceCodeText));
            OpenBrowser?.Invoke(this, code.VerificationUri);

            var interval = TimeSpan.FromSeconds(Math.Clamp(code.IntervalSeconds, 1, 30));
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(interval, token).ConfigureAwait(true);
                var session = await services.Credentials.PollCopilotLoginAsync(definition.Id, code, host: null, token).ConfigureAwait(true);
                if (session is not null)
                {
                    Status = Loc.Instance[ProviderStrings.ReadyCopilotKey];
                    LoginState = Loc.Instance[ProviderStrings.EditorSignedInKey];
                    DeviceCode = null;
                    VerificationUri = null;
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            LoginState = null;
        }
        catch (Exception exception) when (exception is ProviderException or HttpRequestException or IOException)
        {
            Error = exception.Message;
            LoginState = null;
        }
        finally
        {
            isBusy = false;
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    /// <summary>The view opens the device page; the model asks, the window does.</summary>
    internal event EventHandler<string>? OpenBrowser;

    private void SignOut()
    {
        if (original is { } definition)
        {
            services.Credentials.ForgetCopilot(definition.Id);
        }

        RaiseStatus();
    }

    /// <summary>A stable, readable id: the display name reduced to its slug, with a numeric suffix on a collision.</summary>
    private static string Slug(string name, IReadOnlyList<string> models)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length > 0 ? slug : "provider-" + Math.Abs(models.Count);
    }
}
