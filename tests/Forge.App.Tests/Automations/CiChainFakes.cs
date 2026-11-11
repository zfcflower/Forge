using System.Collections.Concurrent;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.Workspace;

namespace Forge.App.Tests.Automations;

internal sealed class CiFakeEngine : IAutomationEngine
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public List<AutomationRunRequest> Started { get; } = [];

    public List<string> Resets { get; } = [];

    public AutomationStartStatus NextStatus { get; set; } = AutomationStartStatus.Started;

    public AutomationConfig Config => new();

    public IReadOnlyList<AutomationDefinitionError> ConfigErrors => [];

    public IReadOnlyList<AutomationDefinition> Definitions => [];

    public IReadOnlyList<AutomationRun> ActiveRuns => [];

    public DateTimeOffset? NextFireTime(string automationId) => null;

    public Task UpdateConfigAsync(Func<AutomationConfig, AutomationConfig> change, CancellationToken ct) => Task.CompletedTask;

    public Task<AutomationStartResult> StartAsync(AutomationRunRequest request, CancellationToken ct)
    {
        Started.Add(request);
        return Task.FromResult(new AutomationStartResult(NextStatus, null));
    }

    public bool Cancel(string runId, string reason) => false;

    public Task ResetTargetBudgetAsync(string targetKey, CancellationToken ct)
    {
        Resets.Add(targetKey);
        return Task.CompletedTask;
    }
}

internal sealed class CiFakeCatalog : ISessionCatalog
{
    public IReadOnlyList<SessionInfo> Sessions { get; set; } = [];

    public bool ThrowOnce { get; set; }

    public int Calls { get; private set; }

    public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct)
    {
        Calls++;
        if (ThrowOnce)
        {
            ThrowOnce = false;
            throw new InvalidOperationException("boom");
        }

        return Task.FromResult(Sessions);
    }

    public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => throw new NotSupportedException();

    public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class CiFakeSessions : ISessionStore
{
    public SessionInfo? Session { get; set; }

    public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) => Task.FromResult(Session);

    public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) => throw new NotSupportedException();

    public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct) => throw new NotSupportedException();

    public IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) => throw new NotSupportedException();

    public Task<long> LastSeqAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

    public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>Only the key/value state of the store is used by the CI chain.</summary>
internal sealed class CiFakeState : IAutomationRunStore
{
    private readonly ConcurrentDictionary<string, string> values = new();

    public Task<string?> GetStateAsync(string key, CancellationToken ct) =>
        Task.FromResult(values.TryGetValue(key, out var value) ? value : null);

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

    public Task<bool> TryInsertAsync(AutomationRun run, CancellationToken ct) => throw new NotSupportedException();

    public Task UpdateAsync(AutomationRun run, CancellationToken ct) => throw new NotSupportedException();

    public Task<AutomationRun?> GetAsync(string runId, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<AutomationRun>> QueryAsync(AutomationRunQuery query, CancellationToken ct) => throw new NotSupportedException();

    public Task<int> MarkInterruptedAsync(DateTimeOffset endedAt, string message, CancellationToken ct) => throw new NotSupportedException();

    public Task<int> TrimAsync(int perAutomation, int total, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class CiFakePr : IPrPlatform
{
    public string Name => "Fake";

    public PullRequestInfo? Pull { get; set; }

    public IReadOnlyList<PrCheck> Checks { get; set; } = [];

    public IReadOnlyDictionary<long, string> Logs { get; set; } = new Dictionary<long, string>();

    public string? ThrowFor { get; set; }

    public int ViewCalls { get; private set; }

    public Task<PrPlatformAvailability> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default) =>
        Task.FromResult(new PrPlatformAvailability(PrPlatformState.Ready, "ok"));

    public Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(string workingDirectory, string? selector = null, CancellationToken ct = default)
    {
        ViewCalls++;
        if (workingDirectory == ThrowFor)
        {
            throw new InvalidOperationException("boom");
        }

        return Task.FromResult(Pull is { } pull
            ? PrResult<PullRequestInfo>.Ok(pull)
            : PrResult<PullRequestInfo>.Fail(GitRemoteFailureKind.Other, "none", reason: PrFailureReason.PullRequestNotFound));
    }

    public Task<PrResult<PrChecksSnapshot>> GetChecksAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        Task.FromResult(PrResult<PrChecksSnapshot>.Ok(new PrChecksSnapshot(Checks)));

    public Task<PrResult<PrText>> GetFailedJobLogAsync(string workingDirectory, long? runId, long jobId, CancellationToken ct = default) =>
        Task.FromResult(Logs.TryGetValue(jobId, out var log)
            ? PrResult<PrText>.Ok(new PrText(log, false))
            : PrResult<PrText>.Fail(GitRemoteFailureKind.Other, "no log"));

    public Task<PrResult<PrReference>> CreatePullRequestAsync(string workingDirectory, string title, string body, string baseBranch, string headBranch, bool draft = false, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrReference>> EditPullRequestAsync(string workingDirectory, int number, string? title = null, string? body = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrMergeState>> GetMergeStateAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<IReadOnlyList<PrReviewThread>>> GetReviewThreadsAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrText>> GetPullRequestDiffAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrReviewComment>> ReplyToReviewThreadAsync(string workingDirectory, string threadId, string body, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrThreadResolution>> ResolveReviewThreadAsync(string workingDirectory, string threadId, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

internal sealed class CiFakeGit : Forge.App.Automations.ICiGit
{
    private int headCalls;

    /// <summary>Consecutive HEAD readings; the last one repeats.</summary>
    public string[] Heads { get; set; } = ["h1"];

    public Forge.App.Automations.CiPushResult PushResult { get; set; } = new(true, null);

    public List<(string Worktree, string Branch)> Pushes { get; } = [];

    public Task<string?> HeadAsync(string worktree, CancellationToken ct) =>
        Task.FromResult<string?>(Heads[Math.Min(headCalls++, Heads.Length - 1)]);

    public Task<Forge.App.Automations.CiPushResult> PushAsync(string worktree, string branch, CancellationToken ct)
    {
        Pushes.Add((worktree, branch));
        return Task.FromResult(PushResult);
    }
}

internal sealed class CiFakeMailbox : ISessionMailbox
{
    public MailboxResult Result { get; set; } = new(MailboxStatus.Completed);

    public Exception? Throw { get; set; }

    public bool ReportWaiting { get; set; }

    public CiFakeContext? Context { get; set; }

    public List<MailboxMessage> Messages { get; } = [];

    public bool IsIdle(string sessionId) => true;

    public Task<MailboxResult> DeliverAsync(MailboxMessage message, IProgress<bool>? waiting, CancellationToken ct)
    {
        if (Throw is not null)
        {
            throw Throw;
        }

        Messages.Add(message);
        if (ReportWaiting)
        {
            waiting?.Report(true);
            waiting?.Report(false);
        }

        return Task.FromResult(Result);
    }
}

internal sealed class CiFakeLauncher : IChildAgentLauncher
{
    public ChildAgentResult Result { get; set; } = new("c", string.Empty, false, new TokenUsage(0, 0));

    public List<ChildAgentRequest> Requests { get; } = [];

    public Task<ChildAgentResult> LaunchAsync(ChildAgentRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Result);
    }
}

internal sealed class CiFakeContext : IAutomationRunContext
{
    private readonly object gate = new();
    private readonly List<AutomationRunStatus> statuses = [];

    public CiFakeContext(AutomationRun run)
    {
        Run = run;
    }

    public AutomationRun Run { get; private set; }

    public AutomationDefinition Definition { get; } = new(BuiltInAutomations.CiChain, "ci");

    public bool IsResumed => false;

    public IReadOnlyList<AutomationRunStatus> Statuses
    {
        get
        {
            lock (gate)
            {
                return [.. statuses];
            }
        }
    }

    public Task UpdateAsync(Func<AutomationRun, AutomationRun> change, CancellationToken ct)
    {
        lock (gate)
        {
            Run = change(Run);
            statuses.Add(Run.Status);
        }

        return Task.CompletedTask;
    }
}

internal sealed class CiFakeNotes : INotifications
{
    public List<(NotificationLevel Level, string Title, string Body)> CallsRaw { get; } = [];

    public IReadOnlyList<CiNote> Calls => [.. CallsRaw.Select(c => new CiNote(c.Level, c.Title, c.Body))];

    public Task NotifyAsync(NotificationLevel level, LocText title, LocText body, string? key = null, CancellationToken ct = default)
    {
        CallsRaw.Add((level, title.Resolve(), body.Resolve()));
        return Task.CompletedTask;
    }
}

internal sealed record CiNote(NotificationLevel Level, string Title, string Body);

/// <summary>A clock whose timers fire only when advanced; lets the watcher loop run without real waiting.</summary>
internal sealed class CiFakeTime : TimeProvider
{
    private readonly object gate = new();
    private readonly List<FakeTimer> timers = [];
    private DateTimeOffset now = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state, dueTime);
        lock (gate)
        {
            timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<FakeTimer> due = [];
        lock (gate)
        {
            now += by;
            foreach (var timer in timers)
            {
                if (timer.Due is { } at && at <= now)
                {
                    timer.Due = null;
                    due.Add(timer);
                }
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    public async Task WaitForTimerAsync()
    {
        for (var i = 0; i < 500; i++)
        {
            lock (gate)
            {
                if (timers.Exists(t => t.Due is not null))
                {
                    return;
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("no timer was armed");
    }

    public async Task WaitForPollAsync(CiFakeCatalog catalog, int calls)
    {
        for (var i = 0; i < 500 && catalog.Calls < calls; i++)
        {
            await Task.Delay(10);
        }
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly CiFakeTime owner;
        private readonly TimerCallback callback;
        private readonly object? state;

        public FakeTimer(CiFakeTime owner, TimerCallback callback, object? state, TimeSpan dueTime)
        {
            this.owner = owner;
            this.callback = callback;
            this.state = state;
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
        }

        public DateTimeOffset? Due { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner.gate)
            {
                Due = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
