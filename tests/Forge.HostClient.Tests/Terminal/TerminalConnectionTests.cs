using Forge.Abstractions;
using Forge.Ipc;
using Forge.Ipc.Protocol;
using Xunit;

namespace Forge.HostClient.Tests.Terminal;

/// <summary>断连语义、客户端停止/释放与按世代终止（任务条目 8、12）。</summary>
public sealed class TerminalConnectionTests
{
    [Fact]
    public async Task Dropped_connection_faults_output_and_exited_with_a_connection_error()
    {
        await using var harness = await HostClientHarness.StartAsync();
        var session = await TerminalFixtures.StartSessionAsync(harness.Host, harness.Client, "t-drop", harness.Limit.Token);

        await harness.Host.DropConnectionAsync();

        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => session.Output.Completion.Bounded());
        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => session.Exited.Bounded());
    }

    [Fact]
    public async Task Client_stop_faults_output_and_exited_with_a_connection_error()
    {
        await using var harness = await HostClientHarness.StartAsync();
        var session = await TerminalFixtures.StartSessionAsync(harness.Host, harness.Client, "t-stop-client", harness.Limit.Token);

        await harness.Client.StopAsync(harness.Limit.Token);

        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => session.Output.Completion.Bounded());
        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => session.Exited.Bounded());
    }

    [Fact]
    public async Task Client_dispose_faults_live_sessions_with_a_connection_error()
    {
        await using var host = new FakeHost();
        var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var session = await TerminalFixtures.StartSessionAsync(host, client, "t-dispose-client", limit.Token);

        await client.DisposeAsync();

        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => session.Output.Completion.Bounded());
        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => session.Exited.Bounded());
    }

    [Fact]
    public async Task Reconnect_retires_old_sessions_and_leaves_the_new_generation_healthy()
    {
        await using var host = new FakeHost();
        await using var client = await host.StartTrackedAsync(out var statuses);
        using var limit = TestLimit.Create();
        var oldSession = await TerminalFixtures.StartSessionAsync(host, client, "t-old", limit.Token);

        await host.DropConnectionAsync();
        await statuses.UntilAsync(events => events.Any(e => e.Status == HostStatus.Failed));
        await client.StartConnectedAsync(host);

        // 旧世代会话随连接退役而收口。
        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => oldSession.Output.Completion.Bounded());
        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => oldSession.Exited.Bounded());

        // 新世代不受影响：建终端、收输出、拿退出码全流程正常。
        var fresh = await TerminalFixtures.StartSessionAsync(host, client, "t-new", limit.Token);
        await host.NotifyClientAsync(TerminalOperations.Output, TerminalFixtures.Output("t-new", 0, "hi"));
        await host.NotifyClientAsync(TerminalOperations.Exit, TerminalFixtures.Exit("t-new", 0));
        await host.SyncAsync();

        var body = await TerminalFixtures.DrainAsync(fresh);
        Assert.Equal("hi", System.Text.Encoding.UTF8.GetString(body));
        Assert.Equal(0, await fresh.Exited.Bounded());
    }

    [Fact]
    public async Task Dropped_connection_faults_every_registered_terminal_not_just_the_first()
    {
        await using var harness = await HostClientHarness.StartAsync();
        var counter = 0;
        harness.Host.On(
            TerminalOperations.Start,
            (_, _) =>
            {
                var id = "t-multi-" + Interlocked.Increment(ref counter);
                return Task.FromResult(TerminalFixtures.StartResult(id));
            });

        var sessions = new List<ITerminalSession>();
        for (var index = 0; index < 3; index++)
        {
            sessions.Add(await harness.Client.StartAsync(TerminalFixtures.Request(), harness.Limit.Token));
        }

        Assert.Equal(["t-multi-1", "t-multi-2", "t-multi-3"], sessions.Select(session => session.Id).ToArray());

        await harness.Host.DropConnectionAsync();

        foreach (var session in sessions)
        {
            await Assert.ThrowsAsync<IpcConnectionClosedException>(() => session.Output.Completion.Bounded());
            await Assert.ThrowsAsync<IpcConnectionClosedException>(() => session.Exited.Bounded());
        }
    }
}
