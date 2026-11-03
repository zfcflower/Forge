using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;
using static Forge.HostClient.Tests.ToolFixtures;

namespace Forge.HostClient.Tests;

public sealed class ExtensionHostClientCrashTests
{
    [Fact]
    public async Task A_host_crash_fails_the_pending_call_with_connection_closed()
    {
        await using var host = new FakeHost();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On("plugin.execute", async (_, ct) =>
        {
            received.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Json.Element("{}");
        });
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        var pending = tool.InvokeAsync(new ToolCall("c1", "echo", "{}"), new ToolContext("s", "C:/w"), limit.Token).AsTask();
        await received.Task.Bounded();
        await host.DropConnectionAsync();

        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => pending.Bounded());
    }

    [Fact]
    public async Task A_host_crash_marks_the_client_Failed_with_a_detail_once_and_keeps_the_tool_snapshot()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        var toolChanges = new EventLog<int>();
        client.ToolsChanged += () => toolChanges.Add(1);
        await client.StartConnectedAsync(host);
        await RegisterEchoAsync(client, host);
        var changesBeforeCrash = toolChanges.Snapshot().Count;

        await host.DropConnectionAsync();

        var events = await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
        Assert.Equal(HostStatus.Failed, client.Status);
        var failures = events.Where(e => e.Status == HostStatus.Failed).ToArray();
        Assert.Single(failures);
        Assert.NotNull(failures[0].Detail);
        Assert.Contains(host.PipeName, failures[0].Detail);
        Assert.Contains("closed", failures[0].Detail, StringComparison.Ordinal);
        Assert.Equal(new[] { "echo" }, client.Tools.Select(tool => tool.Definition.Name).ToArray());
        Assert.Equal(new[] { "alpha" }, client.ToolsByPlugin.Keys.ToArray());
        Assert.Equal(changesBeforeCrash, toolChanges.Snapshot().Count);
    }

    [Fact]
    public async Task After_a_crash_calls_fail_with_connection_closed_while_the_client_is_Failed()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        await client.StartConnectedAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        await host.DropConnectionAsync();
        await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<IpcConnectionClosedException>(() =>
            tool.InvokeAsync(new ToolCall("c1", "echo", "{}"), new ToolContext("s", "C:/w"), limit.Token).AsTask());
        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => client.RefreshPluginsAsync(limit.Token));

        Assert.Equal(HostStatus.Failed, client.Status);
        Assert.Empty(host.Requests("plugins.refresh"));
        Assert.Empty(host.Requests("plugin.execute"));
    }

    [Fact]
    public async Task StopAsync_after_a_crash_is_clean_sends_no_host_stop_and_clears_the_tools()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        await client.StartConnectedAsync(host);
        await RegisterEchoAsync(client, host);
        await host.DropConnectionAsync();
        await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
        using var limit = TestLimit.Create();

        await client.StopAsync(limit.Token);

        Assert.Equal(HostStatus.Stopped, client.Status);
        Assert.Empty(client.Tools);
        Assert.Empty(client.ToolsByPlugin);
        Assert.Empty(host.Requests("host.stop"));
        Assert.Equal((HostStatus.Stopped, (string?)null), statuses.Snapshot()[^1]);
    }

    [Fact]
    public async Task StartAsync_after_a_crash_recovers_on_the_same_instance_and_drops_the_stale_snapshot()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        await client.StartConnectedAsync(host);
        await RegisterEchoAsync(client, host);
        await host.DropConnectionAsync();
        await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
        var toolChanges = new EventLog<int>();
        client.ToolsChanged += () => toolChanges.Add(1);
        using var limit = TestLimit.Create();

        var accepting = host.AcceptAsync();
        await client.StartAsync(limit.Token).Bounded();
        await accepting.Bounded();

        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Empty(client.Tools);
        Assert.Empty(client.ToolsByPlugin);
        Assert.Single(toolChanges.Snapshot());
        Assert.Equal((HostStatus.Running, (string?)null), statuses.Snapshot()[^1]);

        await RegisterEchoAsync(client, host);
        Assert.Equal(new[] { "echo" }, client.Tools.Select(tool => tool.Definition.Name).ToArray());
    }

    [Fact]
    public async Task StopAsync_during_a_recovery_start_leaves_the_client_stopped()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host, startTimeout: TimeSpan.FromSeconds(10));
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        await client.StartConnectedAsync(host);
        await RegisterEchoAsync(client, host);
        await host.DropConnectionAsync();
        await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
        using var limit = TestLimit.Create();

        var recovering = client.StartAsync(limit.Token);
        await statuses.UntilAsync(seen => seen.Count(e => e.Status == HostStatus.Starting) == 2);
        var stopping = client.StopAsync(limit.Token);
        var accepting = host.AcceptAsync();
        await recovering.Bounded();
        await accepting.Bounded();
        await stopping.Bounded();

        Assert.Equal(HostStatus.Stopped, client.Status);
        Assert.Empty(client.Tools);
        var sequence = statuses.Snapshot().Select(e => e.Status).ToArray();
        Assert.Equal(HostStatus.Stopped, sequence[^1]);
        Assert.Equal(2, sequence.Count(status => status == HostStatus.Running));
        Assert.Single(host.Requests("host.stop"));
    }

    [Fact]
    public async Task StopAsync_on_a_deliberate_stop_clears_the_tools_and_fires_ToolsChanged()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        await RegisterEchoAsync(client, host);
        var toolChanges = new EventLog<int>();
        client.ToolsChanged += () => toolChanges.Add(1);
        using var limit = TestLimit.Create();

        await client.StopAsync(limit.Token);

        Assert.Equal(HostStatus.Stopped, client.Status);
        Assert.Empty(client.Tools);
        Assert.Empty(client.ToolsByPlugin);
        Assert.Single(toolChanges.Snapshot());
    }

    [Fact]
    public async Task Dispose_after_a_crash_completes_without_error()
    {
        await using var host = new FakeHost();
        var client = ClientFactory.Create(host);
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        await client.StartConnectedAsync(host);
        await host.DropConnectionAsync();
        await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));

        await client.DisposeAsync().AsTask().Bounded();

        Assert.Empty(host.Requests("host.stop"));
    }
}
