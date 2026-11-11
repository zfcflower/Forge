namespace Forge.Abstractions;

// Automations: scheduled and event-driven runs. Semantics live in .thinkrail/context/TASK-automations.md and the
// forge-core / forge-app SPECs; these are the contracts between Core (engine, stores, quota) and App (runners, UI).

/// <summary>Trigger shapes of the first version; all evaluated on the local clock.</summary>
public enum AutomationTriggerKind { Hourly, Daily, Weekly, Cron }

/// <summary>A wall-clock time of day (hour 0-23, minute 0-59).</summary>
public readonly record struct AutomationTime(int Hour, int Minute);

/// <summary>
/// One trigger. <see cref="AutomationTriggerKind.Hourly"/>: every <see cref="IntervalHours"/> hours (1-12, counted from
/// midnight) at <see cref="Minute"/>. <see cref="AutomationTriggerKind.Daily"/>: every <see cref="Times"/> entry.
/// <see cref="AutomationTriggerKind.Weekly"/>: <see cref="Days"/> x <see cref="Times"/>.
/// <see cref="AutomationTriggerKind.Cron"/>: the 5-field expression in <see cref="Cron"/>.
/// </summary>
public sealed record AutomationTrigger(AutomationTriggerKind Kind)
{
    public int IntervalHours { get; init; } = 1;

    public int Minute { get; init; }

    public IReadOnlyList<AutomationTime> Times { get; init; } = [];

    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];

    public string? Cron { get; init; }
}

/// <summary>What a run does when it fires.</summary>
public enum AutomationAction
{
    /// <summary>Runs <see cref="AutomationDefinition.Prompt"/> through an agent at <see cref="AutomationDefinition.Placement"/>.</summary>
    Prompt,

    /// <summary>Only raises a notification with <see cref="AutomationDefinition.Prompt"/> as its text; no session, no model.</summary>
    Remind,
}

/// <summary>Where a <see cref="AutomationAction.Prompt"/> run executes.</summary>
public enum AutomationPlacement
{
    /// <summary>A new root session in a scratch folder of the General project.</summary>
    Scratch,

    /// <summary>A new root session in the project's fixed fork worktree for this automation, synced from its source first.</summary>
    FixedFork,

    /// <summary>A read-only Scout sub-agent under <see cref="AutomationDefinition.TargetSessionId"/> (attach; no new root session).</summary>
    Scout,
}

/// <summary>A user-editable automation. Ids starting with <see cref="BuiltInAutomations.Prefix"/> are built in.</summary>
public sealed record AutomationDefinition(string Id, string Name)
{
    public bool Enabled { get; init; } = true;

    /// <summary>Empty = manual runs only.</summary>
    public IReadOnlyList<AutomationTrigger> Triggers { get; init; } = [];

    public AutomationAction Action { get; init; } = AutomationAction.Prompt;

    public string Prompt { get; init; } = string.Empty;

    public AutomationPlacement Placement { get; init; } = AutomationPlacement.Scratch;

    /// <summary>Project of a <see cref="AutomationPlacement.FixedFork"/> run.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Session a <see cref="AutomationPlacement.Scout"/> run attaches to.</summary>
    public string? TargetSessionId { get; init; }

    /// <summary>Scratch runs only: give the agent read-only tools.</summary>
    public bool ReadOnlyTools { get; init; }

    /// <summary>Null = the global default model.</summary>
    public ModelSettings? Model { get; init; }

    /// <summary>Run once on the next start for occurrences missed while Forge was closed.</summary>
    public bool CatchUp { get; init; }

    public bool IsBuiltIn => BuiltInAutomations.IsBuiltIn(Id);
}

public static class BuiltInAutomations
{
    public const string Prefix = "builtin.";
    public const string QuotaResume = "builtin.quota-resume";
    public const string CiChain = "builtin.ci-chain";
    public const string SessionWatch = "builtin.session-watch";
    public const string Housekeeping = "builtin.housekeeping";

    public static IReadOnlyList<string> All { get; } = [QuotaResume, CiChain, SessionWatch, Housekeeping];

    public static bool IsBuiltIn(string id) => id.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>The user-level automation file (<c>automations.json</c>).</summary>
public sealed record AutomationConfig
{
    /// <summary>Global pause: nothing fires on schedule; manual runs still work.</summary>
    public bool Paused { get; init; }

    /// <summary>User-defined automations (never built-ins).</summary>
    public IReadOnlyList<AutomationDefinition> Definitions { get; init; } = [];

    /// <summary>Built-in ids the user switched off.</summary>
    public IReadOnlyList<string> DisabledBuiltIns { get; init; } = [];

    /// <summary>Project ids whose CI fixes are not pushed automatically.</summary>
    public IReadOnlyList<string> CiAutoPushOffProjects { get; init; } = [];
}

/// <summary>A definition the store could not read; the others still load.</summary>
public sealed record AutomationDefinitionError(int Index, string? Id, string Message);

public sealed record AutomationConfigLoad(AutomationConfig Config, IReadOnlyList<AutomationDefinitionError> Errors);

/// <summary>Reads and writes <see cref="AutomationConfig"/>; a missing file is an empty config.</summary>
public interface IAutomationDefinitionStore
{
    Task<AutomationConfigLoad> LoadAsync(CancellationToken ct);

    /// <summary>Writes atomically and reads the result back; throws when the read-back differs.</summary>
    Task SaveAsync(AutomationConfig config, CancellationToken ct);
}

/// <summary>
/// Run lifecycle. "Active" (the title-bar badge) = <see cref="Running"/> + <see cref="WaitingIdle"/> +
/// <see cref="WaitingQuota"/>; the rest are terminal.
/// </summary>
public enum AutomationRunStatus { Running, WaitingIdle, WaitingQuota, Completed, Failed, Cancelled, Interrupted, Skipped }

public static class AutomationRunStatuses
{
    public static bool IsActive(AutomationRunStatus status) =>
        status is AutomationRunStatus.Running or AutomationRunStatus.WaitingIdle or AutomationRunStatus.WaitingQuota;
}

public enum AutomationTriggerSource { Schedule, CatchUp, Manual, Event, Chain }

/// <summary>One run record (<c>automation_runs</c>).</summary>
/// <param name="Occurrence">Unique key: <c>&lt;triggerKey&gt;@&lt;UTC yyyy-MM-ddTHH:mm:ssZ&gt;</c> for scheduled runs, the event key for
/// event runs, a fresh id for manual runs.</param>
public sealed record AutomationRun(
    string Id,
    string AutomationId,
    string AutomationName,
    AutomationTriggerSource Source,
    string Occurrence,
    AutomationRunStatus Status,
    DateTimeOffset StartedAt)
{
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Why it fired, shown on the origin card (e.g. the failing check, the exhausted provider).</summary>
    public string? Detail { get; init; }

    /// <summary>Session the run created (create placements).</summary>
    public string? SessionId { get; init; }

    /// <summary>Session the run attached to (attach placements).</summary>
    public string? TargetSessionId { get; init; }

    public string? CauseRunId { get; init; }

    public int ChainDepth { get; init; }

    /// <summary>Attempt-budget key (e.g. <c>ci:&lt;repo&gt;#&lt;pr&gt;</c>); null = no budget.</summary>
    public string? TargetKey { get; init; }

    /// <summary>When a <see cref="AutomationRunStatus.WaitingQuota"/> run resumes; persisted across restarts.</summary>
    public DateTimeOffset? ResumeAt { get; init; }

    public TokenUsage? Usage { get; init; }

    /// <summary>Outcome or reason (every "did not run" is recorded here).</summary>
    public string? Message { get; init; }
}

public sealed record AutomationRunQuery
{
    public string? AutomationId { get; init; }

    /// <summary>Null = any status.</summary>
    public IReadOnlyList<AutomationRunStatus>? Statuses { get; init; }

    public string? TargetSessionId { get; init; }

    public int Limit { get; init; } = 100;
}

/// <summary>Run records plus a small key/value state table (heartbeat, budgets, CI watermarks).</summary>
public interface IAutomationRunStore
{
    /// <summary>False when <see cref="AutomationRun.Occurrence"/> already exists (that occurrence already ran).</summary>
    Task<bool> TryInsertAsync(AutomationRun run, CancellationToken ct);

    Task UpdateAsync(AutomationRun run, CancellationToken ct);

    Task<AutomationRun?> GetAsync(string runId, CancellationToken ct);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<AutomationRun>> QueryAsync(AutomationRunQuery query, CancellationToken ct);

    /// <summary>
    /// Startup recovery: every <see cref="AutomationRunStatus.Running"/> / <see cref="AutomationRunStatus.WaitingIdle"/> run
    /// becomes <see cref="AutomationRunStatus.Interrupted"/> with <c>EndedAt = endedAt</c>; <see cref="AutomationRunStatus.WaitingQuota"/>
    /// is left alone. Returns the number changed.
    /// </summary>
    Task<int> MarkInterruptedAsync(DateTimeOffset endedAt, string message, CancellationToken ct);

    /// <summary>Keeps the newest <paramref name="perAutomation"/> per automation and <paramref name="total"/> overall; never deletes active runs.</summary>
    Task<int> TrimAsync(int perAutomation, int total, CancellationToken ct);

    Task<string?> GetStateAsync(string key, CancellationToken ct);

    /// <summary>Null removes the key.</summary>
    Task SetStateAsync(string key, string? value, CancellationToken ct);
}

/// <summary>Why <see cref="IAutomationEngine.StartAsync"/> did or did not start a run. Every refusal is also recorded as a <see cref="AutomationRunStatus.Skipped"/> run, except <see cref="Duplicate"/> and <see cref="UnknownAutomation"/>.</summary>
public enum AutomationStartStatus { Started, Busy, Duplicate, ChainTooDeep, SelfTriggered, BudgetExhausted, UnknownAutomation }

public sealed record AutomationStartResult(AutomationStartStatus Status, AutomationRun? Run);

/// <summary>A run to start. Scheduled runs are built by the engine itself; App code uses this for manual and event runs.</summary>
public sealed record AutomationRunRequest(string AutomationId, AutomationTriggerSource Source)
{
    /// <summary>Null = a fresh unique key (manual).</summary>
    public string? Occurrence { get; init; }

    public string? Detail { get; init; }

    public string? TargetSessionId { get; init; }

    /// <summary>The run that caused this one (chain); depth = cause depth + 1, max 5, never the same automation.</summary>
    public string? CauseRunId { get; init; }

    public string? TargetKey { get; init; }

    /// <summary>Consecutive attempts allowed for <see cref="TargetKey"/>; ignored without a key.</summary>
    public int MaxTargetAttempts { get; init; } = 3;

    /// <summary>Initial status; event runs that start by waiting use <see cref="AutomationRunStatus.WaitingQuota"/>.</summary>
    public AutomationRunStatus InitialStatus { get; init; } = AutomationRunStatus.Running;

    public DateTimeOffset? ResumeAt { get; init; }
}

/// <summary>Handed to a runner for one run; updates are persisted and raise <see cref="IAutomationEngine.Changed"/>.</summary>
public interface IAutomationRunContext
{
    AutomationRun Run { get; }

    /// <summary>The definition the run started from (a synthesized one for built-ins).</summary>
    AutomationDefinition Definition { get; }

    /// <summary>True when the run is being resumed after a restart (only <see cref="AutomationRunStatus.WaitingQuota"/> runs are).</summary>
    bool IsResumed { get; }

    /// <summary>Changes the non-terminal state of the run (status, <see cref="AutomationRun.ResumeAt"/>, session ids, detail, usage).</summary>
    Task UpdateAsync(Func<AutomationRun, AutomationRun> change, CancellationToken ct);
}

public sealed record AutomationRunOutcome(AutomationRunStatus Status, string? Message = null)
{
    public static AutomationRunOutcome Completed(string? message = null) => new(AutomationRunStatus.Completed, message);

    public static AutomationRunOutcome Failed(string message) => new(AutomationRunStatus.Failed, message);

    public static AutomationRunOutcome Skipped(string message) => new(AutomationRunStatus.Skipped, message);

    public static AutomationRunOutcome Cancelled(string message) => new(AutomationRunStatus.Cancelled, message);
}

/// <summary>Executes runs of one automation (or the default runner for user definitions). Implemented in App.</summary>
public interface IAutomationRunner
{
    /// <summary>
    /// Runs to a terminal outcome. The token is cancelled by <see cref="IAutomationEngine.Cancel"/> and on shutdown
    /// (then the run ends as <see cref="AutomationRunStatus.Cancelled"/> or, on shutdown, stays for startup recovery).
    /// </summary>
    Task<AutomationRunOutcome> RunAsync(IAutomationRunContext context, CancellationToken ct);
}

/// <summary>The scheduler and run bookkeeping (Core). Thread-safe; events are raised on the caller's / timer thread.</summary>
public interface IAutomationEngine
{
    /// <summary>Raised after runs, config or next times change.</summary>
    event EventHandler? Changed;

    AutomationConfig Config { get; }

    IReadOnlyList<AutomationDefinitionError> ConfigErrors { get; }

    /// <summary>Built-in and user definitions, built-ins first, with their effective enabled state.</summary>
    IReadOnlyList<AutomationDefinition> Definitions { get; }

    /// <summary>Runs currently active (see <see cref="AutomationRunStatuses.IsActive"/>).</summary>
    IReadOnlyList<AutomationRun> ActiveRuns { get; }

    /// <summary>Next scheduled fire time; null when disabled, paused, manual-only or none within 8 years.</summary>
    DateTimeOffset? NextFireTime(string automationId);

    /// <summary>Saves the changed config and reschedules.</summary>
    Task UpdateConfigAsync(Func<AutomationConfig, AutomationConfig> change, CancellationToken ct);

    Task<AutomationStartResult> StartAsync(AutomationRunRequest request, CancellationToken ct);

    /// <summary>Cancels an active run; false when it is not active.</summary>
    bool Cancel(string runId, string reason);

    /// <summary>Clears the attempt budget of <paramref name="targetKey"/> (e.g. the PR went green).</summary>
    Task ResetTargetBudgetAsync(string targetKey, CancellationToken ct);
}

/// <summary>Outcome of a mailbox delivery.</summary>
public enum MailboxStatus
{
    /// <summary>The message ran as a turn and the turn ended normally.</summary>
    Completed,

    /// <summary>The turn ran and failed (see <see cref="MailboxResult.Reason"/>).</summary>
    TurnFailed,

    /// <summary>Not delivered: the user took over, archived or removed the session, or it does not exist.</summary>
    Withdrawn,

    /// <summary>The caller cancelled before delivery.</summary>
    Cancelled,
}

public sealed record MailboxResult(MailboxStatus Status, string? Reason = null, TokenUsage? Usage = null)
{
    /// <summary>The delivered turn failed because the root agent's provider quota is spent (set with <see cref="MailboxStatus.TurnFailed"/>).</summary>
    public ProviderQuotaExhausted? Quota { get; init; }
}

/// <summary>A message an automation sends to a root session as if the user had typed it.</summary>
/// <param name="Origin">Short label of the sending automation, shown on the delivered message.</param>
public sealed record MailboxMessage(string SessionId, string Text, string RunId, string Origin)
{
    /// <summary>
    /// The agent this one turn runs with (read-only tools, a model override); null = the session's own agent. It is applied
    /// right before the turn and the session's own selection is restored after it.
    /// </summary>
    public AgentDefinition? Agent { get; init; }
}

/// <summary>
/// Delivers automation messages to root sessions only while they are idle (red line 2). Never hands over, archives or
/// replaces a session (red line 1).
/// </summary>
public interface ISessionMailbox
{
    bool IsIdle(string sessionId);

    /// <summary>
    /// Waits until the session is idle, runs <paramref name="message"/> as a turn and completes when that turn ends.
    /// <paramref name="waiting"/> receives true while waiting for idle and false once the turn starts.
    /// </summary>
    Task<MailboxResult> DeliverAsync(MailboxMessage message, IProgress<bool>? waiting, CancellationToken ct);
}

public sealed record ChildAgentRequest(string ParentSessionId, string Kind, string Task);

public sealed record ChildAgentResult(string ChildSessionId, string Text, bool IsError, TokenUsage Usage);

/// <summary>Starts a sub-agent under a root session outside of a model turn (the host side of <c>spawn_agent</c>).</summary>
public interface IChildAgentLauncher
{
    Task<ChildAgentResult> LaunchAsync(ChildAgentRequest request, CancellationToken ct);
}

/// <summary>A provider refused because a quota (not a short rate limit) is used up. <paramref name="ResetAt"/> null = unknown.</summary>
public sealed record ProviderQuotaExhausted(string? ProviderId, string Model, DateTimeOffset? ResetAt, string Message);

/// <summary>Emitted just before the <see cref="AgentFailed"/> of a turn that failed because a quota is exhausted.</summary>
public sealed record AgentQuotaExhausted(string AgentId, ProviderQuotaExhausted Quota) : AgentEvent(AgentId);

/// <summary>Minimal request to see whether a quota is available again (same model, smallest output).</summary>
public interface IProviderQuotaProbe
{
    /// <summary>True = the model answered; false = still exhausted. Other failures throw.</summary>
    Task<bool> ProbeAsync(ModelSettings model, CancellationToken ct);
}
