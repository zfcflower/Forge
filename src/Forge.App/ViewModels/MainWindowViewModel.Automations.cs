using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Forge.Abstractions;
using Forge.App.Automations;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;

namespace Forge.App.ViewModels;

/// <summary>
/// The window side of automations: the <see cref="IAutomationHost"/> the engine's runners reach sessions through, the
/// title-bar entry (running count) and the per-session banner.
/// </summary>
internal sealed partial class MainWindowViewModel : IAutomationHost
{
    private RelayCommand? openAutomationsCommand;

    private bool automationsAttached;

    /// <summary>Running and waiting automation runs (the title-bar badge; hidden at zero).</summary>
    public int AutomationsRunning => composition.Automations?.ActiveCount ?? 0;

    public bool HasRunningAutomations => AutomationsRunning > 0;

    public string AutomationsRunningText => AutomationsRunning.ToString(CultureInfo.CurrentCulture);

    public string AutomationsTooltip => AutomationStrings.TitleBarTooltip(AutomationsRunningText);

    public RelayCommand OpenAutomationsCommand =>
        openAutomationsCommand ??= new RelayCommand(() => OpenSettingsSection(SettingsSectionIds.Automations));

    IAutomationSession? IAutomationHost.FindOpen(string sessionId) =>
        sessions.TryGetValue(sessionId, out var session) && !session.IsReadOnlyView ? session : null;

    async Task<IAutomationSession?> IAutomationHost.OpenAsync(string sessionId, CancellationToken ct)
    {
        if (disposed || closing)
        {
            return null;
        }

        var store = composition.Sessions;
        var info = await BackgroundRead.RunAsync(token => store.GetSessionAsync(sessionId, token), ct).ConfigureAwait(true);
        if (info is null || info.ParentSessionId is not null || info.Status != SessionStatus.Active)
        {
            return null;
        }

        // Opening activates the new tab; an automation must not take the user away from the session they are in.
        var previous = ActiveSession;
        await OpenSessionAsync(info, ct).ConfigureAwait(true);
        if (previous is not null && !ReferenceEquals(previous, ActiveSession) && sessions.ContainsKey(previous.SessionId))
        {
            ShowSessionTab(previous);
            ActiveSession = previous;
        }

        return sessions.TryGetValue(sessionId, out var opened) && !opened.IsReadOnlyView ? opened : null;
    }

    IReadOnlyList<IAutomationSession> IAutomationHost.OpenSessions
    {
        get
        {
            var list = new List<IAutomationSession>(sessions.Count);
            foreach (var session in sessions.Values)
            {
                if (!session.IsReadOnlyView)
                {
                    list.Add(session);
                }
            }

            return list;
        }
    }

    bool IAutomationHost.IsWindowActive =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window } &&
        window.IsActive;

    Task IAutomationHost.RefreshTreesAsync() => RefreshTreesAfterLifecycleAsync();

    /// <summary>Connects the window to the automation service once the sessions are restored (the engine starts here).</summary>
    private void AttachAutomations()
    {
        if (automationsAttached || composition.Automations is not { } automations)
        {
            return;
        }

        automationsAttached = true;
        composition.AutomationUi.OpenSession = id => _ = OpenAutomationSessionAsync(id);
        composition.AutomationUi.Confirm = ConfirmAutomationDeleteAsync;
        automations.Changed += OnAutomationsChanged;
        automations.Attach(this);
        OnAutomationsChanged();
    }

    private void DetachAutomations()
    {
        if (!automationsAttached || composition.Automations is not { } automations)
        {
            return;
        }

        automations.Changed -= OnAutomationsChanged;
        composition.AutomationUi.OpenSession = null;
        composition.AutomationUi.Confirm = null;
    }

    /// <summary>Hooks one live session to the service (quota failures, user turns, background completions).</summary>
    private void HookAutomations(SessionWorkspaceViewModel session)
    {
        session.TurnStarted += OnSessionTurnStarted;
        session.QuotaExhausted += OnSessionQuotaExhausted;
        UpdateAutomationBanner(session);
    }

    private void UnhookAutomations(SessionWorkspaceViewModel session)
    {
        session.TurnStarted -= OnSessionTurnStarted;
        session.QuotaExhausted -= OnSessionQuotaExhausted;
    }

    private void OnSessionTurnStarted(SessionWorkspaceViewModel session, bool automationTurn) =>
        composition.Automations?.OnTurnStarted(session, automationTurn);

    private void OnSessionQuotaExhausted(SessionWorkspaceViewModel session, ProviderQuotaExhausted quota) =>
        composition.Automations?.OnQuotaExhausted(session, quota);

    private void NotifyAutomationsTurnCompleted(SessionWorkspaceViewModel session)
    {
        if (!session.IsReadOnlyView)
        {
            composition.Automations?.OnTurnCompleted(session, session.IsAutomationTurn);
        }
    }

    private void OnAutomationsChanged()
    {
        OnPropertyChanged(nameof(AutomationsRunning));
        OnPropertyChanged(nameof(HasRunningAutomations));
        OnPropertyChanged(nameof(AutomationsRunningText));
        OnPropertyChanged(nameof(AutomationsTooltip));
        foreach (var session in sessions.Values)
        {
            UpdateAutomationBanner(session);
        }
    }

    private void UpdateAutomationBanner(SessionWorkspaceViewModel session)
    {
        if (composition.Automations is not { } automations ||
            session.IsReadOnlyView ||
            automations.ActiveRunFor(session.SessionId) is not { } run)
        {
            session.SetAutomationBanner(null, null);
            return;
        }

        var name = AutomationStrings.DisplayName(run.AutomationId, run.AutomationName);
        var text = run.Status switch
        {
            AutomationRunStatus.WaitingQuota when run.ResumeAt is { } at => AutomationStrings.BannerWaitingQuota(TextFormat.ShortStamp(at)),
            AutomationRunStatus.WaitingQuota => AutomationStrings.BannerWaitingQuotaProbe,
            AutomationRunStatus.WaitingIdle => AutomationStrings.BannerWaitingIdle(name),
            _ => AutomationStrings.BannerRunning(name),
        };
        var runId = run.Id;
        session.SetAutomationBanner(text, () => automations.Cancel(runId));
    }

    private async Task OpenAutomationSessionAsync(string sessionId)
    {
        try
        {
            await OpenSessionByIdAsync(sessionId, CancellationToken.None).ConfigureAwait(true);
            ShowMainWindow();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("[automation] open session failed: " + exception.Message);
        }
    }

    private Task<bool> ConfirmAutomationDeleteAsync(string name) =>
        dialogs is null
            ? Task.FromResult(true)
            : dialogs.ConfirmAsync(
                Loc.Instance[AutomationSettingsStrings.SectionTitleKey],
                Loc.Instance.Format(AutomationSettingsStrings.DeleteConfirmKey, name),
                Loc.Instance[AutomationSettingsStrings.DeleteKey],
                Loc.Instance[ShellStrings.CancelKey]);

    private static void ShowMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            window.Activate();
        }
    }
}
