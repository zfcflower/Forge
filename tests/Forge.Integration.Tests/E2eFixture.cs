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
    /// <summary>Bound on a wait for a state the host has to reach. A safety net, so it follows what this run's host
    /// starts cost rather than a fixed number.</summary>
    public static TimeSpan WaitLimit => TestBudgets.Condition;

    /// <summary>Overall budget of one fixture, a safety net for a test that never finishes rather than a
    /// performance assertion: it has to leave room for the host start below on a loaded runner.</summary>
    private readonly CancellationTokenSource budget = new(TestBudgets.Fixture);

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
            StartTimeout = TestBudgets.HostStart,
        };
        var options = new ExtensionHostClientOptions
        {
            PluginsDirectory = PluginsDirectory,
            DataDirectory = DataDirectory,
            RequestTimeout = TimeSpan.FromSeconds(60),
            StopRequestTimeout = TimeSpan.FromSeconds(5),
            // Safety net: a host that does not exit on its own is killed after it. StopLifecycleTests asserts the host
            // exits by itself, so the bound must not expire on a loaded runner first.
            ProcessExitTimeout = TestBudgets.Condition,
        };
        Client = new ExtensionHostClient(connection, toolResolver, approvalPath, options);
    }

    public static string HostAssemblyPath => Path.Combine(AppContext.BaseDirectory, "Forge.ExtensionHost.dll");

    /// <summary>A fixture whose approval path allows every request; <paramref name="tool"/> is the only main-program tool
    /// plugins and workflows may call.</summary>
    public static E2eFixture AllowingApprovals(ITool tool, IReadOnlyList<string>? hostArguments = null) =>
        new(
            hostArguments,
            toolResolver: name => string.Equals(name, tool.Definition.Name, StringComparison.Ordinal) ? tool : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));

    /// <summary>A fixture whose approval path allows every request and whose main program exposes no tool.</summary>
    public static E2eFixture AllowingApprovals() =>
        new(
            toolResolver: _ => null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));

    /// <summary>A fixture whose approval path allows every request; <paramref name="toolResolver"/> decides which
    /// main-program tools plugins and workflows see.</summary>
    public static E2eFixture AllowingApprovals(
        Func<string, ITool?> toolResolver,
        IReadOnlyList<string>? hostArguments = null) =>
        new(
            hostArguments,
            toolResolver: toolResolver,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));

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

    /// <summary>Writes the plugin that registers every non-UI contribution kind and records its own registration in a
    /// marker file; returns the marker path.</summary>
    public string WriteContributedPlugin(string id)
    {
        var marker = Path.Combine(Root, "registered.txt");
        WritePluginWithManifest(
            id,
            PluginSources.Contributed(id, marker),
            PluginSources.ManifestWithPermissions(id, "1.0.0", "ReadFile"));
        return marker;
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

    /// <summary>Waits for the draft status row of a plugin nobody approved yet and asserts the host still reports it as a
    /// clean draft, with no error against it.</summary>
    public async Task WaitForCleanDraftAsync(string id)
    {
        var status = await WaitForStatusAsync(id, $"a status row for {id}");
        Assert.NotNull(status);
        Assert.Equal("draft", status!.Status);
        Assert.Null(status.Error);
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
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        await Client.StartAsync(Ct);
        TestBudgets.NoteHostStart(System.Diagnostics.Stopwatch.GetElapsedTime(start));
        await Client.RefreshPluginsAsync(Ct);
    }

    /// <summary>Waits until <paramref name="toolCount"/> tools of <paramref name="id"/> are registered.</summary>
    public Task WaitForToolsAsync(string id, int toolCount, string? what = null) =>
        WaitUntilAsync(() => ToolsOf(id).Count == toolCount, what ?? id + " tools");

    /// <summary>Starts the host, approves <paramref name="id"/> with the hashes it persisted, and waits for its tools.</summary>
    public async Task StartAndApproveAsync(string id, int toolCount, string? what = null)
    {
        await StartAsync();
        await ApproveAsync(id);
        await WaitForToolsAsync(id, toolCount, what);
    }

    /// <summary>Writes the standard six-tool plugin, starts the host, and approves it with the hashes it persisted.</summary>
    public async Task StartWithApprovedPluginAsync(string id, string echoPrefix, int toolCount)
    {
        WritePlugin(id, PluginSources.Standard(id, echoPrefix));
        await StartAndApproveAsync(id, toolCount);
    }

    /// <summary>Approves the hashes <c>plugins.status</c> reports for <paramref name="id"/> and waits for its tools.</summary>
    public async Task ApproveViaStatusAndWaitForToolsAsync(string id, int toolCount, string? what = null)
    {
        await ApproveViaStatusAsync(id);
        await WaitForToolsAsync(id, toolCount, what);
    }

    /// <summary>Starts the host and approves the hashes <c>plugins.status</c> reports for <paramref name="id"/>.</summary>
    public async Task StartAndApproveViaStatusAsync(string id)
    {
        await StartAsync();
        await ApproveViaStatusAsync(id);
    }

    /// <summary>Approves the hashes <c>plugins.status</c> reports for <paramref name="id"/> and asks the host to load it
    /// again, so a draft that cannot load is tried again.</summary>
    public async Task ApproveViaStatusAndRefreshAsync(string id)
    {
        await ApproveViaStatusAsync(id);
        await Client.RefreshPluginsAsync(Ct);
    }

    /// <summary>Writes the standard six-tool plugin (<see cref="PluginSources.Standard"/>), starts the host, and approves
    /// the hashes <c>plugins.status</c> reports for it.</summary>
    public async Task StartWithStatusApprovedPluginAsync(string id, string echoPrefix, string toolPrefix = "")
    {
        WritePlugin(id, PluginSources.Standard(id, echoPrefix, toolPrefix: toolPrefix));
        await StartAndApproveViaStatusAsync(id);
    }

    /// <summary>The one status row <c>plugins.status</c> reports for <paramref name="id"/>.</summary>
    public async Task<Sdk.PluginStatusInfo> StatusOfAsync(string id)
    {
        var statuses = await Client.GetPluginStatusesAsync(Ct);
        return statuses.Single(status => string.Equals(status.PluginId, id, StringComparison.Ordinal));
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

    /// <summary>Calls <paramref name="toolName"/> of <paramref name="pluginId"/> with <paramref name="argumentsJson"/> and
    /// asserts the call succeeded with exactly <paramref name="expectedContent"/>.</summary>
    public async Task AssertToolContentAsync(
        string pluginId,
        string toolName,
        string argumentsJson,
        string expectedContent)
    {
        var result = await InvokeAsync(pluginId, toolName, argumentsJson);
        Assert.False(result.IsError, result.Content);
        Assert.Equal(expectedContent, result.Content);
    }

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
                using var stopBudget = new CancellationTokenSource(TestBudgets.Condition);
                await Client.StopAsync(stopBudget.Token);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException or IOException)
        {
        }

        await Client.DisposeAsync();
        budget.Dispose();
        TempRoots.Delete(Root, throwOnPersistentFailure: true);
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
