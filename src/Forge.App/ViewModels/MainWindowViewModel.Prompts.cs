using Forge.App.Localization;
using Forge.App.Notifications;

namespace Forge.App.ViewModels;

/// <summary>
/// The title-bar "reload prompts" button of the active session. A session's agent host reads templates, agents, skills and
/// <c>AGENTS.md</c> once and keeps that snapshot (the cached prompt prefix must not move under a running conversation), so an
/// edit on disk reaches a running session only through this button: one click is a hot reload (the next message runs on a fresh
/// host; the prompt cache is lost once). There is no choice dialog: the toast after the click and the toast that announces a
/// change both say what it costs and suggest opening a new session by hand; the decision stays with the user. The button wears a
/// dot when a watched file changed after the snapshot.
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private SessionWorkspaceViewModel? promptSession;

    /// <summary>Runs the hot reload of the active session; one click, no choice dialog.</summary>
    public RelayCommand HotReloadPromptsCommand { get; private set; } = null!;

    /// <summary>True when the active session's prompt files changed after its snapshot; drives the dot on the button.</summary>
    public bool HasPromptsStale => activeSession?.PromptsStale == true;

    /// <summary>Whether the button does anything: a live active session.</summary>
    public bool CanReloadPrompts => activeSession?.CanReloadPrompts == true;

    private void InitializePrompts()
    {
        HotReloadPromptsCommand = new RelayCommand(HotReloadPrompts, () => CanReloadPrompts);
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
            SessionStrings.PromptsReloadedTitle,
            SessionStrings.PromptsReloaded,
            "prompts-reloaded:" + session.SessionId);
    }
}
