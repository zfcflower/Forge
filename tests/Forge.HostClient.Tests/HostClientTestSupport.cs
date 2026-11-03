using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;

namespace Forge.HostClient.Tests;

internal static class TestLimit
{
    public static readonly TimeSpan Span = TimeSpan.FromSeconds(15);

    public static CancellationTokenSource Create() => new(Span);

    public static Task Bounded(this Task task) => task.WaitAsync(Span);

    public static Task<T> Bounded<T>(this Task<T> task) => task.WaitAsync(Span);
}

internal static class Json
{
    public static JsonElement Element(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}

/// <summary>Records events in arrival order and lets a test await a condition over the recorded sequence.</summary>
internal sealed class EventLog<T>
{
    private readonly Lock gate = new();
    private readonly List<T> items = [];
    private TaskCompletionSource changed = NewSignal();

    public void Add(T item)
    {
        TaskCompletionSource previous;
        lock (gate)
        {
            items.Add(item);
            previous = changed;
            changed = NewSignal();
        }

        previous.TrySetResult();
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (gate)
        {
            return items.ToArray();
        }
    }

    public async Task<IReadOnlyList<T>> UntilAsync(Func<IReadOnlyList<T>, bool> condition)
    {
        using var limit = TestLimit.Create();
        while (true)
        {
            Task signal;
            lock (gate)
            {
                var snapshot = items.ToArray();
                if (condition(snapshot))
                {
                    return snapshot;
                }

                signal = changed.Task;
            }

            await signal.WaitAsync(limit.Token).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>An in-process <see cref="ITool"/> that counts invocations and runs an optional body.</summary>
internal sealed class StubTool(
    string name,
    ToolSideEffect sideEffect = ToolSideEffect.ReadOnly,
    Func<ToolCall, ToolContext, CancellationToken, ValueTask<ToolResult>>? body = null) : ITool
{
    private readonly ConcurrentQueue<(ToolCall Call, ToolContext Context)> invocations = new();

    public string Name { get; } = name;

    public ToolDefinition Definition { get; } = new(name, "Stub tool " + name, "{}");

    public ToolSideEffect SideEffect { get; } = sideEffect;

    public int InvocationCount => invocations.Count;

    public IReadOnlyList<(ToolCall Call, ToolContext Context)> Invocations => invocations.ToArray();

    public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        invocations.Enqueue((call, context));
        return body is null
            ? ValueTask.FromResult(new ToolResult("ran " + Name))
            : body(call, context, ct);
    }
}

internal static class ClientFactory
{
    public static HostConnection Connection(FakeHost host, TimeSpan? startTimeout = null) => new()
    {
        PipeName = host.PipeName,
        StartTimeout = startTimeout ?? TimeSpan.FromSeconds(10),
    };

    public static ExtensionHostClient Create(
        FakeHost host,
        Func<string, ITool?>? resolver = null,
        Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>>? approval = null,
        ExtensionHostClientOptions? options = null,
        TimeSpan? startTimeout = null) =>
        new(Connection(host, startTimeout), resolver, approval, options);

    /// <summary>Starts <paramref name="client"/> against <paramref name="host"/> and returns the accepted server peer.</summary>
    public static async Task<IpcPeer> StartConnectedAsync(this ExtensionHostClient client, FakeHost host)
    {
        var accepting = host.AcceptAsync();
        using var limit = TestLimit.Create();
        await client.StartAsync(limit.Token).ConfigureAwait(false);
        return await accepting.Bounded().ConfigureAwait(false);
    }
}

internal static class ToolFixtures
{
    /// <summary>Registers plugin "alpha" with one read-only tool "echo" and waits until the client has processed it.</summary>
    public static async Task<ExtensionHostTool> RegisterEchoAsync(ExtensionHostClient client, FakeHost host)
    {
        await host.NotifyClientAsync("tools.register",
            """{"pluginId":"alpha","tools":[{"name":"echo","description":"Echoes","inputSchemaJson":"{}","sideEffect":0}]}""");
        await host.SyncAsync();
        return Assert.IsType<ExtensionHostTool>(Assert.Single(client.Tools));
    }
}
