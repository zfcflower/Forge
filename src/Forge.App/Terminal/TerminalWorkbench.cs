using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Workbench;

namespace Forge.App.Terminal;

/// <summary>
/// The terminals of the workbench: one list per session, one tab per terminal, and the close hook that may veto a close
/// while a program is running. Terminals belong to their session, so switching sessions only moves the docked tabs out of
/// the bottom group (the processes keep running) and switching back docks them again; archiving a session or shutting the
/// app down ends them.
/// <para>UI-thread only, except for <see cref="DisposeAsync"/>, which is awaited on a bounded budget while the shell
/// shuts down. The coordinator never parses output: that lives in <see cref="TerminalViewModel"/>'s pump.</para>
/// </summary>
internal sealed class TerminalWorkbench : IAsyncDisposable
{
    /// <summary>How long the shell waits for the host to stop every terminal on shutdown.</summary>
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// How long the close hook waits for the host's foreground-process probe. A probe that runs out of time is treated
    /// like a busy terminal: the banner asks the user instead of ending a program nobody could see.
    /// </summary>
    private static readonly TimeSpan ForegroundProbeBudget = TimeSpan.FromSeconds(2);

    private readonly WorkbenchLayout layout;
    private readonly AppPreferences preferences;
    private readonly ITerminalHost? host;
    private readonly Action revealBottomGroup;
    private readonly Dictionary<string, List<TerminalViewModel>> bySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TerminalViewModel> byId = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    private string? activeSessionId;
    private string? activeWorkDir;
    private int nextIndex;
    private int disposed;

    /// <summary>
    /// <paramref name="revealBottomGroup"/> restores the bottom group before a new tab is docked into it. It is the same
    /// door the tree's sub-agent rows use, not a second write to the layout: the panel only ever follows the active
    /// session's own state.
    /// </summary>
    public TerminalWorkbench(WorkbenchLayout layout, AppPreferences preferences, ITerminalHost? host, Action revealBottomGroup)
    {
        ArgumentNullException.ThrowIfNull(revealBottomGroup);
        this.layout = layout;
        this.preferences = preferences;
        this.host = host;
        this.revealBottomGroup = revealBottomGroup;
        layout.TabCloseRequested = RequestCloseAsync;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Opens a terminal in the active session: the working directory is the session worktree, the shell and its arguments
    /// come from the preferences. Returns null when there is no active session (the caller reports it in the status bar).
    /// </summary>
    public TerminalViewModel? OpenTerminal()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return null;
        }

        var sessionId = activeSessionId;
        var workDir = activeWorkDir;
        if (sessionId is null || string.IsNullOrEmpty(workDir))
        {
            return null;
        }

        var shell = (preferences.TerminalShell ?? string.Empty).Trim();
        var request = new TerminalStartRequest(
            sessionId,
            workDir,
            new TerminalSize(TerminalViewModel.InitialColumns, TerminalViewModel.InitialRows),
            shell.Length == 0 ? null : shell,
            TerminalCommandLine.Split(preferences.TerminalArguments));

        var index = ++nextIndex;
        var terminal = new TerminalViewModel(sessionId, index, host, request, preferences);
        terminal.TitleChanged += OnTerminalTitleChanged;
        terminal.ConfirmCloseRequested = () => ForceClose(terminal);

        lock (gate)
        {
            byId[terminal.Id] = terminal;
            if (!bySession.TryGetValue(sessionId, out var list))
            {
                list = [];
                bySession.Add(sessionId, list);
            }

            list.Add(terminal);
        }

        // A minimized bottom group would only show the new tab as a title-bar chip; opening a terminal restores its body.
        revealBottomGroup();
        Dock(terminal, activate: true);
        terminal.Start();
        return terminal;
    }

    /// <summary>
    /// Follows the active session: docked terminals of other sessions leave the bottom group without ending their process,
    /// floating ones stay in their window, and the terminals of the session that became active are docked again.
    /// </summary>
    public void OnActiveSessionChanged(string? sessionId, string? workDir)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        activeSessionId = sessionId;
        activeWorkDir = workDir;

        // A terminal tab may have been dragged out of the bottom group, so the lookup goes through the layout by id
        // instead of scanning one group; CloseTab removes the tab from whichever group holds it.
        TerminalViewModel[] known;
        lock (gate)
        {
            known = [.. byId.Values];
        }

        for (var i = 0; i < known.Length; i++)
        {
            var terminal = known[i];
            if (string.Equals(terminal.SessionId, sessionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (layout.FindTab(WorkbenchIds.TerminalTab(terminal.Id)) is { IsFloating: false } tab)
            {
                // Closing the tab only removes it from the group: the terminal object and its process stay alive.
                layout.CloseTab(tab);
            }
        }

        if (sessionId is null)
        {
            return;
        }

        List<TerminalViewModel> mine;
        lock (gate)
        {
            if (!bySession.TryGetValue(sessionId, out var list))
            {
                return;
            }

            mine = [.. list];
        }

        for (var i = 0; i < mine.Count; i++)
        {
            if (layout.FindTab(WorkbenchIds.TerminalTab(mine[i].Id)) is null)
            {
                Dock(mine[i], activate: false);
            }
        }
    }

    /// <summary>Ends every terminal of an archived session and closes its tabs, floating ones included.</summary>
    public void OnSessionArchived(string sessionId)
    {
        List<TerminalViewModel> doomed;
        lock (gate)
        {
            if (!bySession.TryGetValue(sessionId, out var list))
            {
                return;
            }

            doomed = [.. list];
        }

        for (var i = 0; i < doomed.Count; i++)
        {
            ForceClose(doomed[i]);
        }
    }

    /// <summary>
    /// Ends every terminal in parallel and waits at most <see cref="ShutdownBudget"/>: the extension host is stopped right
    /// after this, so the budget only decides how much of the graceful <c>terminal.stop</c> is seen.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        layout.TabCloseRequested = null;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;

        List<TerminalViewModel> all;
        lock (gate)
        {
            all = [.. byId.Values];
            byId.Clear();
            bySession.Clear();
        }

        if (all.Count == 0)
        {
            return;
        }

        var tasks = new List<Task>(all.Count);
        for (var i = 0; i < all.Count; i++)
        {
            CloseTabOf(all[i]);
            tasks.Add(EndTerminalAsync(all[i]));
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(ShutdownBudget).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            Trace.WriteLine("[terminal] shutdown ran out of time; the extension host stops the rest");
        }
    }

    /// <summary>
    /// The layout's close hook: a terminal whose shell still has a foreground child is not closed. The tab is activated
    /// and shows the banner instead, so the user decides between ending the program and keeping it.
    /// </summary>
    private async Task<bool> RequestCloseAsync(TabModel tab)
    {
        if (tab.Kind != TabKind.Terminal || tab.Content is not TerminalViewModel terminal)
        {
            return true;
        }

        if (layout.FindTab(WorkbenchIds.TerminalTab(terminal.Id)) is null)
        {
            // Already gone (a second close request while the first one was waiting): nothing to veto.
            return false;
        }

        var busy = false;
        using (var timeout = new CancellationTokenSource(ForegroundProbeBudget))
        {
            try
            {
                busy = await terminal.HasForegroundProcessAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // No answer in time: the safe side is "a program may be running", which shows the banner.
                Trace.WriteLine("[terminal] the foreground probe timed out; asking the user before closing");
                busy = true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.WriteLine("[terminal] foreground probe failed: " + exception.Message);
            }
        }

        if (!busy)
        {
            await EndTerminalAsync(terminal);
            return true;
        }

        if (!tab.IsFloating)
        {
            layout.ActivateTab(tab);
        }

        terminal.ShowClosePrompt();
        return false;
    }

    private void Dock(TerminalViewModel terminal, bool activate) =>
        layout.OpenTab(
            layout.BottomTabs,
            WorkbenchIds.TerminalTab(terminal.Id),
            TabKind.Terminal,
            terminal,
            terminal.TabTitle,
            activate);

    private void ForceClose(TerminalViewModel terminal)
    {
        CloseTabOf(terminal);
        _ = EndTerminalAsync(terminal);
    }

    private void CloseTabOf(TerminalViewModel terminal)
    {
        if (layout.FindTab(WorkbenchIds.TerminalTab(terminal.Id)) is { } tab)
        {
            layout.CloseTab(tab);
        }
    }

    private async Task EndTerminalAsync(TerminalViewModel terminal)
    {
        lock (gate)
        {
            byId.Remove(terminal.Id);
            if (bySession.TryGetValue(terminal.SessionId, out var list))
            {
                list.Remove(terminal);
                if (list.Count == 0)
                {
                    bySession.Remove(terminal.SessionId);
                }
            }
        }

        terminal.TitleChanged -= OnTerminalTitleChanged;
        try
        {
            await terminal.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.WriteLine("[terminal] the terminal did not shut down cleanly: " + exception.Message);
        }
    }

    private void OnTerminalTitleChanged(object? sender, EventArgs e)
    {
        if (sender is TerminalViewModel terminal &&
            layout.FindTab(WorkbenchIds.TerminalTab(terminal.Id)) is { } tab)
        {
            tab.Title = terminal.TabTitle;
        }
    }

    /// <summary>A terminal without an OSC title shows "Terminal n", which has to follow a language switch.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        TerminalViewModel[] known;
        lock (gate)
        {
            known = [.. byId.Values];
        }

        for (var i = 0; i < known.Length; i++)
        {
            if (layout.FindTab(WorkbenchIds.TerminalTab(known[i].Id)) is { } tab)
            {
                tab.Title = known[i].TabTitle;
            }
        }
    }
}
