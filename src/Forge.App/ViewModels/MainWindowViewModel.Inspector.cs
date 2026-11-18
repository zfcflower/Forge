namespace Forge.App.ViewModels;

/// <summary>
/// The window's resource window slot. <see cref="Inspector"/> is the overlay the window shows: null hides it, an instance is
/// drawn by the template registered for its type in <c>App.axaml</c>.
/// <para>The window shows the one session whose composer button opened it (its automation agents, its tools and prompt
/// snapshot) and the app-wide MCP servers. It is created when the button is pressed and disposed when it closes, so a closed
/// window holds no language subscription and no listener on any session or on the MCP manager. A session switch or a closing
/// session closes it: what it shows belongs to the session it was opened for.</para>
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private ResourceInspectorViewModel? inspector;

    /// <summary>The resource window overlay; null while it is closed.</summary>
    public ResourceInspectorViewModel? Inspector
    {
        get => inspector;
        private set => Set(ref inspector, value);
    }

    /// <summary>Called once from the constructor, before the window binds the overlay.</summary>
    private void InitializeInspector() => PropertyChanged += OnInspectorActiveSessionChanged;

    /// <summary>Another tab is active now: the window described the previous session, so it goes.</summary>
    private void OnInspectorActiveSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActiveSession))
        {
            CloseInspector();
        }
    }

    /// <summary>
    /// Opens the resource window for the active session. Called by the session's resource button through its
    /// <c>openInspector</c> callback; a window that is already open keeps the one it has.
    /// </summary>
    private void OpenInspector()
    {
        if (inspector is not null || activeSession is not { } session)
        {
            return;
        }

        var panel = new ResourceInspectorViewModel(session, composition.Mcp, CloseInspector, HotReloadPrompts);
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
}
