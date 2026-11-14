namespace Forge.App.ViewModels;

/// <summary>
/// The window's resource inspector slot (wave W8 owns this file). <see cref="Inspector"/> is the overlay the window shows:
/// null hides it, an instance is drawn by the template registered for its type in <c>App.axaml</c>.
/// <para>The panel is created when it is opened from the active session's resources menu and disposed when it closes, so a
/// closed panel holds no timer and no language subscription. It belongs to one session: a session switch closes it, because
/// it lists that session's running calls and sub-agents and leaving another session's live rows on screen would report the
/// wrong work (the same rule the checks and plan panels follow).</para>
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
    /// the panel it has, so a second click never stacks a second timer on the same session.
    /// </summary>
    public RelayCommand OpenInspectorCommand { get; private set; } = null!;

    /// <summary>Called once from the constructor, before the window binds the overlay.</summary>
    private void InitializeInspector()
    {
        // The window-level opener is not bound to a control (the strip's resources menu row reaches
        // <see cref="OpenInspector"/> through the session), but it is the one gesture a test or a future host can drive
        // without a session view, and it follows the active session exactly like the strip does.
        OpenInspectorCommand = new RelayCommand(OpenInspector, () => activeSession is not null);
        PropertyChanged += OnInspectorActiveSessionChanged;
    }

    /// <summary>
    /// A session switch closes the panel: it lists one session's live work, and the window shows one session at a time. The
    /// command follows the same fact - without an active session there is nothing to list.
    /// </summary>
    private void OnInspectorActiveSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveSession))
        {
            return;
        }

        OpenInspectorCommand.NotifyCanExecuteChanged();
        CloseInspector();
    }

    /// <summary>
    /// Builds the panel for the active session and starts its pull. Called by the session's resources menu row through
    /// <see cref="SessionWorkspaceViewModel"/>'s <c>openInspector</c> callback, and by the window-level command.
    /// </summary>
    private void OpenInspector()
    {
        if (inspector is not null || activeSession is null)
        {
            return;
        }

        var panel = new ResourceInspectorViewModel(DescribeInspectorSession, composition.Preferences, CloseInspector);
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

    /// <summary>The active session as the panel reads it. Asked once per pull, so a switched or closed session is felt there.</summary>
    private ResourceInspectorSession? DescribeInspectorSession() =>
        activeSession is { } session
            ? new ResourceInspectorSession(session.Title, session.RunningTools, session.SubAgents)
            : null;
}