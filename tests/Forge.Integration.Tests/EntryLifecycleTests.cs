using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>
/// Entry-lifecycle races: a request that captured a plugin entry before the per-plugin gate must not act on that entry
/// after a <c>plugins.refresh</c> replaced or removed it. The hook below pauses a request between the two, so each test
/// can change the plugin directory and refresh while the request is already holding the earlier entry.
/// </summary>
public sealed class EntryLifecycleTests
{
    private const int TaskCount = 8;
    private const int Iterations = 50;

    /// <summary>Errors the host documents for concurrent approval changes, refreshes and executions.</summary>
    private static readonly string[] DocumentedCodes =
    [
        "unknown_plugin",
        "plugin_changed",
        "plugin_not_approved",
        "plugin_not_loadable",
        "unknown_tool",
        "version_mismatch",
        "approval_mismatch",
        "worker_failed",
        "worker_restarted",
        "worker_not_loaded",
        "worker_disabled",
        "tool_timeout",
    ];

    [Fact]
    public async Task Execute_paused_before_its_gate_never_runs_a_plugin_that_refresh_removed()
    {
        await using var fx = new E2eFixture();
        using var hook = EntryCaptureHook.Activate(fx.Root, fx.PipeName);
        var loadLog = Path.Combine(fx.Root, "loads.txt");
        fx.WritePlugin("alpha", TrackedSource("alpha", "a", loadLog));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == 1, "the tools of alpha");

        await fx.WaitUntilAsync(() => File.Exists(loadLog), "the worker of alpha to load the plugin");
        var workerPid = PidOf(Assert.Single(await ReadLinesAsync(loadLog)));

        hook.Arm();
        var pending = fx.InvokeAsync(fx.ToolOf("alpha", "echo"), """{"text":"x"}""");
        var capture = await hook.WaitForCaptureAsync("alpha");

        File.Delete(Path.Combine(fx.PluginDirectory("alpha"), "manifest.json"));
        await fx.Client.RefreshPluginsAsync(fx.Ct);
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == 0, "the tools of the removed plugin to be unregistered");
        await fx.WaitForExitAsync(workerPid, "the worker of the removed plugin");

        hook.Release(capture);

        var result = await pending;
        hook.Disarm();
        Assert.True(result.IsError, result.Content);
        Assert.Equal("unknown_plugin", ErrorCode(result.Content));

        await Task.Delay(TimeSpan.FromMilliseconds(750), fx.Ct);
        Assert.Empty(fx.ToolsOf("alpha"));
        Assert.Empty(fx.Client.Tools);
        Assert.Single(await ReadLinesAsync(loadLog));
    }

    [Fact]
    public async Task Execute_paused_before_its_gate_never_runs_the_version_a_prebuilt_switch_replaced()
    {
        await using var fx = new E2eFixture();
        using var hook = EntryCaptureHook.Activate(fx.Root, fx.PipeName);
        fx.WritePlugin("bodyold", PluginSources.Standard("bodyold", echoPrefix: "old"));
        fx.WritePlugin("bodynew", PluginSources.Standard("bodynew", echoPrefix: "new"));
        await fx.StartAsync();

        var directory = Directory.CreateDirectory(fx.PluginDirectory("alpha")).FullName;
        Directory.CreateDirectory(Path.Combine(directory, "bin", "1.0.0"));
        Directory.CreateDirectory(Path.Combine(directory, "bin", "2.0.0"));
        File.Copy(SingleCompiled(fx, "bodyold"), Path.Combine(directory, "bin", "1.0.0", "plugin.dll"));
        File.Copy(SingleCompiled(fx, "bodynew"), Path.Combine(directory, "bin", "2.0.0", "plugin.dll"));
        WritePrebuiltManifest(fx, "alpha", "1.0.0");

        await fx.Client.RefreshPluginsAsync(fx.Ct);
        await fx.ApproveAsync("alpha", "1.0.0");
        await fx.WaitUntilAsync(
            () => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length,
            "the tools of alpha 1.0.0");

        var first = await fx.InvokeAsync("alpha", PluginSources.EchoTool, """{"text":"x"}""");
        Assert.False(first.IsError, first.Content);
        Assert.Equal("old:x", first.Content);

        hook.Arm();
        var pending = fx.InvokeAsync(fx.ToolOf("alpha", PluginSources.EchoTool), """{"text":"y"}""");
        var capture = await hook.WaitForCaptureAsync("alpha");

        WritePrebuiltManifest(fx, "alpha", "2.0.0");
        await fx.Client.RefreshPluginsAsync(fx.Ct);
        await fx.WaitUntilAsync(
            () => fx.ToolsOf("alpha").Count == 0,
            "the tools of the superseded version to be unregistered");

        hook.Release(capture);

        var result = await pending;
        hook.Disarm();
        Assert.True(result.IsError, result.Content);
        Assert.Equal("plugin_changed", ErrorCode(result.Content));
        Assert.DoesNotContain("old:", result.Content, StringComparison.Ordinal);

        await fx.ApproveAsync("alpha", "2.0.0");
        await fx.WaitUntilAsync(
            () => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length,
            "the tools of alpha 2.0.0");

        var second = await fx.InvokeAsync("alpha", PluginSources.EchoTool, """{"text":"z"}""");
        Assert.False(second.IsError, second.Content);
        Assert.Equal("new:z", second.Content);
    }

    [Fact]
    public async Task Concurrent_approve_revoke_refresh_and_execute_stay_consistent()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "the tools of alpha");

        var staleEcho = fx.ToolOf("alpha", PluginSources.EchoTool);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(fx.Ct);
        bound.CancelAfter(TimeSpan.FromSeconds(110));

        var churn = new Task[TaskCount];
        for (var index = 0; index < churn.Length; index++)
        {
            var seed = index + 1;
            churn[index] = Task.Run(() => ChurnAsync(fx, staleEcho, seed, bound.Token), CancellationToken.None);
        }

        try
        {
            await Task.WhenAll(churn).WaitAsync(TimeSpan.FromSeconds(115), fx.Ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                "The approve/revoke/refresh/execute churn did not finish within 115 s; the host stopped making progress.");
        }

        await fx.Client.RefreshPluginsAsync(fx.Ct);
        var record = fx.ReadRecord("alpha");
        Assert.NotNull(record);
        if (string.Equals(record.Status, "Approved", StringComparison.Ordinal))
        {
            await fx.WaitUntilAsync(
                () => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length,
                "the tools of the approved plugin");
            var allowed = await fx.InvokeAsync("alpha", PluginSources.EchoTool, """{"text":"final"}""");
            Assert.False(allowed.IsError, allowed.Content);
            Assert.Equal("a:final", allowed.Content);
            return;
        }

        Assert.Equal("Draft", record.Status);
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == 0, "no tools for the plugin that is not approved");
        var blocked = await fx.InvokeAsync(staleEcho, """{"text":"final"}""");
        Assert.True(blocked.IsError, blocked.Content);
        Assert.Equal("plugin_not_approved", ErrorCode(blocked.Content));
    }

    private static async Task ChurnAsync(E2eFixture fx, ITool echo, int seed, CancellationToken ct)
    {
        var random = new Random(seed * 7919 + 17);
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            switch (random.Next(8))
            {
                case < 4:
                    var result = await fx.InvokeAsync(echo, """{"text":"churn"}""");
                    if (result.IsError)
                    {
                        var code = ErrorCode(result.Content);
                        Assert.True(code is not null, $"the host failed without a documented error code: {result.Content}");
                        Assert.Contains(code, DocumentedCodes);
                    }
                    else
                    {
                        Assert.Equal("a:churn", result.Content);
                    }

                    break;
                case < 6:
                    await fx.Client.RefreshPluginsAsync(ct);
                    break;
                case 6:
                    await ApproveAsync(fx, ct);
                    break;
                default:
                    await RevokeAsync(fx, ct);
                    break;
            }
        }
    }

    private static async Task ApproveAsync(E2eFixture fx, CancellationToken ct)
    {
        var record = ReadRecord(fx, "alpha");
        if (record is null)
        {
            return;
        }

        try
        {
            await fx.Client.ApprovePluginAsync(
                record.PluginId,
                record.Version,
                record.SourceHash,
                record.DependencyHash,
                record.SdkVersion,
                ct);
        }
        catch (IpcRemoteException exception)
        {
            Assert.Contains(exception.Code, DocumentedCodes);
        }
    }

    private static async Task RevokeAsync(E2eFixture fx, CancellationToken ct)
    {
        var record = ReadRecord(fx, "alpha");
        try
        {
            await fx.Client.RevokePluginAsync("alpha", record?.Version ?? "1.0.0", ct);
        }
        catch (IpcRemoteException exception)
        {
            Assert.Contains(exception.Code, DocumentedCodes);
        }
    }

    /// <summary>Reads <c>approvals.json</c> while other tasks rewrite it.</summary>
    private static ApprovalRecord? ReadRecord(E2eFixture fx, string pluginId)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return fx.ReadRecord(pluginId);
            }
            catch (Exception exception) when (attempt < 20 && exception is IOException or JsonException)
            {
                Thread.Sleep(5);
            }
        }
    }

    /// <summary>The error code of a failed plugin call, or <c>null</c> when the failure carries none.</summary>
    private static string? ErrorCode(string content)
    {
        const string marker = "IPC remote error '";
        var start = content.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = content.IndexOf('\'', start);
        return end < 0 ? null : content[start..end];
    }

    private static string SingleCompiled(E2eFixture fx, string pluginId) =>
        Assert.Single(Directory.GetFiles(
            Path.Combine(fx.PluginDirectory(pluginId), "bin"),
            "plugin.dll",
            SearchOption.AllDirectories));

    private static void WritePrebuiltManifest(E2eFixture fx, string pluginId, string version) =>
        File.WriteAllText(
            Path.Combine(fx.PluginDirectory(pluginId), "manifest.json"),
            JsonSerializer.Serialize(new { id = pluginId, version, entryAssembly = "plugin.dll" }));

    private static async Task<string[]> ReadLinesAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.Exists(path) ? await File.ReadAllLinesAsync(path) : [];
            }
            catch (Exception exception) when (attempt < 20 && exception is IOException)
            {
                await Task.Delay(5);
            }
        }
    }

    /// <summary>Worker process id recorded in one line of a plugin's load log.</summary>
    private static int PidOf(string line) =>
        int.Parse(line.Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A plugin that appends one line with the worker process id whenever a worker loads it, so a test can
    /// prove that no worker was started for it after a refresh removed it.</summary>
    private static string TrackedSource(string id, string echoPrefix, string loadLog) =>
        $$"""
        internal static class LoadTracker
        {
            [System.Runtime.CompilerServices.ModuleInitializer]
            public static void Init()
            {
                try
                {
                    File.AppendAllText(@"{{loadLog}}", "loaded " + Environment.ProcessId + "\n");
                }
                catch (IOException)
                {
                }
            }
        }

        public sealed class TestPlugin : IPlugin
        {
            public PluginInfo Info => new("{{id}}", "1.0.0", "entry lifecycle test plugin");

            public void Register(IRegistrationContext context)
            {
                context.AddTool(new EchoTool());
            }
        }

        public sealed class EchoTool : IPluginTool
        {
            private const string Prefix = "{{echoPrefix}}";

            public ToolDeclaration Declaration { get; } =
                new("echo", "Echoes the text argument", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
            {
                var text = invocation.Arguments.ValueKind == JsonValueKind.Object &&
                    invocation.Arguments.TryGetProperty("text", out var value)
                        ? value.ToString()
                        : "";
                return Task.FromResult(new ToolResult(Prefix + ":" + text));
            }
        }
        """;
}

/// <summary>
/// Drives the host's file-protocol test hook: it points <c>FORGE_HOST_TEST_HOOK</c> at a file naming this fixture's pipe
/// name and a private directory, waits for a request to register that it captured its plugin entry, and releases it.
/// The hook only answers the host whose pipe name matches, so a host started by a parallel test is never paused.
/// </summary>
internal sealed class EntryCaptureHook : IDisposable
{
    public const string EnvironmentVariable = "FORGE_HOST_TEST_HOOK";

    private readonly string? previous;

    private EntryCaptureHook(string directory, string? previous)
    {
        Directory = directory;
        this.previous = previous;
    }

    public string Directory { get; }

    public string ArmPath => Path.Combine(Directory, "armed");

    /// <summary>Makes the next request that captures a plugin entry wait until the test releases it.</summary>
    public void Arm() => File.WriteAllText(ArmPath, "armed");

    public void Disarm()
    {
        if (File.Exists(ArmPath))
        {
            File.Delete(ArmPath);
        }
    }

    public static EntryCaptureHook Activate(string root, string pipeName)
    {
        var directory = Path.Combine(root, "hook");
        System.IO.Directory.CreateDirectory(directory);
        var configuration = Path.Combine(root, "hook.json");
        File.WriteAllText(configuration, JsonSerializer.Serialize(new { pipe = pipeName, directory }));
        var previous = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Environment.SetEnvironmentVariable(EnvironmentVariable, configuration);
        return new EntryCaptureHook(directory, previous);
    }

    /// <summary>Waits for a request for <paramref name="pluginId"/> to be paused and returns its sequence number.</summary>
    public async Task<long> WaitForCaptureAsync(string pluginId)
    {
        var deadline = DateTime.UtcNow + E2eFixture.WaitLimit;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var file in System.IO.Directory.GetFiles(Directory, "*.captured"))
            {
                if (string.Equals(File.ReadAllText(file), pluginId, StringComparison.Ordinal))
                {
                    return long.Parse(Path.GetFileNameWithoutExtension(file), System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"Timed out after {E2eFixture.WaitLimit.TotalSeconds:0} s waiting for a request for '{pluginId}' to be paused " +
            $"by the test hook in '{Directory}'; check {EnvironmentVariable}.");
    }

    public void Release(long sequence) =>
        File.WriteAllText(
            Path.Combine(Directory, sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".release"),
            "release");

    public void Dispose() => Environment.SetEnvironmentVariable(EnvironmentVariable, previous);
}
