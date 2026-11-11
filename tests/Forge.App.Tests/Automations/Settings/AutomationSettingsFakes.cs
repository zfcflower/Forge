using Forge.Abstractions;
using Forge.App.Automations.Settings;

namespace Forge.App.Tests.Automations.Settings;

/// <summary>An in-memory engine: built-ins first, config writes applied synchronously, <c>Changed</c> raised after each.</summary>
internal sealed class FakeAutomationEngine : IAutomationEngine
{
    public event EventHandler? Changed;

    public AutomationConfig Config { get; set; } = new();

    public IReadOnlyList<AutomationDefinitionError> ConfigErrors { get; set; } = [];

    public Dictionary<string, DateTimeOffset> NextTimes { get; } = new(StringComparer.Ordinal);

    public List<AutomationRunRequest> Started { get; } = [];

    public AutomationStartStatus StartStatus { get; set; } = AutomationStartStatus.Started;

    public int UpdateCount { get; private set; }

    public IReadOnlyList<AutomationDefinition> Definitions =>
    [
        .. BuiltInAutomations.All.Select(id => new AutomationDefinition(id, id)
        {
            Enabled = !Config.DisabledBuiltIns.Contains(id),
            Triggers = id == BuiltInAutomations.Housekeeping ? [new AutomationTrigger(AutomationTriggerKind.Daily) { Times = [new AutomationTime(3, 0)] }] : [],
        }),
        .. Config.Definitions,
    ];

    public IReadOnlyList<AutomationRun> ActiveRuns => [];

    public DateTimeOffset? NextFireTime(string automationId) => NextTimes.TryGetValue(automationId, out var time) ? time : null;

    public Task UpdateConfigAsync(Func<AutomationConfig, AutomationConfig> change, CancellationToken ct)
    {
        Config = change(Config);
        UpdateCount++;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<AutomationStartResult> StartAsync(AutomationRunRequest request, CancellationToken ct)
    {
        Started.Add(request);
        return Task.FromResult(new AutomationStartResult(StartStatus, null));
    }

    public bool Cancel(string runId, string reason) => false;

    public Task ResetTargetBudgetAsync(string targetKey, CancellationToken ct) => Task.CompletedTask;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>Only <see cref="QueryAsync"/> is used by the settings page.</summary>
internal sealed class FakeRunStore : IAutomationRunStore
{
    public List<AutomationRun> Runs { get; } = [];

    public List<AutomationRunQuery> Queries { get; } = [];

    public Task<IReadOnlyList<AutomationRun>> QueryAsync(AutomationRunQuery query, CancellationToken ct)
    {
        Queries.Add(query);
        IReadOnlyList<AutomationRun> result =
        [
            .. Runs.Where(r => query.AutomationId is null || r.AutomationId == query.AutomationId).Take(query.Limit),
        ];
        return Task.FromResult(result);
    }

    public Task<bool> TryInsertAsync(AutomationRun run, CancellationToken ct) => throw new NotSupportedException();

    public Task UpdateAsync(AutomationRun run, CancellationToken ct) => throw new NotSupportedException();

    public Task<AutomationRun?> GetAsync(string runId, CancellationToken ct) => throw new NotSupportedException();

    public Task<int> MarkInterruptedAsync(DateTimeOffset endedAt, string message, CancellationToken ct) => throw new NotSupportedException();

    public Task<int> TrimAsync(int perAutomation, int total, CancellationToken ct) => throw new NotSupportedException();

    public Task<string?> GetStateAsync(string key, CancellationToken ct) => throw new NotSupportedException();

    public Task SetStateAsync(string key, string? value, CancellationToken ct) => throw new NotSupportedException();
}

internal static class AutomationTestData
{
    public static AutomationRun Run(string id, string automationId, AutomationRunStatus status = AutomationRunStatus.Completed) =>
        new(id, automationId, automationId, AutomationTriggerSource.Manual, "occ-" + id, status, new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));

    public static readonly AutomationProjectChoice RepoA = new("p1", "Alpha", true);

    public static readonly AutomationProjectChoice Plain = new("p2", "Notes", false);

    public static readonly AutomationProjectChoice RepoB = new("p3", "Beta", true);

    public static AutomationPreview NoPreview(AutomationTrigger trigger, int count) => new([], null);
}
