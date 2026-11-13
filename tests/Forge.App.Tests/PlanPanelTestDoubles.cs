using Forge.Abstractions;
using Forge.App.ViewModels;

namespace Forge.App.Tests;

/// <summary>
/// An in-memory <see cref="ITodoStore"/> for the plan-panel suites: items, groups, one plan summary and a revision
/// list, with the counters and gates the refresh semantics tests need. Reads may be held with
/// <see cref="GateNextSummaryRead"/> so a test can release them in the order it wants; <see cref="SummaryFailure"/>
/// makes the next read fail. Writes keep the dictionaries consistent and raise <see cref="Changed"/> like the real
/// store, but the plan-panel tests seed state through <see cref="Seed"/>/<see cref="SeedGroup"/> and refresh
/// explicitly, so seeding never wakes the panel on its own.
/// </summary>
internal sealed class FakePlanStore : ITodoStore
{
    private readonly Dictionary<string, TodoItem> items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TodoGroup> groups = new(StringComparer.Ordinal);
    private readonly Queue<TaskCompletionSource> summaryGates = new();

    public event Action<string>? Changed;

    /// <summary>The plan summary the next read returns; null when none was written.</summary>
    public PlanSummary? Summary { get; set; }

    /// <summary>The replaced summaries, in the order the store hands them out (the panel sorts them anyway).</summary>
    public List<PlanRevision> Revisions { get; } = [];

    /// <summary>When set, the next summary read fails with it (an unreadable plan).</summary>
    public Exception? SummaryFailure { get; set; }

    /// <summary>How many times the summary was read; the coalescing tests count this.</summary>
    public int GetPlanSummaryCalls { get; private set; }

    /// <summary>How many gated summary reads finished (after their gate), for waiting on a held read's end.</summary>
    public int SummaryReadsCompleted { get; private set; }

    /// <summary>Holds the next <c>GetPlanSummaryAsync</c> read until the test releases the gate; one gate per read.</summary>
    public void GateNextSummaryRead(TaskCompletionSource gate) => summaryGates.Enqueue(gate);

    public void Seed(TodoItem item) => items[item.Id] = item;

    public void SeedGroup(TodoGroup group) => groups[group.Id] = group;

    public void Remove(string id) => items.Remove(id);

    public void RemoveGroup(string id) => groups.Remove(id);

    public IReadOnlyList<TodoItem> Items() => [.. items.Values];

    public IReadOnlyList<TodoGroup> Groups() => [.. groups.Values];

    public void Emit(string sessionId) => Changed?.Invoke(sessionId);

    public async Task<PlanSummary?> GetPlanSummaryAsync(string sessionId, CancellationToken ct)
    {
        GetPlanSummaryCalls++;
        if (summaryGates.Count > 0)
        {
            await summaryGates.Dequeue().Task.WaitAsync(ct);
            SummaryReadsCompleted++;
        }

        if (SummaryFailure is { } failure)
        {
            throw failure;
        }

        return Summary;
    }

    public Task<PlanSummary> SetPlanSummaryAsync(string sessionId, string text, CancellationToken ct)
    {
        var written = new PlanSummary(sessionId, text, DateTimeOffset.UtcNow);
        Summary = written;
        Emit(sessionId);
        return Task.FromResult(written);
    }

    public Task<IReadOnlyList<PlanRevision>> ListPlanRevisionsAsync(string sessionId, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PlanRevision>>(Revisions.Take(limit).ToList());

    public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TodoItem>>(
            items.Values.Where(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal)).ToList());

    public Task<IReadOnlyList<TodoGroup>> ListGroupsAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TodoGroup>>(
            groups.Values.Where(group => string.Equals(group.SessionId, sessionId, StringComparison.Ordinal)).ToList());

    public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct) =>
        AddAsync(new TodoAddRequest(sessionId, title, note, afterId), ct);

    public Task<TodoItem> AddAsync(TodoAddRequest request, CancellationToken ct)
    {
        var order = request.AfterId is null
            ? items.Values.Count(item => string.Equals(item.SessionId, request.SessionId, StringComparison.Ordinal) &&
                                         string.Equals(item.GroupId, request.GroupId, StringComparison.Ordinal))
            : 0;
        var item = new TodoItem(
            Guid.NewGuid().ToString("n"),
            request.SessionId,
            request.Title,
            TodoStatus.Pending,
            request.Note,
            order,
            DateTimeOffset.UtcNow,
            GroupId: request.GroupId,
            Origin: request.Origin);
        items[item.Id] = item;
        Emit(request.SessionId);
        return Task.FromResult(item);
    }

    public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct) =>
        UpdateAsync(id, title, status, note, step: null, ct);

    public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, TodoStepFields? step, CancellationToken ct)
    {
        if (!items.TryGetValue(id, out var current))
        {
            return Task.FromResult<TodoItem?>(null);
        }

        var next = current with
        {
            Title = title ?? current.Title,
            Status = status ?? current.Status,
            Note = note ?? current.Note,
            CommitSubject = step?.CommitSubject ?? current.CommitSubject,
            Summary = step?.Summary ?? current.Summary,
            Verification = step?.Verification ?? current.Verification,
        };
        items[id] = next;
        Emit(next.SessionId);
        return Task.FromResult<TodoItem?>(next);
    }

    public Task<TodoItem?> SetCommitResultAsync(string id, string? commitId, string? commitError, CancellationToken ct)
    {
        if (!items.TryGetValue(id, out var current))
        {
            return Task.FromResult<TodoItem?>(null);
        }

        var next = current with { CommitId = commitId, CommitError = commitError };
        items[id] = next;
        Emit(next.SessionId);
        return Task.FromResult<TodoItem?>(next);
    }

    public Task<TodoGroup> FindOrCreateGroupAsync(string sessionId, string title, CancellationToken ct)
    {
        var existing = groups.Values.FirstOrDefault(group =>
            string.Equals(group.SessionId, sessionId, StringComparison.Ordinal) &&
            string.Equals(group.Title.Trim(), title.Trim(), StringComparison.Ordinal));
        if (existing is not null)
        {
            return Task.FromResult(existing);
        }

        var created = new TodoGroup(Guid.NewGuid().ToString("n"), sessionId, title, groups.Count, DateTimeOffset.UtcNow);
        groups[created.Id] = created;
        Emit(sessionId);
        return Task.FromResult(created);
    }

    public Task<TodoGroup?> PromoteAsync(string itemId, string? title, CancellationToken ct)
    {
        if (!items.TryGetValue(itemId, out var item))
        {
            return Task.FromResult<TodoGroup?>(null);
        }

        var group = new TodoGroup(itemId, item.SessionId, title ?? item.Title, groups.Count, DateTimeOffset.UtcNow);
        groups[group.Id] = group;
        items[itemId] = item with { GroupId = group.Id };
        Emit(item.SessionId);
        return Task.FromResult<TodoGroup?>(group);
    }

    public Task<bool> RemoveAsync(string id, CancellationToken ct)
    {
        if (!items.Remove(id, out var removed))
        {
            return Task.FromResult(false);
        }

        Emit(removed.SessionId);
        return Task.FromResult(true);
    }

    public Task<bool> MoveAsync(string id, int order, CancellationToken ct)
    {
        if (!items.TryGetValue(id, out var item))
        {
            return Task.FromResult(false);
        }

        items[id] = item with { Order = order };
        Emit(item.SessionId);
        return Task.FromResult(true);
    }
}

/// <summary>
/// The shell's side of the plan panel, scripted per test: the session to read, the hand-off outcome and counters for
/// the three calls the panel makes into the host.
/// </summary>
internal sealed class FakePlanHost : IPlanPanelHost
{
    public PlanPanelSession? PlanSession { get; set; }

    public bool CanOpenPlanPullRequest { get; set; } = true;

    public PlanHandoff HandoffOutcome { get; set; } = PlanHandoff.Sent;

    public List<string> HandoffPrompts { get; } = [];

    public int CloseCalls { get; private set; }

    public int OpenPullRequestCalls { get; private set; }

    public PlanHandoff HandoffToAgent(string prompt)
    {
        HandoffPrompts.Add(prompt);
        return HandoffOutcome;
    }

    public void OpenPlanPullRequest() => OpenPullRequestCalls++;

    public void ClosePlan() => CloseCalls++;
}
