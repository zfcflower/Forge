using System.Diagnostics;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Approval;
using Forge.HostClient;
using Forge.Ipc;
using Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Integration.Tests;

public sealed class ApprovalLifecycleTests
{
    [Fact]
    public async Task Unapproved_plugin_registers_no_tools_and_approval_publishes_them()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));

        var changes = 0;
        fx.Client.ToolsChanged += () => Interlocked.Increment(ref changes);

        await fx.StartAsync();
        Assert.Equal(HostStatus.Running, fx.Client.Status);

        var draft = await fx.WaitForRecordAsync("alpha");
        Assert.Equal("Draft", draft.Status);
        Assert.Empty(fx.Client.Tools);
        Assert.Empty(fx.ToolsOf("alpha"));

        await fx.ApproveAsync("alpha");

        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "six tools of alpha");
        Assert.True(Volatile.Read(ref changes) >= 1);
        Assert.Equal(PluginSources.AllTools.OrderBy(name => name, StringComparer.Ordinal),
            fx.Client.Tools.Select(tool => tool.Definition.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.All(fx.Client.Tools, tool => Assert.Equal("alpha", ((ExtensionHostTool)tool).PluginId));

        var approved = fx.ReadRecord("alpha");
        Assert.NotNull(approved);
        Assert.Equal("Approved", approved.Status);

        var echo = await fx.InvokeAsync("alpha", "echo", """{"text":"hello"}""");
        Assert.False(echo.IsError, echo.Content);
        Assert.Equal("a:hello", echo.Content);
    }

    [Fact]
    public async Task Unapproved_plugin_rejects_plugin_execute_over_the_protocol()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-e2e-raw-" + Guid.NewGuid().ToString("N")[..10]);
        var plugins = Path.Combine(root, "plugins");
        var data = Path.Combine(root, "data");
        var pipe = "forge-e2e-raw-" + Guid.NewGuid().ToString("N")[..20];
        Directory.CreateDirectory(plugins);
        Directory.CreateDirectory(data);
        var plugin = Path.Combine(plugins, "alpha");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "manifest.json"), """{"id":"alpha","version":"1.0.0","source":"Plugin.cs"}""");
        File.WriteAllText(Path.Combine(plugin, "Plugin.cs"), PluginSources.Standard("alpha", echoPrefix: "a"));

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = budget.Token;
        using var hostProcess = StartRawHost(pipe, plugins, data);
        await using var transport = await ConnectRawAsync(pipe, ct);
        await using var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = TimeSpan.FromSeconds(60) });
        var registrations = new List<IpcMessage>();
        var registered = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.NotificationReceived += message =>
        {
            if (message.Operation == ExtensionProtocol.ToolsRegister)
            {
                registrations.Add(message);
                registered.TrySetResult(message);
            }
        };
        await peer.StartAsync(ct);
        try
        {
            await peer.SendRequestAsync(ExtensionProtocol.PluginsRefresh, Empty(), ct);

            var executeArgs = JsonSerializer.SerializeToElement(new
            {
                pluginId = "alpha",
                tool = "echo",
                args = new { text = "hi" },
                sessionId = "raw",
                workingDir = root,
                depth = 0,
            });
            var rejected = await Assert.ThrowsAsync<IpcRemoteException>(
                () => peer.SendRequestAsync(ExtensionProtocol.PluginExecute, executeArgs, ct));
            Assert.Equal("plugin_not_approved", rejected.Code);

            Assert.Empty(registrations);

            var record = ApprovalDatabase.ReadRecord(data, "alpha", "1.0.0")
                ?? throw new InvalidOperationException("the host persisted no approval record for alpha 1.0.0");
            var approveArgs = JsonSerializer.SerializeToElement(new
            {
                pluginId = "alpha",
                version = "1.0.0",
                sourceHash = record.SourceHash,
                dependencyHash = record.DependencyHash,
                sdkVersion = record.SdkVersion,
            });
            await peer.SendRequestAsync(ExtensionProtocol.PluginApprove, approveArgs, ct);

            var notification = await registered.Task.WaitAsync(ct);
            Assert.True(notification.Arguments is { ValueKind: JsonValueKind.Object });
            var declared = notification.Arguments!.Value.GetProperty("tools");
            Assert.Equal(PluginSources.AllTools.Length, declared.GetArrayLength());
            var echoDeclaration = declared.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "echo");
            Assert.Equal(JsonValueKind.Number, echoDeclaration.GetProperty("sideEffect").ValueKind);
            Assert.Equal(0, echoDeclaration.GetProperty("sideEffect").GetInt32());

            var executed = await peer.SendRequestAsync(ExtensionProtocol.PluginExecute, executeArgs, ct);
            Assert.Equal("a:hi", executed.Result!.Value.GetProperty("content").GetString());
            Assert.False(executed.Result!.Value.GetProperty("isError").GetBoolean());
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
    public async Task Source_edit_blocks_execution_until_reapproved_and_revoke_blocks_again()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "old"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");

        var oldEcho = fx.ToolOf("alpha", "echo");
        var before = await fx.InvokeAsync(oldEcho, """{"text":"x"}""");
        Assert.Equal("old:x", before.Content);
        var originalRecord = fx.ReadRecord("alpha")!;

        var edited = PluginSources.Standard("alpha", echoPrefix: "new");
        fx.EditSource("alpha", edited);

        var blockedBeforeRefresh = await fx.InvokeAsync(oldEcho, """{"text":"x"}""");
        Assert.True(blockedBeforeRefresh.IsError);
        Assert.Contains("changed on disk", blockedBeforeRefresh.Content, StringComparison.Ordinal);

        await fx.Client.RefreshPluginsAsync(fx.Ct);
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == 0, "alpha tools to be unregistered");
        Assert.Empty(fx.Client.Tools);

        var changed = fx.ReadRecord("alpha")!;
        Assert.NotEqual(originalRecord.SourceHash, changed.SourceHash);
        Assert.Equal(Hashing.SourceHash(edited), changed.SourceHash);
        Assert.Equal("Approved", changed.Status);

        var blockedAfterRefresh = await fx.InvokeAsync(oldEcho, """{"text":"x"}""");
        Assert.True(blockedAfterRefresh.IsError);
        Assert.Contains("must not run", blockedAfterRefresh.Content, StringComparison.Ordinal);

        var stale = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            fx.Client.ApprovePluginAsync("alpha", "1.0.0", originalRecord.SourceHash, originalRecord.DependencyHash, originalRecord.SdkVersion, fx.Ct));
        Assert.Equal("approval_mismatch", stale.Code);
        Assert.Empty(fx.Client.Tools);

        await fx.Client.ApprovePluginAsync("alpha", "1.0.0", changed.SourceHash, changed.DependencyHash, changed.SdkVersion, fx.Ct);
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools after re-approval");

        var reapproved = await fx.InvokeAsync("alpha", "echo", """{"text":"x"}""");
        Assert.False(reapproved.IsError, reapproved.Content);
        Assert.Equal("new:x", reapproved.Content);

        var reapprovedEcho = fx.ToolOf("alpha", "echo");
        await fx.Client.RevokePluginAsync("alpha", "1.0.0", fx.Ct);
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == 0, "alpha tools to be unregistered after revoke");
        Assert.Equal("Draft", fx.ReadRecord("alpha")!.Status);

        var revoked = await fx.InvokeAsync(reapprovedEcho, """{"text":"x"}""");
        Assert.True(revoked.IsError);
        Assert.Contains("must not run", revoked.Content, StringComparison.Ordinal);
    }

    private static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();

    private static Process StartRawHost(string pipe, string plugins, string data)
    {
        var info = new System.Diagnostics.ProcessStartInfo
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
