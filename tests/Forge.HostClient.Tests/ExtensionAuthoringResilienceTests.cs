using System.IO;
using System.Reflection;
using Forge.Ipc;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary>Failure semantics of the authoring surface: the compile-budget floor for
/// <c>extensions.stage</c>/<c>extensions.commit</c> (proved without waiting minutes), short-timeout behavior of
/// <c>extensions.pending</c>, no deadline when no timeout is configured, passthrough of host business errors and
/// caller cancellation, refusal before <c>StartAsync</c>, and a mid-call disconnect.</summary>
public sealed class ExtensionAuthoringResilienceTests
{
    private const string ValidStagedJson =
        "{\"pluginId\":\"demo\",\"version\":\"1.0.0\",\"kind\":\"plugin\"," +
        "\"files\":[{\"path\":\"Plugin.cs\",\"text\":\"// code\"}]," +
        "\"permissions\":[{\"kind\":0}]," +
        "\"dependencies\":[{\"name\":\"Dep\",\"version\":\"1.0.0\"}]," +
        "\"sourceHash\":\"s\",\"dependencyHash\":\"d\",\"sdkVersion\":\"sdk-1\"," +
        "\"diagnostics\":[]}";

    private const string ValidCommitJson =
        "{\"status\":{\"pluginId\":\"demo\",\"version\":\"1.0.0\",\"status\":\"approved\"," +
        "\"sourceHash\":\"s\",\"dependencyHash\":\"d\",\"sdkVersion\":\"sdk-1\"," +
        "\"permissions\":[],\"error\":null},\"toolNames\":[\"demo-tool\"]}";

    [Fact]
    public async Task Compile_budget_defaults_to_the_five_minute_floor()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);

        Assert.Equal(TimeSpan.FromMinutes(5), ReadCompileBudget(client));
    }

    [Fact]
    public async Task Compile_budget_keeps_a_tiny_request_timeout_at_the_floor()
    {
        await using var host = new FakeHost();
        var options = new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(200) };
        await using var client = ClientFactory.Create(host, options: options);

        Assert.Equal(TimeSpan.FromMinutes(5), ReadCompileBudget(client));
    }

    [Fact]
    public async Task Compile_budget_keeps_a_request_timeout_above_the_floor()
    {
        await using var host = new FakeHost();
        var options = new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromMinutes(10) };
        await using var client = ClientFactory.Create(host, options: options);

        Assert.Equal(TimeSpan.FromMinutes(10), ReadCompileBudget(client));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Compile_budget_is_unset_without_a_positive_request_timeout(int? milliseconds)
    {
        await using var host = new FakeHost();
        var options = new ExtensionHostClientOptions
        {
            RequestTimeout = milliseconds is { } value ? TimeSpan.FromMilliseconds(value) : null,
        };
        await using var client = ClientFactory.Create(host, options: options);

        Assert.Null(ReadCompileBudget(client));
    }

    [Fact]
    public async Task Stage_survives_a_compile_delay_longer_than_RequestTimeout()
    {
        await using var host = new FakeHost();
        var options = new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(250) };
        host.On(Sdk.ExtensionProtocol.ExtensionsStage, async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            return Json.Element(ValidStagedJson);
        });
        await using var client = ClientFactory.Create(host, options: options);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var staged = await client.StageExtensionAsync(
            ExtensionAuthoringPayloadTests.ValidStageRequest(), limit.Token);

        Assert.Equal("demo", staged.PluginId);
        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Empty(client.Diagnostics);
    }

    [Fact]
    public async Task Commit_survives_a_validation_delay_longer_than_RequestTimeout()
    {
        await using var host = new FakeHost();
        var options = new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(250) };
        host.On(Sdk.ExtensionProtocol.ExtensionsCommit, async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            return Json.Element(ValidCommitJson);
        });
        await using var client = ClientFactory.Create(host, options: options);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var result = await client.CommitExtensionAsync(
            ExtensionAuthoringPayloadTests.ValidCommitRequest(), limit.Token);

        Assert.Equal(["demo-tool"], result.ToolNames);
        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Empty(client.Diagnostics);
    }

    [Fact]
    public async Task Pending_uses_RequestTimeout_and_recovers_afterwards()
    {
        await using var host = new FakeHost();
        var options = new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(200) };
        var calls = 0;
        host.On(Sdk.ExtensionProtocol.ExtensionsPending, (message, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return FakeHost.NeverAnswer(message, ct);
            }

            return Task.FromResult(Json.Element("{\"items\":[]}"));
        });
        await using var client = ClientFactory.Create(host, options: options);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<IpcTimeoutException>(
            () => client.GetPendingExtensionsAsync(limit.Token));

        Assert.Equal(Sdk.ExtensionProtocol.ExtensionsPending, error.Operation);
        Assert.Equal(TimeSpan.FromMilliseconds(200), error.Timeout);
        Assert.Equal(HostStatus.Running, client.Status);
        var diagnostic = Assert.Single(
            client.Diagnostics,
            entry => entry.Contains(Sdk.ExtensionProtocol.ExtensionsPending, StringComparison.Ordinal));
        Assert.Contains("budget", diagnostic);

        Assert.Empty(await client.GetPendingExtensionsAsync(limit.Token));
        Assert.Equal(2, calls);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Stage_without_a_request_timeout_has_no_deadline()
    {
        await using var host = new FakeHost();
        var options = new ExtensionHostClientOptions { RequestTimeout = null };
        host.On(Sdk.ExtensionProtocol.ExtensionsStage, async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800), ct).ConfigureAwait(false);
            return Json.Element(ValidStagedJson);
        });
        await using var client = ClientFactory.Create(host, options: options);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var staged = await client.StageExtensionAsync(
            ExtensionAuthoringPayloadTests.ValidStageRequest(), limit.Token);

        Assert.Equal("demo", staged.PluginId);
        Assert.Empty(client.Diagnostics);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Theory]
    [InlineData("approval_mismatch")]
    [InlineData("extension_blocked")]
    [InlineData("invalid_extension")]
    [InlineData("unknown_plugin")]
    public async Task Stage_business_error_propagates_with_code_and_no_diagnostic(string code)
    {
        await using var host = new FakeHost();
        host.On(Sdk.ExtensionProtocol.ExtensionsStage, (_, _) => throw new FakeHostError(code, "host refused: " + code));
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<IpcRemoteException>(
            () => client.StageExtensionAsync(ExtensionAuthoringPayloadTests.ValidStageRequest(), limit.Token));

        Assert.Equal(code, error.Code);
        Assert.Equal(Sdk.ExtensionProtocol.ExtensionsStage, error.Operation);
        Assert.Contains(code, error.Message);
        Assert.Empty(client.Diagnostics);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Commit_approval_mismatch_propagates_without_a_diagnostic()
    {
        await using var host = new FakeHost();
        host.On(
            Sdk.ExtensionProtocol.ExtensionsCommit,
            (_, _) => throw new FakeHostError("approval_mismatch", "the staged bytes changed since approval"));
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<IpcRemoteException>(
            () => client.CommitExtensionAsync(ExtensionAuthoringPayloadTests.ValidCommitRequest(), limit.Token));

        Assert.Equal("approval_mismatch", error.Code);
        Assert.Equal(Sdk.ExtensionProtocol.ExtensionsCommit, error.Operation);
        Assert.Empty(client.Diagnostics);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Pending_unknown_plugin_propagates_without_a_diagnostic()
    {
        await using var host = new FakeHost();
        host.On(
            Sdk.ExtensionProtocol.ExtensionsPending,
            (_, _) => throw new FakeHostError("unknown_plugin", "no such staged extension"));
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<IpcRemoteException>(
            () => client.GetPendingExtensionsAsync(limit.Token));

        Assert.Equal("unknown_plugin", error.Code);
        Assert.Empty(client.Diagnostics);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_as_OperationCanceledException_without_a_diagnostic()
    {
        await using var host = new FakeHost();
        host.On(Sdk.ExtensionProtocol.ExtensionsStage, FakeHost.NeverAnswer);
        host.On(Sdk.ExtensionProtocol.ExtensionsPending, FakeHost.NeverAnswer);
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.StageExtensionAsync(
                ExtensionAuthoringPayloadTests.ValidStageRequest(), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetPendingExtensionsAsync(canceled.Token));

        Assert.Empty(client.Diagnostics);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Authoring_calls_without_StartAsync_throw_InvalidOperationException()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        using var limit = TestLimit.Create();

        var stage = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StageExtensionAsync(ExtensionAuthoringPayloadTests.ValidStageRequest(), limit.Token));
        Assert.Contains("StartAsync", stage.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetPendingExtensionsAsync(limit.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CommitExtensionAsync(ExtensionAuthoringPayloadTests.ValidCommitRequest(), limit.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DiscardExtensionAsync("demo", limit.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DisablePluginAsync("demo", "1.0.0", limit.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.EnablePluginAsync("demo", "1.0.0", limit.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DeletePluginAsync("demo", "1.0.0", limit.Token));
    }

    [Fact]
    public async Task Disconnect_during_pending_propagates_as_IpcConnectionClosedException()
    {
        await using var host = new FakeHost();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On(Sdk.ExtensionProtocol.ExtensionsPending, async (_, ct) =>
        {
            arrived.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return Json.Element("{\"items\":[]}");
        });
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var pending = client.GetPendingExtensionsAsync(limit.Token);
        await arrived.Task.Bounded();
        Assert.Single(host.Requests(Sdk.ExtensionProtocol.ExtensionsPending));
        await host.DropConnectionAsync();

        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => pending);
    }

    [Fact]
    public async Task Disconnect_during_stage_propagates_as_IpcConnectionClosedException()
    {
        await using var host = new FakeHost();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On(Sdk.ExtensionProtocol.ExtensionsStage, async (_, ct) =>
        {
            arrived.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return Json.Element(ValidStagedJson);
        });
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var staging = client.StageExtensionAsync(
            ExtensionAuthoringPayloadTests.ValidStageRequest(), limit.Token);
        await arrived.Task.Bounded();
        Assert.Single(host.Requests(Sdk.ExtensionProtocol.ExtensionsStage));
        await host.DropConnectionAsync();

        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => staging);
    }

    private static TimeSpan? ReadCompileBudget(ExtensionHostClient client)
    {
        var property = typeof(ExtensionHostClient).GetProperty(
            "CompileBudget", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(property);
        return (TimeSpan?)property.GetValue(client);
    }
}
