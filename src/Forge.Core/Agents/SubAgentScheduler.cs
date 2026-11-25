using System.Diagnostics;
using System.Globalization;
using System.Text;
using Forge.Abstractions;
using Forge.Core.Prompts;
using Forge.Core.Storage;

namespace Forge.Core.Agents;

/// <summary>Result of one spawn: the child's final text, or the reason it could not run.</summary>
public sealed record SubAgentOutcome(string AgentId, string Text, bool IsError)
{
    /// <summary>
    /// The child was stopped (stop tool or the UI) before it finished: <see cref="Text"/> is the stop notice plus whatever
    /// it had written so far, not a finished answer. A caller that reads a contract out of the text (a review's verdict
    /// line) must not trust it.
    /// </summary>
    public bool Stopped { get; init; }

    /// <summary>
    /// The scheduler picked an idle same-kind child instead of starting a new one, so this spawn continues that child's
    /// conversation (and its prompt cache). The tool reports it to the model: the answer's id is not a fresh agent.
    /// </summary>
    public bool Resumed { get; init; }

    /// <summary>
    /// Why a stopped or failed run ended, in the run's own words (the stop note or the exception). Null for a run that finished;
    /// the receipt's status line has to name it rather than say the reason is unknown.
    /// </summary>
    public string? Reason { get; init; }

}

/// <summary>
/// How one background child ended (<see cref="SubAgentScheduler.StartBackgroundAsync"/>): the parent session it was spawned
/// from, which child finished, and the report its blocking counterpart would have returned as the tool's result. It is what
/// the parent reads when the child ends, as a reply message - never as a turn of its own.
/// </summary>
public sealed record BackgroundChildReport(
    string ParentSessionId,
    string ParentAgentId,
    string AgentId,
    string ChildSessionId,
    string Kind,
    string TaskLabel,
    SessionReplyStatus Status,
    string? Reason,
    string Report);

/// <summary>Who drives an instance: the model (<c>spawn_agent</c> and its control tools) or the user (a session the user opened under a root).</summary>
public enum AgentOwner
{
    Model,
    User,
}

/// <summary>
/// Owns sub-agent instances: id allocation, the run gate per instance, the depth limit and the control surface
/// (<see cref="ISubAgentControl"/>) the parent tools and the UI stop button share. Each spawn is one run with
/// its own cancellation source, so stopping a child never reaches the caller or its siblings.
/// </summary>
public sealed class SubAgentScheduler : ISubAgentControl
{
    private const int TitleLimit = 80;
    private const string ParentCancelled = "parent cancelled";
    private const string StoppedBeforeStart = "stopped before it started";

    /// <summary>A user-driven session sits directly under the root session; its own spawns are the only ones below it.</summary>
    private const int UserSessionDepth = 1;

    private readonly AgentCatalog catalog;
    private readonly PromptLayers layers;
    private readonly IPromptComposer composer;
    private readonly ISessionStore? store;
    private readonly ISessionCatalog? sessions;
    private readonly AgentOptions options;
    private readonly Dictionary<string, AgentInstance> instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentOwner> owners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubAgentRun> runs = new(StringComparer.Ordinal);
    private readonly HashSet<string> finished = new(StringComparer.Ordinal);

    /// <summary>
    /// The background runs (<see cref="StartBackgroundAsync"/>) still in flight, by agent id, with the parent session each was
    /// spawned from. A background child is not linked to the turn it was spawned from, so this map - not a caller's token - is
    /// what a stop and a host shutdown walk; the session is what makes a stop scoped (one agent session's tab stops its own
    /// children, a root's tab stops the whole tree). Guarded by <see cref="gate"/>.
    /// </summary>
    private readonly Dictionary<string, string> backgroundRuns = new(StringComparer.Ordinal);

    /// <summary>
    /// Bumped by every stop: one number for all sessions (teardown, a root's whole-tree stop) and one per parent session (an agent
    /// session's own stop). A spawn that read them before its (awaited) preparation can tell afterwards that a stop covering it ran
    /// meanwhile, and ends itself instead of starting a run the stop never saw.
    /// </summary>
    private long backgroundEpochAll;

    private readonly Dictionary<string, long> backgroundEpochs = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs a stop named before their <see cref="SubAgentRun"/> existed, so they are stopped the moment they attach one. Guarded by
    /// <see cref="gate"/>.
    /// </summary>
    private readonly HashSet<string> backgroundStopPending = new(StringComparer.Ordinal);


    /// <summary>
    /// Set by <see cref="CloseBackgroundRuns"/> only (the runtime is being torn down): this host then takes no new background
    /// child at all. An ordinary stop does not set it - the session keeps working, and its next turn may spawn again.
    /// </summary>
    private bool backgroundClosed;

    /// <summary>
    /// When each child's last run ended, for the reuse window only: a child is a candidate while its last run ended less
    /// than <see cref="AgentOptions.SubAgentReuseWindow"/> ago. Kept beside <see cref="finished"/> rather than on the
    /// instance because it is scheduling state - the instance itself does not care when it last ran.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> lastFinishedAt = new(StringComparer.Ordinal);

    /// <summary>
    /// Children a <c>reuse: false</c> spawn created - an automation's child. Its run is not the parent conversation's work,
    /// so the scheduler never hands it a later <c>spawn_agent</c> task on its own; an explicit <c>resume_id</c> still can.
    /// </summary>
    private readonly HashSet<string> notReusable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> counters = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    /// <summary>Where a child's id, kind, model and run end outlive the process; null when the store has no such table.</summary>
    private readonly ISubAgentStateStore? stateStore;

    public SubAgentScheduler(
        AgentCatalog catalog,
        PromptLayers layers,
        IPromptComposer composer,
        ISessionStore? store,
        AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(options);
        this.catalog = catalog;
        this.layers = layers;
        this.composer = composer;
        this.store = store;
        stateStore = store as ISubAgentStateStore;
        sessions = SessionCatalogs.From(store);
        this.options = options;
        ModelControl = new OwnerScopedControl(this);
    }

    /// <summary>
    /// The control surface the model's tools get: an instance the user owns does not exist for it (<c>NotFound</c>, the same
    /// answer an unknown id gets), while <see cref="ISubAgentControl"/> on the host stays the full surface the UI stop button
    /// uses for both owners.
    /// </summary>
    public ISubAgentControl ModelControl { get; }

    internal Func<AgentInstance, ChatMessage, IAgentEventSink, CancellationToken, Task<AgentRunResult>> Runner { get; set; } =
        static (_, _, _, _) => Task.FromException<AgentRunResult>(new InvalidOperationException("No sub-agent runner is configured."));

    public IReadOnlyList<AgentDefinition> Kinds => catalog.All;

    public IReadOnlyList<AgentInstance> Instances
    {
        get
        {
            lock (gate)
            {
                return [.. instances.Values.OrderBy(static instance => instance.Id, StringComparer.Ordinal)];
            }
        }
    }

    public bool TryGetInstance(string id, out AgentInstance instance)
    {
        lock (gate)
        {
            return instances.TryGetValue(id, out instance!);
        }
    }

    /// <summary>Who registered <paramref name="id"/>; <see cref="AgentOwner.Model"/> for ids this scheduler never handed out.</summary>
    public AgentOwner OwnerOf(string id)
    {
        lock (gate)
        {
            return owners.TryGetValue(id, out var owner) ? owner : AgentOwner.Model;
        }
    }

    /// <summary>
    /// Takes over the living children of a scheduler whose host was replaced for the same session: a prompt reload builds a
    /// new <see cref="AgentHost"/>, and its scheduler would otherwise start from an empty roster and hand out fresh children
    /// of kinds that are still sitting idle. The instances, their ownership, finish times and not-reusable marks move here,
    /// and the id counters move with them so a new child cannot take an id a living one still holds.
    /// <para>A child still running stays behind: its run ends, and reports, in the scheduler that started it (a reload refuses
    /// to rebuild while any child runs, so this is the defensive half of the same rule).</para>
    /// </summary>
    public void TakeOverFrom(SubAgentScheduler previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (ReferenceEquals(previous, this))
        {
            return;
        }

        lock (previous.gate)
        {
            lock (gate)
            {
                var taken = new List<string>(previous.instances.Count);
                foreach (var (id, instance) in previous.instances)
                {
                    if (instance.IsAcquired)
                    {
                        continue;
                    }

                    taken.Add(id);
                    instances[id] = instance;
                    if (previous.owners.TryGetValue(id, out var owner))
                    {
                        owners[id] = owner;
                    }

                    if (previous.finished.Contains(id))
                    {
                        finished.Add(id);
                    }

                    if (previous.lastFinishedAt.TryGetValue(id, out var finishedAt))
                    {
                        lastFinishedAt[id] = finishedAt;
                    }

                    if (previous.notReusable.Contains(id))
                    {
                        notReusable.Add(id);
                    }
                }

                foreach (var (prefix, current) in previous.counters)
                {
                    counters[prefix] = Math.Max(current, counters.GetValueOrDefault(prefix));
                }

                // The replaced host is going away, so it must not answer for an id this scheduler now drives.
                foreach (var id in taken)
                {
                    previous.instances.Remove(id);
                    previous.owners.Remove(id);
                    previous.finished.Remove(id);
                    previous.lastFinishedAt.Remove(id);
                    previous.notReusable.Remove(id);
                }
            }
        }
    }

    public SubAgentControlResult Steer(string agentId, string instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        return TryGetRun(agentId, out var run) ? run.Steer(instruction) : Missing(agentId);
    }

    public SubAgentControlResult FollowUp(string agentId, string instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        return TryGetRun(agentId, out var run) ? run.FollowUp(instruction) : Missing(agentId);
    }

    public SubAgentControlResult Stop(string agentId, string? reason) =>
        TryGetRun(agentId, out var run) ? run.Stop(reason) : Missing(agentId);

    /// <summary>
    /// Why an agent without a <c>tools</c> list is not started: it would receive every tool, which is rarely what its author meant,
    /// and a sub-agent sees the whole tool array as its cacheable prefix. The text tells the model what to ask the user and how the
    /// fix reaches the session (kinds are a per-host snapshot, so an edited file applies from the next session start or after a hot reload).
    /// </summary>
    internal static string MissingToolsMessage(string name) =>
        $"Agent '{name}' declares no usable `tools` list (missing or empty), so it was not started: without one it would get every tool. "
        + "Ask the user (ask_user) which role it should have, then add a `tools:` line to the frontmatter of its agent file "
        + "(`<workspace>/.forge/agents/<name>.md` you can edit; a file under `~/.forge/agents/` is outside the workspace, so give the user "
        + "the exact line to add). Suggested lists: read-only scout or planner: read, grep, find, ls; implementer: read, grep, find, ls, edit, write, bash; "
        + "reviewer: read, grep, find, ls, git_diff. Agent files are read when a session starts, so the change applies from the next session, or from the next message once the user presses the reload-prompts button in the title bar. "
        + "An agent contributed by a plugin cannot be edited here. For this task use a built-in kind: Scout, Plan, Worker or Reviewer.";

    /// <summary>
    /// A child is meant to stay within its parent's tools, but this is not enforced (the child's own definition decides).
    /// When a sub-agent parent's declared list lacks tools the child declares, say so to the user instead of staying silent.
    /// A root agent's surface is not known here, so only a parent that is itself a sub-agent is compared.
    /// </summary>
    private void WarnWhenBroaderThanParent(AgentDefinition child, string parentId, IAgentEventSink sink)
    {
        if (!TryGetInstance(parentId, out var parent) || parent.Definition.ToolNames is not { Count: > 0 } allowed)
        {
            return;
        }

        var beyond = child.ToolNames!.Where(name => !allowed.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (beyond.Length > 0)
        {
            sink.Emit(new AgentNotice(parentId, $"Sub-agent '{child.Name}' has tools its parent '{parent.Definition.Name}' does not declare: {string.Join(", ", beyond)}."));
        }
    }

    private SubAgentControlResult Missing(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return SubAgentControlResult.NotFound;
        }

        lock (gate)
        {
            return finished.Contains(agentId) ? SubAgentControlResult.AlreadyFinished : SubAgentControlResult.NotFound;
        }
    }

    /// <summary>
    /// Runs a sub-agent and waits for it. This is the path for work whose result the caller needs before it can go on - a step or a
    /// change review (<see cref="Tools.RequestReviewTool"/>), a nested spawn inside another child. <c>spawn_agent</c> never uses it:
    /// a spawn from the model is always asynchronous (see <see cref="StartBackgroundAsync"/>), so a parent's turn is never parked on
    /// a child and the user can always hand it something else.
    /// </summary>
    public async Task<SubAgentOutcome> SpawnAsync(
        string kind,
        string task,
        string? resumeId,
        ToolContext context,
        IAgentEventSink sink,
        string parentId,
        CancellationToken ct,
        AgentOwner owner = AgentOwner.Model,
        ModelSettings? model = null,
        bool reuse = true)
    {
        var prepared = await PrepareAsync(kind, task, resumeId, context, sink, parentId, ct, owner, model, reuse).ConfigureAwait(false);
        if (prepared.Refusal is { } refusal)
        {
            return refusal;
        }

        return await DriveToEndAsync(prepared.Instance!, task, sink, parentId, prepared.Watch, ct, prepared.Reused)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a spawn that does <b>not</b> wait for the child: the parent's tool call returns at once and goes on working, and the
    /// child's finish is reported to <see cref="BackgroundReports"/> instead of coming back as the tool's result. The run is not
    /// linked to the caller's token - that token dies with the turn the spawn came from, which is exactly what must not happen
    /// to a child that is meant to outlive it - so it ends by itself, or through <see cref="Stop"/>. Everything a blocking spawn
    /// does (depth limit, catalog, tool list, model override, reuse, slot pool, session row, start/finish events) happens here
    /// first and unchanged; only the waiting moves out.
    /// </summary>
    public async Task<BackgroundSpawn> StartBackgroundAsync(
        string kind,
        string task,
        string? resumeId,
        ToolContext context,
        IAgentEventSink sink,
        string parentId,
        CancellationToken ct = default,
        AgentOwner owner = AgentOwner.Model,
        ModelSettings? model = null,
        bool reuse = true)
    {
        // A stop of this parent session is captured as an epoch: the preparation below waits on the store, so a stop may run while
        // this spawn has no run to stop yet. The check when it registers is what refuses it then. Keyed by parent session, because a
        // stop of one session must not cancel another session's spawn; the all-sessions number is the teardown's and a root's.
        long epochAll;
        long epochSession;
        lock (gate)
        {
            if (backgroundClosed)
            {
                return new BackgroundSpawn(null, StoppedStarting(resumeId));
            }

            epochAll = backgroundEpochAll;
            epochSession = backgroundEpochs.GetValueOrDefault(context.SessionId);
        }

        var prepared = await PrepareAsync(kind, task, resumeId, context, sink, parentId, ct, owner, model, reuse)
            .ConfigureAwait(false);
        if (prepared.Refusal is { } refusal)
        {
            return new BackgroundSpawn(null, refusal);
        }

        var instance = prepared.Instance!;
        bool stale;
        lock (gate)
        {
            // The preparation waited on the store, so a stop - or the cancellation of the tool call that asked for this - may have
            // happened while this spawn had no run to stop. The run is in the map a stop walks from here on, and one that already
            // happened is honoured by refusing to start at all.
            stale = ct.IsCancellationRequested
                || backgroundClosed
                || epochAll != backgroundEpochAll
                || epochSession != backgroundEpochs.GetValueOrDefault(context.SessionId);
            if (!stale)
            {
                backgroundRuns[instance.Id] = context.SessionId;
            }
        }

        if (stale)
        {
            // Nothing was run. The instance keeps its gate free and is barred from the reuse pool, so a later spawn cannot pick up a
            // child that never had a turn; its (empty) session row stays, the way any spawn that failed before its first turn leaves one.
            lock (gate)
            {
                notReusable.Add(instance.Id);
            }

            instance.Release();
            return new BackgroundSpawn(null, StoppedStarting(resumeId));
        }
        // No await between the registration above and this call, and CompleteInBackgroundAsync reaches DriveAsync (which attaches
        // the run the stop control needs) before its first await: a stop that saw the registration can always stop it.
        // The caller's cancellation is live only across the start itself: if it fires before this method has answered, the child is
        // stopped again (the model never learns its id, so nothing could ever steer or stop it). Once the start is reported, the
        // background run belongs to the session, not to that turn, and this lease is gone.
        using var lease = ct.Register(static state =>
        {
            var (owner, id) = ((SubAgentScheduler, string))state!;
            owner.Stop(id, "the tool call that started it was cancelled");
        }, (this, instance.Id));

        _ = CompleteInBackgroundAsync(instance, task, sink, parentId, prepared.Watch, prepared.Reused, context);

        if (!ct.IsCancellationRequested)
        {
            return new BackgroundSpawn(instance, Refusal: null);
        }

        // The cancellation landed after the run was registered but before this call answers: the model will never learn the child's
        // id, so it must not keep running. The lease may have stopped it already (the run attaches synchronously); Stop is idempotent.
        Stop(instance.Id, "the tool call that started it was cancelled");
        return new BackgroundSpawn(null, StoppedStarting(resumeId));
    }

    /// <summary>
    /// The child of one <see cref="StartBackgroundAsync"/>: what the spawn's tool call names (its id and session) and nothing
    /// else - it is still running when the call returns. <see cref="Refusal"/> is set instead when the spawn never started.
    /// </summary>
    public sealed record BackgroundSpawn(AgentInstance? Instance, SubAgentOutcome? Refusal)
    {
        public string AgentId => Instance?.Id ?? Refusal?.AgentId ?? string.Empty;

        public string ChildSessionId => Instance?.SessionId ?? string.Empty;
    }

    /// <summary>
    /// Where a background child's end is reported: the parent session it belongs to, the child that finished, and the report its
    /// blocking counterpart would have returned as the tool's result. Null drops the report (a scheduler without a host), which is
    /// what a bench or a bare unit test looks like.
    /// </summary>
    public Func<BackgroundChildReport, ValueTask>? BackgroundReports { get; set; }

    /// <summary>
    /// Stops the background children spawned from <paramref name="parentSessionId"/> - every one of them when it is null, which is
    /// what a root's stop means (the whole tree). A background run no longer dies with the turn that started it, so this registry
    /// walk is what ends it; the same control surface the model's stop tool and a sub-agent's own stop button use, so each run
    /// ends the way a stop always ends (partial text kept and reported).
    /// <para>The host keeps working afterwards: a new turn may spawn again. Only <see cref="CloseBackgroundRuns"/> refuses.</para>
    /// <para>Deliberately driven from this registry rather than from the app's sub-agent rows: a background child can be running
    /// before its start event has travelled through the frame coalescer, and a stop that trusted the rows would leave exactly
    /// that child behind.</para>
    /// </summary>
    public int StopBackgroundRuns(string? parentSessionId, string? reason = null)
    {
        string[] ids;
        lock (gate)
        {
            if (parentSessionId is null)
            {
                backgroundEpochAll++;
            }
            else
            {
                backgroundEpochs[parentSessionId] = backgroundEpochs.GetValueOrDefault(parentSessionId) + 1;
            }

            ids = [.. backgroundRuns
                .Where(entry => parentSessionId is null || string.Equals(entry.Value, parentSessionId, StringComparison.Ordinal))
                .Select(static entry => entry.Key)];
            foreach (var id in ids)
            {
                // A run whose SubAgentRun is not attached yet takes the stop when it is (see Attach).
                backgroundStopPending.Add(id);
            }
        }

        var stopped = 0;
        foreach (var id in ids)
        {
            if (Stop(id, reason) is SubAgentControlResult.Accepted or SubAgentControlResult.NotFound)
            {
                stopped++;
            }
        }

        return stopped;
    }

    /// <summary>
    /// The runtime is going away: every background child stops and this host takes no new one. Called only from the teardown of a
    /// session runtime - an ordinary stop goes through <see cref="StopBackgroundRuns"/>, which leaves the host usable.
    /// </summary>
    public int CloseBackgroundRuns(string? reason = null)
    {
        lock (gate)
        {
            backgroundClosed = true;
        }

        return StopBackgroundRuns(parentSessionId: null, reason);
    }

    private static SubAgentOutcome StoppedStarting(string? resumeId) => new(
        resumeId ?? string.Empty,
        "This session stopped while the background sub-agent was starting, so it was not run.",
        IsError: true);

    /// <summary>
    /// The run itself, once an instance has been acquired: drive it, hand any leftover interjections to the store, and free the
    /// instance for the next turn. Shared by the tool's spawn (which never waits) and by the caller that waits for its result - a
    /// step or change review - so both drive and clean up identically.
    /// </summary>
    private async Task<SubAgentOutcome> DriveToEndAsync(
        AgentInstance instance,
        string task,
        IAgentEventSink sink,
        string parentId,
        Stopwatch watch,
        CancellationToken ct,
        bool reused)
    {
        try
        {
            var outcome = await DriveAsync(instance, ChatMessage.User(task), task, sink, parentId, watch, ct).ConfigureAwait(false);
            return reused ? outcome with { Resumed = true } : outcome;
        }
        catch (OperationCanceledException)
        {
            // The caller's token was cancelled: that is not the child failing, and the caller (the tool loop) turns it into the
            // "cancelled by the user" result every other tool gets. Swallowing it here would record a review as failed when the
            // user only stopped it.
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new SubAgentOutcome(instance.Id, $"Sub-agent '{instance.Id}' failed: {exception.Message}", IsError: true)
            {
                Reason = exception.Message,
            };
        }
        finally
        {
            // Order matters: the leftovers are taken while this run still owns the instance (a new turn cannot start under the
            // write), and the gate is released only after they are in the history.
            await PersistLeftoverInterjectionsAsync(instance).ConfigureAwait(false);
            instance.Release();
        }
    }

    /// <summary>One background child's run: drive it, then say how it ended.</summary>
    private async Task CompleteInBackgroundAsync(
        AgentInstance instance,
        string task,
        IAgentEventSink sink,
        string parentId,
        Stopwatch watch,
        bool reused,
        ToolContext context)
    {
        try
        {
            // A background run's token is nobody's: DriveAsync converts a stop into a Stopped outcome, so the only cancellation
            // that can surface here is a run torn down under it. Reported as stopped either way, never as an unobserved fault.
            var outcome = await DriveToEndAsync(instance, task, sink, parentId, watch, CancellationToken.None, reused)
                .ConfigureAwait(false);
            await ReportAsync(instance, outcome, task, parentId, context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Trace.TraceWarning($"background sub-agent '{instance.Id}' was cancelled while reporting");
        }
        finally
        {
            lock (gate)
            {
                backgroundRuns.Remove(instance.Id);
                backgroundStopPending.Remove(instance.Id);
            }
        }
    }

    /// <summary>How one finished child is reported to the session that spawned it (see <see cref="BackgroundReports"/>).</summary>
    private async Task ReportAsync(
        AgentInstance instance,
        SubAgentOutcome outcome,
        string task,
        string parentId,
        ToolContext context)
    {
        if (BackgroundReports is not { } reports)
        {
            return;
        }

        var report = new BackgroundChildReport(
            context.SessionId,
            parentId,
            instance.Id,
            instance.SessionId,
            instance.Definition.Name,
            task,
            StatusOf(outcome),
            outcome.Reason,
            outcome.Text);
        try
        {
            await reports(report).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"background sub-agent report failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Persists the interjections a model-spawned child's run never reached a boundary for. The root's tab hands its own leftovers
    /// back to the queue and a user-driven session's tab does the same; a child of the model's own has no tab and no guaranteed
    /// next turn, so an interjected message would otherwise sit in memory and vanish with the instance. The store is the one place
    /// the next turn - or a reopened session - reads it from.
    /// </summary>
    internal async Task PersistLeftoverInterjectionsAsync(AgentInstance instance)
    {
        // A model-owned run (the root, a model-spawned child) has no tab to hand its leftovers back to, so they are stored here. A
        // user-driven session's tab does that itself - but only when an app is behind this host at all; headless, nobody does, and
        // the same store write is what keeps the message.
        var modelOwned = OwnerOf(instance.Id) == AgentOwner.Model;
        if (store is null || (!modelOwned && (ExternalHandBack?.Invoke() ?? false)))
        {
            return;
        }

        var leftovers = instance.TakeInterjections();
        for (var index = 0; index < leftovers.Count; index++)
        {
            var interjection = leftovers[index];
            var message = new ChatMessage(MessageRole.User, interjection.Content);
            try
            {
                var seq = await store.AppendAsync(instance.SessionId, message, null, CancellationToken.None).ConfigureAwait(false);
                instance.Add(message, seq);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The store could not take it: this one and everything behind it go back to the inbox in their original order, so
                // nothing is dropped and nothing overtakes what was typed before it.
                for (var back = leftovers.Count - 1; back >= index; back--)
                {
                    instance.Requeue(leftovers[back]);
                }

                Trace.TraceWarning($"a leftover interjection could not be stored: {exception.Message}");
                return;
            }
        }
    }

    /// <summary>
    /// Whether an app behind this host hands a user-driven session's leftover interjections back to its own queue (the tab's job).
    /// Set by the host to the same lazy lookup its delivery seam uses; null (a scheduler on its own) means nobody does.
    /// </summary>
    internal Func<bool>? ExternalHandBack { get; set; }

    /// <summary>How one finished background child ended, in the terms the reply receipt's status line uses.</summary>
    private static SessionReplyStatus StatusOf(SubAgentOutcome outcome) => outcome switch
    {
        { Stopped: true } => SessionReplyStatus.Stopped,
        { IsError: true } => SessionReplyStatus.Failed,
        _ => SessionReplyStatus.Completed,
    };

    /// <summary>
    /// Everything both kinds of spawn do before a turn runs: validation, the instance to run (a resumed one, a reused idle one, or
    /// a freshly created session), and the gate held for the drive. A refusal is returned instead of throwing, exactly as it was
    /// returned from <see cref="SpawnAsync"/> before the waiting moved out of it.
    /// </summary>
    private async Task<Prepared> PrepareAsync(
        string kind,
        string task,
        string? resumeId,
        ToolContext context,
        IAgentEventSink sink,
        string parentId,
        CancellationToken ct,
        AgentOwner owner,
        ModelSettings? model,
        bool reuse)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(task);

        if (context.AgentDepth >= options.MaxSubAgentDepth)
        {
            return new SubAgentOutcome(
                resumeId ?? string.Empty,
                $"Sub-agents cannot be spawned at depth {context.AgentDepth}: the limit is {options.MaxSubAgentDepth}.",
                IsError: true);
        }

        if (!catalog.TryGet(kind, out var definition))
        {
            return new SubAgentOutcome(resumeId ?? string.Empty, $"Unknown agent kind '{kind}'.", IsError: true);
        }

        if (definition.ToolNames is not { Count: > 0 })
        {
            return new SubAgentOutcome(resumeId ?? string.Empty, MissingToolsMessage(definition.Name), IsError: true);
        }

        if (model is not null)
        {
            // A resume continues an instance that already carries its model: changing it would swap the prefix under a
            // conversation that is being continued, so the override is refused rather than silently dropped.
            if (resumeId is not null)
            {
                return new SubAgentOutcome(
                    resumeId,
                    "A resumed sub-agent keeps the model it started with; start a new spawn to change it.",
                    IsError: true);
            }

            definition = definition with { Model = model };
        }

        WarnWhenBroaderThanParent(definition, parentId, sink);

        AgentInstance? instance = null;
        var reused = false;
        var watch = Stopwatch.StartNew();
        try
        {
            if (resumeId is not null)
            {
                if (!TryGetInstance(resumeId, out instance))
                {
                    return new SubAgentOutcome(resumeId, $"No sub-agent with id '{resumeId}' exists.", IsError: true);
                }

                // A user-owned session is not the model's to continue: it answers exactly like an unknown id.
                if (owner == AgentOwner.Model && OwnerOf(resumeId) != AgentOwner.Model)
                {
                    return new SubAgentOutcome(resumeId, $"No sub-agent with id '{resumeId}' exists.", IsError: true);
                }

                if (!string.Equals(instance.Definition.Name, definition.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return new SubAgentOutcome(
                        resumeId,
                        $"Sub-agent '{resumeId}' is a {instance.Definition.Name}, not a {definition.Name}.",
                        IsError: true);
                }

                if (!instance.TryAcquire())
                {
                    return new SubAgentOutcome(resumeId, $"Sub-agent '{resumeId}' is still running.", IsError: true);
                }
            }

            if (instance is null && resumeId is null && reuse && owner == AgentOwner.Model && model is null)
            {
                // Nobody named an id, so the scheduler may hand the task to a child that just did this same work: its
                // context and its cached prompt prefix are exactly what a fresh child would have to rebuild.
                foreach (var candidate in Reusable(definition.Name, task))
                {
                    if (!candidate.TryAcquire())
                    {
                        continue;
                    }

                    instance = candidate;
                    reused = true;
                    break;
                }
            }

            if (instance is null)
            {
                var sessionId = await CreateSessionAsync(definition, task, context, ct).ConfigureAwait(false);
                instance = NewInstance(definition, sessionId, context.WorkingDirectory, context.AgentDepth + 1, owner);
                Register(instance, owner);
                if (!reuse)
                {
                    // The caller opted out (an automation's child, which is nobody's next task): its child must not be picked
                    // up by a later spawn either.
                    lock (gate)
                    {
                        notReusable.Add(instance.Id);
                    }
                }

                // The id and the model it starts with are what the next process needs to continue this child at all. No caller
                // token: a stop that arrives now would otherwise abandon a child whose session row is already written, and the
                // bookkeeping must not turn a spawn into a failure.
                await SaveStateAsync(instance, reuse, null, CancellationToken.None).ConfigureAwait(false);

                instance.TryAcquire();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The run never started, so no finish event is due; report it the way a failed run would.
            var id = instance?.Id ?? resumeId ?? string.Empty;
            return new SubAgentOutcome(id, $"Sub-agent '{id}' failed: {exception.Message}", IsError: true);
        }

        return new Prepared(instance, Refusal: null, reused, watch);
    }

    /// <summary>
    /// What <see cref="PrepareAsync"/> hands back: the instance whose gate is held for the run, or the refusal that ended the
    /// spawn before it started. A bare <see cref="SubAgentOutcome"/> converts, so a refusal reads exactly as it did when the
    /// whole spawn lived in one method.
    /// </summary>
    private readonly record struct Prepared(AgentInstance? Instance, SubAgentOutcome? Refusal, bool Reused, Stopwatch Watch)
    {
        public static implicit operator Prepared(SubAgentOutcome refusal) => new(null, refusal, Reused: false, Stopwatch.StartNew());
    }

    /// <summary>
    /// The children a spawn of <paramref name="kind"/> may continue, best first: same kind, the model's own, idle, and with
    /// their last run ended inside the reuse window; ranked by <see cref="SubAgentAffinity"/> over the whole prose of their
    /// history, then by how recently they ran, and only above the threshold - an unrelated task starts a fresh child rather
    /// than joining somebody else's conversation. The gate is still taken by the caller, so two spawns racing for one child
    /// cannot both win it.
    /// </summary>
    private List<AgentInstance> Reusable(string kind, string task)
    {
        var now = options.SubAgentReuseClock.GetUtcNow();
        var eligible = new List<(AgentInstance Instance, DateTimeOffset FinishedAt, AgentMessage[] History)>();
        lock (gate)
        {
            foreach (var instance in instances.Values)
            {
                if (!string.Equals(instance.Definition.Name, kind, StringComparison.OrdinalIgnoreCase)
                    || instance.IsAcquired
                    || notReusable.Contains(instance.Id)
                    || (owners.TryGetValue(instance.Id, out var owner) && owner != AgentOwner.Model)
                    || !lastFinishedAt.TryGetValue(instance.Id, out var finishedAt)
                    || now - finishedAt > options.SubAgentReuseWindow)
                {
                    continue;
                }

                // Copied under the instance's own lock and scored outside the scheduler gate: walking a whole conversation is
                // the expensive half, and every run start and end waits on that gate.
                eligible.Add((instance, finishedAt, instance.SnapshotHistory()));
            }
        }

        var candidates = new List<(AgentInstance Instance, int Score, DateTimeOffset FinishedAt)>();
        foreach (var (instance, finishedAt, history) in eligible)
        {
            int score;
            try
            {
                score = SubAgentAffinity.Score(task, SubAgentAffinity.ReadHistory(history));
            }
            catch (Exception exception)
            {
                // Reading text is a heuristic over whatever a conversation happens to contain: a shape it cannot parse is one
                // more reason not to continue that child, never a reason for the spawn of a new one to fail.
                Trace.TraceWarning($"sub-agent '{instance.Id}' was not scored: {exception.GetType().Name}: {exception.Message}");
                continue;
            }

            if (score >= options.SubAgentReuseThreshold)
            {
                candidates.Add((instance, score, finishedAt));
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        candidates.Sort(static (left, right) => left.Score != right.Score
            ? right.Score.CompareTo(left.Score)
            : right.FinishedAt.CompareTo(left.FinishedAt));
        var ordered = new List<AgentInstance>(candidates.Count);
        foreach (var candidate in candidates)
        {
            ordered.Add(candidate.Instance);
        }

        return ordered;
    }

    /// <summary>
    /// Starts a user-driven child session (an "Agent session" the user opened under the root session) and runs its first
    /// turn. Everything a model spawn uses is shared: the tree's run slots and queue order, the approval path, the
    /// cancellation table and the rate limits. Only ownership differs - the session row records
    /// <see cref="StorageSchema.ChildDriverUser"/> with <paramref name="rootSessionId"/> as its parent, and the instance is
    /// registered as <see cref="AgentOwner.User"/> under the <c>session-&lt;n&gt;</c> namespace, so the model's tools answer
    /// for its id exactly like for one that never existed. The definition is the caller's choice (the app offers the kinds
    /// and models the user picked); this method never looks a kind up in the catalog.
    /// <para><paramref name="message"/> is the first turn's message as the user sent it - text plus its attachments - so
    /// the images of a first message reach the child exactly like those of every later one; its typed text is what the
    /// started event names as the task and what the session row stores on a session without an explicit title.</para>
    /// </summary>
    public async Task<SubAgentOutcome> StartUserSessionAsync(
        AgentDefinition definition,
        ChatMessage message,
        string? sessionTitle,
        string rootSessionId,
        string workingDirectory,
        IAgentEventSink sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var task = TextOf(message);
        var watch = Stopwatch.StartNew();
        var context = new ToolContext(rootSessionId, workingDirectory, 0);
        var sessionId = await CreateSessionAsync(definition, task, context, ct, sessionTitle, StorageSchema.ChildDriverUser)
            .ConfigureAwait(false);
        var instance = NewInstance(definition, sessionId, context.WorkingDirectory, UserSessionDepth, AgentOwner.User);
        Register(instance, AgentOwner.User);

        // A session that was just registered has never run, so its gate is free.
        instance.TryAcquire();
        try
        {
            return await DriveAsync(instance, message, task, sink, rootSessionId, watch, ct).ConfigureAwait(false);
        }
        finally
        {
            instance.Release();
        }
    }

    /// <summary>
    /// Runs one more turn of a user-driven session with the same lifecycle every child run has: the run joins the
    /// cancellation table (so <see cref="ISubAgentControl.Stop"/> reaches it), queues for a tree slot, and ends with exactly
    /// one <see cref="AgentSubAgentFinished"/> carrying this turn's usage and elapsed time. An instance this scheduler did
    /// not register as a user session is not driven at all: the sink gets one <see cref="AgentFailed"/> saying why.
    /// <paramref name="parentId"/> is what the started event names as the parent, or null to announce no start.
    /// </summary>
    public async Task<SubAgentOutcome> RunUserTurnAsync(
        AgentInstance instance,
        ChatMessage message,
        string? parentId,
        IAgentEventSink sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(sink);
        if (!IsUserSession(instance))
        {
            return Refuse(instance.Id, $"Agent '{instance.Id}' is not a user-driven session of this host, so it was not run.", sink);
        }

        if (!instance.TryAcquire())
        {
            return Refuse(instance.Id, $"Agent session '{instance.Id}' is still running.", sink);
        }

        try
        {
            return await DriveAsync(instance, message, TextOf(message), sink, parentId, Stopwatch.StartNew(), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            instance.Release();
        }
    }

    /// <summary>
    /// Rebuilds a stored user-driven session as an instance of this host, with the history the compactor leaves
    /// (<see cref="SessionHistory.LoadAsync"/>, the path <see cref="AgentHost.ResumeRootAsync"/> takes) and registered as
    /// <see cref="AgentOwner.User"/> under the <c>session-&lt;n&gt;</c> namespace. Resuming a session this scheduler already
    /// holds returns that instance instead of building a second agent for it. Nothing announces a start here: after a
    /// restart the shell places the session from this instance, and each turn announces its own run.
    /// </summary>
    public async Task<AgentInstance> ResumeUserSessionAsync(
        string sessionId,
        string workingDirectory,
        AgentDefinition definition,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(definition);
        if (FindUserSession(sessionId) is { } existing)
        {
            return existing;
        }

        var instance = NewInstance(definition, sessionId, Path.GetFullPath(workingDirectory), UserSessionDepth, AgentOwner.User);
        if (RegisterUserSession(sessionId, instance) is { } raced)
        {
            return raced;
        }

        if (store is { } sessionStore)
        {
            instance.ReplaceHistory(await SessionHistory.LoadAsync(sessionStore, sessionId, ct).ConfigureAwait(false));
        }

        return instance;
    }

    /// <summary>
    /// Forgets the user-driven session instance <paramref name="agentId"/> names, without touching its run. One caller case
    /// exists: the user changed the agent kind or the model of their own agent session, and the app then resumes that session
    /// from the store with the new definition - <see cref="ResumeUserSessionAsync"/> would otherwise hand the instance it
    /// already holds right back and the change would never take effect. Nothing of the conversation is lost (it is in the
    /// store), the rebuilt instance has a new prompt prefix so the provider's cache misses once, and a turn that is already
    /// running keeps its instance and its cancellation entry, so the stop button still reaches it. False for an id this
    /// scheduler does not hold as a user-driven session (a model-spawned child, or one that never existed).
    /// </summary>
    public bool ForgetUserSession(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return false;
        }

        lock (gate)
        {
            if (!owners.TryGetValue(agentId, out var owner) || owner != AgentOwner.User)
            {
                return false;
            }

            owners.Remove(agentId);
            finished.Remove(agentId);
            return instances.Remove(agentId);
        }
    }

    /// <summary>
    /// Whether <paramref name="instance"/> is a user-driven session this scheduler registered - the same instance, not just
    /// an id - so an instance built elsewhere (or a model-spawned child whose id was copied) is never driven as one.
    /// </summary>
    public bool IsUserSession(AgentInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (gate)
        {
            return instances.TryGetValue(instance.Id, out var registered)
                   && ReferenceEquals(registered, instance)
                   && owners.TryGetValue(instance.Id, out var owner)
                   && owner == AgentOwner.User;
        }
    }

    /// <summary>The user-driven session instance <paramref name="sessionId"/> names - running or idle - if this scheduler holds it.</summary>
    public bool TryGetUserSession(string sessionId, out AgentInstance instance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (gate)
        {
            instance = FindUserSessionLocked(sessionId)!;
            return instance is not null;
        }
    }

    /// <summary>
    /// Stops every run of a user-driven session this scheduler owns - queued or running - and returns how many were stopped.
    /// Runs the model owns are untouched, and so is the root session's own turn: the shell stops that one and calls this for
    /// the sessions under it when the user stops the root.
    /// </summary>
    public int StopUserSessions(string? reason = null)
    {
        List<SubAgentRun> victims = [];
        lock (gate)
        {
            foreach (var (id, run) in runs)
            {
                if (owners.TryGetValue(id, out var owner) && owner == AgentOwner.User)
                {
                    victims.Add(run);
                }
            }
        }

        var stopped = 0;
        foreach (var run in victims)
        {
            if (!run.StopRequested && run.Stop(reason) == SubAgentControlResult.Accepted)
            {
                stopped++;
            }
        }

        return stopped;
    }

    private static SubAgentOutcome Refuse(string id, string reason, IAgentEventSink sink)
    {
        sink.Emit(new AgentFailed(id, reason));
        return new SubAgentOutcome(id, reason, IsError: true);
    }

    /// <summary>
    /// Runs one turn of an instance under the lifecycle every child run shares: it joins the cancellation table, queues for a
    /// tree slot, announces itself when <paramref name="parentId"/> is known, and ends with exactly one
    /// <see cref="AgentSubAgentFinished"/> carrying this run's usage and elapsed time (measured from <paramref name="watch"/>,
    /// so queueing is included).
    /// </summary>
    private async Task<SubAgentOutcome> DriveAsync(
        AgentInstance instance,
        ChatMessage message,
        string task,
        IAgentEventSink sink,
        string? parentId,
        Stopwatch watch,
        CancellationToken ct)
    {
        SubAgentRun? run = null;
        var usageBefore = instance.Usage;
        var completed = false;
        string? failure = null;
        try
        {
            instance.ResetText();
            run = Attach(instance, sink, ct);
            if (parentId is not null)
            {
                sink.Emit(new AgentSubAgentStarted(
                    parentId,
                    instance.Id,
                    instance.Definition.Name,
                    task,
                    instance.Definition.Model.Model,
                    instance.SessionId,
                    DateTimeOffset.UtcNow,
                    instance.Definition.Model.ProviderId));
            }

            var result = await Runner(instance, message, sink, run.Token).ConfigureAwait(false);
            completed = true;
            return new SubAgentOutcome(instance.Id, result.Text, IsError: false);
        }
        catch (OperationCanceledException) when (run is not null && run.StopRequested)
        {
            return new SubAgentOutcome(instance.Id, StoppedText(instance, run), IsError: false) { Stopped = true, Reason = run.StopReason };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            sink.Emit(new AgentFailed(instance.Id, exception.Message));
            return new SubAgentOutcome(instance.Id, $"Sub-agent '{instance.Id}' failed: {exception.Message}", IsError: true) { Reason = exception.Message };
        }
        finally
        {
            if (run is not null)
            {
                var (status, error) = FinishState(completed, run, failure, ct);
                run.Finish(status, UsageOf(instance, usageBefore), watch.Elapsed, error);
                instance.Control = null;
                Detach(instance.Id, run);
                run.Dispose();
                var finishedAt = options.SubAgentReuseClock.GetUtcNow();
                bool reusable;
                lock (gate)
                {
                    lastFinishedAt[instance.Id] = finishedAt;
                    reusable = !notReusable.Contains(instance.Id);
                }

                // Written as the run ends, not at shutdown: a graceful close that follows already has the row, and it is the
                // only thing the next process needs to continue this child.
                await SaveStateAsync(instance, reusable, finishedAt, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static (SubAgentStatus Status, string? Error) FinishState(
        bool completed,
        SubAgentRun run,
        string? failure,
        CancellationToken parentToken)
    {
        if (completed)
        {
            return (SubAgentStatus.Completed, null);
        }

        if (run.StopRequested)
        {
            return (SubAgentStatus.Stopped, run.StopReason);
        }

        if (failure is not null)
        {
            return (SubAgentStatus.Failed, failure);
        }

        return parentToken.IsCancellationRequested
            ? (SubAgentStatus.Stopped, ParentCancelled)
            : (SubAgentStatus.Failed, "cancelled");
    }

    private static TokenUsage UsageOf(AgentInstance instance, TokenUsage before)
    {
        var total = instance.Usage;
        return new TokenUsage(
            total.InputTokens - before.InputTokens,
            total.OutputTokens - before.OutputTokens,
            total.CacheReadTokens - before.CacheReadTokens,
            total.CacheWriteTokens - before.CacheWriteTokens);
    }

    /// <summary>The typed text of a message, which the started event carries as that run's task.</summary>
    private static string TextOf(ChatMessage message)
    {
        StringBuilder? builder = null;
        foreach (var block in message.Content)
        {
            if (block is TextBlock text)
            {
                builder ??= new StringBuilder(text.Text.Length + 32);
                builder.Append(text.Text);
            }
        }

        return builder?.ToString() ?? string.Empty;
    }

    private static string StoppedText(AgentInstance instance, SubAgentRun run)
    {
        var partial = instance.PartialText;
        var builder = new StringBuilder(partial.Length + 96);
        builder.Append("Sub-agent '").Append(instance.Id).Append("' was stopped (").Append(run.StopReason).Append("). Partial output:\n");
        builder.Append(partial.Length == 0 ? "(nothing was produced before it stopped)" : partial);
        return builder.ToString();
    }

    private SubAgentRun Attach(AgentInstance instance, IAgentEventSink sink, CancellationToken parentToken)
    {
        var run = new SubAgentRun(instance.Id, sink, parentToken);
        lock (gate)
        {
            runs[instance.Id] = run;
            finished.Remove(instance.Id);
            // A stop that named this background run before it had one to stop: it is honoured here, inside the same lock the stop
            // takes, so a stop landing between "is it pending?" and "attach" cannot be lost.
            if (backgroundStopPending.Remove(instance.Id))
            {
                run.Stop(StoppedBeforeStart);
            }
        }

        instance.Control = run;
        return run;
    }

    private void Detach(string id, SubAgentRun run)
    {
        lock (gate)
        {
            if (runs.TryGetValue(id, out var current) && ReferenceEquals(current, run))
            {
                runs.Remove(id);
                finished.Add(id);
            }
        }
    }

    private bool TryGetRun(string agentId, out SubAgentRun run)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            run = null!;
            return false;
        }

        lock (gate)
        {
            return runs.TryGetValue(agentId, out run!);
        }
    }

    private void Register(AgentInstance instance, AgentOwner owner)
    {
        lock (gate)
        {
            instances[instance.Id] = instance;
            owners[instance.Id] = owner;
        }
    }

    /// <summary>An instance with a fresh id of <paramref name="owner"/>'s namespace and its own prompt prefix.</summary>
    private AgentInstance NewInstance(
        AgentDefinition definition,
        string sessionId,
        string workingDirectory,
        int depth,
        AgentOwner owner) =>
        new(
            NextId(definition.Name, owner),
            definition,
            sessionId,
            workingDirectory,
            depth,
            composer.Compose(new PromptInput(layers, definition.Prompt, null)))
        {
            // The tool surface reads this: only a user-driven session may deliver session messages (its root may too).
            IsUserSession = owner == AgentOwner.User,
        };

    private AgentInstance? FindUserSession(string sessionId)
    {
        lock (gate)
        {
            return FindUserSessionLocked(sessionId);
        }
    }

    /// <summary><paramref name="instance"/> registered, or the instance another caller registered for the same session first.</summary>
    private AgentInstance? RegisterUserSession(string sessionId, AgentInstance instance)
    {
        lock (gate)
        {
            if (FindUserSessionLocked(sessionId) is { } raced)
            {
                return raced;
            }

            instances[instance.Id] = instance;
            owners[instance.Id] = AgentOwner.User;
            return null;
        }
    }

    /// <summary>The user-driven instance of <paramref name="sessionId"/> this scheduler holds, if any. Called under <see cref="gate"/>.</summary>
    private AgentInstance? FindUserSessionLocked(string sessionId)
    {
        foreach (var (id, instance) in instances)
        {
            if (string.Equals(instance.SessionId, sessionId, StringComparison.Ordinal)
                && owners.TryGetValue(id, out var owner)
                && owner == AgentOwner.User)
            {
                return instance;
            }
        }

        return null;
    }

    private string NextId(string kind, AgentOwner owner)
    {
        // Model-spawned children stay <kind>-<n>; user-driven sessions get their own namespace so an id alone says who
        // registered it (and a future kind named "session" cannot collide).
        var prefix = owner == AgentOwner.User ? "session" : Identifier(kind);
        lock (gate)
        {
            var next = counters.TryGetValue(prefix, out var current) ? current + 1 : 1;
            counters[prefix] = next;
            return string.Concat(prefix, "-", next.ToString(CultureInfo.InvariantCulture));
        }
    }

    private async Task<string> CreateSessionAsync(
        AgentDefinition definition,
        string task,
        ToolContext context,
        CancellationToken ct,
        string? title = null,
        string? childDriver = null)
    {
        if (store is null)
        {
            return Guid.NewGuid().ToString("n");
        }

        var full = string.IsNullOrWhiteSpace(title)
            ? string.Concat(definition.Name, ": ", task.Length <= TitleLimit ? task : task[..TitleLimit])
            : title;
        if (sessions is null)
        {
            var created = await store.CreateSessionAsync(context.WorkingDirectory, full, ct).ConfigureAwait(false);
            return created.Id;
        }

        var parent = string.IsNullOrWhiteSpace(context.SessionId)
            ? null
            : await store.GetSessionAsync(context.SessionId, ct).ConfigureAwait(false);
        var session = await sessions
            .CreateAsync(
                new SessionCreate(context.WorkingDirectory, full)
                {
                    ParentSessionId = context.SessionId,
                    AgentKind = definition.Name,
                    Task = task,
                    ChildDriver = childDriver,
                    WorktreePath = parent?.WorktreePath,
                    ProjectId = parent?.ProjectId,
                },
                ct)
            .ConfigureAwait(false);
        return session.Id;
    }

    /// <summary>
    /// Writes one child's state for the next process. Best effort: a store that fails must not fail the spawn or the run that
    /// just ended - the reusable window is a convenience, the report is the result.
    /// </summary>
    private async Task SaveStateAsync(
        AgentInstance instance,
        bool reusable,
        DateTimeOffset? finishedAt,
        CancellationToken ct)
    {
        if (stateStore is null || OwnerOf(instance.Id) != AgentOwner.Model)
        {
            return;
        }

        try
        {
            await stateStore.SaveSubAgentStateAsync(
                new SubAgentState(instance.SessionId, instance.Id, instance.Definition.Name, instance.Definition.Model, finishedAt)
                {
                    Reusable = reusable,
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceWarning($"sub-agent state of '{instance.Id}' was not stored: {exception.Message}");
        }
    }

    /// <summary>
    /// Rebuilds the children a previous process left continuable: the same agent id and conversation, the model the child
    /// started with, and a window counted from the run end the store recorded. Only what
    /// <see cref="AgentOptions.SubAgentReuseWindow"/> still covers is restored; a kind that no longer exists, a child marked
    /// not reusable, and an id this scheduler already holds are skipped. Nothing is announced here: the app places its rows
    /// from the store itself, and a run announces itself when it starts.
    /// </summary>
    public async Task RestoreAsync(string rootSessionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSessionId);
        if (stateStore is null || store is not { } sessionStore)
        {
            return;
        }

        IReadOnlyList<RestorableSubAgent> restorable;
        try
        {
            restorable = await stateStore.LoadSubAgentStatesAsync(rootSessionId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceWarning($"sub-agent state could not be read: {exception.Message}");
            return;
        }

        var now = options.SubAgentReuseClock.GetUtcNow();
        foreach (var candidate in restorable)
        {
            var state = candidate.State;
            if (!state.Reusable
                || state.FinishedAt is not { } finishedAt
                || finishedAt > now
                || now - finishedAt > options.SubAgentReuseWindow
                || !catalog.TryGet(state.Kind, out var definition)
                || definition.ToolNames is not { Count: > 0 })
            {
                continue;
            }

            lock (gate)
            {
                if (instances.ContainsKey(state.AgentId))
                {
                    continue;
                }
            }

            definition = definition with { Model = state.Model };
            try
            {
                var instance = new AgentInstance(
                    state.AgentId,
                    definition,
                    state.SessionId,
                    Path.GetFullPath(candidate.WorkingDirectory),
                    candidate.Depth,
                    composer.Compose(new PromptInput(layers, definition.Prompt, null)));
                instance.ReplaceHistory(await SessionHistory.LoadAsync(sessionStore, state.SessionId, ct).ConfigureAwait(false));
                lock (gate)
                {
                    if (!instances.TryAdd(state.AgentId, instance))
                    {
                        continue;
                    }

                    owners[state.AgentId] = AgentOwner.Model;
                    lastFinishedAt[state.AgentId] = finishedAt;
                    BumpCounterLocked(definition.Name, state.AgentId);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace.TraceWarning($"sub-agent '{state.AgentId}' was not restored: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Moves the id counter past a restored id, so the next spawn of that kind cannot hand out a number a living child still
    /// holds. Called under <see cref="gate"/>.
    /// </summary>
    private void BumpCounterLocked(string kind, string agentId)
    {
        var prefix = Identifier(kind);
        if (!agentId.StartsWith(prefix + "-", StringComparison.Ordinal)
            || !int.TryParse(agentId.AsSpan(prefix.Length + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return;
        }

        if (number > counters.GetValueOrDefault(prefix))
        {
            counters[prefix] = number;
        }
    }

    private static string Identifier(string kind)
    {
        var builder = new StringBuilder(kind.Length);
        foreach (var ch in kind)
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        }

        return builder.Length == 0 ? "agent" : builder.ToString();
    }

    /// <summary>
    /// The model's view of the control surface: ids the user owns answer <see cref="SubAgentControlResult.NotFound"/>,
    /// indistinguishable from an id that never existed, so the model can neither steer nor stop a session the user drives.
    /// The UI uses the scheduler itself and is unaffected.
    /// </summary>
    private sealed class OwnerScopedControl(SubAgentScheduler scheduler) : ISubAgentControl
    {
        public SubAgentControlResult Steer(string agentId, string instruction) =>
            scheduler.OwnerOf(agentId) == AgentOwner.Model ? scheduler.Steer(agentId, instruction) : SubAgentControlResult.NotFound;

        public SubAgentControlResult FollowUp(string agentId, string instruction) =>
            scheduler.OwnerOf(agentId) == AgentOwner.Model ? scheduler.FollowUp(agentId, instruction) : SubAgentControlResult.NotFound;

        public SubAgentControlResult Stop(string agentId, string? reason) =>
            scheduler.OwnerOf(agentId) == AgentOwner.Model ? scheduler.Stop(agentId, reason) : SubAgentControlResult.NotFound;
    }
}
