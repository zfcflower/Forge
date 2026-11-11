using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Providers;

namespace Forge.App.Settings;

/// <summary>
/// The "登录" dialog of a built-in provider that signs in instead of taking a key. Claude reuses the local Claude Code
/// sign-in, so this dialog only checks for the command and its login and, when the command is missing, explains the install.
/// GitHub Copilot runs its device-code flow here. Either way, the dialog closes with the connected definition it produced.
/// </summary>
internal sealed class ProviderLoginViewModel : ObservableObject, IDisposable
{
    /// <summary>The official Claude Code setup page, opened from the install guidance.</summary>
    internal const string ClaudeCodeDocsUrl = "https://code.claude.com/docs/en/setup";

    private readonly ProviderServices services;
    private readonly ProviderPreset preset;
    private readonly string id;
    private readonly Func<ProviderDefinition, Task<string?>>? persist;
    private readonly bool isClaude;
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
        id = ProviderIdentity.Unique(preset.Id, existingProviderIds ?? []);

        RecheckCommand = new AsyncRelayCommand(_ => RecheckAsync(), () => !IsBusy);
        InstallCommand = new RelayCommand(() => OpenUrl?.Invoke(this, ClaudeCodeDocsUrl));
        UseClaudeCommand = new AsyncRelayCommand(_ => UseClaudeAsync(), () => ClaudeReady && !IsBusy);
        StartCopilotCommand = new AsyncRelayCommand(_ => StartCopilotAsync(), () => !IsBusy);
        CancelCommand = new RelayCommand(() => Cancelled?.Invoke(this, EventArgs.Empty));
        _ = RecheckAsync();
    }

    /// <summary>Raised with the connected definition when the sign-in succeeds; the window closes with it.</summary>
    public event EventHandler<ProviderDefinition>? Done;

    /// <summary>Raised when the user cancels; the window closes with nothing.</summary>
    public event EventHandler? Cancelled;

    /// <summary>Raised when the view should open a page in the browser.</summary>
    public event EventHandler<string>? OpenUrl;

    public bool IsClaude => isClaude;

    public bool IsCopilot => !isClaude;

    public string Title => Loc.Instance.Format(ProviderStrings.LoginTitleKey, preset.DisplayName);

    public bool ClaudeNotInstalled => isClaude && !cliInstalled;

    public bool ClaudeNotSignedIn => isClaude && cliInstalled && !claudeSignedIn;

    public bool ClaudeReady => isClaude && cliInstalled && claudeSignedIn;

    public string ClaudeNotInstalledText => Loc.Instance[ProviderStrings.LoginClaudeNotInstalledKey];

    public string ClaudeStepsText => Loc.Instance[ProviderStrings.LoginClaudeStepsKey];

    public string ClaudeNotSignedInText => Loc.Instance[ProviderStrings.NeedsClaudeSignInKey];

    public string ClaudeReadyText => Loc.Instance[ProviderStrings.ReadyClaudeKey];

    public string InstallText => Loc.Instance[ProviderStrings.LoginClaudeInstallKey];

    public string DocsText => Loc.Instance[ProviderStrings.LoginClaudeDocsKey];

    public string RecheckText => Loc.Instance[ProviderStrings.LoginClaudeRecheckKey];

    public string UseText => Loc.Instance[ProviderStrings.LoginClaudeUseKey];

    public string StartCopilotText => Loc.Instance[ProviderStrings.LoginCopilotStartKey];

    public string CancelText => Loc.Instance[ProviderStrings.EditorCancelKey];

    public string CopilotHint => Loc.Instance[ProviderStrings.EditorCopilotHintKey];

    public string DeviceCodeText => deviceCode is null ? string.Empty : Loc.Instance.Format(ProviderStrings.EditorDeviceCodeKey, deviceCode);

    public string DeviceCodeHint => Loc.Instance[ProviderStrings.EditorDeviceCodeHintKey];

    public bool HasDeviceCode => deviceCode is { Length: > 0 };

    /// <summary>The device-code wait state ("waiting for you to authorize"), or the signed-in line once it succeeds.</summary>
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

    public bool IsBusy => isBusy;

    public AsyncRelayCommand RecheckCommand { get; }

    public RelayCommand InstallCommand { get; }

    public AsyncRelayCommand UseClaudeCommand { get; }

    public AsyncRelayCommand StartCopilotCommand { get; }

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
        try
        {
            await CompleteAsync(Build(ProviderCredentialKind.ClaudeSubscription)).ConfigureAwait(true);
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

    /// <summary>Best effort: a failed listing leaves the models empty, and the row's refresh can fetch them later.</summary>
    private async Task<ProviderDefinition> FetchModelsAsync(ProviderDefinition definition, CancellationToken token)
    {
        try
        {
            var list = await services.RefreshModelsAsync(services.CreateTransient(definition), token).ConfigureAwait(true);
            return definition with { Models = [.. list.Ids], DefaultModel = list.Ids.FirstOrDefault() ?? string.Empty };
        }
        catch (Exception exception) when (exception is ProviderException or HttpRequestException or IOException or TimeoutException)
        {
            return definition;
        }
    }

    private ProviderDefinition Build(ProviderCredentialKind kind)
    {
        var definition = ProviderDefinition.FromPreset(preset, id);
        return definition with
        {
            CredentialKind = kind,
            Thinking = ThinkingEffort.Medium,
            // The Claude subscription is the CLI's own credential, so the definition holds no secret reference.
            SecretReference = null,
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
    }
}
