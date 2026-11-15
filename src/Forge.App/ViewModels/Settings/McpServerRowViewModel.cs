using System.Collections.ObjectModel;
using System.Windows.Input;
using ICommand = System.Windows.Input.ICommand;
using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.Mcp;

namespace Forge.App.ViewModels.Settings;

/// <summary>One configured server: its entry (the file's words), the manager's state for it, and every action around it.</summary>
internal sealed class McpServerRowViewModel : NotifyingObject, IDisposable
{
    private readonly McpSettingsSectionViewModel owner;
    private readonly AsyncRelayCommand start;
    private readonly AsyncRelayCommand stop;
    private readonly AsyncRelayCommand toggleRunning;
    private readonly AsyncRelayCommand restart;
    private readonly AsyncRelayCommand test;
    private readonly AsyncRelayCommand refreshTools;
    private readonly AsyncRelayCommand delete;
    private readonly AsyncRelayCommand signIn;
    private readonly AsyncRelayCommand signOut;
    private readonly AsyncRelayCommand refreshResources;
    private bool actionRunning;
    private int resourceRefreshPending;
    private bool toolsLoaded;
    private bool resourcesLoaded;
    private bool expanded;
    private bool enabled;
    private bool needsSignIn;
    private bool signedIn;
    private bool disposed;

    public McpServerRowViewModel(
        McpSettingsSectionViewModel owner,
        McpServerEntry entry,
        string sourcePath,
        bool isProject)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(entry);
        this.owner = owner;
        Entry = entry;
        SourcePath = sourcePath;
        ScopeText = isProject ? McpSettingsStrings.ScopeProject : McpSettingsStrings.ScopeGlobal;
        Name = entry.Name;
        IsHttp = entry.IsHttp;

        var notBusy = () => !actionRunning;
        start = new AsyncRelayCommand(StartAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        stop = new AsyncRelayCommand(StopAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        toggleRunning = new AsyncRelayCommand(ToggleRunningAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        restart = new AsyncRelayCommand(RestartAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        test = new AsyncRelayCommand(TestAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        refreshTools = new AsyncRelayCommand(RefreshToolsAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        delete = new AsyncRelayCommand(DeleteAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        signIn = new AsyncRelayCommand(SignInAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        signOut = new AsyncRelayCommand(SignOutAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        refreshResources = new AsyncRelayCommand(RefreshResourcesAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        EditCommand = new RelayCommand(() => owner.Edit(this));
        ToggleExpandCommand = new RelayCommand(ToggleExpand);
    }

    // ------------------------------------------------------------------ bindable surface

    /// <summary>The entry as the file spells it; the editor starts from it so unknown fields survive an edit.</summary>
    public McpServerEntry Entry { get; private set; }

    /// <summary>
    /// The file that defines this row's entry; the toggle and the delete write there. Follows the entry when an edit moved it to
    /// the other file, so a delete never looks for the name in the file the entry left.
    /// </summary>
    public string SourcePath { get; private set; }

    public string Name { get; private set; }

    public string TransportText { get; private set; } = string.Empty;

    public string StatusText { get; private set; } = string.Empty;

    public bool IsReady { get; private set; }

    public bool IsHttp { get; private set; }

    /// <summary>A 401 without a usable token: the row shows the localized sign-in action instead of a raw error line.</summary>
    public bool NeedsSignIn => needsSignIn;

    public bool IsSignedIn => signedIn;

    /// <summary>Sign-in is only meaningful for an HTTP server whose manager has the token store and browser UI wired.</summary>
    public bool CanSignIn => IsHttp && owner.Services?.CanSignIn == true;

    public bool HasSignInAction => NeedsSignIn || IsSignedIn;

    /// <summary>The switch: flips the manager and writes <c>disabled</c> back to the entry's file.</summary>
    public bool IsEnabled
    {
        get => enabled;
        set
        {
            if (disposed || enabled == value)
            {
                return;
            }

            enabled = value;
            OnPropertyChanged();
            _ = ApplyEnabledAsync(value);
        }
    }

    public bool IsExpanded
    {
        get => expanded;
        set
        {
            if (!Set(ref expanded, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ExpandText));
            if (value)
            {
                if (!toolsLoaded)
                {
                    _ = LoadToolsAsync();
                }

                if (IsHttp && !resourcesLoaded)
                {
                    _ = LoadResourcesAsync();
                }
            }
        }
    }

    public string ExpandText => expanded ? McpSettingsStrings.RowCollapse : McpSettingsStrings.RowExpand;

    public string RunningToggleText => IsReady ? McpSettingsStrings.RowStop : McpSettingsStrings.RowStart;

    public bool HasError { get; private set; }

    public string ErrorText { get; private set; } = string.Empty;

    public string? StderrTailText { get; private set; }

    public bool HasStderr { get; private set; }

    /// <summary>Filled when the row is first expanded (and by the refresh action); capped at <see cref="McpSettingsSectionViewModel.MaxVisibleTools"/>.</summary>
    public ObservableCollection<McpToolRowViewModel> Tools { get; } = [];

    public bool HasTools => Tools.Count > 0;

    public bool ToolsTruncated { get; private set; }

    public string ToolsTruncatedText =>
        McpSettingsStrings.ToolsTruncated(McpSettingsSectionViewModel.MaxVisibleTools);

    /// <summary>True once tools were read and none came back; before the first read nothing is claimed.</summary>
    public bool ToolsEmpty => toolsLoaded && Tools.Count == 0;

    public string TestResultText { get; private set; } = string.Empty;

    public bool HasTestResult => TestResultText.Length > 0;

    public string ScopeText { get; private set; }

    // ------------------------------------------------------------------ resources

    /// <summary>Filled when the row is expanded and the server is ready; refreshed on demand and on update notifications.</summary>
    public ObservableCollection<McpResourceRowViewModel> Resources { get; } = [];

    public bool HasResources => Resources.Count > 0;

    public bool ResourcesEmpty => resourcesLoaded && Resources.Count == 0;

    public string ResourceReadResultText { get; private set; } = string.Empty;

    public bool HasResourceReadResult => ResourceReadResultText.Length > 0;

    public string ResourceUpdatedText { get; private set; } = string.Empty;

    public bool HasResourceUpdated => ResourceUpdatedText.Length > 0;

    // ------------------------------------------------------------------ commands

    public ICommand StartCommand => start;

    public ICommand StopCommand => stop;

    public ICommand ToggleRunningCommand => toggleRunning;

    public ICommand RestartCommand => restart;

    public ICommand TestCommand => test;

    public ICommand RefreshToolsCommand => refreshTools;

    public ICommand SignInCommand => signIn;

    public ICommand SignOutCommand => signOut;

    public ICommand RefreshResourcesCommand => refreshResources;

    public ICommand EditCommand { get; }

    public ICommand DeleteCommand => delete;

    public ICommand ToggleExpandCommand { get; }

    public void Dispose()
    {
        disposed = true;
    }

    // ------------------------------------------------------------------ state

    /// <summary>Applies the fresh entry and manager state; every localized text is recomputed so a language switch re-renders.</summary>
    public void Apply(McpServerEntry entry, McpServerState? state, string sourcePath, bool isProject)
    {
        Entry = entry;
        Name = entry.Name;
        var moved = !string.Equals(SourcePath, sourcePath, StringComparison.Ordinal);
        SourcePath = sourcePath;
        ScopeText = isProject ? McpSettingsStrings.ScopeProject : McpSettingsStrings.ScopeGlobal;
        TransportText = entry.IsHttp ? McpSettingsStrings.TransportHttp : McpSettingsStrings.TransportStdio;
        IsHttp = entry.IsHttp;
        var current = state ?? new McpServerState(entry.Name, McpServerStatus.Stopped, !entry.Disabled);
        IsReady = current.IsReady;
        StatusText = McpSettingsStrings.StatusOf(current);
        enabled = current.Enabled;
        needsSignIn = current.NeedsSignIn;
        signedIn = owner.Services?.IsSignedIn(entry.Name) == true;
        RunningToggleTextValue();
        HasError = current.LastError is { Length: > 0 };
        ErrorText = needsSignIn ? McpSettingsStrings.NeedsSignIn : current.LastError ?? string.Empty;
        StderrTailText = McpServices.Tail(current.StderrTail, owner.StderrTailLines);
        HasStderr = StderrTailText is { Length: > 0 };
        OnPropertyChanged(nameof(Entry));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(TransportText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsHttp));
        OnPropertyChanged(nameof(CanSignIn));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(StderrTailText));
        OnPropertyChanged(nameof(HasStderr));
        OnPropertyChanged(nameof(ScopeText));
        OnPropertyChanged(nameof(NeedsSignIn));
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(HasSignInAction));
        if (moved)
        {
            OnPropertyChanged(nameof(SourcePath));
        }
    }

    private void RunningToggleTextValue() => OnPropertyChanged(nameof(RunningToggleText));

    // ------------------------------------------------------------------ actions

    internal void Delete() => _ = DeleteAsync(CancellationToken.None);

    /// <summary>A <c>tools/list_changed</c> notification arrived: re-read the tools unless the user is mid-action.</summary>
    internal void RequestToolsRefresh()
    {
        if (disposed || !toolsLoaded || actionRunning)
        {
            return;
        }

        _ = RefreshToolsAsync(owner.NotificationToken(), userAction: false);
    }

    /// <summary>A <c>resources/list_changed</c> notification arrived: one re-list per burst, on the section's notification token.</summary>
    internal void RequestResourcesRefresh()
    {
        if (disposed || !resourcesLoaded || actionRunning || Interlocked.CompareExchange(ref resourceRefreshPending, 1, 0) != 0)
        {
            return;
        }

        _ = RefreshResourcesAsync(owner.NotificationToken(), userAction: false);
    }

    /// <summary>
    /// A subscribed resource changed: surface it and re-list, so the browser stays current. The re-list is not a user action — it
    /// takes the section's notification token instead of <see cref="McpSettingsSectionViewModel.ActionToken"/> (taking that one
    /// from a notification cancelled a delete or a restart the user had just started, and the swallowed cancellation made the
    /// click look like it did nothing), it is dropped while an action runs, and a burst of updates in one tick lists once.
    /// </summary>
    internal void OnResourceUpdated(McpResourceUpdated update)
    {
        if (disposed || !owner.CanApply)
        {
            return;
        }

        ResourceUpdatedText = McpSettingsStrings.ResourceUpdated(update.Uri);
        OnPropertyChanged(nameof(ResourceUpdatedText));
        OnPropertyChanged(nameof(HasResourceUpdated));
        if (!resourcesLoaded || actionRunning || Interlocked.CompareExchange(ref resourceRefreshPending, 1, 0) != 0)
        {
            return;
        }

        _ = RefreshResourcesAsync(owner.NotificationToken(), userAction: false);
    }

    private Task StartAsync(CancellationToken _) => RunManagerActionAsync(
        static async (manager, name, token) => await manager.StartAsync(name, token).ConfigureAwait(false));

    private Task StopAsync(CancellationToken _) => RunManagerActionAsync(
        static async (manager, name, token) => await manager.StopAsync(name, token).ConfigureAwait(false));

    private Task ToggleRunningAsync(CancellationToken _) => RunManagerActionAsync(
        static async (manager, name, token) =>
        {
            // The toggle reads the live state, not the row's cached copy, so a stale row cannot start a ready server again.
            if (manager.GetState(name).IsReady)
            {
                await manager.StopAsync(name, token).ConfigureAwait(false);
            }
            else
            {
                await manager.StartAsync(name, token).ConfigureAwait(false);
            }
        });

    private Task RestartAsync(CancellationToken _) => RunManagerActionAsync(
        static async (manager, name, token) => await manager.RestartAsync(name, token).ConfigureAwait(false));

    private Task SignInAsync(CancellationToken _) => RunManagerActionAsync(
        static async (manager, name, token) => await manager.SignInAsync(name, token).ConfigureAwait(false));

    private async Task SignOutAsync(CancellationToken _)
    {
        var services = owner.Services;
        if (services is null || disposed)
        {
            return;
        }

        SetBusy(true);
        var ct = owner.ActionToken();
        try
        {
            await BackgroundRead
                .RunAsync(token => services.SignOutAsync(Name, token), ct)
                .ConfigureAwait(true);
            signedIn = false;
            needsSignIn = false;
            if (!disposed && owner.CanApply)
            {
                OnPropertyChanged(nameof(IsSignedIn));
                OnPropertyChanged(nameof(NeedsSignIn));
                OnPropertyChanged(nameof(HasSignInAction));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            if (!disposed && owner.CanApply)
            {
                owner.ShowError(exception.Message);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RunManagerActionAsync(Func<McpServerManager, string, CancellationToken, Task> action)
    {
        var services = owner.Services;
        if (services is null || disposed)
        {
            return;
        }

        SetBusy(true);
        var ct = owner.ActionToken();
        try
        {
            await BackgroundRead
                .RunAsync(token => action(services.Manager, Name, token), ct)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            if (!disposed && owner.CanApply)
            {
                owner.ShowError(exception.Message);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ApplyEnabledAsync(bool value)
    {
        var services = owner.Services;
        if (services is null || disposed)
        {
            return;
        }

        SetBusy(true);
        var ct = owner.ActionToken();
        try
        {
            await BackgroundRead.RunAsync(
                async token =>
                {
                    // The file first: its apply reconciles the manager; the explicit call then pins the final state
                    // without a second restart.
                    await services.SetDisabledAsync(SourcePath, Name, !value, token).ConfigureAwait(false);
                    await services.Manager.SetEnabledAsync(Name, value, token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            if (!disposed && owner.CanApply)
            {
                owner.ShowError(exception.Message);
                var manager = owner.Services?.Manager;
                if (manager is not null)
                {
                    enabled = manager.GetState(Name).Enabled;
                    OnPropertyChanged(nameof(IsEnabled));
                }
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DeleteAsync(CancellationToken _)
    {
        var services = owner.Services;
        if (services is null || disposed)
        {
            return;
        }

        SetBusy(true);
        var ct = owner.ActionToken();
        try
        {
            await BackgroundRead
                .RunAsync(token => services.RemoveAsync(SourcePath, Name, token), ct)
                .ConfigureAwait(true);
            if (!disposed && owner.CanApply)
            {
                owner.ShowStatus(McpSettingsStrings.StatusDeleted(Name), isError: false);
                owner.Reload();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            if (!disposed && owner.CanApply)
            {
                owner.ShowError(exception.Message);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task TestAsync(CancellationToken _)
    {
        var services = owner.Services;
        if (services is null || disposed)
        {
            return;
        }

        if (owner.CanApply)
        {
            TestResultText = McpSettingsStrings.RowTesting;
            OnPropertyChanged(nameof(HasTestResult));
        }

        SetBusy(true);
        var ct = owner.ActionToken();
        try
        {
            var probe = await BackgroundRead
                .RunAsync(token => services.ProbeAsync(Name, token), ct)
                .ConfigureAwait(true);
            if (disposed || !owner.CanApply)
            {
                return;
            }

            TestResultText = probe.IsReady
                ? McpSettingsStrings.TestOk(
                    TextFormat.Elapsed(probe.Elapsed),
                    probe.Tools.Count,
                    McpSettingsStrings.Capabilities(probe.Capabilities))
                : McpSettingsStrings.TestFailed(TextFormat.Elapsed(probe.Elapsed), probe.Error ?? "-");
            OnPropertyChanged(nameof(HasTestResult));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            if (!disposed && owner.CanApply)
            {
                TestResultText = McpSettingsStrings.TestFailed("-", exception.Message);
                OnPropertyChanged(nameof(HasTestResult));
                owner.ShowError(exception.Message);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadToolsAsync()
    {
        if (toolsLoaded || disposed)
        {
            return;
        }

        var services = owner.Services;
        if (services is null)
        {
            return;
        }

        var ct = owner.ActionToken();
        IReadOnlyList<McpToolAdapter> tools;
        try
        {
            tools = await BackgroundRead
                .RunAsync(token => Task.FromResult(services.ToolsOf(Name)), ct)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            return;
        }

        if (disposed || !owner.CanApply)
        {
            return;
        }

        ApplyTools(tools);
        toolsLoaded = true;
        OnPropertyChanged(nameof(ToolsEmpty));
    }

    private Task RefreshToolsAsync(CancellationToken _) => RefreshToolsAsync(owner.ActionToken(), userAction: true);

    /// <param name="userAction">A click (busy state, errors in the status bar) rather than a notification-driven refresh.</param>
    private async Task RefreshToolsAsync(CancellationToken ct, bool userAction)
    {
        var services = owner.Services;
        if (services is null || disposed)
        {
            return;
        }

        if (userAction)
        {
            SetBusy(true);
        }

        try
        {
            IReadOnlyList<McpToolAdapter> tools = IsReady
                ? await BackgroundRead
                    .RunAsync(token => services.Manager.RefreshToolsAsync(Name, token), ct)
                    .ConfigureAwait(true)
                : await BackgroundRead
                    .RunAsync(token => Task.FromResult(services.ToolsOf(Name)), ct)
                    .ConfigureAwait(true);
            if (disposed || !owner.CanApply)
            {
                return;
            }

            ApplyTools(tools);
            toolsLoaded = true;
            OnPropertyChanged(nameof(ToolsEmpty));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            if (userAction && !disposed && owner.CanApply)
            {
                owner.ShowError(exception.Message);
            }
        }
        finally
        {
            if (userAction)
            {
                SetBusy(false);
            }
        }
    }

    private void ApplyTools(IReadOnlyList<McpToolAdapter> tools)
    {
        Tools.Clear();
        ToolsTruncated = tools.Count > McpSettingsSectionViewModel.MaxVisibleTools;
        var kept = Math.Min(tools.Count, McpSettingsSectionViewModel.MaxVisibleTools);
        for (var index = 0; index < kept; index++)
        {
            var tool = tools[index];
            Tools.Add(new McpToolRowViewModel(
                tool.ExposedName,
                tool.Definition.Description ?? string.Empty,
                McpSettingsStrings.SideEffect(tool.SideEffect)));
        }

        OnPropertyChanged(nameof(ToolsTruncated));
        OnPropertyChanged(nameof(ToolsTruncatedText));
        OnPropertyChanged(nameof(HasTools));
    }

    // ------------------------------------------------------------------ resources

    private async Task LoadResourcesAsync()
    {
        if (resourcesLoaded || disposed)
        {
            return;
        }

        var services = owner.Services;
        if (services is null)
        {
            return;
        }

        var ct = owner.ActionToken();
        IReadOnlyList<McpResourceInfo> resources;
        try
        {
            resources = await BackgroundRead
                .RunAsync(token => services.ListResourcesAsync(Name, token), ct)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            return;
        }

        if (disposed || !owner.CanApply)
        {
            return;
        }

        ApplyResources(resources);
        resourcesLoaded = true;
        OnPropertyChanged(nameof(ResourcesEmpty));
    }

    private Task RefreshResourcesAsync(CancellationToken _) => RefreshResourcesAsync(owner.ActionToken(), userAction: true);

    /// <param name="userAction">A click (busy state, errors in the status bar) rather than a notification-driven refresh.</param>
    private async Task RefreshResourcesAsync(CancellationToken ct, bool userAction)
    {
        var services = owner.Services;
        if (services is null || disposed)
        {
            Volatile.Write(ref resourceRefreshPending, 0);
            return;
        }

        if (userAction)
        {
            SetBusy(true);
        }

        try
        {
            var resources = await BackgroundRead
                .RunAsync(token => services.ListResourcesAsync(Name, token), ct)
                .ConfigureAwait(true);
            if (disposed || !owner.CanApply)
            {
                return;
            }

            ApplyResources(resources);
            resourcesLoaded = true;
            OnPropertyChanged(nameof(ResourcesEmpty));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            if (userAction && !disposed && owner.CanApply)
            {
                ShowResourceError(exception);
            }
        }
        finally
        {
            if (userAction)
            {
                SetBusy(false);
            }

            Volatile.Write(ref resourceRefreshPending, 0);
        }
    }

    private void ApplyResources(IReadOnlyList<McpResourceInfo> resources)
    {
        Resources.Clear();
        foreach (var resource in resources)
        {
            // The manager owns the subscription set and replays it onto every new session, so the badge reads its state and not
            // what the previous rows happened to hold: after a reconnect the rows are rebuilt from the truth.
            Resources.Add(new McpResourceRowViewModel(
                resource.Uri,
                resource.Name,
                owner.Services?.IsSubscribed(Name, resource.Uri) == true,
                ReadResourceAsync,
                ToggleSubscribeAsync));
        }

        OnPropertyChanged(nameof(HasResources));
        OnPropertyChanged(nameof(ResourcesEmpty));
    }

    private async Task ReadResourceAsync(McpResourceRowViewModel resource)
    {
        var services = owner.Services;
        if (services is null || disposed || resource is null)
        {
            return;
        }

        SetBusy(true);
        var ct = owner.ActionToken();
        try
        {
            var result = await BackgroundRead
                .RunAsync(token => services.ReadResourceAsync(Name, resource.Uri, token), ct)
                .ConfigureAwait(true);
            if (disposed || !owner.CanApply)
            {
                return;
            }

            ResourceReadResultText = McpSettingsStrings.ResourceReadResult(resource.Uri, TruncateResourceText(result.Text));
            OnPropertyChanged(nameof(ResourceReadResultText));
            OnPropertyChanged(nameof(HasResourceReadResult));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            if (!disposed && owner.CanApply)
            {
                ShowResourceError(exception);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ToggleSubscribeAsync(McpResourceRowViewModel resource)
    {
        var services = owner.Services;
        if (services is null || disposed || resource is null)
        {
            return;
        }

        SetBusy(true);
        var ct = owner.ActionToken();
        var subscribe = !resource.IsSubscribed;
        try
        {
            await BackgroundRead.RunAsync(
                token => subscribe
                    ? services.SubscribeAsync(Name, resource.Uri, token)
                    : services.UnsubscribeAsync(Name, resource.Uri, token),
                ct).ConfigureAwait(true);
            if (disposed || !owner.CanApply)
            {
                return;
            }

            resource.IsSubscribed = subscribe;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            if (!disposed && owner.CanApply)
            {
                ShowResourceError(exception);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Reports a failed resource action. A 401 is not a technical detail the user can judge: it means this server needs a
    /// sign-in, which the row's own action already offers, so the status bar says that in the current language instead.
    /// </summary>
    private void ShowResourceError(Exception exception) =>
        owner.ShowError(exception is McpOAuthRequiredException ? McpSettingsStrings.NeedsSignIn : exception.Message);

    private static string TruncateResourceText(string text)
    {
        const int max = 64 * 1024;
        if (text.Length <= max)
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, max), "\n…[truncated]");
    }

    private void ToggleExpand() => IsExpanded = !expanded;

    private void SetBusy(bool busy)
    {
        actionRunning = busy;
        start.NotifyCanExecuteChanged();
        stop.NotifyCanExecuteChanged();
        toggleRunning.NotifyCanExecuteChanged();
        restart.NotifyCanExecuteChanged();
        test.NotifyCanExecuteChanged();
        refreshTools.NotifyCanExecuteChanged();
        delete.NotifyCanExecuteChanged();
        signIn.NotifyCanExecuteChanged();
        signOut.NotifyCanExecuteChanged();
        refreshResources.NotifyCanExecuteChanged();
    }
}

/// <summary>One tool a server exposes: its exposed name, the server's description and its localized side effect.</summary>
internal sealed record McpToolRowViewModel(string Name, string Description, string SideEffectText)
{
    public bool HasDescription => Description.Length > 0;
}

/// <summary>One resource a server exposes: read and subscribe/unsubscribe, with the subscribed state kept in the row.</summary>
internal sealed class McpResourceRowViewModel : NotifyingObject
{
    private bool subscribed;

    public McpResourceRowViewModel(
        string uri,
        string? name,
        bool subscribed,
        Func<McpResourceRowViewModel, Task> read,
        Func<McpResourceRowViewModel, Task> toggleSubscribe)
    {
        Uri = uri;
        Name = string.IsNullOrWhiteSpace(name) ? uri : name;
        this.subscribed = subscribed;
        ReadCommand = new AsyncRelayCommand(_ => read(this), onError: _ => { });
        SubscribeCommand = new AsyncRelayCommand(_ => toggleSubscribe(this), onError: _ => { });
    }

    public string Uri { get; }

    public string Name { get; }

    public string UriText => Uri;

    public bool HasName => !string.Equals(Name, Uri, StringComparison.Ordinal);

    public bool IsSubscribed
    {
        get => subscribed;
        set
        {
            if (!Set(ref subscribed, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SubscribeText));
        }
    }

    public string SubscribeText => IsSubscribed ? McpSettingsStrings.ResourceUnsubscribe : McpSettingsStrings.ResourceSubscribe;

    public bool IsSubscribedBadge => IsSubscribed;

    public ICommand ReadCommand { get; }

    public ICommand SubscribeCommand { get; }
}
