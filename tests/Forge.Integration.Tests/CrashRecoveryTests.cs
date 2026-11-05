using System.Diagnostics;
using System.Text.Json;
using Forge.HostClient;
using Forge.Ipc;
using Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Integration.Tests;

public sealed class CrashRecoveryTests
{
    [Fact]
    public async Task Worker_crash_fails_the_inflight_call_and_the_next_call_runs_on_a_restarted_worker()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");

        var crashedPid = await fx.WorkerPidAsync("alpha");

        var crashed = await fx.InvokeAsync("alpha", "crash");
        Assert.True(crashed.IsError);
        Assert.Contains("exited", crashed.Content, StringComparison.Ordinal);
        await fx.WaitForExitAsync(crashedPid, "the crashed worker");

        var echo = await fx.InvokeAsync("alpha", "echo", """{"text":"ok"}""");
        Assert.False(echo.IsError, echo.Content);
        Assert.Equal("a:ok", echo.Content);

        var restartedPid = await fx.WorkerPidAsync("alpha");
        Assert.NotEqual(crashedPid, restartedPid);
        Assert.Equal(PluginSources.AllTools.Length, fx.ToolsOf("alpha").Count);
    }

    [Fact]
    public async Task Repeated_worker_crashes_disable_the_worker_with_a_clear_error()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var crashed = await fx.InvokeAsync("alpha", "crash");
            Assert.True(crashed.IsError, $"crash #{attempt + 1} returned: {crashed.Content}");
        }

        var disabled = await fx.InvokeAsync("alpha", "echo", """{"text":"x"}""");
        Assert.True(disabled.IsError);
        Assert.Contains("failed 4 times in a row", disabled.Content, StringComparison.Ordinal);
        Assert.Contains("stays stopped", disabled.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_re_enables_a_worker_disabled_by_repeated_crashes_and_republishes_its_tools()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-e2e-refresh-" + Guid.NewGuid().ToString("N")[..10]);
        var plugins = Path.Combine(root, "plugins");
        var data = Path.Combine(root, "data");
        var pipe = "forge-e2e-refresh-" + Guid.NewGuid().ToString("N")[..20];
        Directory.CreateDirectory(plugins);
        Directory.CreateDirectory(data);
        var plugin = Path.Combine(plugins, "alpha");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "manifest.json"), """{"id":"alpha","version":"1.0.0","source":"Plugin.cs"}""");
        File.WriteAllText(Path.Combine(plugin, "Plugin.cs"), PluginSources.Standard("alpha", echoPrefix: "a"));

        using var budget = new CancellationTokenSource(TestBudgets.Safety(TimeSpan.FromSeconds(150), TimeSpan.FromMinutes(10)));
        var ct = budget.Token;
        using var hostProcess = StartRawHost(pipe, plugins, data);
        await using var transport = await ConnectRawAsync(pipe, ct);
        await using var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = TimeSpan.FromSeconds(60) });
        var registers = new List<IpcMessage>();
        peer.NotificationReceived += message =>
        {
            if (message.Operation == ExtensionProtocol.ToolsRegister)
            {
                lock (registers)
                {
                    registers.Add(message);
                }
            }
        };
        await peer.StartAsync(ct);
        try
        {
            await peer.SendRequestAsync(ExtensionProtocol.PluginsRefresh, Empty(), ct);
            var record = ApprovalDatabase.ReadRecord(data, "alpha", "1.0.0")
                ?? throw new InvalidOperationException("the host persisted no approval record for alpha 1.0.0");
            await peer.SendRequestAsync(ExtensionProtocol.PluginApprove, ApproveArgs(record), ct);
            await WaitForCountAsync(registers, 1, ct, "the first tools.register");
            Assert.Equal(PluginSources.AllTools.Length, DeclaredTools(registers[0]));
            var pidBefore = await ExecuteAsync(peer, "alpha", "pid", "{}", root, ct);

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var crashed = await Assert.ThrowsAsync<IpcRemoteException>(() =>
                    ExecuteRequestAsync(peer, "alpha", "crash", "{}", root, ct));
                Assert.Equal("worker_failed", crashed.Code);
            }

            var disabled = await Assert.ThrowsAsync<IpcRemoteException>(() =>
                ExecuteRequestAsync(peer, "alpha", "echo", """{"text":"x"}""", root, ct));
            Assert.Equal("worker_disabled", disabled.Code);
            lock (registers)
            {
                Assert.Single(registers);
            }

            await peer.SendRequestAsync(ExtensionProtocol.PluginsRefresh, Empty(), ct);
            await WaitForCountAsync(registers, 2, ct, "the tools.register republished by the refresh");
            Assert.Equal(PluginSources.AllTools.Length, DeclaredTools(registers[1]));

            Assert.Equal("a:revived", await ExecuteAsync(peer, "alpha", "echo", """{"text":"revived"}""", root, ct));
            Assert.NotEqual(pidBefore, await ExecuteAsync(peer, "alpha", "pid", "{}", root, ct));
        }
        finally
        {
            await peer.StopAsync(CancellationToken.None);
            KillQuietly(hostProcess);
            await hostProcess.WaitForExitAsync(CancellationToken.None);
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Host_crash_fails_pending_calls_keeps_the_tool_snapshot_and_leaves_no_orphan_processes()
    {
        await using var fx = new E2eFixture();
        var marker = Path.Combine(fx.Root, "sleep-started.txt");
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");

        var workerPid = await fx.WorkerPidAsync("alpha");
        var hostPid = fx.Client.HostProcessId ?? throw new InvalidOperationException("The client did not start a host.");
        Assert.True(E2eFixture.IsRunning(hostPid));

        var statusEvents = new List<(HostStatus Status, string? Detail)>();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Client.StatusChanged += (status, detail) =>
        {
            lock (statusEvents)
            {
                statusEvents.Add((status, detail));
            }

            if (status == HostStatus.Failed)
            {
                failed.TrySetResult();
            }
        };

        var pending = fx.InvokeAsync("alpha", "sleep", JsonSerializer.Serialize(new { ms = 60_000, marker }));
        await fx.WaitUntilAsync(() => File.Exists(marker), "the sleeping tool to start");

        Process.GetProcessById(hostPid).Kill(entireProcessTree: false);

        await Assert.ThrowsAnyAsync<IOException>(() => pending);
        await failed.Task.WaitAsync(fx.Ct);

        Assert.Equal(HostStatus.Failed, fx.Client.Status);
        lock (statusEvents)
        {
            Assert.Contains(statusEvents, entry => entry.Status == HostStatus.Failed && !string.IsNullOrEmpty(entry.Detail));
        }

        Assert.Equal(PluginSources.AllTools.Length, fx.ToolsOf("alpha").Count);
        Assert.Equal(PluginSources.AllTools.Length, fx.Client.Tools.Count);

        await Assert.ThrowsAnyAsync<IOException>(() => fx.InvokeAsync("alpha", "echo", """{"text":"late"}"""));

        await fx.WaitForExitAsync(hostPid, "the killed host");
        await fx.WaitForExitAsync(workerPid, "the worker of the killed host");

        await fx.Client.StopAsync(fx.Ct);
        Assert.Equal(HostStatus.Stopped, fx.Client.Status);
        Assert.False(E2eFixture.IsRunning(hostPid));
        Assert.False(E2eFixture.IsRunning(workerPid));
    }

    private static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();

    private static JsonElement ApproveArgs(ApprovalRecord record) => JsonSerializer.SerializeToElement(new
    {
        pluginId = "alpha",
        version = "1.0.0",
        sourceHash = record.SourceHash,
        dependencyHash = record.DependencyHash,
        sdkVersion = record.SdkVersion,
    });

    private static async Task<string> ExecuteAsync(IpcPeer peer, string pluginId, string tool, string argsJson, string workingDir, CancellationToken ct)
    {
        var response = await ExecuteRequestAsync(peer, pluginId, tool, argsJson, workingDir, ct);
        Assert.True(response.Result is { ValueKind: JsonValueKind.Object });
        Assert.False(response.Result!.Value.GetProperty("isError").GetBoolean());
        return response.Result!.Value.GetProperty("content").GetString() ?? string.Empty;
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

    private static int DeclaredTools(IpcMessage register)
    {
        Assert.True(register.Arguments is { ValueKind: JsonValueKind.Object });
        Assert.Equal("alpha", register.Arguments!.Value.GetProperty("pluginId").GetString());
        return register.Arguments!.Value.GetProperty("tools").GetArrayLength();
    }

    /// <summary>Waits for <paramref name="expected" /> registrations; the bound is a safety net, the arrival of the
    /// frame is the condition.</summary>
    private static async Task WaitForCountAsync(List<IpcMessage> registers, int expected, CancellationToken ct, string what)
    {
        var start = Stopwatch.GetTimestamp();
        var limit = TestBudgets.Condition;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        while (true)
        {
            lock (registers)
            {
                if (registers.Count >= expected)
                {
                    return;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"waited {Stopwatch.GetElapsedTime(start).TotalSeconds:0.###} s for {what}; the host published " +
                    $"{registers.Count} of {expected}.");
            }
        }
    }

    private static Process StartRawHost(string pipe, string plugins, string data)
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
        var process = Process.Start(info) ?? throw new InvalidOperationException("The raw host did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static Task<NamedPipeClientTransport> ConnectRawAsync(string pipe, CancellationToken ct) =>
        PipeWait.ConnectAsync(pipe, () => "The host is started by this test.", ct);

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
