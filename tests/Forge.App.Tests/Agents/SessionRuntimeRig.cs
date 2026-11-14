using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// A real <see cref="SessionRuntime"/> over a scripted model and a throwaway SQLite store. The runtime is assembled from
/// <see cref="AgentRuntimeServices"/>, so a test injects one seam (the <c>compaction</c> settings, the sub-agent dials) and
/// then runs turns against the host the runtime builds and keeps across them.
/// </summary>
internal sealed class SessionRuntimeRig : IAsyncDisposable
{
    /// <summary>Context window the rig's models report, so every turn ends with a root context report.</summary>
    public const int Window = 200_000;

    private readonly SqliteSessionStore store;
    private readonly SqliteTodoStore todos;
    private bool disposed;

    public SessionRuntimeRig(string root, Action<AgentRuntimeServices>? configure = null)
    {
        var data = Path.Combine(root, "data-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "ws");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(workspace);
        store = new SqliteSessionStore(data);
        todos = new SqliteTodoStore(data);
        var services = new AgentRuntimeServices(
            _ => Provider,
            HostClient: null,
            store,
            store,
            todos,
            new ModelSettings("main-model"))
        {
            ContextWindow = static _ => Window,
        };
        configure?.Invoke(services);
        Runtime = new SessionRuntime(services, new SessionInfo("s1", workspace, "t", DateTimeOffset.UnixEpoch));
    }

    public ScriptedProvider Provider { get; } = new();

    public SessionRuntime Runtime { get; }

    /// <summary>Runs one turn that must succeed and returns the root's last context report of it.</summary>
    public async Task<AgentContextMeasured> TurnAsync()
    {
        var events = new ConcurrentQueue<AgentEvent>();
        await Runtime.RunTurnAsync("hello", events.Enqueue, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Empty(events.OfType<AgentFailed>());
        return events.OfType<AgentContextMeasured>().Last(static e => e.AgentId == "root");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Runtime.Dispose();
        await store.DisposeAsync();
        await todos.DisposeAsync();
    }
}

/// <summary>
/// A model that records every request and answers "done". <see cref="BeforeReply"/> optionally holds a request before it is
/// answered, so a test can gate a sub-agent at the provider instead of inferring progress from a delay.
/// </summary>
internal sealed class ScriptedProvider : IChatProvider
{
    private readonly ConcurrentQueue<ChatRequest> queue = new();

    public IReadOnlyList<ChatRequest> Requests => [.. queue];

    /// <summary>Optional gate a request waits on before it is answered; null answers at once.</summary>
    public Func<ChatRequest, CancellationToken, Task>? BeforeReply { get; set; }

    public string Id => "scripted";

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        queue.Enqueue(request);
        if (BeforeReply is { } before)
        {
            await before(request, ct).ConfigureAwait(false);
        }

        await Task.Yield();
        yield return new TextDelta("done");
        yield return new MessageCompleted(ChatMessage.Assistant("done"), new TokenUsage(3, 2), StopReason.EndTurn);
    }
}
