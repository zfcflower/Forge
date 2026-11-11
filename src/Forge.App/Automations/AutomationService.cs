using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Approval;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.Core.Automations;
using Forge.Core.Providers;
using Forge.Core.Storage;

namespace Forge.App.Automations;

/// <summary>
/// The app side of automations: owns the engine and its stores, the mailbox and the runners, and connects them to the
/// window once it exists (<see cref="Attach"/>). Created by the composition root; the engine loop starts on attach and
/// stops on dispose (runs that were waiting for a quota stay recorded and resume on the next start).
/// </summary>
internal sealed class AutomationService : IAsyncDisposable
{
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMinutes(1);

    private readonly HostProxy host = new();
    private readonly IUiThread ui;
    private readonly TimeProvider time;
    private readonly SqliteAutomationRunStore runs;
    private readonly INotifications? notifications;
    private readonly CancellationTokenSource stopping = new();
    private readonly QuotaResumeRunner quotaRunner;
    private readonly SessionWatch watch;
    private readonly List<Task> loops = [];
    private readonly List<Func<CancellationToken, Task>> onAttach = [];
    private int activeCount;
    private bool attached;
    private bool disposed;

    public AutomationService(
        string definitionsPath,
        string dataDirectory,
        IProjectRegistry projects,
        ISessionCatalog catalog,
        ISessionStore store,
        Services.WorktreeService worktrees,
        Func<ModelSettings, IChatProvider> providers,
        Func<ModelSettings> defaultModel,
        INotifications? notifications,
        IUiThread? ui = null,
        TimeProvider? time = null)
    {
        this.ui = ui ?? DispatcherUiThread.Instance;
        this.time = time ?? TimeProvider.System;
        this.notifications = notifications;
        runs = new SqliteAutomationRunStore(dataDirectory);
        Definitions = new AutomationDefinitionStore(definitionsPath);
        Mailbox = new SessionMailbox(host, this.ui, this.time);
        Launcher = new SessionChildAgentLauncher(host, this.ui);
        var sessions = new CatalogAutomationSessions(projects, catalog, store, worktrees, runs);
        var prompt = new PromptRunner(sessions, Mailbox, Launcher, host, this.ui, notifications, defaultModel, this.time);
        Engine = new AutomationEngine(Definitions, runs, this.time, TimeZoneInfo.Local, prompt);

        var probe = new ProviderQuotaProbe((model, _) => ValueTask.FromResult(providers(model)), this.time);
        var schedule = new QuotaProbeSchedule(
            time: this.time,
            onProbeError: static (key, exception) => Trace.WriteLine("[automation] quota probe " + key + " failed: " + exception.Message));
        quotaRunner = new QuotaResumeRunner(Mailbox, runs, probe, schedule, new ResumeStagger(this.time), notifications, this.time);
        Engine.RegisterRunner(BuiltInAutomations.QuotaResume, quotaRunner);
        Engine.RegisterRunner(BuiltInAutomations.SessionWatch, new SessionWatchRunner(notifications));
        Engine.RegisterRunner(
            BuiltInAutomations.Housekeeping,
            new HousekeepingRunner(
                projects,
                catalog,
                runs,
                Definitions,
                (root, ct) => worktrees.Manager.PruneAsync(root, ct),
                () => OpenSessionIdsSnapshot,
                this.time));
        watch = new SessionWatch(Engine, host, PendingApprovals, () => IsEnabled(BuiltInAutomations.SessionWatch), this.time);
        Engine.Changed += OnEngineChanged;
    }

    public AutomationEngine Engine { get; }

    public IAutomationRunStore Runs => runs;

    public AutomationDefinitionStore Definitions { get; }

    public ISessionMailbox Mailbox { get; }

    public IChildAgentLauncher Launcher { get; }

    /// <summary>Running and waiting runs (the title-bar badge); read and raised on the UI thread.</summary>
    public int ActiveCount => activeCount;

    /// <summary>Raised on the UI thread after the active runs changed.</summary>
    public event Action? Changed;

    /// <summary>Snapshot of the open session ids, refreshed on the UI thread by each watch tick (read off-thread by housekeeping).</summary>
    private volatile IReadOnlySet<string> openSessionIdsSnapshot = new HashSet<string>(StringComparer.Ordinal);

    private IReadOnlySet<string> OpenSessionIdsSnapshot
    {
        get => openSessionIdsSnapshot;
        set => openSessionIdsSnapshot = value;
    }

    /// <summary>The built-in is enabled and automations are not paused (event-started runs check this themselves).</summary>
    public bool IsEnabled(string automationId)
    {
        var config = Engine.Config;
        return !config.Paused && !config.DisabledBuiltIns.Contains(automationId);
    }

    /// <summary>The active run attached to <paramref name="sessionId"/>, if any (the session banner).</summary>
    public AutomationRun? ActiveRunFor(string sessionId)
    {
        foreach (var run in Engine.ActiveRuns)
        {
            if (string.Equals(run.TargetSessionId, sessionId, StringComparison.Ordinal) ||
                string.Equals(run.SessionId, sessionId, StringComparison.Ordinal))
            {
                return run;
            }
        }

        return null;
    }

    /// <summary>Registers an extra built-in runner (the CI chain) before <see cref="Attach"/>.</summary>
    public void RegisterRunner(string automationId, IAutomationRunner runner) => Engine.RegisterRunner(automationId, runner);

    /// <summary>Adds a background loop that ends with the service (the CI watcher).</summary>
    public void AddLoop(Func<CancellationToken, Task> loop)
    {
        lock (loops)
        {
            loops.Add(Task.Run(() => loop(stopping.Token)));
        }
    }

    /// <summary>Adds a loop that starts once the window is attached (it may deliver to sessions).</summary>
    public void AddLoopOnAttach(Func<CancellationToken, Task> loop)
    {
        if (attached)
        {
            AddLoop(loop);
            return;
        }

        onAttach.Add(loop);
    }

    /// <summary>Connects the window and starts the engine: recovery of interrupted runs, catch-up, then the schedule loop.</summary>
    public void Attach(IAutomationHost window)
    {
        if (attached || disposed)
        {
            return;
        }

        attached = true;
        host.Target = window;
        AddLoop(async ct =>
        {
            try
            {
                await Engine.StartupAsync(ct).ConfigureAwait(false);
                await Engine.RunAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        });
        AddLoop(WatchLoopAsync);
        foreach (var loop in onAttach)
        {
            AddLoop(loop);
        }

        onAttach.Clear();
    }

    /// <summary>A root turn of <paramref name="session"/> failed on a spent quota: start (or note for) a resume run.</summary>
    public void OnQuotaExhausted(IAutomationSession session, ProviderQuotaExhausted quota) =>
        _ = OnQuotaExhaustedAsync(session.SessionId, session.Title, quota);

    /// <summary>A turn started; a user turn on a session waiting for its quota cancels the resume (the user went first).</summary>
    public void OnTurnStarted(IAutomationSession session, bool automationTurn)
    {
        if (automationTurn)
        {
            return;
        }

        foreach (var run in Engine.ActiveRuns)
        {
            if (run.AutomationId == BuiltInAutomations.QuotaResume &&
                string.Equals(run.TargetSessionId, session.SessionId, StringComparison.Ordinal) &&
                run.Status is AutomationRunStatus.WaitingQuota or AutomationRunStatus.WaitingIdle)
            {
                Engine.Cancel(run.Id, AutomationStrings.UserContinuedText);
            }
        }
    }

    public void OnTurnCompleted(IAutomationSession session, bool automationTurn) => watch.OnTurnCompleted(session, automationTurn);

    /// <summary>The banner's cancel button.</summary>
    public bool Cancel(string runId) => Engine.Cancel(runId, AutomationStrings.CancelledByUserText);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Engine.Changed -= OnEngineChanged;
        await stopping.CancelAsync().ConfigureAwait(false);
        Task[] pending;
        lock (loops)
        {
            pending = [.. loops];
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
        }

        await Engine.DisposeAsync().ConfigureAwait(false);
        await runs.DisposeAsync().ConfigureAwait(false);
        stopping.Dispose();
    }

    private async Task OnQuotaExhaustedAsync(string sessionId, string title, ProviderQuotaExhausted quota)
    {
        try
        {
            var note = new QuotaNote(quota.ProviderId, quota.Model, quota.ResetAt, time.GetUtcNow(), title, quota.Message);
            await runs.SetStateAsync(QuotaNote.LatestPrefix + sessionId, note.Serialize(), CancellationToken.None).ConfigureAwait(false);
            if (!IsEnabled(BuiltInAutomations.QuotaResume))
            {
                return;
            }

            foreach (var run in Engine.ActiveRuns)
            {
                if (run.AutomationId == BuiltInAutomations.QuotaResume &&
                    string.Equals(run.TargetSessionId, sessionId, StringComparison.Ordinal))
                {
                    // The active run sees the failure of its own "continue" through the latest note and waits again.
                    return;
                }
            }

            var resumeAt = quotaRunner.ResumeAtFor(quota.ResetAt);
            var occurrence = "quota:" + sessionId + "@" + note.At.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // The runner reads the model it waits for from state keyed by the run's occurrence (stable across restarts).
            await runs.SetStateAsync(QuotaNote.RunPrefix + occurrence, note.Serialize(), CancellationToken.None).ConfigureAwait(false);
            var started = await Engine.StartAsync(
                    new AutomationRunRequest(BuiltInAutomations.QuotaResume, AutomationTriggerSource.Event)
                    {
                        Occurrence = occurrence,
                        TargetSessionId = sessionId,
                        InitialStatus = AutomationRunStatus.WaitingQuota,
                        ResumeAt = resumeAt,
                        Detail = quota.Model + ": " + quota.Message,
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (started.Status != AutomationStartStatus.Started)
            {
                await runs.SetStateAsync(QuotaNote.RunPrefix + occurrence, null, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            notifications.Notify(
                NotificationLevel.Warning,
                AutomationStrings.QuotaWaitingTitle,
                resumeAt is { } at
                    ? AutomationStrings.QuotaWaitingBody(title, TextFormat.ShortStamp(at))
                    : AutomationStrings.QuotaWaitingProbeBody(title),
                "quota-waiting:" + sessionId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.WriteLine("[automation] quota resume could not start: " + exception.Message);
        }
    }

    private async Task WatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WatchInterval, time, ct).ConfigureAwait(false);
                await ui.InvokeAsync(() =>
                {
                    OpenSessionIdsSnapshot = OpenSessionIds();
                    watch.Check();
                    return Task.FromResult(true);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.WriteLine("[automation] watch tick failed: " + exception.Message);
            }
        }
    }

    private HashSet<string> OpenSessionIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in host.OpenSessions)
        {
            ids.Add(session.SessionId);
        }

        return ids;
    }

    private static IReadOnlyList<(string SessionId, DateTimeOffset CreatedAt)> PendingApprovals()
    {
        if (ApprovalServices.Current is not { } services)
        {
            return [];
        }

        var list = new List<(string, DateTimeOffset)>();
        foreach (var card in services.Queue.Items)
        {
            if (card.SessionId is { Length: > 0 } id)
            {
                list.Add((id, card.CreatedAt));
            }
        }

        return list;
    }

    private void OnEngineChanged(object? sender, EventArgs e) => ui.Post(() =>
    {
        var count = 0;
        foreach (var run in Engine.ActiveRuns)
        {
            if (AutomationRunStatuses.IsActive(run.Status))
            {
                count++;
            }
        }

        activeCount = count;
        Changed?.Invoke();
    });

    /// <summary>Forwards to the window once attached; before that nothing is open.</summary>
    private sealed class HostProxy : IAutomationHost
    {
        public IAutomationHost? Target { get; set; }

        public IReadOnlyList<IAutomationSession> OpenSessions => Target?.OpenSessions ?? [];

        public bool IsWindowActive => Target?.IsWindowActive ?? false;

        public IAutomationSession? FindOpen(string sessionId) => Target?.FindOpen(sessionId);

        public Task<IAutomationSession?> OpenAsync(string sessionId, CancellationToken ct) =>
            Target?.OpenAsync(sessionId, ct) ?? Task.FromResult<IAutomationSession?>(null);

        public Task RefreshTreesAsync() => Target?.RefreshTreesAsync() ?? Task.CompletedTask;
    }
}
