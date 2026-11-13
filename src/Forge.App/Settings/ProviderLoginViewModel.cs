using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Providers;

namespace Forge.App.Settings;

/// <summary>
/// The "登录" dialog of a built-in provider that signs in instead of taking a key. Claude reuses the local Claude Code
/// sign-in, so this dialog only checks for the command and its login and, when the command is missing, explains the install.
/// GitHub Copilot, Kimi For Coding, xAI, Meta and ChatGPT run their device-code flows here, and OpenRouter runs its PKCE
/// browser sign-in here. Either way, the dialog closes with the connected definition it produced.
/// </summary>
internal sealed class ProviderLoginViewModel : NotifyingObject, IDisposable
{
    /// <summary>The official Claude Code setup page, opened from the install guidance.</summary>
    internal const string ClaudeCodeDocsUrl = "https://code.claude.com/docs/en/setup";

    private readonly ProviderServices services;
    private readonly ProviderPreset preset;
    private readonly string id;
    private readonly Func<ProviderDefinition, Task<string?>>? persist;
    private readonly bool isClaude;
    private readonly bool isKimi;
    private readonly bool isXAi;
    private readonly bool isMeta;
    private readonly bool isChatGpt;
    private readonly bool isOpenRouter;
    private bool cliInstalled;
    private bool claudeSignedIn;
    private bool isBusy;
    private string? deviceCode;
    private string? loginState;
    private string? error;
    private CancellationTokenSource? cancellation;

    /// <summary>
    /// <paramref name="persist"/> writes the connected definition to the settings and returns the failure to show, or
    /// <c>null</c> on success; the dialog closes only after it succeeds.
    /// </summary>
    public ProviderLoginViewModel(
        ProviderServices services,
        ProviderPreset preset,
        IReadOnlyCollection<string> existingProviderIds,
        Func<ProviderDefinition, Task<string?>>? persist = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(preset);
        this.services = services;
        this.preset = preset;
        this.persist = persist;
        isClaude = preset.Auth.HasFlag(ProviderAuthOptions.ClaudeSubscription);
        isKimi = preset.Auth.HasFlag(ProviderAuthOptions.KimiCode);
        isXAi = preset.Auth.HasFlag(ProviderAuthOptions.XAiSubscription);
        isMeta = preset.Auth.HasFlag(ProviderAuthOptions.MetaSubscription);
        isChatGpt = preset.Auth.HasFlag(ProviderAuthOptions.ChatGptSubscription);
        isOpenRouter = preset.Auth.HasFlag(ProviderAuthOptions.OpenRouterOAuth);
        id = ProviderIdentity.Unique(preset.Id, existingProviderIds ?? []);

        RecheckCommand = new AsyncRelayCommand(_ => RecheckAsync(), () => !IsBusy);
        InstallCommand = new RelayCommand(() => OpenUrl?.Invoke(this, ClaudeCodeDocsUrl));
        UseClaudeCommand = new AsyncRelayCommand(_ => UseClaudeAsync(), () => ClaudeReady && !IsBusy);
        StartCopilotCommand = new AsyncRelayCommand(_ => StartCopilotAsync(), () => !IsBusy);
        StartKimiCommand = new AsyncRelayCommand(_ => StartKimiAsync(), () => !IsBusy);
        StartXAiCommand = new AsyncRelayCommand(_ => StartXAiAsync(), () => !IsBusy);
        StartMetaCommand = new AsyncRelayCommand(_ => StartMetaAsync(), () => !IsBusy);
        StartChatGptCommand = new AsyncRelayCommand(_ => StartChatGptAsync(), () => !IsBusy);
        StartOpenRouterCommand = new AsyncRelayCommand(_ => StartOpenRouterAsync(), () => !IsBusy);
        CancelCommand = new RelayCommand(() => Cancelled?.Invoke(this, EventArgs.Empty));
        _ = RecheckAsync();
    }

    /// <summary>Raised with the connected definition when the sign-in succeeds; the window closes with it.</summary>
    public event EventHandler<ProviderDefinition>? Done;

    /// <summary>Raised when the user cancels; the window closes with nothing.</summary>
    public event EventHandler? Cancelled;

    /// <summary>Raised when the view should open a page in the browser.</summary>
    public event EventHandler<string>? OpenUrl;

    public bool IsCopilot => preset.Auth.HasFlag(ProviderAuthOptions.GitHubCopilot);

    public bool IsKimi => isKimi;

    public bool IsXAi => isXAi;

    public bool IsMeta => isMeta;

    public bool IsChatGpt => isChatGpt;

    public bool IsOpenRouter => isOpenRouter;

    public string Title => Loc.Instance.Format(ProviderStrings.LoginTitleKey, preset.DisplayName);

    public bool ClaudeNotInstalled => isClaude && !cliInstalled;

    public bool ClaudeNotSignedIn => isClaude && cliInstalled && !claudeSignedIn;

    public bool ClaudeReady => isClaude && cliInstalled && claudeSignedIn;

    public string ClaudeNotInstalledText => Loc.Instance[ProviderStrings.LoginClaudeNotInstalledKey];

    public string ClaudeStepsText => Loc.Instance[ProviderStrings.LoginClaudeStepsKey];

    public string ClaudeNotSignedInText => Loc.Instance[ProviderStrings.NeedsClaudeSignInKey];

    public string ClaudeReadyText => Loc.Instance[ProviderStrings.ReadyClaudeKey];

    public string InstallText => Loc.Instance[ProviderStrings.LoginClaudeInstallKey];

    public string RecheckText => Loc.Instance[ProviderStrings.LoginClaudeRecheckKey];

    public string UseText => Loc.Instance[ProviderStrings.LoginClaudeUseKey];

    public string StartCopilotText => Loc.Instance[ProviderStrings.LoginCopilotStartKey];

    public string StartKimiText => Loc.Instance[ProviderStrings.LoginKimiStartKey];

    public string KimiHint => Loc.Instance[ProviderStrings.LoginKimiHintKey];

    public string StartXAiText => Loc.Instance[ProviderStrings.LoginXAiStartKey];

    public string XAiHint => Loc.Instance[ProviderStrings.LoginXAiHintKey];

    public string StartMetaText => Loc.Instance[ProviderStrings.LoginMetaStartKey];

    public string MetaHint => Loc.Instance[ProviderStrings.LoginMetaHintKey];

    public string StartChatGptText => Loc.Instance[ProviderStrings.LoginChatGptStartKey];

    public string ChatGptHint => Loc.Instance[ProviderStrings.LoginChatGptHintKey];

    public string StartOpenRouterText => Loc.Instance[ProviderStrings.LoginOpenRouterStartKey];

    public string OpenRouterHint => Loc.Instance[ProviderStrings.LoginOpenRouterHintKey];

    public string CancelText => Loc.Instance[ProviderStrings.EditorCancelKey];

    public string CopilotHint => Loc.Instance[ProviderStrings.EditorCopilotHintKey];

    public string DeviceCodeText => deviceCode is null
        ? string.Empty
        : Loc.Instance.Format(DeviceCodeLabelKey, deviceCode);

    public string DeviceCodeHint => Loc.Instance[ProviderStrings.EditorDeviceCodeHintKey];

    public bool HasDeviceCode => deviceCode is { Length: > 0 };

    /// <summary>The device-code wait state ("waiting for you to authorize"), or the signed-in line once it succeeds.</summary>
    public string? LoginState
    {
        get => loginState;
        private set => SetAndNotify(ref loginState, value, nameof(HasLoginState));
    }

    public bool HasLoginState => LoginState is { Length: > 0 };

    public string? Error
    {
        get => error;
        private set => SetAndNotify(ref error, value, nameof(HasError));
    }

    public bool HasError => Error is { Length: > 0 };

    public bool IsBusy => isBusy;

    public AsyncRelayCommand RecheckCommand { get; }

    public RelayCommand InstallCommand { get; }

    public AsyncRelayCommand UseClaudeCommand { get; }

    public AsyncRelayCommand StartCopilotCommand { get; }

    public AsyncRelayCommand StartKimiCommand { get; }

    public AsyncRelayCommand StartXAiCommand { get; }

    public AsyncRelayCommand StartMetaCommand { get; }

    public AsyncRelayCommand StartChatGptCommand { get; }

    public AsyncRelayCommand StartOpenRouterCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>
    /// Re-reads whether the command exists and whether it is signed in. Both are file-system reads, so they run on the pool
    /// and the result is applied back on the dialog's thread.
    /// </summary>
    public async Task RecheckAsync()
    {
        if (!isClaude)
        {
            return;
        }

        var (installed, signedIn) = await Task.Run(() =>
        {
            var found = ClaudeCodeInstallation.IsInstalled();
            return (found, found && services.Credentials.HasClaudeLogin());
        }).ConfigureAwait(true);
        cliInstalled = installed;
        claudeSignedIn = signedIn;
        RaiseClaudeState();
    }

    private async Task UseClaudeAsync()
    {
        if (!ClaudeReady)
        {
            return;
        }

        isBusy = true;
        RaiseBusy();
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        try
        {
            // The local login is usable for listing too, so the new provider starts from what the account actually serves
            // instead of the seeds; a failed listing keeps them and the row's refresh can try again later.
            var definition = await FetchModelsAsync(Build(ProviderCredentialKind.ClaudeSubscription), token)
                .ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            await CompleteAsync(definition).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            LoginState = null;
        }
        finally
        {
            isBusy = false;
            RaiseBusy();
        }
    }

    /// <summary>The dialog closes with the definition only once it has been written; a refused write keeps it open with the reason.</summary>
    private async Task CompleteAsync(ProviderDefinition definition)
    {
        try
        {
            var failure = persist is null ? null : await persist(definition).ConfigureAwait(true);
            if (failure is not null)
            {
                Error = failure;
                return;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = exception.Message;
            return;
        }

        Done?.Invoke(this, definition);
    }

    /// <summary>
    /// Runs the GitHub device-code flow until the user authorises, cancels or the code expires. On success the Copilot model
    /// list is fetched once, so the connected provider has models before the dialog closes.
    /// </summary>
    private async Task StartCopilotAsync()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        isBusy = true;
        RaiseBusy();
        Error = null;
        LoginState = Loc.Instance[ProviderStrings.EditorDeviceCodeWaitingKey];
        try
        {
            var code = await services.Credentials.StartCopilotLoginAsync(host: null, token).ConfigureAwait(true);
            deviceCode = code.UserCode;
            RaiseDeviceCode();
            OpenUrl?.Invoke(this, code.VerificationUri);

            var interval = TimeSpan.FromSeconds(Math.Clamp(code.IntervalSeconds, 1, 30));
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(interval, token).ConfigureAwait(true);
                var session = await services.Credentials.PollCopilotLoginAsync(id, code, host: null, token).ConfigureAwait(true);
                if (session is null)
                {
                    continue;
                }

                LoginState = Loc.Instance[ProviderStrings.EditorSignedInKey];
                deviceCode = null;
                RaiseDeviceCode();
                var definition = Build(ProviderCredentialKind.GitHubCopilot);
                definition = await FetchModelsAsync(definition, token).ConfigureAwait(true);
                await CompleteAsync(definition).ConfigureAwait(true);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            LoginState = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Any failure of the device flow ends the wait and says why; nothing may vanish inside the command.
            Error = exception.Message;
            LoginState = null;
        }
        finally
        {
            isBusy = false;
            RaiseBusy();
        }
    }

    /// <summary>
    /// Runs the Kimi For Coding device-code flow until the user authorises, cancels or the code expires. The credential layer
    /// owns the poll loop and hands back the user code as soon as it exists; on success the model list is fetched once, exactly
    /// as the Copilot branch does, so the connected provider has models before the dialog closes.
    /// </summary>
    private async Task StartKimiAsync()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        isBusy = true;
        RaiseBusy();
        Error = null;
        LoginState = Loc.Instance[ProviderStrings.EditorDeviceCodeWaitingKey];
        try
        {
            await services.Credentials
                .SignInKimiAsync(id, ShowKimiCode, token)
                .ConfigureAwait(true);
            LoginState = Loc.Instance[ProviderStrings.EditorSignedInKey];
            deviceCode = null;
            RaiseDeviceCode();
            var definition = Build(ProviderCredentialKind.KimiCode);
            definition = await FetchModelsAsync(definition, token).ConfigureAwait(true);
            await CompleteAsync(definition).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            LoginState = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Any failure of the device flow ends the wait and says why; nothing may vanish inside the command.
            Error = exception.Message;
            LoginState = null;
        }
        finally
        {
            isBusy = false;
            RaiseBusy();
        }
    }

    /// <summary>Shows the code the user has to type and opens the page that accepts it.</summary>
    private void ShowKimiCode(KimiDeviceCode code)
    {
        // The complete URL carries the code, so the user confirms instead of typing it once the browser is there.
        deviceCode = code.UserCode;
        RaiseDeviceCode();
        OpenUrl?.Invoke(this, code.VerificationUriComplete);
    }

    /// <summary>
    /// Runs the xAI device-code flow the same way the Kimi branch does: the credential layer owns the poll loop and hands back
    /// the user code as soon as it exists; on success the model list is fetched once, best effort, so the connected provider
    /// has models before the dialog closes.
    /// </summary>
    private async Task StartXAiAsync()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        isBusy = true;
        RaiseBusy();
        Error = null;
        LoginState = Loc.Instance[ProviderStrings.EditorDeviceCodeWaitingKey];
        try
        {
            await services.Credentials
                .SignInXAiAsync(id, ShowXAiCode, token)
                .ConfigureAwait(true);
            LoginState = Loc.Instance[ProviderStrings.EditorSignedInKey];
            deviceCode = null;
            RaiseDeviceCode();
            var definition = Build(ProviderCredentialKind.XAiSubscription);
            definition = await FetchModelsAsync(definition, token).ConfigureAwait(true);
            await CompleteAsync(definition).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            LoginState = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Any failure of the device flow ends the wait and says why; nothing may vanish inside the command.
            Error = exception.Message;
            LoginState = null;
        }
        finally
        {
            isBusy = false;
            RaiseBusy();
        }
    }

    /// <summary>Shows the xAI code the user has to type and opens the page that accepts it.</summary>
    private void ShowXAiCode(XAiDeviceCode code)
    {
        deviceCode = code.UserCode;
        RaiseDeviceCode();
        OpenUrl?.Invoke(this, code.VerificationUriComplete);
    }

    /// <summary>
    /// Runs the Meta subscription sign-in: the device flow for an identity token, then the mint that turns it into the API key
    /// the requests use. The mint is a second round trip, so the dialog says "enabling access" while it runs instead of
    /// looking stuck between the approval and the result. The model list is fetched once, best effort, like the other vendors.
    /// </summary>
    private async Task StartMetaAsync()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        isBusy = true;
        RaiseBusy();
        Error = null;
        LoginState = Loc.Instance[ProviderStrings.EditorDeviceCodeWaitingKey];
        try
        {
            await services.Credentials
                .SignInMetaAsync(id, ShowMetaCode, ShowMetaMinting, token)
                .ConfigureAwait(true);
            LoginState = Loc.Instance[ProviderStrings.EditorSignedInKey];
            deviceCode = null;
            RaiseDeviceCode();
            var definition = Build(ProviderCredentialKind.MetaSubscription);
            definition = await FetchModelsAsync(definition, token).ConfigureAwait(true);
            await CompleteAsync(definition).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            LoginState = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Any failure of the flow ends the wait and says why — including the mint's "finish the setup at <url>"; nothing
            // may vanish inside the command.
            Error = exception.Message;
            LoginState = null;
        }
        finally
        {
            isBusy = false;
            RaiseBusy();
        }
    }

    /// <summary>Shows the Meta code and opens the page that accepts it; the complete URL is only sent when Meta gave one.</summary>
    private void ShowMetaCode(MetaDeviceCode code)
    {
        deviceCode = code.UserCode;
        RaiseDeviceCode();
        if (code.VerificationUriComplete is { Length: > 0 } page)
        {
            OpenUrl?.Invoke(this, page);
        }
    }

    /// <summary>The user approved; the key is being minted for this machine, which is not instant.</summary>
    private void ShowMetaMinting() => LoginState = Loc.Instance[ProviderStrings.LoginMetaEnablingKey];

    /// <summary>
    /// Runs the ChatGPT plan sign-in the same way the xAI branch does. There is no model list to fetch: the ChatGPT preset has
    /// none on the server side, so the connected provider keeps whatever models it was configured with.
    /// </summary>
    private async Task StartChatGptAsync()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        isBusy = true;
        RaiseBusy();
        Error = null;
        LoginState = Loc.Instance[ProviderStrings.EditorDeviceCodeWaitingKey];
        try
        {
            await services.Credentials
                .SignInChatGptAsync(id, ShowChatGptCode, token)
                .ConfigureAwait(true);
            LoginState = Loc.Instance[ProviderStrings.EditorSignedInKey];
            deviceCode = null;
            RaiseDeviceCode();
            await CompleteAsync(Build(ProviderCredentialKind.ChatGptSubscription)).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            LoginState = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = exception.Message;
            LoginState = null;
        }
        finally
        {
            isBusy = false;
            RaiseBusy();
        }
    }

    /// <summary>Shows the ChatGPT code and opens the one page that accepts it.</summary>
    private void ShowChatGptCode(ChatGptDeviceCode code)
    {
        deviceCode = code.UserCode;
        RaiseDeviceCode();
        OpenUrl?.Invoke(this, ChatGptAuthentication.VerificationUrl);
    }

    /// <summary>
    /// Runs the OpenRouter sign-in: the credential layer starts listening first, the browser approves the app, and the key that
    /// comes back is stored exactly where a pasted one goes. There is no device code to type, so the dialog only says that the
    /// browser was opened and is being waited on; the model list is fetched once, best effort, like the other vendors.
    /// </summary>
    private async Task StartOpenRouterAsync()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        isBusy = true;
        RaiseBusy();
        Error = null;
        LoginState = Loc.Instance[ProviderStrings.LoginOpenRouterWaitingKey];
        try
        {
            await services.Credentials
                .SignInOpenRouterAsync(id, page => OpenUrl?.Invoke(this, page.AbsoluteUri), token)
                .ConfigureAwait(true);
            LoginState = Loc.Instance[ProviderStrings.EditorSignedInKey];
            var definition = Build(ProviderCredentialKind.ApiKey);
            definition = await FetchModelsAsync(definition, token).ConfigureAwait(true);
            await CompleteAsync(definition).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            LoginState = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The sign-in ends the wait with its own reason: a timeout, a callback with no code or a refused exchange.
            Error = exception.Message;
            LoginState = null;
        }
        finally
        {
            isBusy = false;
            RaiseBusy();
        }
    }

    /// <summary>Which vendor's wording the code line uses; Copilot is the one that is none of the others.</summary>
    private string DeviceCodeLabelKey => isKimi
        ? ProviderStrings.LoginKimiCodeKey
        : isXAi
            ? ProviderStrings.LoginXAiCodeKey
            : isMeta
                ? ProviderStrings.LoginMetaCodeKey
                : isChatGpt
                    ? ProviderStrings.LoginChatGptCodeKey
                    : ProviderStrings.EditorDeviceCodeKey;

    /// <summary>
    /// Best effort: a listing that fails — no network, a refused credential, a body that is not a model list — leaves the
    /// definition on its seeds, because a sign-in that already succeeded must not be undone by the optional step that
    /// decorates it. The row's refresh can fetch the list later.
    /// </summary>
    private async Task<ProviderDefinition> FetchModelsAsync(ProviderDefinition definition, CancellationToken token)
    {
        try
        {
            var list = await services.RefreshModelsAsync(services.CreateTransient(definition), token).ConfigureAwait(true);
            return definition with
            {
                Models = [.. list.Ids],
                DefaultModel = ProviderDefinition.PreferredDefaultModel(definition.PresetId, list.Ids),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return definition;
        }
    }

    private ProviderDefinition Build(ProviderCredentialKind kind)
    {
        // The flow that just completed names the kind it produced, and the kind picks the endpoint too: a ChatGPT plan is a
        // Responses endpoint with its own seed models, not the API-key one the preset's primary fields describe.
        var definition = ProviderDefinition.FromPreset(preset, id, credentialKind: kind);
        return definition with
        {
            Thinking = ThinkingEffort.Medium,
            // A subscription keeps its bundle in the store under its own reference, so the definition holds none; an API-key
            // sign-in (OpenRouter) produced an ordinary key, and that goes under the reference a pasted one uses.
            SecretReference = kind == ProviderCredentialKind.ApiKey ? ProviderCredentials.ApiKeyReference(id) : null,
        };
    }

    public void Dispose()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }

    private void RaiseClaudeState()
    {
        OnPropertyChanged(nameof(ClaudeNotInstalled));
        OnPropertyChanged(nameof(ClaudeNotSignedIn));
        OnPropertyChanged(nameof(ClaudeReady));
        UseClaudeCommand.NotifyCanExecuteChanged();
    }

    private void RaiseDeviceCode()
    {
        OnPropertyChanged(nameof(DeviceCodeText));
        OnPropertyChanged(nameof(HasDeviceCode));
    }

    private void RaiseBusy()
    {
        OnPropertyChanged(nameof(IsBusy));
        RecheckCommand.NotifyCanExecuteChanged();
        UseClaudeCommand.NotifyCanExecuteChanged();
        StartCopilotCommand.NotifyCanExecuteChanged();
        StartKimiCommand.NotifyCanExecuteChanged();
        StartXAiCommand.NotifyCanExecuteChanged();
        StartMetaCommand.NotifyCanExecuteChanged();
        StartChatGptCommand.NotifyCanExecuteChanged();
        StartOpenRouterCommand.NotifyCanExecuteChanged();
    }
}
