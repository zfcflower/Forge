using Forge.App.Localization;
using Forge.App.Notifications;

namespace Forge.App.ViewModels;

/// <summary>
/// The title-bar "reload prompts" button of the active session. A session's agent host reads templates, agents, skills and
/// <c>AGENTS.md</c> once and keeps that snapshot (the cached prompt prefix must not move under a running conversation), so an
/// edit on disk reaches a session only through this button: a hot reload (next message runs on a fresh host; the prompt cache is
/// lost once) or a new session in the same place (the old one keeps its cache). The button wears a dot when a watched file
/// changed after the snapshot; the toast that announces the change is raised once by the session itself.
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private SessionWorkspaceViewModel? promptSession;

    /// <summary>Runs the hot reload of the active session; the choice is made in the flyout.</summary>
    public RelayCommand HotReloadPromptsCommand { get; private set; } = null!;

    /// <summary>Starts a new session in the place the active one lives (same project, worktree and branch; general stays general).</summary>
    public AsyncRelayCommand NewSessionForPromptsCommand { get; private set; } = null!;

    /// <summary>True when the active session's prompt files changed after its snapshot; drives the dot on the button.</summary>
    public bool HasPromptsStale => activeSession?.PromptsStale == true;

    /// <summary>Whether the button does anything: a live active session.</summary>
    public bool CanReloadPrompts => activeSession?.CanReloadPrompts == true;

    private void InitializePrompts()
    {
        HotReloadPromptsCommand = new RelayCommand(HotReloadPrompts, () => CanReloadPrompts);
        NewSessionForPromptsCommand = new AsyncRelayCommand(
            NewSessionForPromptsAsync,
            () => CanReloadPrompts,
            exception => composition.Notifications.Notify(
                NotificationLevel.Error,
                SessionStrings.PromptsNewSessionTitle,
                LocText.Literal(exception.Message)));
        PropertyChanged += OnPromptsActiveSessionChanged;
    }

    private void OnPromptsActiveSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveSession))
        {
            return;
        }

        if (promptSession is not null)
        {
            promptSession.PromptsStaleChanged -= OnPromptsStaleChanged;
        }

        promptSession = activeSession;
        if (promptSession is not null)
        {
            promptSession.PromptsStaleChanged += OnPromptsStaleChanged;
        }

        OnPromptsStaleChanged();
    }

    private void OnPromptsStaleChanged()
    {
        OnPropertyChanged(nameof(HasPromptsStale));
        OnPropertyChanged(nameof(CanReloadPrompts));
        HotReloadPromptsCommand.NotifyCanExecuteChanged();
        NewSessionForPromptsCommand.NotifyCanExecuteChanged();
    }

    private void HotReloadPrompts()
    {
        if (activeSession is not { CanReloadPrompts: true } session)
        {
            return;
        }

        session.HotReloadPrompts();
        composition.Notifications.Notify(
            NotificationLevel.Success,
            SessionStrings.PromptsHotReloadTitle,
            SessionStrings.PromptsReloaded,
            "prompts-reloaded:" + session.SessionId);
    }

    private async Task NewSessionForPromptsAsync(CancellationToken ct)
    {
        if (activeSession is not { CanReloadPrompts: true } session)
        {
            return;
        }

        await Projects.NewSessionLikeAsync(session.Info, ct).ConfigureAwait(true);
    }
}
