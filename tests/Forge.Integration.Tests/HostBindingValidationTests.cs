using System.Text.Json;
using Forge.Ipc;
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
        await peer.SendRequestAsync(Sdk.ExtensionProtocol.PluginsRefresh, RawHost.EmptyObject(), budget.Token);
        var status = await FirstStatusAsync(peer, "alpha", budget.Token);
        await peer.SendRequestAsync(
            Sdk.ExtensionProtocol.PluginApprove,
            JsonSerializer.SerializeToElement(new
            {
                pluginId = "alpha",
                version = "1.0.0",
                sourceHash = status.SourceHash,
                dependencyHash = status.DependencyHash,
                sdkVersion = status.SdkVersion,
            }),
            budget.Token);

        var marker = Path.Combine(host.Root, "nested-missing-root.txt");
        var context = new
        {
            runId = "nest-no-root-1",
            sessionId = "s",
            workingDirectory = host.Root,
            depth = 1,
            pluginChain = new[] { "other" },
        };
        var error = await Assert.ThrowsAsync<IpcRemoteException>(() => peer.SendRequestAsync(
            Sdk.ExtensionProtocol.PluginExecute,
            JsonSerializer.SerializeToElement(new
            {
                pluginId = "alpha",
                tool = "sleep",
                args = JsonSerializer.SerializeToElement(new { marker, ms = 1000 }),
                sessionId = "s",
                workingDir = host.Root,
                depth = 1,
                context,
            }),
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
        await peer.SendRequestAsync(Sdk.ExtensionProtocol.PluginsRefresh, RawHost.EmptyObject(), budget.Token);
        var status = await FirstStatusAsync(peer, "alpha", budget.Token);
        await peer.SendRequestAsync(
            Sdk.ExtensionProtocol.PluginApprove,
            JsonSerializer.SerializeToElement(new
            {
                pluginId = "alpha",
                version = "1.0.0",
                sourceHash = status.SourceHash,
                dependencyHash = status.DependencyHash,
                sdkVersion = status.SdkVersion,
            }),
            budget.Token);

        var marker = Path.Combine(host.Root, "chain-cycle.txt");
        var runId = "cycle-run-1";
        var context = new
        {
            runId,
            sessionId = "s",
            workingDirectory = host.Root,
            depth = 1,
            pluginChain = new[] { "alpha" },
            rootRunId = runId,
        };
        var error = await Assert.ThrowsAsync<IpcRemoteException>(() => peer.SendRequestAsync(
            Sdk.ExtensionProtocol.PluginExecute,
            JsonSerializer.SerializeToElement(new
            {
                pluginId = "alpha",
                tool = "sleep",
                args = JsonSerializer.SerializeToElement(new { marker, ms = 1000 }),
                sessionId = "s",
                workingDir = host.Root,
                depth = 1,
                context,
            }),
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
        await peer.SendRequestAsync(Sdk.ExtensionProtocol.PluginsRefresh, RawHost.EmptyObject(), budget.Token);

        var runId = "reserved-run-1";
        var context = new
        {
            runId,
            sessionId = "s",
            workingDirectory = host.Root,
            depth = 0,
            pluginChain = Array.Empty<string>(),
            rootRunId = runId,
        };
        var error = await Assert.ThrowsAsync<IpcRemoteException>(() => peer.SendRequestAsync(
            Sdk.ExtensionProtocol.PluginExecute,
            JsonSerializer.SerializeToElement(new
            {
                pluginId = "$run_code",
                tool = "echo",
                args = JsonSerializer.SerializeToElement(new { text = "x" }),
                sessionId = "s",
                workingDir = host.Root,
                depth = 0,
                context,
            }),
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
        await peer.SendRequestAsync(Sdk.ExtensionProtocol.PluginsRefresh, RawHost.EmptyObject(), budget.Token);
        var status = await FirstStatusAsync(peer, "alpha", budget.Token);
        await peer.SendRequestAsync(
            Sdk.ExtensionProtocol.PluginApprove,
            JsonSerializer.SerializeToElement(new
            {
                pluginId = "alpha",
                version = "1.0.0",
                sourceHash = status.SourceHash,
                dependencyHash = status.DependencyHash,
                sdkVersion = status.SdkVersion,
            }),
            budget.Token);

        var marker = Path.Combine(host.Root, "dup-holder.txt");
        var runId = "dup-run-0001";
        JsonElement ExecutePayload() => JsonSerializer.SerializeToElement(new
        {
            pluginId = "alpha",
            tool = "sleep",
            args = JsonSerializer.SerializeToElement(new { marker, ms = 20000 }),
            sessionId = "s",
            workingDir = host.Root,
            depth = 0,
            context = new
            {
                runId,
                sessionId = "s",
                workingDirectory = host.Root,
                depth = 0,
                pluginChain = Array.Empty<string>(),
                rootRunId = runId,
            },
        });

        var first = peer.SendRequestAsync(Sdk.ExtensionProtocol.PluginExecute, ExecutePayload(), budget.Token);
        await WaitForFileAsync(marker, budget.Token);

        var error = await Assert.ThrowsAsync<IpcRemoteException>(() => peer.SendRequestAsync(
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
                response = await peer.SendRequestAsync(
                    Sdk.ExtensionProtocol.PluginsStatus, RawHost.EmptyObject(), deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"no plugins.status row for '{id}' appeared within {limit.TotalSeconds:0} s.");
            }

            var element = response.Result!.Value;
            foreach (var entry in element.GetProperty("plugins").EnumerateArray())
            {
                if (entry.TryGetProperty("pluginId", out var pluginId) &&
                    pluginId.GetString() == id)
                {
                    return (
                        entry.GetProperty("sourceHash").GetString()!,
                        entry.GetProperty("dependencyHash").GetString()!,
                        entry.GetProperty("sdkVersion").GetString()!);
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
