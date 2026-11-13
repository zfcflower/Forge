using Forge.App.Localization;
using Forge.App.ViewModels;
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
    private ExtensionSnapshot? snapshot;
    private string? problem;
    private string? actionProblem;
    private bool isRefreshing;
    private bool disposed;
    private int generation;

    public ExtensionsSettingsSectionViewModel(
        IExtensionsAdmin? admin,
        IExtensionHostState host,
        ApprovalRules rules,
        Func<string, CancellationToken, Task<bool>> openPendingCard,
        IApprovalDispatcher dispatcher)
    {
        this.admin = admin;
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.openPendingCard = openPendingCard ?? throw new ArgumentNullException(nameof(openPendingCard));
        this.dispatcher = dispatcher ?? UiThreadDispatcher.Instance;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !isRefreshing, OnCommandFailed);

        rules.Changed += OnRulesChanged;
        host.Changed += OnHostChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;

        RebuildRules();
    }

    public RangeObservableCollection<InstalledExtensionViewModel> Installed { get; } = [];

    public RangeObservableCollection<PendingExtensionViewModel> Pending { get; } = [];

    public RangeObservableCollection<RememberedRuleGroupViewModel> RuleGroups { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public string InstalledTitle => Loc.Instance[ExtensionsStrings.SettingsInstalled];

    public string PendingTitle => Loc.Instance[ExtensionsStrings.SettingsPending];

    public string RulesTitle => Loc.Instance[ExtensionsStrings.SettingsRules];

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
                OnPropertyChanged(nameof(RefreshText));
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

    public string ToolsText => row.ToolNames.Count == 0
        ? string.Empty
        : Loc.Instance.Format(ExtensionsStrings.SettingsToolsLine, string.Join(", ", row.ToolNames));

    public bool HasTools => row.ToolNames.Count > 0;

    public string? ErrorText => row.Status.Error;

    public bool HasError => !string.IsNullOrWhiteSpace(row.Status.Error);

    /// <summary>Only an approved version runs, so only it can be disabled.</summary>
    public bool CanDisable => string.Equals(row.Status.Status, "Approved", StringComparison.Ordinal);

    /// <summary>A disabled version can be re-enabled; the host re-checks the approved hashes when it runs.</summary>
    public bool CanEnable => string.Equals(row.Status.Status, "Disabled", StringComparison.Ordinal);

    /// <summary>An uninstalled version has nothing left to delete.</summary>
    public bool CanDelete => !string.Equals(row.Status.Status, "Uninstalled", StringComparison.Ordinal);

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
