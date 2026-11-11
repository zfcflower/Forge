using Forge.Abstractions;
using Forge.App.Localization;

namespace Forge.App.Automations;

/// <summary>
/// The live view of one root session as an automation sees it (implemented by the session view model). Every member is
/// used on the UI thread.
/// </summary>
internal interface IAutomationSession
{
    string SessionId { get; }

    string Title { get; }

    /// <summary>A turn is running.</summary>
    bool IsRunning { get; }

    /// <summary>Last agent event of the running turn (or the turn start); the session watch measures "no progress" from it.</summary>
    DateTimeOffset LastActivityAt { get; }

    /// <summary>Idle in the lifecycle sense (no turn, no running or queued sub-agent, no queued message, no handover).</summary>
    bool IsAutomationIdle { get; }

    /// <summary>Taken over, archived, removed or closed: an automation must not deliver here any more.</summary>
    bool IsAutomationClosed { get; }

    /// <summary>Runs <paramref name="text"/> as a turn; completes when the turn ends.</summary>
    Task<MailboxResult> RunAutomationTurnAsync(string text, LocText origin);

    /// <summary>The agent the next turns run with: null = the configured main agent (see <c>SessionRuntime.SetRootDefinition</c>).</summary>
    void UseAutomationAgent(AgentDefinition? definition);

    /// <summary>Starts a sub-agent under the session's root; the returned task completes off the UI thread.</summary>
    Task<ChildAgentResult> LaunchChildAsync(string kind, string task, CancellationToken ct);
}

/// <summary>What automations need from the window (implemented by the main window view model, used on the UI thread).</summary>
internal interface IAutomationHost
{
    /// <summary>The session's live view when it is already open; never opens anything.</summary>
    IAutomationSession? FindOpen(string sessionId);

    /// <summary>
    /// Opens the session's live view without taking the focus from the session the user is in. Null when it is not an
    /// active root session (archived, removed, a sub-agent, unknown).
    /// </summary>
    Task<IAutomationSession?> OpenAsync(string sessionId, CancellationToken ct);

    /// <summary>Live views of every loaded root session.</summary>
    IReadOnlyList<IAutomationSession> OpenSessions { get; }

    /// <summary>The main window has the focus (a finished turn is worth a notification only when it has not).</summary>
    bool IsWindowActive { get; }

    /// <summary>Re-reads the project tree after an automation created or archived sessions.</summary>
    Task RefreshTreesAsync();
}

/// <summary>Runs work on the UI thread; the product uses the Avalonia dispatcher, tests run it inline.</summary>
internal interface IUiThread
{
    Task<T> InvokeAsync<T>(Func<Task<T>> work);

    void Post(Action action);
}

/// <summary>The Avalonia dispatcher.</summary>
internal sealed class DispatcherUiThread : IUiThread
{
    public static DispatcherUiThread Instance { get; } = new();

    public Task<T> InvokeAsync<T>(Func<Task<T>> work) =>
        Avalonia.Threading.Dispatcher.UIThread.CheckAccess()
            ? work()
            : Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(work);

    public void Post(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);
}
