namespace Forge.App.ViewModels;

/// <summary>
/// The window's resource inspector slot (wave W8 owns this file). <see cref="Inspector"/> is the overlay the window shows:
/// null hides it, an instance is drawn by the template registered for its type in <c>App.axaml</c>.
/// <para>The panel is created when it is opened from a session's resources menu and disposed when it closes, so a closed
/// panel holds no timer, no language subscription and no listener on any session. It lists the live work of <em>every</em>
/// open session, grouped by session with the active one first: a session switch only re-orders the groups (one coalesced
/// refresh), a session that closes drops its rows on the refresh its own close triggers, and a newly opened one is listed
/// from the next pull.</para>
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private ResourceInspectorViewModel? inspector;

    /// <summary>The resource inspector overlay; null while it is closed.</summary>
    public ResourceInspectorViewModel? Inspector
    {
        get => inspector;
        private set => Set(ref inspector, value);
    }

    /// <summary>
    /// Opens the resource inspector. The strip's resources menu carries the row for it; a window that already shows it keeps
    /// the panel it has, so a second click never stacks a second timer.
    /// </summary>
    public RelayCommand OpenInspectorCommand { get; private set; } = null!;

    /// <summary>Called once from the constructor, before the window binds the overlay.</summary>
    private void InitializeInspector()
    {
        // The window-level opener is not bound to a control (the strip's resources menu row reaches
        // <see cref="OpenInspector"/> through the session), but it is the one gesture a test or a future host can drive
        // without a session view.
        OpenInspectorCommand = new RelayCommand(OpenInspector, () => sessions.Count > 0);
        PropertyChanged += OnInspectorActiveSessionChanged;
    }

    /// <summary>A session switch (or a tab opening, which switches to it) re-orders the groups: the active session goes first.</summary>
    private void OnInspectorActiveSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveSession))
        {
            return;
        }

        OpenInspectorCommand.NotifyCanExecuteChanged();
        inspector?.RequestRefresh();
    }

    /// <summary>
    /// Builds the panel over the open sessions and starts its pull. Called by a session's resources menu row through
    /// <see cref="SessionWorkspaceViewModel"/>'s <c>openInspector</c> callback, and by the window-level command.
    /// </summary>
    private void OpenInspector()
    {
        if (inspector is not null || sessions.Count == 0)
        {
            return;
        }

        var panel = new ResourceInspectorViewModel(
            CollectInspectorSessions,
            () => activeSession,
            composition.Preferences,
            CloseInspector);
        Inspector = panel;
        panel.Start();
    }

    private void CloseInspector()
    {
        if (inspector is not { } closing)
        {
            return;
        }

        Inspector = null;
        closing.Dispose();
    }

    /// <summary>
    /// Every open session, live and read-only alike, in the window's own order. A session leaves this dictionary before it is
    /// disposed, so the refresh its close triggers no longer lists it.
    /// </summary>
    private void CollectInspectorSessions(List<IResourceInspectorSource> into)
    {
        foreach (var session in sessions.Values)
        {
            into.Add(session);
        }
    }
}
