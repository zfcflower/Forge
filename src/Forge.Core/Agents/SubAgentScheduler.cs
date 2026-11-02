using System.Text;
using Forge.Abstractions;
using Forge.Core.Prompts;

namespace Forge.Core.Agents;

/// <summary>Result of one spawn: the child's final text, or the reason it could not run.</summary>
public sealed record SubAgentOutcome(string AgentId, string Text, bool IsError);

/// <summary>
/// Owns sub-agent instances: id allocation, the run gate per instance and the depth limit. Each instance
/// keeps its own context, so a spawn by id continues where that agent stopped while a fresh spawn starts
/// from the shared stable prefix alone.
/// </summary>
public sealed class SubAgentScheduler
{
    private const int TitleLimit = 80;

    private readonly AgentCatalog catalog;
    private readonly PromptLayers layers;
    private readonly IPromptComposer composer;
    private readonly ISessionStore? store;
    private readonly AgentOptions options;
    private readonly Dictionary<string, AgentInstance> instances = new(StringComparer.Ordinal);
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
        var holdsGate = false;
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

            sink.Emit(new AgentSubAgentStarted(parentId, instance.Id, instance.Definition.Name));
            var result = await Runner(instance, ChatMessage.User(task), sink, ct).ConfigureAwait(false);
            return new SubAgentOutcome(instance.Id, result.Text, IsError: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (instance is not null)
            {
                sink.Emit(new AgentFailed(instance.Id, exception.Message));
            }

            var id = instance?.Id ?? resumeId ?? string.Empty;
            return new SubAgentOutcome(id, $"Sub-agent '{id}' failed: {exception.Message}", IsError: true);
        }
        finally
        {
            if (holdsGate)
            {
                instance!.Release();
            }
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
            return string.Concat(prefix, "-", next.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private async Task<string> CreateSessionAsync(AgentDefinition definition, string task, ToolContext context, CancellationToken ct)
    {
        if (store is null)
        {
            return Guid.NewGuid().ToString("n");
        }

        var title = task.Length <= TitleLimit ? task : task[..TitleLimit];
        var session = await store
            .CreateSessionAsync(context.WorkingDirectory, string.Concat(definition.Name, ": ", title), ct)
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
