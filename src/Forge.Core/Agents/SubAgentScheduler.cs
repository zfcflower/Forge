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
}

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
    private readonly Dictionary<string, int> counters = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

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

    public async Task<SubAgentOutcome> SpawnAsync(
        string kind,
        string task,
        string? resumeId,
        ToolContext context,
        IAgentEventSink sink,
        string parentId,
        CancellationToken ct,
        AgentOwner owner = AgentOwner.Model)
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

        WarnWhenBroaderThanParent(definition, parentId, sink);

        AgentInstance? instance = null;
        var holdsGate = false;
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

                holdsGate = true;
            }

            if (instance is null)
            {
                var sessionId = await CreateSessionAsync(definition, task, context, ct).ConfigureAwait(false);
                instance = NewInstance(definition, sessionId, context.WorkingDirectory, context.AgentDepth + 1, owner);
                Register(instance, owner);
                instance.TryAcquire();
                holdsGate = true;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The run never started, so no finish event is due; report it the way a failed run would.
            var id = instance?.Id ?? resumeId ?? string.Empty;
            return new SubAgentOutcome(id, $"Sub-agent '{id}' failed: {exception.Message}", IsError: true);
        }

        try
        {
            return await DriveAsync(instance, ChatMessage.User(task), task, sink, parentId, watch, ct).ConfigureAwait(false);
        }
        finally
        {
            if (holdsGate)
            {
                instance.Release();
            }
        }
    }

    /// <summary>
    /// Starts a user-driven child session (an "Agent session" the user opened under the root session) and runs its first
    /// turn. Everything a model spawn uses is shared: the tree's run slots and queue order, the approval path, the
    /// cancellation table and the rate limits. Only ownership differs - the session row records
    /// <see cref="StorageSchema.ChildDriverUser"/> with <paramref name="rootSessionId"/> as its parent, and the instance is
    /// registered as <see cref="AgentOwner.User"/> under the <c>session-&lt;n&gt;</c> namespace, so the model's tools answer
    /// for its id exactly like for one that never existed. The definition is the caller's choice (the app offers the kinds
    /// and models the user picked); this method never looks a kind up in the catalog.
    /// </summary>
    public async Task<SubAgentOutcome> StartUserSessionAsync(
        AgentDefinition definition,
        string task,
        string? sessionTitle,
        string rootSessionId,
        string workingDirectory,
        IAgentEventSink sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

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
            return await DriveAsync(instance, ChatMessage.User(task), task, sink, rootSessionId, watch, ct).ConfigureAwait(false);
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
                    DateTimeOffset.UtcNow));
            }

            var result = await Runner(instance, message, sink, run.Token).ConfigureAwait(false);
            completed = true;
            return new SubAgentOutcome(instance.Id, result.Text, IsError: false);
        }
        catch (OperationCanceledException) when (run is not null && run.StopRequested)
        {
            return new SubAgentOutcome(instance.Id, StoppedText(instance, run), IsError: false) { Stopped = true };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            sink.Emit(new AgentFailed(instance.Id, exception.Message));
            return new SubAgentOutcome(instance.Id, $"Sub-agent '{instance.Id}' failed: {exception.Message}", IsError: true);
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
            composer.Compose(new PromptInput(layers, definition.Prompt, null)));

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
