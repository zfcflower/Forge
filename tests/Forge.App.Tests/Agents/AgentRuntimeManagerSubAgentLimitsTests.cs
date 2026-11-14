using System.Collections.Concurrent;
using System.Reflection;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// <see cref="AgentRuntimeManager.RefreshSubAgentLimits"/> reaches every live session host at once: a cap edited in
/// settings must not wait for the next turn of each tab. The hosts are read back through the cap they report.
/// </summary>
public sealed class AgentRuntimeManagerSubAgentLimitsTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-manager-limits-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteSessionStore store;
    private readonly SqliteTodoStore todos;
    private readonly ScriptedProvider provider = new();
    private readonly AgentRuntimeManager manager;
    private int cap = 2;

    public AgentRuntimeManagerSubAgentLimitsTests()
    {
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(root, "ws"));
        store = new SqliteSessionStore(data);
        todos = new SqliteTodoStore(data);
        var services = new AgentRuntimeServices(_ => provider, HostClient: null, store, store, todos, new ModelSettings("main-model"))
        {
            ContextWindow = static _ => SessionRuntimeRig.Window,
            SubAgents = () => new SubAgentLimits(cap, Enabled: true),
        };
        manager = new AgentRuntimeManager(services);
    }

    private SessionInfo Session(string id) => new(id, Path.Combine(root, "ws"), "t", DateTimeOffset.UnixEpoch);

    private static async Task TurnAsync(SessionRuntime runtime)
    {
        var events = new ConcurrentQueue<AgentEvent>();
        await runtime.RunTurnAsync("hello", events.Enqueue, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Empty(events.OfType<AgentFailed>());
    }

    /// <summary>The cap the runtime live host reports; the host is private to the runtime.</summary>
    private static int? HostCap(SessionRuntime runtime)
    {
        var field = typeof(SessionRuntime).GetField("host", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (field.GetValue(runtime) as AgentHost)?.MaxParallelSubAgents;
    }

    [Fact]
    public async Task A_refresh_reaches_every_live_runtime_without_a_turn()
    {
        var first = manager.Get(Session("a"));
        var second = manager.Get(Session("b"));
        await TurnAsync(first);
        await TurnAsync(second);
        Assert.Equal(2, HostCap(first));
        Assert.Equal(2, HostCap(second));

        cap = 7;
        manager.RefreshSubAgentLimits();

        Assert.Equal(7, HostCap(first));
        Assert.Equal(7, HostCap(second));

        // Lowering goes the same way, and an unchanged value is a plain no-op.
        cap = 1;
        manager.RefreshSubAgentLimits();
        manager.RefreshSubAgentLimits();
        Assert.Equal(1, HostCap(first));
        Assert.Equal(1, HostCap(second));
    }

    [Fact]
    public async Task A_runtime_without_a_host_and_a_released_runtime_do_not_break_the_refresh()
    {
        var live = manager.Get(Session("live"));
        var idle = manager.Get(Session("idle"));
        manager.Get(Session("gone"));
        await TurnAsync(live);
        manager.Release("gone");

        cap = 5;
        var failure = Record.Exception(manager.RefreshSubAgentLimits);

        Assert.Null(failure);
        Assert.Equal(5, HostCap(live));
        Assert.Null(HostCap(idle));
    }

    [Fact]
    public void A_manager_with_no_runtimes_refreshes_as_a_no_op()
    {
        Assert.Null(Record.Exception(manager.RefreshSubAgentLimits));
    }

    [Fact]
    public async Task Refreshing_a_disposed_manager_does_not_throw()
    {
        var runtime = manager.Get(Session("a"));
        await TurnAsync(runtime);
        manager.Dispose();

        Assert.Null(Record.Exception(manager.RefreshSubAgentLimits));
    }

    public async ValueTask DisposeAsync()
    {
        manager.Dispose();
        await store.DisposeAsync();
        await todos.DisposeAsync();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is harmless.
        }
    }
}
