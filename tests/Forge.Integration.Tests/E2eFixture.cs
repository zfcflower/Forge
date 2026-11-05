using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using Forge.Abstractions;
using Forge.HostClient;
using Sdk = Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>Approval record as persisted in <c>approvals.db</c> under the host's data directory.</summary>
internal sealed record ApprovalRecord(
    string PluginId,
    string Version,
    string SourceHash,
    string DependencyHash,
    string SdkVersion,
    string Status);

/// <summary>
/// One end-to-end run: a temp plugins/data layout, a unique pipe, and an <see cref="ExtensionHostClient"/> that launches
/// the real <c>Forge.ExtensionHost</c> (which in turn launches the real <c>Forge.PluginWorker</c>). Disposing the fixture
/// stops the client when it is still running and removes the temp directory.
/// </summary>
internal sealed class E2eFixture : IAsyncDisposable
{
    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    /// <summary>Overall budget of one fixture, a safety net for a test that never finishes rather than a
    /// performance assertion: it has to leave room for the host start below on a loaded runner.</summary>
    private readonly CancellationTokenSource budget = new(TimeSpan.FromSeconds(240));

    public E2eFixture(
        IReadOnlyList<string>? hostArguments = null,
        Func<string, ITool?>? toolResolver = null,
        Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>>? approvalPath = null,
        string? hostExecutablePath = null)
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        Root = Path.Combine(Path.GetTempPath(), "forge-e2e-" + token);
        PluginsDirectory = Path.Combine(Root, "plugins");
        DataDirectory = Path.Combine(Root, "data");
        PipeName = "forge-e2e-" + Guid.NewGuid().ToString("N")[..20];
        Directory.CreateDirectory(PluginsDirectory);
        Directory.CreateDirectory(DataDirectory);

        var connection = new HostConnection
        {
            PipeName = PipeName,
            HostExecutablePath = hostExecutablePath ?? HostAssemblyPath,
            RuntimePath = "dotnet",
            HostArguments = hostArguments ?? [],
            // Starting a host is bounded by how fast the machine creates a process and pages in a .NET app, not by
            // this number: a runner that already runs a dozen test assemblies at once can take far longer than a
            // quiet desktop. It stays a bound, so a host that never listens still fails the test.
            StartTimeout = TimeSpan.FromSeconds(120),
        };
        var options = new ExtensionHostClientOptions
        {
            PluginsDirectory = PluginsDirectory,
            DataDirectory = DataDirectory,
            RequestTimeout = TimeSpan.FromSeconds(60),
            StopRequestTimeout = TimeSpan.FromSeconds(5),
            ProcessExitTimeout = TimeSpan.FromSeconds(10),
        };
        Client = new ExtensionHostClient(connection, toolResolver, approvalPath, options);
    }

    public static string HostAssemblyPath => Path.Combine(AppContext.BaseDirectory, "Forge.ExtensionHost.dll");

    public string Root { get; }

    public string PluginsDirectory { get; }

    public string DataDirectory { get; }

    public string PipeName { get; }

    public ExtensionHostClient Client { get; }

    public CancellationToken Ct => budget.Token;

    public string ApprovalsPath => ApprovalDatabase.PathOf(DataDirectory);

    public string PluginDirectory(string id) => Path.Combine(PluginsDirectory, id);

    public string SourcePath(string id) => Path.Combine(PluginDirectory(id), "Plugin.cs");

    /// <summary>Writes <c>&lt;plugins&gt;/&lt;id&gt;/manifest.json</c> and its <c>Plugin.cs</c> source.</summary>
    public string WritePlugin(string id, string source, string version = "1.0.0")
    {
        var directory = PluginDirectory(id);
        Directory.CreateDirectory(directory);
        var manifest = JsonSerializer.Serialize(new { id, version, source = "Plugin.cs" });
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest);
        File.WriteAllText(SourcePath(id), source);
        return directory;
    }

    /// <summary>Writes a plugin with a caller-supplied manifest document (permissions, prebuilt entry, ...).</summary>
    public string WritePluginWithManifest(string id, string source, string manifestJson)
    {
        var directory = PluginDirectory(id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifestJson);
        File.WriteAllText(SourcePath(id), source);
        return directory;
    }

    /// <summary>Polls <c>plugins.status</c> until <paramref name="id"/> has a row; never touches the approval database.</summary>
    public async Task<Sdk.PluginStatusInfo?> WaitForStatusAsync(string id, string what)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(WaitLimit);
        while (true)
        {
            var statuses = await Client.GetPluginStatusesAsync(Ct);
            var found = statuses.FirstOrDefault(
                status => string.Equals(status.PluginId, id, StringComparison.Ordinal));
            if (found is not null)
            {
                return found;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out after {WaitLimit.TotalSeconds:0} s waiting for {what}.");
            }
        }
    }

    /// <summary>Approves <paramref name="id"/> with the hashes the host itself reports; never reads the database.</summary>
    public async Task ApproveViaStatusAsync(string id, string version = "1.0.0")
    {
        var status = await WaitForStatusAsync(id, $"a status row for {id} {version}");
        Assert.NotNull(status);
        Assert.Equal(version, status!.Version);
        await Client.ApprovePluginAsync(id, version, status.SourceHash, status.DependencyHash, status.SdkVersion, Ct);
    }

    public void EditSource(string id, string source) => File.WriteAllText(SourcePath(id), source);

    public async Task StartAsync()
    {
        await Client.StartAsync(Ct);
        await Client.RefreshPluginsAsync(Ct);
    }

    /// <summary>Reads the persisted record for one plugin version; null when the approval database has none.</summary>
    public ApprovalRecord? ReadRecord(string id, string version = "1.0.0") =>
        ApprovalDatabase.ReadRecord(DataDirectory, id, version);

    /// <summary>Approves the version recorded for <paramref name="id"/> with the hashes the host persisted.</summary>
    public async Task ApproveAsync(string id, string version = "1.0.0")
    {
        var record = await WaitForRecordAsync(id, version);
        await Client.ApprovePluginAsync(id, version, record.SourceHash, record.DependencyHash, record.SdkVersion, Ct);
    }

    public async Task<ApprovalRecord> WaitForRecordAsync(string id, string version = "1.0.0")
    {
        ApprovalRecord? found = null;
        await WaitUntilAsync(() => (found = ReadRecord(id, version)) is not null, $"approval record for {id} {version}");
        return found!;
    }

    /// <summary>Tool proxies registered for one plugin, or an empty list.</summary>
    public IReadOnlyList<ITool> ToolsOf(string pluginId) =>
        Client.ToolsByPlugin.TryGetValue(pluginId, out var tools) ? tools : [];

    public ITool ToolOf(string pluginId, string toolName) =>
        ToolsOf(pluginId).Single(tool => tool.Definition.Name == toolName);

    public async Task<ToolResult> InvokeAsync(ITool tool, string argumentsJson = "{}")
    {
        var call = new ToolCall(Guid.NewGuid().ToString("N"), tool.Definition.Name, argumentsJson);
        return await tool.InvokeAsync(call, new ToolContext("e2e-session", Root), Ct);
    }

    public async Task<ToolResult> InvokeAsync(string pluginId, string toolName, string argumentsJson = "{}") =>
        await InvokeAsync(ToolOf(pluginId, toolName), argumentsJson);

    /// <summary>Calls the <c>pid</c> tool and returns the worker process id it reports.</summary>
    public async Task<int> WorkerPidAsync(string pluginId)
    {
        var result = await InvokeAsync(pluginId, "pid");
        Assert.False(result.IsError, result.Content);
        return int.Parse(result.Content);
    }

    public async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(WaitLimit);
        while (!condition())
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out after {WaitLimit.TotalSeconds:0} s waiting for {what}.");
            }
        }
    }

    public async Task WaitForExitAsync(int processId, string what)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(WaitLimit);
        while (IsRunning(processId))
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out after {WaitLimit.TotalSeconds:0} s waiting for {what} (pid {processId}) to exit.");
            }
        }
    }

    public static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>True when a server is waiting on the pipe name and accepts a client right now.</summary>
    public static bool PipeAcceptsConnections(string pipeName)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        try
        {
            client.Connect(100);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Client.Status != HostStatus.Stopped)
            {
                using var stopBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await Client.StopAsync(stopBudget.Token);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException or IOException)
        {
        }

        await Client.DisposeAsync();
        budget.Dispose();
        DeleteDirectory(Root);
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
                    throw;
                }

                Thread.Sleep(100);
            }
        }
    }
}

/// <summary>
/// The host's integration-test hooks (<c>FORGE_HOST_TEST_HOOK</c>, <c>FORGE_HOST_TEST_FAIL_JOB_ASSIGN</c>) are compiled
/// into Debug builds of <c>Forge.ExtensionHost</c> only, so a Release build of the host cannot serve the tests that need
/// them: those tests must fail with this message instead of timing out or passing for the wrong reason.
/// </summary>
internal static class HostTestHooks
{
    public const string DebugRequired = "integration tests require a Debug build of Forge.ExtensionHost";

    /// <summary>Fails the calling test unless the host assembly under test was built as Debug.</summary>
    public static void Require()
    {
        var host = typeof(Forge.ExtensionHost.Program).Assembly;
        var configuration = host.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        if (!string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{DebugRequired}: '{host.Location}' was built as '{configuration ?? "unknown"}'.");
        }
    }
}
