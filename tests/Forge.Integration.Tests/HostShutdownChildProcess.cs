using System.Text.Json;
using Forge.Abstractions;
using Forge.ExtensionHost;
using Forge.HostClient;
using Forge.Ipc;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sdk = Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>
/// Builds the fake script workers used by the shutdown-fence tests: tiny programs compiled with the Roslyn assemblies
/// that are already on disk. <see cref="FakeWorkerMode.NeverConnect"/> reports its pid and readiness through files
/// next to its own binary and then blocks <i>before</i> connecting the real worker pipe, so the parent's worker
/// startup stalls exactly where the startup race lives: after the child exists, before any session is published.
/// <see cref="FakeWorkerMode.ConnectHold"/> connects the worker pipe, proves the lease by reading the first frame,
/// then answers nothing until the host kills the process tree, so the graceful-stop round trip always runs its full
/// window and a concurrent shutdown that returns early is observable without timing guesses. No environment variable
/// and no shared <c>ModuleInitializer</c> is involved; every test uses its own directory.
/// </summary>
internal enum FakeWorkerMode
{
    NeverConnect,
    ConnectHold,
}

internal static class FakeScriptWorker
{
    public const string ReadyFile = "ready.txt";

    public const string ConnectedFile = "connected.txt";

    public const string RequestSeenFile = "request-seen.txt";

    private const string NeverConnectSource = """
        using System;
        using System.IO;
        using System.Threading;
        public static class FakeWorker
        {
            public static int Main(string[] args)
            {
                try
                {
                    var dir = AppContext.BaseDirectory!;
                    File.WriteAllText(Path.Combine(dir, "started-" + Environment.ProcessId + ".txt"), Environment.ProcessId.ToString());
                    File.WriteAllText(Path.Combine(dir, "ready.txt"), "ready");
                    var release = Path.Combine(dir, "release.txt");
                    var deadline = Environment.TickCount64 + 120000;
                    while (!File.Exists(release) && Environment.TickCount64 < deadline)
                    {
                        Thread.Sleep(50);
                    }

                    return 0;
                }
                catch
                {
                    return 1;
                }
            }
        }
        """;

    private const string ConnectHoldSource = """
        using System;
        using System.IO;
        using System.IO.Pipes;
        using System.Threading;
        public static class FakeWorker
        {
            public static int Main(string[] args)
            {
                try
                {
                    var dir = AppContext.BaseDirectory!;
                    File.WriteAllText(Path.Combine(dir, "started-" + Environment.ProcessId + ".txt"), Environment.ProcessId.ToString());
                    string? pipe = null;
                    for (var i = 0; i + 1 < args.Length; i++)
                    {
                        if (args[i] == "--pipe")
                        {
                            pipe = args[i + 1];
                            break;
                        }
                    }

                    if (pipe is null)
                    {
                        return 2;
                    }

                    using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                    client.Connect(15000);
                    File.WriteAllText(Path.Combine(dir, "connected.txt"), "connected");
                    var buffer = new byte[65536];
                    try
                    {
                        var first = client.Read(buffer, 0, buffer.Length);
                        File.WriteAllText(Path.Combine(dir, "request-seen.txt"), "seen:" + first);
                        while (true)
                        {
                            try
                            {
                                if (client.Read(buffer, 0, buffer.Length) <= 0)
                                {
                                    break;
                                }
                            }
                            catch (IOException)
                            {
                                break;
                            }
                        }
                    }
                    catch (IOException)
                    {
                    }

                    var release = Path.Combine(dir, "release.txt");
                    var deadline = Environment.TickCount64 + 120000;
                    while (!File.Exists(release) && Environment.TickCount64 < deadline)
                    {
                        Thread.Sleep(50);
                    }

                    return 0;
                }
                catch
                {
                    return 1;
                }
            }
        }
        """;

    /// <summary>Compiles the fake worker into <paramref name="directory"/> and returns its assembly path.</summary>
    public static string Build(string directory, FakeWorkerMode mode = FakeWorkerMode.NeverConnect)
    {
        Directory.CreateDirectory(directory);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "FakeShutdownWorker",
            [CSharpSyntaxTree.ParseText(mode == FakeWorkerMode.ConnectHold ? ConnectHoldSource : NeverConnectSource)],
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
                .WithOptimizationLevel(OptimizationLevel.Release));
        var assemblyPath = Path.Combine(directory, "FakeWorker.dll");
        var result = compilation.Emit(assemblyPath);
        Assert.True(
            result.Success,
            "the fake worker did not compile: " +
            string.Join("\n", result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));

        var runtime = Environment.Version;
        File.WriteAllText(
            Path.Combine(directory, "FakeWorker.runtimeconfig.json"),
            JsonSerializer.Serialize(new
            {
                runtimeOptions = new
                {
                    tfm = $"net{runtime.Major}.{runtime.Minor}",
                    framework = new
                    {
                        name = "Microsoft.NETCore.App",
                        version = $"{runtime.Major}.{runtime.Minor}.0",
                    },
                },
            }));
        return assemblyPath;
    }

    public static int StartedCount(string directory) =>
        Directory.EnumerateFiles(directory, "started-*.txt").Count();

    public static int ReadPid(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "started-*.txt").ToArray();
        Assert.Single(files);
        Assert.True(int.TryParse(File.ReadAllText(files[0]).Trim(), out var pid), "the fake worker wrote no pid");
        return pid;
    }
}

/// <summary>
/// Shared oracle for the shutdown-fence tests. After the shutdown a call may only end as an explicit shutdown error
/// result or as an allowed shutdown exception; a successful result is always a fence breach, and any other exception
/// is a product bug the test must not swallow.
/// </summary>
internal static class ShutdownFenceOracle
{
    private static readonly string[] ShutdownMarkers =
    [
        "stopp", "shut", "cancel", "clos", "disconnect", "connect",
        "kill", "worker", "host", "timeout", "timed out", "did not finish",
        "was not run", "was not started", "not running", "disposed", "could not run",
    ];

    public static bool IsShutdownError(string? content) =>
        !string.IsNullOrWhiteSpace(content) &&
        ShutdownMarkers.Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public static void AssertShutdownOutcome(ToolResult? result, Exception? fault, string what)
    {
        if (fault is not null)
        {
            AssertShutdownFault(fault, what);
            return;
        }

        Assert.True(
            result is { IsError: true },
            $"{what} succeeded after the shutdown; the fence must tear the call down.");
        Assert.True(
            IsShutdownError(result.Content),
            $"{what} failed without a shutdown reason ('{result.Content}'); only an explicit shutdown/cancel/disconnect error is legitimate here.");
    }

    public static void AssertShutdownFault(Exception fault, string what)
    {
        var allowed = fault is OperationCanceledException
            or IpcConnectionClosedException
            or IpcTimeoutException
            or IpcRemoteException
            or IOException
            or InvalidOperationException
            or ObjectDisposedException;
        Assert.True(
            allowed,
            $"{what} threw {fault.GetType().FullName} after the shutdown; only cancellation/disconnect failures are legitimate here: {fault.Message}");
    }
}

/// <summary>Shutdown fence for plugin workers, observed through real operating-system pids: while one call runs in the
/// worker and a second call queues on its only execution slot, <c>DisposeAsync</c> must return only after the owned
/// worker process is gone, and the queued call must never start a new worker behind the shutdown. Identity is the
/// pid plus the worker's start time, so a recycled pid on a busy machine can never fake a survivor.</summary>
public sealed class HostShutdownChildProcessTests
{
    private static readonly TimeSpan StepLimit = TimeSpan.FromSeconds(60);

    private static readonly string[] ShutdownProbeSourceTools = ["echo", "sleep", "identity"];

    /// <summary>A probe plugin with the tools the fence needs: <c>echo</c>, an uncooperative <c>sleep</c> and
    /// <c>identity</c>, which reports the worker's pid and start time. <c>System.Diagnostics.Process</c> is not in
    /// the plugin reference set, so the start time is read through reflection exactly like
    /// <see cref="PluginSources"/> reaches process creation.</summary>
    private const string ShutdownProbeSource = """
        public sealed class TestPlugin : IPlugin
        {
            public PluginInfo Info => new("alpha", "1.0.0", "shutdown fence probe");

            public void Register(IRegistrationContext context)
            {
                context.AddTool(new EchoTool());
                context.AddTool(new SleepTool());
                context.AddTool(new IdentityTool());
            }

            internal static string Arg(JsonElement args, string name) =>
                args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) ? value.ToString() : "";
        }

        public sealed class EchoTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("echo", "Echoes the text argument", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                Task.FromResult(new ToolResult("probe:" + TestPlugin.Arg(invocation.Arguments, "text")));
        }

        public sealed class SleepTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("sleep", "Sleeps without honoring cancellation", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
            {
                var marker = TestPlugin.Arg(invocation.Arguments, "marker");
                if (marker.Length > 0)
                {
                    File.WriteAllText(marker, "started");
                }

                var milliseconds = int.TryParse(TestPlugin.Arg(invocation.Arguments, "ms"), out var parsed) ? parsed : 1000;
                await Task.Delay(milliseconds);
                return new ToolResult("slept:" + milliseconds);
            }
        }

        public sealed class IdentityTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("identity", "Reports the worker pid and start time", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
            {
                var processType = Type.GetType("System.Diagnostics.Process, System.Diagnostics.Process", throwOnError: true)!;
                var current = processType.GetMethod("GetCurrentProcess", Type.EmptyTypes)!.Invoke(null, null);
                var pid = (int)processType.GetProperty("Id")!.GetValue(current)!;
                var start = (System.DateTime)processType.GetProperty("StartTime")!.GetValue(current)!;
                return Task.FromResult(new ToolResult(pid + ";" + start.ToUniversalTime().Ticks));
            }
        }
        """;

    [Fact]
    public async Task Dispose_stops_a_running_plugin_worker_and_its_queued_call_never_starts_a_new_one()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-shutdown-child-" + Guid.NewGuid().ToString("N")[..10]);
        var plugins = Path.Combine(root, "plugins");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(plugins);
        Directory.CreateDirectory(data);
        var pipe = "forge-shutdown-child-" + Guid.NewGuid().ToString("N")[..16];

        var pluginDirectory = Path.Combine(plugins, "alpha");
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(
            Path.Combine(pluginDirectory, "manifest.json"),
            """{"id":"alpha","version":"1.0.0","source":"Plugin.cs"}""");
        File.WriteAllText(Path.Combine(pluginDirectory, "Plugin.cs"), ShutdownProbeSource);

        var workerPath = Path.Combine(AppContext.BaseDirectory, "Forge.PluginWorker.dll");
        var host = new Forge.ExtensionHost.ExtensionHost(new HostOptions
        {
            PipeName = pipe,
            PluginsDirectory = plugins,
            DataDirectory = data,
            RuntimePath = "dotnet",
            WorkerPath = workerPath,
            MaxConcurrentExecutionsPerWorker = 1,
        });
        var runTask = host.RunAsync();
        await using var client = new ExtensionHostClient(
            new HostConnection { PipeName = pipe, HostExecutablePath = null, StartTimeout = TimeSpan.FromSeconds(30) },
            null,
            null,
            new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromSeconds(60) });
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        try
        {
            await client.StartAsync(ct);

            var status = await WaitForStatusAsync(client, "alpha", ct);
            await client.ApprovePluginAsync(
                "alpha", "1.0.0", status.SourceHash, status.DependencyHash, status.SdkVersion, ct);
            await WaitForToolsAsync(client, "alpha", ShutdownProbeSourceTools, ct);

            var (workerPid, workerStart) = await WorkerIdentityAsync(client, "alpha", root, ct);

            // One call runs inside the worker; the marker file proves it started there.
            var marker = Path.Combine(root, "sleep-started.txt");
            using var runningCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var running = InvokeAsync(
                client, "alpha", "sleep", root,
                JsonSerializer.Serialize(new { marker, ms = 30_000 }), runningCts.Token);
            await WaitForFileAsync(marker, "the running call to start in the worker", ct);

            // A second call queues on the worker's only execution slot: with the first call still
            // running, starting it would require a slot that does not exist.
            using var queuedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var queued = InvokeAsync(
                client, "alpha", "sleep", root,
                JsonSerializer.Serialize(new { ms = 1_000 }), queuedCts.Token);

            await host.DisposeAsync();

            // The fence: DisposeAsync returns only after the owned child is gone. The start time pins
            // the identity, so a recycled pid cannot pose as the worker.
            Assert.False(
                IsRunning(workerPid, workerStart),
                $"DisposeAsync returned while the owned worker (pid {workerPid}) was still alive.");

            // Neither the running call nor the queued one may outlive the shutdown or start a new worker.
            // Each ends as an explicit shutdown error once its worker is gone, or faults when our cancellation
            // wins the race; the queued call never started, so cancelling it ends it promptly.
            runningCts.Cancel();
            queuedCts.Cancel();
            var runningEnding = await EndAsAsync(running, "the running call", ct);
            ShutdownFenceOracle.AssertShutdownOutcome(runningEnding.Result, runningEnding.Fault, "the running call");
            var queuedEnding = await EndAsAsync(queued, "the queued call", ct);
            ShutdownFenceOracle.AssertShutdownOutcome(queuedEnding.Result, queuedEnding.Fault, "the queued call");
        }
        finally
        {
            await client.DisposeAsync();
            DeleteDirectory(root);
        }

        Assert.Equal(0, await runTask.WaitAsync(ct));
    }

    private static async Task<ToolResult> InvokeAsync(
        ExtensionHostClient client, string pluginId, string tool, string workingDirectory, string argumentsJson, CancellationToken ct)
    {
        var proxy = ToolsOf(client, pluginId).Single(t => t.Definition.Name == tool);
        return await proxy.InvokeAsync(new ToolCall(Guid.NewGuid().ToString("N"), tool, argumentsJson), new ToolContext("shutdown", workingDirectory), ct);
    }

    private static IReadOnlyList<ITool> ToolsOf(ExtensionHostClient client, string pluginId) =>
        client.ToolsByPlugin.TryGetValue(pluginId, out var tools) ? tools : [];

    private static async Task<(int Pid, long StartUtcTicks)> WorkerIdentityAsync(
        ExtensionHostClient client, string pluginId, string workingDirectory, CancellationToken ct)
    {
        var result = await InvokeAsync(client, pluginId, "identity", workingDirectory, "{}", ct);
        Assert.False(result.IsError, result.Content);
        var parts = result.Content.Split(';');
        Assert.True(parts.Length == 2 && int.TryParse(parts[0], out _) && long.TryParse(parts[1], out _),
            $"the probe reported no usable identity: '{result.Content}'.");
        return (int.Parse(parts[0]), long.Parse(parts[1]));
    }

    private static async Task<Sdk.PluginStatusInfo> WaitForStatusAsync(
        ExtensionHostClient client, string pluginId, CancellationToken ct)
    {
        // The host publishes its initial discovery asynchronously after the pipe accepts the
        // client, so the entry is awaited instead of assumed.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var match = (await client.GetPluginStatusesAsync(deadline.Token))
                .SingleOrDefault(status => status.PluginId == pluginId);
            if (match is not null)
            {
                return match;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for the status of '{pluginId}'.");
            }
        }
    }

    private static async Task WaitForToolsAsync(
        ExtensionHostClient client, string pluginId, string[] tools, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var names = ToolsOf(client, pluginId).Select(tool => tool.Definition.Name).ToArray();
            if (tools.All(wanted => names.Contains(wanted, StringComparer.Ordinal)))
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for the tools of '{pluginId}'.");
            }
        }
    }

    private static async Task WaitForFileAsync(string path, string what, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!File.Exists(path))
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {what} ('{path}').");
            }
        }
    }

    private static async Task<(ToolResult? Result, Exception? Fault)> EndAsAsync(
        Task<ToolResult> call, string what, CancellationToken ct)
    {
        try
        {
            return (await call.WaitAsync(TimeSpan.FromSeconds(15), ct), null);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{what} never ended after the shutdown.");
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    /// <summary>True only when the process with this pid is still the worker that reported this start time:
    /// an exited worker whose pid was recycled by another process reads as gone.</summary>
    private static bool IsRunning(int processId, long startUtcTicks)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process.StartTime.ToUniversalTime().Ticks != startUtcTicks)
            {
                return false;
            }

            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void DeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 49)
                {
                    return;
                }

                Thread.Sleep(100);
            }
        }
    }
}
