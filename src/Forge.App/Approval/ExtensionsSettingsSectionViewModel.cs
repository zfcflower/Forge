using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Approval;
using Forge.HostClient;
using Forge.Plugin.Sdk;

namespace Forge.App.Approval;

/// <summary>
/// The <c>extensions</c> settings section (<see cref="Preferences.SettingsSectionIds.Extensions"/>): the installed
/// extensions with their state and the enable/disable/delete actions, the staged drafts that can be opened as an approval
/// card, and the remembered rules grouped by scope with a revoke button each. Everything the host answers is read in the
/// background - the section is built from a snapshot on the UI thread - and a host that is missing or not running is shown
/// as exactly that, never as "no extensions".
/// </summary>
internal sealed class ExtensionsSettingsSectionViewModel : ObservableObject, IDisposable
{
    private readonly IExtensionsAdmin? admin;
    private readonly IExtensionHostState host;
    private readonly ApprovalRules rules;
    private readonly Func<string, CancellationToken, Task<bool>> openPendingCard;
    private readonly IApprovalDispatcher dispatcher;
    private readonly ExtensionRepositoryService repository;
    private ExtensionSnapshot? snapshot;
    private string? problem;
    private string? actionProblem;
    private string? repositoryProblem;
    private string repositoryUrl = string.Empty;
    private readonly AppPreferences? preferences;
    private bool isRefreshing;
    private bool isBrowsingRepository;
    private bool disposed;
    private int generation;

    public ExtensionsSettingsSectionViewModel(
        IExtensionsAdmin? admin,
        IExtensionHostState host,
        ApprovalRules rules,
        Func<string, CancellationToken, Task<bool>> openPendingCard,
        IApprovalDispatcher dispatcher,
        AppPreferences? preferences = null,
        HttpClient? repositoryHttp = null)
    {
        this.admin = admin;
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.openPendingCard = openPendingCard ?? throw new ArgumentNullException(nameof(openPendingCard));
        this.dispatcher = dispatcher ?? UiThreadDispatcher.Instance;
        this.preferences = preferences;
        repositoryUrl = preferences?.ExtensionRepositoryUrl ?? string.Empty;
        repository = new ExtensionRepositoryService(repositoryHttp);

        // A run that was killed mid-download leaves extractions under the host data directory that no host will ever
        // accept; the root is transient by contract, so it is emptied once here (startup) instead of being trusted.
        ExtensionRepositoryService.ClearInstallTempRoot();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !isRefreshing, OnCommandFailed);
        BrowseRepositoryCommand = new AsyncRelayCommand(BrowseRepositoryAsync, () => !IsBrowsingRepository && admin is not null, OnCommandFailed);

        rules.Changed += OnRulesChanged;
        host.Changed += OnHostChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;

        RebuildRules();
    }

    public RangeObservableCollection<InstalledExtensionViewModel> Installed { get; } = [];

    public RangeObservableCollection<PendingExtensionViewModel> Pending { get; } = [];

    public RangeObservableCollection<RememberedRuleGroupViewModel> RuleGroups { get; } = [];

    public RangeObservableCollection<RepositoryPackageViewModel> Repository { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand BrowseRepositoryCommand { get; }

    public string InstalledTitle => Loc.Instance[ExtensionsStrings.SettingsInstalled];

    public string PendingTitle => Loc.Instance[ExtensionsStrings.SettingsPending];

    public string RulesTitle => Loc.Instance[ExtensionsStrings.SettingsRules];

    public string RepositoryTitle => Loc.Instance[ExtensionsStrings.SettingsRepository];

    public string RefreshText => Loc.Instance[IsRefreshing ? ExtensionsStrings.SettingsLoading : ExtensionsStrings.SettingsRefresh];

    public bool IsRefreshing
    {
        get => isRefreshing;
        private set
        {
            if (!Set(ref isRefreshing, value))
            {
                return;
            }

            OnPropertyChanged(nameof(RefreshText));
            OnPropertyChanged(nameof(ShowInstalledEmpty));
            OnPropertyChanged(nameof(ShowPendingEmpty));
            RefreshCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The repository index URL the user wants to browse; empty means "no remote repository". Persisted through
    /// <see cref="AppPreferences.ExtensionRepositoryUrl"/> when the shell injected preferences (wave W10a integration).</summary>
    public string RepositoryUrl
    {
        get => repositoryUrl;
        set
        {
            if (Set(ref repositoryUrl, value ?? string.Empty))
            {
                if (preferences is not null)
                {
                    preferences.ExtensionRepositoryUrl = repositoryUrl;
                }

                OnPropertyChanged(nameof(HasRepositoryUrl));
            }
        }
    }

    public bool HasRepositoryUrl => !string.IsNullOrWhiteSpace(RepositoryUrl);

    public bool IsBrowsingRepository
    {
        get => isBrowsingRepository;
        private set
        {
            if (!Set(ref isBrowsingRepository, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowRepositoryEmpty));
            OnPropertyChanged(nameof(RepositoryBrowseText));
            BrowseRepositoryCommand.NotifyCanExecuteChanged();
        }
    }

    public string RepositoryBrowseText => Loc.Instance[IsBrowsingRepository
        ? ExtensionsStrings.SettingsRepositoryLoading
        : ExtensionsStrings.SettingsRepositoryBrowse];

    /// <summary>Why the repository index could not be shown; null while browsing or after a successful read.</summary>
    public string? RepositoryProblem => repositoryProblem;

    public bool HasRepositoryProblem => repositoryProblem is not null;

    public bool ShowRepositoryEmpty => !IsBrowsingRepository && repositoryProblem is null && Repository.Count == 0;

    /// <summary>
    /// Why the extension blocks cannot be shown: no host at all, a host that is not running, or a failed read. Null while
    /// the registry is readable; the remembered rules stay visible either way because they live in this process.
    /// </summary>
    public string? UnavailableText
    {
        get
        {
            if (admin is null)
            {
                var reason = host.HostProblem;
                return Loc.Instance[ExtensionsStrings.SettingsNoHost] +
                       (string.IsNullOrWhiteSpace(reason) ? string.Empty : " " + reason);
            }

            if (host.Status != HostStatus.Running)
            {
                var detail = string.IsNullOrWhiteSpace(host.StatusDetail) ? host.Status.ToString() : host.StatusDetail;
                return Loc.Instance.Format(ExtensionsStrings.SettingsUnavailable, detail);
            }

            return problem is null ? null : Loc.Instance.Format(ExtensionsStrings.SettingsUnavailable, problem);
        }
    }

    public bool HasUnavailableText => UnavailableText is not null;

    /// <summary>The extension blocks are only rendered when the registry could actually be read.</summary>
    public bool ShowExtensions => UnavailableText is null;

    public bool ShowInstalledEmpty => ShowExtensions && !IsRefreshing && Installed.Count == 0;

    public bool ShowPendingEmpty => ShowExtensions && !IsRefreshing && Pending.Count == 0;

    public bool ShowRulesEmpty => RuleGroups.Count == 0;

    /// <summary>The reason the rules file could not be read or written, if any.</summary>
    public string? RulesProblemText
    {
        get
        {
            if (rules.LoadProblem is { Length: > 0 } loadProblem)
            {
                return Loc.Instance.Format(ExtensionsStrings.SettingsLoadProblem, loadProblem);
            }

            return rules.SaveProblem is { Length: > 0 } saveProblem
                ? Loc.Instance.Format(ExtensionsStrings.SettingsLoadProblem, saveProblem)
                : null;
        }
    }

    public bool HasRulesProblem => RulesProblemText is not null;

    public string? ActionProblem => actionProblem;

    public bool HasActionProblem => actionProblem is not null;

    /// <summary>
    /// Re-reads the registry, the staged drafts and the rules. Runs from the settings view when the section becomes
    /// visible, from the refresh button and after every action; the host calls are asynchronous and the collections are
    /// replaced on the UI thread only.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (disposed)
        {
            return;
        }

        var current = ++generation;
        await dispatcher.InvokeAsync(() => IsRefreshing = true).ConfigureAwait(false);
        try
        {
            var load = rules.LoadAsync(ct);
            if (admin is null)
            {
                await load.ConfigureAwait(false);
                await dispatcher.InvokeAsync(() => Apply(null, null, current)).ConfigureAwait(false);
                return;
            }

            var read = await admin.ReadAsync(ct).ConfigureAwait(false);
            await load.ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => Apply(read, read.Problem, current)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await dispatcher.InvokeAsync(() => Apply(null, exception.Message, current)).ConfigureAwait(false);
        }
        finally
        {
            await dispatcher.InvokeAsync(
                    () =>
                    {
                        if (current == generation)
                        {
                            IsRefreshing = false;
                        }
                    })
                .ConfigureAwait(false);
        }
    }

    /// <summary>Fetches the repository index from <see cref="RepositoryUrl"/> and rebuilds the package rows, each one
    /// compared against the installed version. Runs in the background; the UI only receives the finished snapshot.</summary>
    private async Task BrowseRepositoryAsync(CancellationToken ct)
    {
        if (disposed)
        {
            return;
        }

        var url = RepositoryUrl.Trim();
        await dispatcher.InvokeAsync(
                () =>
                {
                    repositoryProblem = null;
                    IsBrowsingRepository = true;
                    NotifyRepositoryState();
                })
            .ConfigureAwait(false);
        try
        {
            var index = await repository.FetchIndexAsync(url, ct).ConfigureAwait(false);
            var installed = InstalledVersions();
            var rows = new List<RepositoryPackageViewModel>(index.Packages.Count);
            foreach (var package in index.Packages)
            {
                var installedVersion = installed.TryGetValue(package.Id, out var version) ? version : null;
                rows.Add(new RepositoryPackageViewModel(package, installedVersion, InstallPackageAsync));
            }

            await dispatcher.InvokeAsync(
                    () =>
                    {
                        Repository.ReplaceAll(rows);
                        repositoryProblem = null;
                    })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await dispatcher.InvokeAsync(
                    () =>
                    {
                        repositoryProblem = Loc.Instance.Format(ExtensionsStrings.SettingsRepositoryFailed, exception.Message);
                        Repository.Clear();
                    })
                .ConfigureAwait(false);
        }
        finally
        {
            await dispatcher.InvokeAsync(
                    () =>
                    {
                        IsBrowsingRepository = false;
                        NotifyRepositoryState();
                    })
                .ConfigureAwait(false);
        }
    }

    /// <summary>Downloads, verifies and installs one package; progress is reported as a text snapshot on the row and the
    /// host install runs in the background. Cancellation aborts the download and removes its temporary files.</summary>
    private async Task InstallPackageAsync(RepositoryPackageViewModel row, CancellationToken ct)
    {
        if (admin is null)
        {
            return;
        }

        await dispatcher.InvokeAsync(
                () =>
                {
                    row.BeginInstall();
                    actionProblem = null;
                    OnPropertyChanged(nameof(ActionProblem));
                    OnPropertyChanged(nameof(HasActionProblem));
                })
            .ConfigureAwait(false);
        string? sourceDir = null;
        try
        {
            sourceDir = await repository.DownloadAndExtractAsync(
                    row.Package,
                    ExtensionRepositoryService.DefaultInstallTempRoot(),
                    new Progress<RepositoryDownloadProgress>(snapshot => dispatcher.Post(() => row.ReportProgress(snapshot))),
                    ct)
                .ConfigureAwait(false);
            await admin.InstallAsync(sourceDir, ExtensionOrigins.Installed, ct).ConfigureAwait(false);
            sourceDir = null; // the host moved it into place
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetActionProblem(exception.Message);
        }
        finally
        {
            if (sourceDir is not null)
            {
                ExtensionRepositoryService.TryDeleteDirectory(sourceDir);
            }

            await dispatcher.InvokeAsync(row.EndInstall).ConfigureAwait(false);
        }
    }

    /// <summary>Installs a local zip file the user picked: extract with the zip-safety bounds into the install temp root,
    /// then hand the extracted directory to the host. The file itself is never moved.</summary>
    public async Task InstallLocalZipAsync(string zipPath, CancellationToken ct)
    {
        if (admin is null)
        {
            return;
        }

        string? sourceDir = null;
        try
        {
            sourceDir = await repository.ExtractLocalZipAsync(
                    zipPath,
                    ExtensionRepositoryService.DefaultInstallTempRoot(),
                    ct)
                .ConfigureAwait(false);
            await admin.InstallAsync(sourceDir, ExtensionOrigins.Installed, ct).ConfigureAwait(false);
            sourceDir = null;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetActionProblem(exception.Message);
        }
        finally
        {
            if (sourceDir is not null)
            {
                ExtensionRepositoryService.TryDeleteDirectory(sourceDir);
            }
        }
    }

    private Dictionary<string, string> InstalledVersions()
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = snapshot?.Rows ?? [];
        foreach (var row in rows)
        {
            if (!versions.TryGetValue(row.Status.PluginId, out var existing) ||
                ExtensionRepositoryService.CompareVersions(existing, row.Status.Version) == VersionComparison.Older)
            {
                versions[row.Status.PluginId] = row.Status.Version;
            }
        }

        return versions;
    }

    private void NotifyRepositoryState()
    {
        OnPropertyChanged(nameof(RepositoryProblem));
        OnPropertyChanged(nameof(HasRepositoryProblem));
        OnPropertyChanged(nameof(ShowRepositoryEmpty));
        OnPropertyChanged(nameof(RepositoryBrowseText));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        rules.Changed -= OnRulesChanged;
        host.Changed -= OnHostChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        foreach (var row in Repository)
        {
            // The section is going away (the shell is closing): an install still running must not finish into a
            // disposed graph, so its own token is cancelled here.
            row.Dispose();
        }

        repository.Dispose();
    }

    /// <summary>Applies one read on the UI thread; the generation guard drops a result a newer refresh superseded.</summary>
    private void Apply(ExtensionSnapshot? read, string? readProblem, int current)
    {
        if (current != generation || disposed)
        {
            return;
        }

        if (read is not null)
        {
            snapshot = read;
        }

        problem = readProblem;
        RebuildInstalled();
        RebuildPending();
        RebuildRules();
        NotifyComputedState();
    }

    /// <summary>Raises every binding derived from the snapshot, the rules and the last action, in that order.</summary>
    private void NotifyComputedState()
    {
        NotifyAvailability();
        OnPropertyChanged(nameof(ShowRulesEmpty));
        OnPropertyChanged(nameof(RulesProblemText));
        OnPropertyChanged(nameof(HasRulesProblem));
        OnPropertyChanged(nameof(ActionProblem));
        OnPropertyChanged(nameof(HasActionProblem));
    }

    /// <summary>Raises the bindings that decide whether the extension blocks are shown at all.</summary>
    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(UnavailableText));
        OnPropertyChanged(nameof(HasUnavailableText));
        OnPropertyChanged(nameof(ShowExtensions));
        OnPropertyChanged(nameof(ShowInstalledEmpty));
        OnPropertyChanged(nameof(ShowPendingEmpty));
    }

    private void RebuildInstalled()
    {
        var rows = snapshot?.Rows ?? [];
        var items = new List<InstalledExtensionViewModel>(rows.Count);
        foreach (var row in rows
                     .OrderBy(static row => string.Equals(row.Status.Status, "Approved", StringComparison.Ordinal) ? 0 : 1)
                     .ThenBy(static row => row.Status.PluginId, StringComparer.Ordinal))
        {
            items.Add(new InstalledExtensionViewModel(row, OnInstalledActionAsync));
        }

        Installed.ReplaceAll(items);
    }

    private void RebuildPending()
    {
        var staged = snapshot?.Pending ?? [];
        var items = new List<PendingExtensionViewModel>(staged.Count);
        foreach (var draft in staged.OrderBy(static draft => draft.PluginId, StringComparer.Ordinal))
        {
            items.Add(new PendingExtensionViewModel(draft, OpenCardAsync));
        }

        Pending.ReplaceAll(items);
    }

    private void RebuildRules()
    {
        var all = rules.Rules;
        var groups = new List<RememberedRuleGroupViewModel>();
        foreach (var scope in new[] { ApprovalScope.Session, ApprovalScope.Project, ApprovalScope.Global })
        {
            var entries = new List<ApprovalRule>();
            foreach (var rule in all)
            {
                if (rule.Key.Scope == scope)
                {
                    entries.Add(rule);
                }
            }

            if (entries.Count == 0)
            {
                continue;
            }

            entries.Sort(static (left, right) => string.CompareOrdinal(left.Key.ToolName, right.Key.ToolName));
            var rows = new List<RememberedRuleViewModel>(entries.Count);
            foreach (var entry in entries)
            {
                rows.Add(new RememberedRuleViewModel(entry, Revoke));
            }

            groups.Add(new RememberedRuleGroupViewModel(ApprovalText.Scope(scope), rows));
        }

        RuleGroups.ReplaceAll(groups);
        OnPropertyChanged(nameof(ShowRulesEmpty));
        OnPropertyChanged(nameof(RulesProblemText));
        OnPropertyChanged(nameof(HasRulesProblem));
    }

    /// <summary>One "open the approval card" click; a draft the host no longer has is reported instead of opening nothing.</summary>
    private async Task OpenCardAsync(PendingExtensionViewModel row)
    {
        try
        {
            var opened = await openPendingCard(row.Id, CancellationToken.None).ConfigureAwait(false);
            if (!opened)
            {
                await dispatcher
                    .InvokeAsync(
                        () =>
                        {
                            SetActionProblem(Loc.Instance[ExtensionsStrings.SettingsOpenCardFailed]);
                            _ = RefreshAsync(CancellationToken.None);
                        })
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await dispatcher.InvokeAsync(() => SetActionProblem(exception.Message)).ConfigureAwait(false);
        }
    }

    private async Task OnInstalledActionAsync(InstalledExtensionViewModel row, InstalledExtensionAction action)
    {
        if (admin is null)
        {
            return;
        }

        try
        {
            switch (action)
            {
                case InstalledExtensionAction.Enable:
                    await admin.SetEnabledAsync(row.Id, row.Version, enabled: true, CancellationToken.None).ConfigureAwait(false);
                    break;
                case InstalledExtensionAction.Disable:
                    await admin.SetEnabledAsync(row.Id, row.Version, enabled: false, CancellationToken.None).ConfigureAwait(false);
                    break;
                default:
                    await admin.DeleteAsync(row.Id, row.Version, CancellationToken.None).ConfigureAwait(false);
                    break;
            }

            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetActionProblem(exception.Message);
        }
    }

    private void Revoke(ApprovalRule rule)
    {
        if (rules.Revoke(rule.Key))
        {
            RebuildRules();
        }
    }

    private void SetActionProblem(string message)
    {
        actionProblem = Loc.Instance.Format(ExtensionsStrings.SettingsActionFailed, message);
        OnPropertyChanged(nameof(ActionProblem));
        OnPropertyChanged(nameof(HasActionProblem));
    }

    private void OnCommandFailed(Exception exception) => SetActionProblem(exception.Message);

    /// <summary>A rule was added or revoked (here, on the card, or in the agent loop): rebuild the groups on the UI thread.</summary>
    private void OnRulesChanged()
    {
        if (disposed)
        {
            return;
        }

        _ = dispatcher.InvokeAsync(
            () =>
            {
                if (!disposed)
                {
                    RebuildRules();
                }
            });
    }

    private void OnHostChanged()
    {
        if (disposed)
        {
            return;
        }

        _ = dispatcher.InvokeAsync(
            () =>
            {
                if (disposed)
                {
                    return;
                }

                NotifyAvailability();
                if (host.Status == HostStatus.Running)
                {
                    _ = RefreshAsync(CancellationToken.None);
                }
            });
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = dispatcher.InvokeAsync(
            () =>
            {
                if (disposed)
                {
                    return;
                }

                OnPropertyChanged(nameof(InstalledTitle));
                OnPropertyChanged(nameof(PendingTitle));
                OnPropertyChanged(nameof(RulesTitle));
                OnPropertyChanged(nameof(RepositoryTitle));
                OnPropertyChanged(nameof(RefreshText));
                OnPropertyChanged(nameof(RepositoryBrowseText));
                OnPropertyChanged(nameof(UnavailableText));
                RebuildInstalled();
                RebuildPending();
                RebuildRules();
            });
    }
}

/// <summary>What a row of the installed list can ask for.</summary>
internal enum InstalledExtensionAction
{
    Enable,
    Disable,
    Delete,
}

/// <summary>One installed extension version: state, its tool names, and the enable/disable/delete actions.</summary>
internal sealed class InstalledExtensionViewModel : NotifyingObject
{
    private readonly ExtensionRow row;
    private readonly Func<InstalledExtensionViewModel, InstalledExtensionAction, Task> action;
    private bool isConfirmingDelete;

    public InstalledExtensionViewModel(
        ExtensionRow row,
        Func<InstalledExtensionViewModel, InstalledExtensionAction, Task> action)
    {
        this.row = row ?? throw new ArgumentNullException(nameof(row));
        this.action = action ?? throw new ArgumentNullException(nameof(action));

        EnableCommand = new AsyncRelayCommand(_ => RunAsync(InstalledExtensionAction.Enable), () => CanEnable);
        DisableCommand = new AsyncRelayCommand(_ => RunAsync(InstalledExtensionAction.Disable), () => CanDisable);
        DeleteCommand = new RelayCommand(() => IsConfirmingDelete = true, () => CanDelete);
        ConfirmDeleteCommand = new AsyncRelayCommand(_ => RunAsync(InstalledExtensionAction.Delete), () => CanDelete);
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);
    }

    public string Id => row.Status.PluginId;

    public string Version => row.Status.Version;

    public string StatusText => ApprovalText.Status(row.Status.Status);

    /// <summary>Where this extension came from (Installed, Bundled, BuiltIn), shown as a badge.</summary>
    public string OriginText => Loc.Instance[row.Origin switch
    {
        PluginOrigin.Bundled => ExtensionsStrings.SettingsOriginBundled,
        PluginOrigin.BuiltIn => ExtensionsStrings.SettingsOriginBuiltIn,
        _ => ExtensionsStrings.SettingsOriginInstalled,
    }];

    public bool IsBuiltIn => row.Origin == PluginOrigin.BuiltIn;

    public string ToolsText => row.ToolNames.Count == 0
        ? string.Empty
        : Loc.Instance.Format(ExtensionsStrings.SettingsToolsLine, string.Join(", ", row.ToolNames));

    public bool HasTools => row.ToolNames.Count > 0;

    public string? ErrorText => row.Status.Error;

    public bool HasError => !string.IsNullOrWhiteSpace(row.Status.Error);

    /// <summary>Only an approved version runs, so only it can be disabled; a built-in plugin never offers it.</summary>
    public bool CanDisable => string.Equals(row.Status.Status, "Approved", StringComparison.Ordinal) && !IsBuiltIn;

    /// <summary>A disabled version can be re-enabled; the host re-checks the approved hashes when it runs.</summary>
    public bool CanEnable => string.Equals(row.Status.Status, "Disabled", StringComparison.Ordinal);

    /// <summary>An uninstalled version has nothing left to delete, and a built-in plugin is part of the app payload.</summary>
    public bool CanDelete => !string.Equals(row.Status.Status, "Uninstalled", StringComparison.Ordinal) && !IsBuiltIn;

    public bool IsConfirmingDelete
    {
        get => isConfirmingDelete;
        private set
        {
            if (SetAndNotify(ref isConfirmingDelete, value, nameof(ShowDelete)))
            {
                OnPropertyChanged(nameof(ShowConfirmDelete));
            }
        }
    }

    /// <summary>The delete button hides itself once the second confirmation is on screen.</summary>
    public bool ShowDelete => CanDelete && !isConfirmingDelete;

    public bool ShowConfirmDelete => CanDelete && isConfirmingDelete;

    public AsyncRelayCommand EnableCommand { get; }

    public AsyncRelayCommand DisableCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public AsyncRelayCommand ConfirmDeleteCommand { get; }

    public RelayCommand CancelDeleteCommand { get; }

    private async Task RunAsync(InstalledExtensionAction requested)
    {
        IsConfirmingDelete = false;
        await action(this, requested);
    }
}

/// <summary>One staged draft in the settings section; opening it puts the approval card on screen.</summary>
internal sealed class PendingExtensionViewModel : ObservableObject
{
    private readonly StagedExtension staged;

    public PendingExtensionViewModel(
        StagedExtension staged,
        Func<PendingExtensionViewModel, Task> open)
    {
        this.staged = staged ?? throw new ArgumentNullException(nameof(staged));
        ArgumentNullException.ThrowIfNull(open);
        OpenCommand = new AsyncRelayCommand(_ => open(this));
    }

    public string Id => staged.PluginId;

    public string Version => staged.Version;

    public string KindText => ApprovalText.Kind(staged.Kind);

    public bool HasDiagnostics => staged.Diagnostics.Count > 0;

    public string DiagnosticsText => string.Join(Environment.NewLine, staged.Diagnostics);

    public AsyncRelayCommand OpenCommand { get; }
}

/// <summary>One scope's remembered rules.</summary>
internal sealed class RememberedRuleGroupViewModel(string title, IReadOnlyList<RememberedRuleViewModel> rules)
{
    public string Title { get; } = title;

    public IReadOnlyList<RememberedRuleViewModel> Rules { get; } = rules;
}

/// <summary>One remembered allow: the tool, its side effect and the revoke button.</summary>
internal sealed class RememberedRuleViewModel
{
    public RememberedRuleViewModel(ApprovalRule rule, Action<ApprovalRule> revoke)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(revoke);
        Rule = rule;
        RevokeCommand = new RelayCommand(() => revoke(rule));
        var head = Loc.Instance.Format(
            ExtensionsStrings.SettingsRuleLine,
            rule.Key.ToolName,
            ApprovalText.SideEffect(rule.Key.SideEffect));
        Text = rule.Key.Scope == ApprovalScope.Global ? head : head + " · " + ShortScope(rule.Key.ScopeId);
    }

    public ApprovalRule Rule { get; }

    public string Text { get; }

    public RelayCommand RevokeCommand { get; }

    private static string ShortScope(string scopeId) => scopeId.Length <= 8 ? scopeId : scopeId[..8];
}

/// <summary>One package of the browsed repository index, compared against the installed version. The row owns the
/// lifetime of its own install: the install command gets a token only that row can cancel (<see cref="CancelCommand"/>),
/// and disposing the row cancels an install that is still running.</summary>
internal sealed class RepositoryPackageViewModel : ObservableObject, IDisposable
{
    private readonly Func<RepositoryPackageViewModel, CancellationToken, Task> install;
    private CancellationTokenSource? installCancellation;
    private bool isInstalling;
    private long receivedBytes;
    private long? totalBytes;

    public RepositoryPackageViewModel(
        RepositoryPackage package,
        string? installedVersion,
        Func<RepositoryPackageViewModel, CancellationToken, Task> install)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
        InstalledVersion = installedVersion;
        this.install = install ?? throw new ArgumentNullException(nameof(install));
        Comparison = ExtensionRepositoryService.CompareVersions(installedVersion, package.Version);
        InstallCommand = new AsyncRelayCommand(_ => RunInstallAsync(), () => CanInstall && !isInstalling);
        CancelCommand = new RelayCommand(CancelInstall, () => isInstalling);
    }

    public RepositoryPackage Package { get; }

    public string? InstalledVersion { get; }

    public VersionComparison Comparison { get; }

    public string Id => Package.Id;

    public string Title => string.IsNullOrWhiteSpace(Package.Title) ? Package.Id : Package.Title;

    public string Version => Package.Version;

    public string Description => Package.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Package.Description);

    public string KindText => ApprovalText.Kind(Package.Kind);

    public bool IsUpdate => Comparison == VersionComparison.Older;

    public bool IsInstalled => Comparison is VersionComparison.Same or VersionComparison.Newer;

    public string InstalledText => InstalledVersion is null
        ? string.Empty
        : Loc.Instance.Format(ExtensionsStrings.SettingsRepositoryInstalled, InstalledVersion);

    public bool CanInstall => Comparison is VersionComparison.Older or VersionComparison.Incomparable;

    public string InstallText => Loc.Instance[IsUpdate
        ? ExtensionsStrings.SettingsRepositoryUpdate
        : ExtensionsStrings.SettingsRepositoryInstall];

    public bool IsInstalling => isInstalling;

    /// <summary>The download progress line, empty unless a download is running.</summary>
    public string ProgressText
    {
        get
        {
            if (!isInstalling)
            {
                return string.Empty;
            }

            return totalBytes is { } total
                ? Loc.Instance.Format(ExtensionsStrings.SettingsRepositoryProgress, receivedBytes, total)
                : Loc.Instance.Format(ExtensionsStrings.SettingsRepositoryProgressBytes, receivedBytes);
        }
    }

    public AsyncRelayCommand InstallCommand { get; }

    /// <summary>Cancels this row's install (download, extraction and the host call): separate from
    /// <see cref="InstallCommand"/> because <see cref="AsyncRelayCommand"/> has no token of its own to cancel.</summary>
    public RelayCommand CancelCommand { get; }

    public void BeginInstall()
    {
        isInstalling = true;
        receivedBytes = 0;
        totalBytes = null;
        OnPropertyChanged(nameof(IsInstalling));
        OnPropertyChanged(nameof(ProgressText));
        InstallCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void ReportProgress(RepositoryDownloadProgress snapshot)
    {
        if (!isInstalling)
        {
            return;
        }

        receivedBytes = snapshot.BytesReceived;
        totalBytes = snapshot.TotalBytes;
        OnPropertyChanged(nameof(ProgressText));
    }

    public void EndInstall()
    {
        isInstalling = false;
        OnPropertyChanged(nameof(IsInstalling));
        OnPropertyChanged(nameof(ProgressText));
        InstallCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Stops an install that is still running; a no-op when the row is idle or the install already finished.
    /// The cancellation itself is reported through the <see cref="OperationCanceledException"/> the install path already
    /// handles, not as a problem on the row.</summary>
    public void CancelInstall()
    {
        try
        {
            Volatile.Read(ref installCancellation)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The install completed between reading the field and cancelling: there is nothing left to stop.
        }
    }

    public void Dispose() => CancelInstall();

    /// <summary>Runs the install with a token that ties the caller's token to this row's cancel command, and drops the
    /// source when the install is over so a late cancel cannot reach a later install.</summary>
    private async Task RunInstallAsync()
    {
        var cancellation = new CancellationTokenSource();
        Volatile.Write(ref installCancellation, cancellation);
        try
        {
            await install(this, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref installCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }
}
