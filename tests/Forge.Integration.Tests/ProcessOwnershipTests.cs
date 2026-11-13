using System.Diagnostics;
using System.Text.Json;
using Forge.HostClient;
using Forge.Ipc;
using Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>
/// Process-tree ownership: a worker that starts a long-lived child process must not leave that child behind when the
/// worker crashes, when the plugin is revoked, when the host stops normally, when the host is killed, or when the tool
/// times out — and a worker whose containment cannot be established must never run plugin code at all.
/// </summary>
public sealed class ProcessOwnershipTests
{
    /// <summary>How long a grandchild may outlive the event that should have killed it. A safety net for an event the
    /// product starts on its own, so it grows with what this run's process starts cost.</summary>
    private static readonly TimeSpan DeathLimit = TestBudgets.Safety(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(120));

    [Fact]
    public async Task A_worker_that_exits_after_starting_a_child_leaves_no_child_behind()
    {
        await using var fx = new E2eFixture();
        var marker = Path.Combine(fx.Root, "grandchild.pid");
        fx.WritePlugin("alpha", PluginSources.Spawnable("alpha"));
        await fx.StartAndApproveAsync("alpha", PluginSources.SpawnableTools.Length);

        var crashed = await fx.InvokeAsync(
            "alpha",
            PluginSources.SpawnAndCrashTool,
            JsonSerializer.Serialize(new { marker }));
        Assert.True(crashed.IsError, crashed.Content);

        var grandchild = await ReadPidAsync(fx, marker);
        await WaitForDeathAsync(fx, grandchild, "the child of the crashed worker");

        await fx.AssertToolContentAsync("alpha", "echo", """{"text":"after"}""", "a:after");
    }

    [Fact]
    public async Task Revoking_a_plugin_kills_the_child_its_worker_started()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Spawnable("alpha"));
        await fx.StartAndApproveAsync("alpha", PluginSources.SpawnableTools.Length);

        var grandchild = await SpawnAsync(fx, "alpha");
        Assert.True(E2eFixture.IsRunning(grandchild), $"the child process of the worker (pid {grandchild}) is not running");

        await fx.Client.RevokePluginAsync("alpha", "1.0.0", fx.Ct);

        await WaitForDeathAsync(fx, grandchild, "the child of the revoked worker");
    }

    [Fact]
    public async Task Stopping_the_host_kills_the_child_the_worker_started()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Spawnable("alpha"));
        await fx.StartAndApproveAsync("alpha", PluginSources.SpawnableTools.Length);

        var grandchild = await SpawnAsync(fx, "alpha");
        Assert.True(E2eFixture.IsRunning(grandchild), $"the child process of the worker (pid {grandchild}) is not running");

        await fx.Client.StopAsync(fx.Ct);
        Assert.Equal(HostStatus.Stopped, fx.Client.Status);

        await WaitForDeathAsync(fx, grandchild, "the child of the worker of the stopped host");
    }

    [Fact]
    public async Task Killing_the_host_kills_the_child_the_worker_started()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Spawnable("alpha"));
        await fx.StartAndApproveAsync("alpha", PluginSources.SpawnableTools.Length);

        var grandchild = await SpawnAsync(fx, "alpha");
        var workerPid = await fx.WorkerPidAsync("alpha");
        var hostPid = fx.Client.HostProcessId ?? throw new InvalidOperationException("The client did not start a host.");
        Assert.True(E2eFixture.IsRunning(grandchild), $"the child process of the worker (pid {grandchild}) is not running");

        Process.GetProcessById(hostPid).Kill(entireProcessTree: false);

        await WaitForDeathAsync(fx, grandchild, "the child of the worker of the killed host");
        await WaitForDeathAsync(fx, workerPid, "the worker of the killed host");
    }

    [Fact]
    public async Task A_tool_that_times_out_kills_the_child_its_worker_started()
    {
        await using var fx = new E2eFixture(["--tool-timeout", "3"]);
        var marker = Path.Combine(fx.Root, "grandchild.pid");
        fx.WritePlugin("alpha", PluginSources.Spawnable("alpha"));
        await fx.StartAndApproveAsync("alpha", PluginSources.SpawnableTools.Length);

        var timedOut = await fx.InvokeAsync(
            "alpha",
            PluginSources.SpawnTool,
            JsonSerializer.Serialize(new { marker, ms = 60_000 }));
        Assert.True(timedOut.IsError, timedOut.Content);
        Assert.Contains("exceeded", timedOut.Content, StringComparison.Ordinal);

        var grandchild = await ReadPidAsync(fx, marker);
        await WaitForDeathAsync(fx, grandchild, "the child of the worker that timed out");

        await fx.AssertToolContentAsync("alpha", "echo", """{"text":"next"}""", "a:next");
    }

    [Fact]
    public async Task A_worker_that_cannot_be_contained_is_killed_and_the_start_fails()
    {
        HostTestHooks.Require();
        var layout = RawHostLayout.Create("forge-e2e-owner-");
        var plugin = Path.Combine(layout.Plugins, "alpha");
        var marker = Path.Combine(layout.Root, "grandchild.pid");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "manifest.json"), """{"id":"alpha","version":"1.0.0","source":"Plugin.cs"}""");
        File.WriteAllText(Path.Combine(plugin, "Plugin.cs"), PluginSources.Spawnable("alpha"));

        using var budget = new CancellationTokenSource(TestBudgets.Safety(TimeSpan.FromSeconds(150), TimeSpan.FromMinutes(10)));
        var ct = budget.Token;
        var output = new List<string>();
        await using var host = RawHost.Start(
            layout,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["FORGE_HOST_TEST_FAIL_JOB_ASSIGN"] = "1",
            },
            output);
        await using var transport = await RawHost.ConnectTransportAsync(layout.Pipe, ct);
        await using var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = TimeSpan.FromSeconds(60) });
        await peer.StartAsync(ct);
        try
        {
            await peer.SendRequestAsync(ExtensionProtocol.PluginsRefresh, RawHost.EmptyObject(), ct);
            var record = ApprovalDatabase.ReadRecord(layout.Data, "alpha", "1.0.0")
                ?? throw new InvalidOperationException("the host persisted no approval record for alpha 1.0.0");
            await peer.SendRequestAsync(ExtensionProtocol.PluginApprove, ApproveArgs(record), ct);

            var failed = await Assert.ThrowsAsync<IpcRemoteException>(() =>
                ExecuteRequestAsync(
                    peer, "alpha", PluginSources.SpawnTool, JsonSerializer.Serialize(new { marker }), layout.Root, ct));
            Assert.Equal("worker_start_failed", failed.Code);

            Assert.False(File.Exists(marker), "plugin code ran although the worker could not be contained");
            await WaitForTextAsync(output, "FORGE_HOST_TEST_FAIL_JOB_ASSIGN", ct);
        }
        finally
        {
            await peer.StopAsync(CancellationToken.None);
            await host.StopAsync();
        }
    }

    /// <summary>Calls the <c>spawn</c> tool and returns the pid of the child process it started.</summary>
    private static async Task<int> SpawnAsync(E2eFixture fx, string pluginId)
    {
        var result = await fx.InvokeAsync(pluginId, PluginSources.SpawnTool);
        Assert.False(result.IsError, result.Content);
        return int.Parse(result.Content);
    }

    private static async Task<int> ReadPidAsync(E2eFixture fx, string path)
    {
        var pid = 0;
        await fx.WaitUntilAsync(
            () => File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out pid),
            $"the pid written to '{path}'");
        return pid;
    }

    private static async Task WaitForDeathAsync(E2eFixture fx, int processId, string what)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(fx.Ct);
        var clock = Stopwatch.StartNew();
        deadline.CancelAfter(DeathLimit);
        while (E2eFixture.IsRunning(processId))
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            catch (OperationCanceledException) when (!fx.Ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"{what} (pid {processId}) was still running {clock.Elapsed.TotalSeconds:0.###} s after it should have " +
                    $"exited; the limit is {DeathLimit.TotalSeconds:0} s on a machine whose slowest observed process start " +
                    $"took {TestBudgets.ObservedHostStart.TotalSeconds:0.###} s.");
            }
        }
    }

    private static async Task WaitForTextAsync(List<string> output, string text, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(E2eFixture.WaitLimit);
        while (true)
        {
            lock (output)
            {
                if (output.Exists(line => line.Contains(text, StringComparison.Ordinal)))
                {
                    return;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"The host never reported '{text}'.");
            }
        }
    }

    private static JsonElement ApproveArgs(ApprovalRecord record) => JsonSerializer.SerializeToElement(new
    {
        pluginId = "alpha",
        version = "1.0.0",
        sourceHash = record.SourceHash,
        dependencyHash = record.DependencyHash,
        sdkVersion = record.SdkVersion,
    });

    private static Task<IpcMessage> ExecuteRequestAsync(IpcPeer peer, string pluginId, string tool, string argsJson, string workingDir, CancellationToken ct)
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            pluginId,
            tool,
            args = JsonDocument.Parse(argsJson).RootElement.Clone(),
            sessionId = "raw",
            workingDir,
            depth = 0,
        });
        return peer.SendRequestAsync(ExtensionProtocol.PluginExecute, args, ct);
    }
}
