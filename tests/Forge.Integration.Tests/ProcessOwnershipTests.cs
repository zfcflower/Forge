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
    /// <summary>How long a grandchild may outlive the event that should have killed it.</summary>
    private static readonly TimeSpan DeathLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_worker_that_exits_after_starting_a_child_leaves_no_child_behind()
    {
        await using var fx = new E2eFixture();
        var marker = Path.Combine(fx.Root, "grandchild.pid");
        fx.WritePlugin("alpha", PluginSources.Spawnable("alpha"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.SpawnableTools.Length, "alpha tools");

        var crashed = await fx.InvokeAsync(
            "alpha",
            PluginSources.SpawnAndCrashTool,
            JsonSerializer.Serialize(new { marker }));
        Assert.True(crashed.IsError, crashed.Content);

        var grandchild = await ReadPidAsync(fx, marker);
        await WaitForDeathAsync(fx, grandchild, "the child of the crashed worker");

        var echo = await fx.InvokeAsync("alpha", "echo", """{"text":"after"}""");
        Assert.False(echo.IsError, echo.Content);
        Assert.Equal("a:after", echo.Content);
    }

    [Fact]
    public async Task Revoking_a_plugin_kills_the_child_its_worker_started()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Spawnable("alpha"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.SpawnableTools.Length, "alpha tools");

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
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.SpawnableTools.Length, "alpha tools");

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
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.SpawnableTools.Length, "alpha tools");

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
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.SpawnableTools.Length, "alpha tools");

        var timedOut = await fx.InvokeAsync(
            "alpha",
            PluginSources.SpawnTool,
            JsonSerializer.Serialize(new { marker, ms = 60_000 }));
        Assert.True(timedOut.IsError, timedOut.Content);
        Assert.Contains("exceeded", timedOut.Content, StringComparison.Ordinal);

        var grandchild = await ReadPidAsync(fx, marker);
        await WaitForDeathAsync(fx, grandchild, "the child of the worker that timed out");

        var echo = await fx.InvokeAsync("alpha", "echo", """{"text":"next"}""");
        Assert.False(echo.IsError, echo.Content);
        Assert.Equal("a:next", echo.Content);
    }

    [Fact]
    public async Task A_worker_that_cannot_be_contained_is_killed_and_the_start_fails()
    {
        HostTestHooks.Require();
        var root = Path.Combine(Path.GetTempPath(), "forge-e2e-owner-" + Guid.NewGuid().ToString("N")[..10]);
        var plugins = Path.Combine(root, "plugins");
        var data = Path.Combine(root, "data");
        var plugin = Path.Combine(plugins, "alpha");
        var marker = Path.Combine(root, "grandchild.pid");
        var pipe = "forge-e2e-owner-" + Guid.NewGuid().ToString("N")[..20];
        Directory.CreateDirectory(plugin);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(plugin, "manifest.json"), """{"id":"alpha","version":"1.0.0","source":"Plugin.cs"}""");
        File.WriteAllText(Path.Combine(plugin, "Plugin.cs"), PluginSources.Spawnable("alpha"));

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = budget.Token;
        var output = new List<string>();
        using var host = StartRawHost(pipe, plugins, data, output, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FORGE_HOST_TEST_FAIL_JOB_ASSIGN"] = "1",
        });
        await using var transport = await ConnectRawAsync(pipe, ct);
        await using var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = TimeSpan.FromSeconds(60) });
        await peer.StartAsync(ct);
        try
        {
            await peer.SendRequestAsync(ExtensionProtocol.PluginsRefresh, Empty(), ct);
            var record = ReadApprovalRecord(data, "alpha");
            await peer.SendRequestAsync(ExtensionProtocol.PluginApprove, ApproveArgs(record), ct);

            var failed = await Assert.ThrowsAsync<IpcRemoteException>(() =>
                ExecuteRequestAsync(peer, "alpha", PluginSources.SpawnTool, JsonSerializer.Serialize(new { marker }), root, ct));
            Assert.Equal("worker_start_failed", failed.Code);

            Assert.False(File.Exists(marker), "plugin code ran although the worker could not be contained");
            await WaitForTextAsync(output, "FORGE_HOST_TEST_FAIL_JOB_ASSIGN", ct);
        }
        finally
        {
            await peer.StopAsync(CancellationToken.None);
            KillQuietly(host);
            await host.WaitForExitAsync(CancellationToken.None);
            TryDeleteDirectory(root);
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
        deadline.CancelAfter(DeathLimit);
        while (E2eFixture.IsRunning(processId))
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            catch (OperationCanceledException) when (!fx.Ct.IsCancellationRequested)
            {
                throw new TimeoutException($"{what} (pid {processId}) was still running {DeathLimit.TotalSeconds:0} s after it should have exited.");
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

    private static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();

    private static JsonElement ApproveArgs(JsonElement record) => JsonSerializer.SerializeToElement(new
    {
        pluginId = "alpha",
        version = "1.0.0",
        sourceHash = record.GetProperty("sourceHash").GetString(),
        dependencyHash = record.GetProperty("dependencyHash").GetString(),
        sdkVersion = record.GetProperty("sdkVersion").GetString(),
    });

    private static JsonElement ReadApprovalRecord(string dataDirectory, string pluginId)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDirectory, "approvals.json")));
        return document.RootElement.EnumerateArray()
            .Single(element => element.GetProperty("pluginId").GetString() == pluginId)
            .Clone();
    }

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

    private static Process StartRawHost(
        string pipe,
        string plugins,
        string data,
        List<string> output,
        IReadOnlyDictionary<string, string> environment)
    {
        var info = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(E2eFixture.HostAssemblyPath);
        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(pipe);
        info.ArgumentList.Add("--plugins");
        info.ArgumentList.Add(plugins);
        info.ArgumentList.Add("--data");
        info.ArgumentList.Add(data);
        foreach (var pair in environment)
        {
            info.Environment[pair.Key] = pair.Value;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("The raw host did not start.");
        void Append(object _, DataReceivedEventArgs args)
        {
            if (!string.IsNullOrEmpty(args.Data))
            {
                lock (output)
                {
                    output.Add(args.Data);
                }
            }
        }

        process.OutputDataReceived += Append;
        process.ErrorDataReceived += Append;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task<NamedPipeClientTransport> ConnectRawAsync(string pipe, CancellationToken ct)
    {
        while (true)
        {
            var transport = new NamedPipeClientTransport(pipe, new IpcPipeOptions { ConnectTimeout = TimeSpan.FromSeconds(2) });
            try
            {
                await transport.StartAsync(ct);
                return transport;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
            {
                await transport.DisposeAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
            }
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
