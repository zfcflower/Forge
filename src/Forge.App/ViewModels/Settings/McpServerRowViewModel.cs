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
    private bool actionRunning;
    private bool toolsLoaded;
    private bool expanded;
    private bool enabled;
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

        var notBusy = () => !actionRunning;
        start = new AsyncRelayCommand(StartAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        stop = new AsyncRelayCommand(StopAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        toggleRunning = new AsyncRelayCommand(ToggleRunningAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        restart = new AsyncRelayCommand(RestartAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        test = new AsyncRelayCommand(TestAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        refreshTools = new AsyncRelayCommand(RefreshToolsAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        delete = new AsyncRelayCommand(DeleteAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        EditCommand = new RelayCommand(() => owner.Edit(this));
        ToggleExpandCommand = new RelayCommand(ToggleExpand);
    }

    // ------------------------------------------------------------------ bindable surface

    /// <summary>The entry as the file spells it; the editor starts from it so unknown fields survive an edit.</summary>
    public McpServerEntry Entry { get; private set; }

    /// <summary>The file that defines this row's entry; the toggle and the delete write there.</summary>
    public string SourcePath { get; }

    public string Name { get; private set; }

    public string TransportText { get; private set; } = string.Empty;

    public string StatusText { get; private set; } = string.Empty;

    public bool IsReady { get; private set; }

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
            if (value && !toolsLoaded)
            {
                _ = LoadToolsAsync();
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

    public string ScopeText { get; }

    // ------------------------------------------------------------------ commands

    public ICommand StartCommand => start;

    public ICommand StopCommand => stop;

    public ICommand ToggleRunningCommand => toggleRunning;

    public ICommand RestartCommand => restart;

    public ICommand TestCommand => test;

    public ICommand RefreshToolsCommand => refreshTools;

    public ICommand EditCommand { get; }

    public ICommand DeleteCommand => delete;

    public ICommand ToggleExpandCommand { get; }

    public void Dispose()
    {
        disposed = true;
    }

    // ------------------------------------------------------------------ state

    /// <summary>Applies the fresh entry and manager state; every localized text is recomputed so a language switch re-renders.</summary>
    public void Apply(McpServerEntry entry, McpServerState? state)
    {
        Entry = entry;
        Name = entry.Name;
        TransportText = entry.IsHttp ? McpSettingsStrings.TransportHttp : McpSettingsStrings.TransportStdio;
        var current = state ?? new McpServerState(entry.Name, McpServerStatus.Stopped, !entry.Disabled);
        IsReady = current.IsReady;
        StatusText = McpSettingsStrings.StatusOf(current);
        enabled = current.Enabled;
        RunningToggleTextValue();
        HasError = current.LastError is { Length: > 0 };
        ErrorText = current.LastError ?? string.Empty;
        StderrTailText = McpServices.Tail(current.StderrTail, owner.StderrTailLines);
        HasStderr = StderrTailText is { Length: > 0 };
        OnPropertyChanged(nameof(Entry));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(TransportText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(StderrTailText));
        OnPropertyChanged(nameof(HasStderr));
        OnPropertyChanged(nameof(ScopeText));
    }

    private void RunningToggleTextValue() => OnPropertyChanged(nameof(RunningToggleText));

    // ------------------------------------------------------------------ actions

    internal void Delete() => _ = DeleteAsync(CancellationToken.None);

    internal void RequestToolsRefresh()
    {
        if (disposed || !toolsLoaded || actionRunning)
        {
            return;
        }

        _ = RefreshToolsAsync(CancellationToken.None);
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

    private async Task RefreshToolsAsync(CancellationToken _)
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
    }
}

/// <summary>One tool a server exposes: its exposed name, the server's description and its localized side effect.</summary>
internal sealed record McpToolRowViewModel(string Name, string Description, string SideEffectText)
{
    public bool HasDescription => Description.Length > 0;
}
