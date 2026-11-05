using System.Diagnostics;
using System.Globalization;
using System.Text;
using Forge.Abstractions;
using Forge.Core.Prompts;

namespace Forge.Core.Agents;

/// <summary>Result of one spawn: the child's final text, or the reason it could not run.</summary>
public sealed record SubAgentOutcome(string AgentId, string Text, bool IsError);

/// <summary>
/// Owns sub-agent instances: id allocation, the run gate per instance, the depth limit and the control surface
/// (<see cref="ISubAgentControl"/>) the parent tools and the UI stop button share. Each spawn is one run with
/// its own cancellation source, so stopping a child never reaches the caller or its siblings.
/// </summary>
public sealed class SubAgentScheduler : ISubAgentControl
{
    private const int TitleLimit = 80;
    private const string ParentCancelled = "parent cancelled";

    private readonly AgentCatalog catalog;
    private readonly PromptLayers layers;
    private readonly IPromptComposer composer;
    private readonly ISessionStore? store;
    private readonly ISessionCatalog? sessions;
    private readonly AgentOptions options;
    private readonly Dictionary<string, AgentInstance> instances = new(StringComparer.Ordinal);
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
    }

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
        CancellationToken ct)
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

        AgentInstance? instance = null;
        SubAgentRun? run = null;
        var holdsGate = false;
        var usageBefore = TokenUsage.Empty;
        var completed = false;
        string? failure = null;
        var watch = Stopwatch.StartNew();
        try
        {
            if (resumeId is not null)
            {
                if (!TryGetInstance(resumeId, out instance))
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
                instance = new AgentInstance(
                    NextId(definition.Name),
                    definition,
                    sessionId,
                    context.WorkingDirectory,
                    context.AgentDepth + 1,
                    composer.Compose(new PromptInput(layers, definition.Prompt, null)));
                Register(instance);
                instance.TryAcquire();
                holdsGate = true;
            }

            usageBefore = instance.Usage;
            instance.ResetText();
            run = Attach(instance, sink, ct);

            sink.Emit(new AgentSubAgentStarted(
                parentId,
                instance.Id,
                instance.Definition.Name,
                task,
                instance.Definition.Model.Model,
                instance.SessionId,
                DateTimeOffset.UtcNow));
            var result = await Runner(instance, ChatMessage.User(task), sink, run.Token).ConfigureAwait(false);
            completed = true;
            return new SubAgentOutcome(instance.Id, result.Text, IsError: false);
        }
        catch (OperationCanceledException) when (run is not null && run.StopRequested)
        {
            return new SubAgentOutcome(instance!.Id, StoppedText(instance, run), IsError: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            if (instance is not null)
            {
                sink.Emit(new AgentFailed(instance.Id, exception.Message));
            }

            var id = instance?.Id ?? resumeId ?? string.Empty;
            return new SubAgentOutcome(id, $"Sub-agent '{id}' failed: {exception.Message}", IsError: true);
        }
        finally
        {
            if (run is not null && instance is not null)
            {
                var (status, error) = FinishState(completed, run, failure, ct);
                run.Finish(status, UsageOf(instance, usageBefore), watch.Elapsed, error);
                instance.Control = null;
                Detach(instance.Id, run);
                run.Dispose();
            }

            if (holdsGate)
            {
                instance!.Release();
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

    private void Register(AgentInstance instance)
    {
        lock (gate)
        {
            instances[instance.Id] = instance;
        }
    }

    private string NextId(string kind)
    {
        var prefix = Identifier(kind);
        lock (gate)
        {
            var next = counters.TryGetValue(prefix, out var current) ? current + 1 : 1;
            counters[prefix] = next;
            return string.Concat(prefix, "-", next.ToString(CultureInfo.InvariantCulture));
        }
    }

    private async Task<string> CreateSessionAsync(AgentDefinition definition, string task, ToolContext context, CancellationToken ct)
    {
        if (store is null)
        {
            return Guid.NewGuid().ToString("n");
        }

        var title = task.Length <= TitleLimit ? task : task[..TitleLimit];
        var full = string.Concat(definition.Name, ": ", title);
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
}
