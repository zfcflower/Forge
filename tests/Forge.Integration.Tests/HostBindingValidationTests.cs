using System.Text.Json;
using Forge.Ipc;
using Forge.Ipc.Protocol;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>Real-Host binding validation over a direct pipe: the Host itself must refuse malformed call contexts
/// with precise codes before any plugin code runs. Fake-host client tests cannot prove this; every case here speaks
/// to the real <c>Forge.ExtensionHost</c> through <see cref="RawHost"/> and asserts the Host's error code plus the
/// absence of the handler's marker file.</summary>
public sealed class HostBindingValidationTests
{
    /// <summary>Whole-test budget: one raw host start, its refresh and the calls this test makes. A safety net, so it
    /// follows this run's measured process cost and keeps the floor the other raw-host tests use.</summary>
    private static readonly TimeSpan Budget = TestBudgets.Safety(TimeSpan.FromSeconds(150), TimeSpan.FromMinutes(10));

    [Fact]
    public async Task Nested_call_without_rootRunId_is_invalid_context()
    {
        await using var host = RawHost.StartNew();
        WritePlugin(host, "alpha");
        await using var peer = await RawHost.ConnectAsync(host.Pipe, new CancellationTokenSource(Budget).Token);
        using var budget = new CancellationTokenSource(Budget);
        await peer.RequestAsync(Sdk.ExtensionProtocol.PluginsRefresh, IpcEmpty.Instance, budget.Token);
        var status = await FirstStatusAsync(peer, "alpha", budget.Token);
        await peer.RequestAsync(
            Sdk.ExtensionProtocol.PluginApprove,
            WireRequests.Approve("alpha", "1.0.0", status.SourceHash, status.DependencyHash, status.SdkVersion),
            budget.Token);

        var marker = Path.Combine(host.Root, "nested-missing-root.txt");
        var context = WireRequests.Context(host.Root, "nest-no-root-1", "s", ["other"]);
        var error = await Assert.ThrowsAsync<IpcRemoteException>(() => peer.RequestAsync(
            Sdk.ExtensionProtocol.PluginExecute,
            WireRequests.Execute("alpha", "sleep", JsonSerializer.Serialize(new { marker, ms = 1000 }), context),
            budget.Token));

        Assert.Equal("invalid_context", error.Code);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Chain_cycle_is_refused()
    {
        await using var host = RawHost.StartNew();
        WritePlugin(host, "alpha");
        await using var peer = await RawHost.ConnectAsync(host.Pipe, new CancellationTokenSource(Budget).Token);
        using var budget = new CancellationTokenSource(Budget);
        await peer.RequestAsync(Sdk.ExtensionProtocol.PluginsRefresh, IpcEmpty.Instance, budget.Token);
        var status = await FirstStatusAsync(peer, "alpha", budget.Token);
        await peer.RequestAsync(
            Sdk.ExtensionProtocol.PluginApprove,
            WireRequests.Approve("alpha", "1.0.0", status.SourceHash, status.DependencyHash, status.SdkVersion),
            budget.Token);

        var marker = Path.Combine(host.Root, "chain-cycle.txt");
        var runId = "cycle-run-1";
        var context = WireRequests.Context(host.Root, runId, "s", ["alpha"], rootRunId: runId);
        var error = await Assert.ThrowsAsync<IpcRemoteException>(() => peer.RequestAsync(
            Sdk.ExtensionProtocol.PluginExecute,
            WireRequests.Execute("alpha", "sleep", JsonSerializer.Serialize(new { marker, ms = 1000 }), context),
            budget.Token));

        Assert.Equal("chain_cycle", error.Code);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Reserved_identity_is_refused()
    {
        await using var host = RawHost.StartNew();
        WritePlugin(host, "alpha");
        await using var peer = await RawHost.ConnectAsync(host.Pipe, new CancellationTokenSource(Budget).Token);
        using var budget = new CancellationTokenSource(Budget);
        await peer.RequestAsync(Sdk.ExtensionProtocol.PluginsRefresh, IpcEmpty.Instance, budget.Token);

        var runId = "reserved-run-1";
        var context = WireRequests.Context(host.Root, runId, "s", [], rootRunId: runId);
        var error = await Assert.ThrowsAsync<IpcRemoteException>(() => peer.RequestAsync(
            Sdk.ExtensionProtocol.PluginExecute,
            WireRequests.Execute("$run_code", "echo", JsonSerializer.Serialize(new { text = "x" }), context),
            budget.Token));

        Assert.Equal("reserved_identity", error.Code);
    }

    [Fact]
    public async Task Duplicate_run_id_is_refused_while_first_execution_runs()
    {
        await using var host = RawHost.StartNew();
        WritePlugin(host, "alpha");
        await using var peer = await RawHost.ConnectAsync(host.Pipe, new CancellationTokenSource(Budget).Token);
        using var budget = new CancellationTokenSource(Budget);
        await peer.RequestAsync(Sdk.ExtensionProtocol.PluginsRefresh, IpcEmpty.Instance, budget.Token);
        var status = await FirstStatusAsync(peer, "alpha", budget.Token);
        await peer.RequestAsync(
            Sdk.ExtensionProtocol.PluginApprove,
            WireRequests.Approve("alpha", "1.0.0", status.SourceHash, status.DependencyHash, status.SdkVersion),
            budget.Token);

        var marker = Path.Combine(host.Root, "dup-holder.txt");
        var runId = "dup-run-0001";
        var argumentsJson = JsonSerializer.Serialize(new { marker, ms = 20000 });
        var context = WireRequests.Context(host.Root, runId, "s", [], rootRunId: runId);
        PluginExecuteRequest ExecutePayload() =>
            WireRequests.Execute("alpha", "sleep", argumentsJson, context);

        var first = peer.RequestAsync(Sdk.ExtensionProtocol.PluginExecute, ExecutePayload(), budget.Token);
        await WaitForFileAsync(marker, budget.Token);

        var error = await Assert.ThrowsAsync<IpcRemoteException>(() => peer.RequestAsync(
            Sdk.ExtensionProtocol.PluginExecute, ExecutePayload(), budget.Token));

        Assert.Equal("duplicate_run_id", error.Code);

        using var release = new CancellationTokenSource(Budget);
        release.Cancel();
        try
        {
            await first.WaitAsync(TimeSpan.FromSeconds(5), release.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IpcRemoteException)
        {
        }
    }

    private static void WritePlugin(RawHost host, string id)
    {
        var directory = Path.Combine(host.Plugins, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(new { id, version = "1.0.0", source = "Plugin.cs" }));
        File.WriteAllText(Path.Combine(directory, "Plugin.cs"), PluginSources.Standard(id));
    }

    private static async Task<(string SourceHash, string DependencyHash, string SdkVersion)> FirstStatusAsync(
        IpcPeer peer, string id, CancellationToken ct)
    {
        var limit = TestBudgets.Condition;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        while (true)
        {
            IpcMessage response;
            try
            {
                response = await peer.RequestAsync(
                    Sdk.ExtensionProtocol.PluginsStatus, IpcEmpty.Instance, deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"no plugins.status row for '{id}' appeared within {limit.TotalSeconds:0} s.");
            }

            foreach (var entry in response.Read<Sdk.PluginStatusResponse>().Plugins)
            {
                if (entry is not null && string.Equals(entry.PluginId, id, StringComparison.Ordinal))
                {
                    return (entry.SourceHash, entry.DependencyHash, entry.SdkVersion);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"no plugins.status row for '{id}' appeared within {limit.TotalSeconds:0} s.");
            }
        }
    }

    private static async Task WaitForFileAsync(string path, CancellationToken ct)
    {
        var limit = TestBudgets.Condition;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        while (!File.Exists(path))
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"'{path}' did not appear within {limit.TotalSeconds:0} s.");
            }
        }
    }
}
