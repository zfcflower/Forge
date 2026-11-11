using System.Collections.Concurrent;
using Forge.Abstractions;
using Forge.App.Automations;
using Forge.App.Localization;

namespace Forge.App.Tests.Automations;

/// <summary>Runs UI-thread work inline, like the production tests' dispatcher stand-in.</summary>
internal sealed class InlineUi : IUiThread
{
    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => work();

    public void Post(Action action) => action();
}

/// <summary>Collects reports synchronously (unlike <see cref="Progress{T}"/>, which posts to a sync context).</summary>
internal sealed class RecordingProgress : IProgress<bool>
{
    public List<bool> Values { get; } = [];

    public void Report(bool value)
    {
        lock (Values)
        {
            Values.Add(value);
        }
    }
}

internal sealed class RunnerSession(string id, string title = "Session") : IAutomationSession
{
    public string SessionId { get; } = id;

    public string Title { get; set; } = title;

    public bool IsRunning { get; set; }

    public DateTimeOffset LastActivityAt { get; set; }

    public bool IsAutomationIdle { get; set; } = true;

    public bool IsAutomationClosed { get; set; }

    public MailboxResult TurnResult { get; set; } = new(MailboxStatus.Completed);

    public List<(string Text, string Origin)> Turns { get; } = [];

    public List<AgentDefinition?> Agents { get; } = [];

    public ChildAgentResult ChildResult { get; set; } = new("child", string.Empty, false, new TokenUsage(0, 0));

    public List<(string Kind, string Task)> Children { get; } = [];

    public Task<MailboxResult> RunAutomationTurnAsync(string text, LocText origin)
    {
        Turns.Add((text, origin.Resolve()));
        return Task.FromResult(TurnResult);
    }

    public void UseAutomationAgent(AgentDefinition? definition) => Agents.Add(definition);

    public Task<ChildAgentResult> LaunchChildAsync(string kind, string task, CancellationToken ct)
    {
        Children.Add((kind, task));
        return Task.FromResult(ChildResult);
    }
}

internal sealed class RunnerHost : IAutomationHost
{
    private readonly Dictionary<string, IAutomationSession> open = new(StringComparer.Ordinal);

    /// <summary>Sessions <see cref="OpenAsync"/> can open (it moves them to the open set).</summary>
    public Dictionary<string, IAutomationSession> Openable { get; } = new(StringComparer.Ordinal);

    public bool IsWindowActive { get; set; }

    public int Refreshes { get; private set; }

    public int Opens { get; private set; }

    /// <summary>Runs inside <see cref="OpenAsync"/>, e.g. to cancel a token at the moment the session is opened.</summary>
    public Action? OnOpen { get; set; }

    public IReadOnlyList<IAutomationSession> OpenSessions => [.. open.Values];

    public void Add(IAutomationSession session) => open[session.SessionId] = session;

    public void Remove(string sessionId) => open.Remove(sessionId);

    public IAutomationSession? FindOpen(string sessionId) => open.GetValueOrDefault(sessionId);

    public Task<IAutomationSession?> OpenAsync(string sessionId, CancellationToken ct)
    {
        Opens++;
        OnOpen?.Invoke();
        if (Openable.TryGetValue(sessionId, out var session))
        {
            open[sessionId] = session;
            return Task.FromResult<IAutomationSession?>(session);
        }

        return Task.FromResult<IAutomationSession?>(null);
    }

    public Task RefreshTreesAsync()
    {
        Refreshes++;
        return Task.CompletedTask;
    }
}

/// <summary>A context with a settable definition; applies updates like the engine does.</summary>
internal sealed class RunnerContext : IAutomationRunContext
{
    private readonly object gate = new();
    private readonly List<AutomationRun> history = [];

    public RunnerContext(AutomationRun run, AutomationDefinition definition)
    {
        Run = run;
        Definition = definition;
    }

    public AutomationRun Run { get; private set; }

    public AutomationDefinition Definition { get; }

    public bool IsResumed => false;

    public Exception? ThrowOnUpdate { get; set; }

    public IReadOnlyList<AutomationRun> History
    {
        get
        {
            lock (gate)
            {
                return [.. history];
            }
        }
    }

    public IReadOnlyList<AutomationRunStatus> Statuses => [.. History.Select(r => r.Status)];

    public Task UpdateAsync(Func<AutomationRun, AutomationRun> change, CancellationToken ct)
    {
        if (ThrowOnUpdate is not null)
        {
            throw ThrowOnUpdate;
        }

        lock (gate)
        {
            Run = change(Run);
            history.Add(Run);
        }

        return Task.CompletedTask;
    }

    public static AutomationRun NewRun(string automationId, DateTimeOffset startedAt, string occurrence = "occ-1") =>
        new("run-1", automationId, automationId, AutomationTriggerSource.Event, occurrence, AutomationRunStatus.Running, startedAt);
}

/// <summary>Key/value state plus a recorded trim; the rest of the store is not used by these runners.</summary>
internal sealed class RunnerRunStore : IAutomationRunStore
{
    private readonly ConcurrentDictionary<string, string> values = new();

    public int TrimResult { get; set; }

    public List<(int PerAutomation, int Total)> Trims { get; } = [];

    public string? Peek(string key) => values.GetValueOrDefault(key);

    public Task<string?> GetStateAsync(string key, CancellationToken ct) =>
        Task.FromResult<string?>(values.GetValueOrDefault(key));

    public Task SetStateAsync(string key, string? value, CancellationToken ct)
    {
        if (value is null)
        {
            values.TryRemove(key, out _);
        }
        else
        {
            values[key] = value;
        }

        return Task.CompletedTask;
    }

    public Task<int> TrimAsync(int perAutomation, int total, CancellationToken ct)
    {
        Trims.Add((perAutomation, total));
        return Task.FromResult(TrimResult);
    }

    public Task<bool> TryInsertAsync(AutomationRun run, CancellationToken ct) => throw new NotSupportedException();

    public Task UpdateAsync(AutomationRun run, CancellationToken ct) => throw new NotSupportedException();

    public Task<AutomationRun?> GetAsync(string runId, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<AutomationRun>> QueryAsync(AutomationRunQuery query, CancellationToken ct) => throw new NotSupportedException();

    public Task<int> MarkInterruptedAsync(DateTimeOffset endedAt, string message, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>Hands out scripted results in order (the last one repeats) and can run a hook when a message arrives.</summary>
internal sealed class ScriptedMailbox : ISessionMailbox
{
    private int calls;

    public List<MailboxResult> Results { get; } = [new(MailboxStatus.Completed)];

    public List<MailboxMessage> Messages { get; } = [];

    /// <summary>Runs when a message is delivered, before its result is returned (1-based call number).</summary>
    public Func<int, Task>? OnDeliver { get; set; }

    public bool IsIdle(string sessionId) => true;

    public async Task<MailboxResult> DeliverAsync(MailboxMessage message, IProgress<bool>? waiting, CancellationToken ct)
    {
        Messages.Add(message);
        var call = ++calls;
        if (OnDeliver is not null)
        {
            await OnDeliver(call);
        }

        return Results[Math.Min(call - 1, Results.Count - 1)];
    }
}

internal sealed class RunnerCatalog : ISessionCatalog
{
    public IReadOnlyList<SessionInfo> Sessions { get; set; } = [];

    public HashSet<string> RefuseStatus { get; } = new(StringComparer.Ordinal);

    public List<(string Id, SessionStatus Status)> StatusChanges { get; } = [];

    public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) => Task.FromResult(Sessions);

    public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct)
    {
        if (RefuseStatus.Contains(sessionId))
        {
            return Task.FromResult(false);
        }

        StatusChanges.Add((sessionId, status));
        return Task.FromResult(true);
    }

    public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => throw new NotSupportedException();

    public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class RunnerProjects : IProjectRegistry
{
    public IReadOnlyList<ProjectInfo> Projects { get; set; } = [];

    public Exception? ThrowOnList { get; set; }

    public Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct)
    {
        if (ThrowOnList is not null)
        {
            throw ThrowOnList;
        }

        return Task.FromResult(Projects);
    }

    public Task<ProjectInfo> AddAsync(string rootPath, string name, ProjectKind kind, CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> RemoveAsync(string projectId, CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> RenameAsync(string projectId, string name, CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> MoveAsync(string projectId, int sortOrder, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class RunnerSessions : IAutomationSessions
{
    public SessionInfo? Scratch { get; set; }

    public SessionInfo? Fork { get; set; }

    public Dictionary<string, SessionInfo> Known { get; } = new(StringComparer.Ordinal);

    public List<string> ScratchTitles { get; } = [];

    public List<string> ForkProjects { get; } = [];

    public List<string> Gets { get; } = [];

    public Task<SessionInfo?> CreateScratchAsync(string title, AutomationDefinition definition, string runId, CancellationToken ct)
    {
        ScratchTitles.Add(title);
        return Task.FromResult(Scratch);
    }

    public Task<SessionInfo?> GetOrCreateForkAsync(string projectId, AutomationDefinition definition, string runId, CancellationToken ct)
    {
        ForkProjects.Add(projectId);
        return Task.FromResult(Fork);
    }

    public Task<SessionInfo?> GetAsync(string sessionId, CancellationToken ct)
    {
        Gets.Add(sessionId);
        return Task.FromResult(Known.GetValueOrDefault(sessionId));
    }
}

internal sealed class RunnerLauncher : IChildAgentLauncher
{
    public ChildAgentResult Result { get; set; } = new("child-1", "done", false, new TokenUsage(3, 4));

    public List<ChildAgentRequest> Requests { get; } = [];

    public Task<ChildAgentResult> LaunchAsync(ChildAgentRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Result);
    }
}

internal sealed class RunnerProbe : IProviderQuotaProbe
{
    public int Calls { get; private set; }

    public Task<bool> ProbeAsync(ModelSettings model, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(true);
    }
}

/// <summary>An engine that records starts and can be told to fail them.</summary>
internal sealed class RunnerEngine : IAutomationEngine
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public List<AutomationRunRequest> Started { get; } = [];

    public Exception? Throw { get; set; }

    public AutomationConfig Config => new();

    public IReadOnlyList<AutomationDefinitionError> ConfigErrors => [];

    public IReadOnlyList<AutomationDefinition> Definitions => [];

    public IReadOnlyList<AutomationRun> ActiveRuns => [];

    public DateTimeOffset? NextFireTime(string automationId) => null;

    public Task UpdateConfigAsync(Func<AutomationConfig, AutomationConfig> change, CancellationToken ct) => Task.CompletedTask;

    public Task<AutomationStartResult> StartAsync(AutomationRunRequest request, CancellationToken ct)
    {
        if (Throw is not null)
        {
            throw Throw;
        }

        Started.Add(request);
        return Task.FromResult(new AutomationStartResult(AutomationStartStatus.Started, null));
    }

    public bool Cancel(string runId, string reason) => false;

    public Task ResetTargetBudgetAsync(string targetKey, CancellationToken ct) => Task.CompletedTask;
}

internal static class RunnerTestKit
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<T> Within<T>(this Task<T> task) => await task.WaitAsync(Timeout);

    public static async Task Within(this Task task) => await task.WaitAsync(Timeout);

    public static string TempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Delete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp folder must not fail a test.
        }
    }
}
