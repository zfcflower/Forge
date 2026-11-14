using System.IO;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;
using static Forge.HostClient.Tests.ExtensionAuthoringResponseTests;
using static Forge.HostClient.Tests.ToolFixtures;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary>Plugin status queries (IPC-only, never a database) and dynamic prompt contributors
/// (bound context, unregistered references).
///
/// Contract correction (fail-closed): a missing or unusable <c>plugins.status</c> answer is
/// "unknown" and throws (<see cref="InvalidDataException"/> for undecodable payloads,
/// <see cref="IpcTimeoutException"/> on deadline); it must never look like an empty registry.
/// Only a decoded empty <see cref="Sdk.PluginStatusResponse"/> returns an empty list. A single malformed entry poisons the whole
/// array: the client throws instead of returning its valid siblings. This replaces the old expectation that
/// null, missing-id and blank-id entries were skipped while valid siblings survived, plus the two older
/// expectations that asserted an empty list plus a diagnostic for malformed answers and timeouts.</summary>
public sealed class PluginStatusPromptTests
{
    [Fact]
    public async Task Statuses_decode_permissions_and_survive_optional_fields()
    {
        await using var host = new FakeHost();
        host.Answer(
            Sdk.ExtensionProtocol.PluginsStatus,
            new Sdk.PluginStatusResponse(
            [
                new Sdk.PluginStatusInfo(
                    "alpha", "1.2.0", "active", "s", "d", "sdk-2.0",
                    [new Sdk.Permission(Sdk.PermissionKind.WriteFile, "data"), new Sdk.Permission(Sdk.PermissionKind.Network)],
                    null),
                new Sdk.PluginStatusInfo("beta", "0.1", "failed", "s", "d", "sdk-2.0", [], "crashed"),
            ]));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var statuses = await client.GetPluginStatusesAsync(limit.Token);

        Assert.Equal(2, statuses.Count);
        Assert.Equal("alpha", statuses[0].PluginId);
        Assert.Equal("sdk-2.0", statuses[0].SdkVersion);
        Assert.Equal(
            [new Sdk.Permission(Sdk.PermissionKind.WriteFile, "data"), new Sdk.Permission(Sdk.PermissionKind.Network, null)],
            statuses[0].Permissions);
        Assert.Equal("failed", statuses[1].Status);
        Assert.Equal("crashed", statuses[1].Error);
        Assert.Single(host.Requests("plugins.status"));
        Assert.Empty(host.Requests("plugin.execute"));
        Assert.Empty(host.Requests("code.execute"));
        Assert.Empty(host.Requests("plugin.command"));
        Assert.Empty(host.Requests("plugin.prompt"));
    }

    /// <summary>Answers without a usable plugin list: no payload, nil, an empty array (no <c>plugins</c> member), a
    /// string, a map, a nil <c>plugins</c> member and a string <c>plugins</c> member.</summary>
    public static TheoryData<string> MalformedStatusAnswers =>
        ["no payload", "nil", "empty array", "string", "map", "nil plugins", "string plugins"];

    [Theory]
    [MemberData(nameof(MalformedStatusAnswers))]
    public async Task Malformed_status_response_throws_InvalidDataException_and_keeps_the_connection(string shape)
    {
        var answer = shape switch
        {
            "no payload" => HostReply.Raw(RawPayload.None),
            "nil" => HostReply.Raw(HostWire.Nil),
            "empty array" => HostReply.Raw(new RawPayload(new byte[] { 0x90 })),
            "string" => HostReply.Raw(HostWire.Payload("text")),
            "map" => HostReply.Raw(new RawPayload(new byte[] { 0x80 })),
            "nil plugins" => HostReply.Raw(new RawPayload(ArrayOf(Nil))),
            _ => HostReply.Raw(new RawPayload(ArrayOf(IpcSerializer.Serialize("nope")))),
        };
        var resolver = new ResolverSpy();
        await using var host = new FakeHost();
        var calls = 0;
        host.On("plugins.status", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(answer);
        });
        host.On("plugin.execute", (_, _) => throw new FakeHostError("must-not-run", "metadata must not execute"));
        await using var client = await ClientFactory.StartAsync(host, resolver: resolver.Resolve);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetPluginStatusesAsync(limit.Token));

        Assert.Contains("plugins.status", error.Message);
        Assert.Equal(1, calls);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Empty(host.Requests("plugin.execute"));
        var diagnostic = Assert.Single(
            client.Diagnostics, entry => entry.Contains("plugins.status", StringComparison.Ordinal));
        Assert.Contains("unusable", diagnostic);
    }

    [Fact]
    public async Task Status_timeout_throws_IpcTimeoutException_and_keeps_the_connection()
    {
        var resolver = new ResolverSpy();
        await using var host = new FakeHost();
        host.On("plugins.status", FakeHost.NeverAnswer);
        var options = new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(200) };
        await using var client = await ClientFactory.StartAsync(host, resolver: resolver.Resolve, options: options);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<IpcTimeoutException>(
            () => client.GetPluginStatusesAsync(limit.Token));

        Assert.Equal("plugins.status", error.Operation);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(HostStatus.Running, client.Status);
        Assert.NotEmpty(client.Diagnostics);
    }

    [Fact]
    public async Task Valid_empty_registry_returns_empty_and_is_distinct_from_unknown()
    {
        var resolver = new ResolverSpy();
        await using var host = new FakeHost();
        host.Answer(Sdk.ExtensionProtocol.PluginsStatus, new Sdk.PluginStatusResponse([]));
        await using var client = await ClientFactory.StartAsync(host, resolver: resolver.Resolve);
        using var limit = TestLimit.Create();

        var statuses = await client.GetPluginStatusesAsync(limit.Token);

        Assert.Empty(statuses);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Host_business_error_on_status_propagates_as_IpcRemoteException()
    {
        var resolver = new ResolverSpy();
        await using var host = new FakeHost();
        host.On("plugins.status", (_, _) => throw new FakeHostError("store-offline", "status store is unavailable"));
        await using var client = await ClientFactory.StartAsync(host, resolver: resolver.Resolve);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<IpcRemoteException>(
            () => client.GetPluginStatusesAsync(limit.Token));

        Assert.Equal("store-offline", error.Code);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Caller_cancellation_of_status_propagates_as_OperationCanceledException()
    {
        var resolver = new ResolverSpy();
        await using var host = new FakeHost();
        host.On("plugins.status", FakeHost.NeverAnswer);
        await using var client = await ClientFactory.StartAsync(host, resolver: resolver.Resolve);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetPluginStatusesAsync(canceled.Token));

        Assert.Equal(0, resolver.Calls);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Disconnect_during_status_propagates_as_IpcConnectionClosedException()
    {
        var resolver = new ResolverSpy();
        await using var host = new FakeHost();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On("plugins.status", async (message, ct) =>
        {
            arrived.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new Sdk.PluginStatusResponse([]);
        });
        await using var client = await ClientFactory.StartAsync(host, resolver: resolver.Resolve);
        using var limit = TestLimit.Create();

        var pending = client.GetPluginStatusesAsync(limit.Token);
        await arrived.Task.Bounded();
        Assert.Single(host.Requests("plugins.status"));
        await host.DropConnectionAsync();

        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => pending);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task Entire_malformed_status_array_throws_without_a_partial_result()
    {
        await using var host = new FakeHost();
        host.AnswerRaw(
            Sdk.ExtensionProtocol.PluginsStatus,
            new RawPayload(ArrayOf(ArrayOf(IpcSerializer.Serialize(OkRow), Nil))));
        host.On("plugin.execute", (_, _) => throw new FakeHostError("must-not-run", "metadata must not execute"));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetPluginStatusesAsync(limit.Token));

        Assert.Contains("plugins.status", error.Message);
        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Empty(host.Requests("plugin.execute"));
        Assert.Contains(
            client.Diagnostics,
            entry => entry.Contains("plugins.status", StringComparison.Ordinal) && entry.Contains("unusable", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("null row")]
    [InlineData("version only")]
    [InlineData("blank plugin id")]
    public async Task Malformed_entry_mixed_with_valid_rows_throws_without_a_partial_result(string shape)
    {
        // Every member but the version (or the id and version) left out: nil on the wire.
        byte[] first = shape switch
        {
            "null row" => Nil,
            "version only" => ArrayOf(Nil, IpcSerializer.Serialize("1.0"), Nil, Nil, Nil, Nil, Nil, Nil),
            _ => ArrayOf(IpcSerializer.Serialize("  "), IpcSerializer.Serialize("1"), Nil, Nil, Nil, Nil, Nil, Nil),
        };
        await using var host = new FakeHost();
        host.AnswerRaw(
            Sdk.ExtensionProtocol.PluginsStatus,
            new RawPayload(ArrayOf(ArrayOf(first, IpcSerializer.Serialize(OkRow)))));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetPluginStatusesAsync(limit.Token));

        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Valid_invalid_status_and_error_rows_remain_observable()
    {
        await using var host = new FakeHost();
        host.Answer(
            Sdk.ExtensionProtocol.PluginsStatus,
            new Sdk.PluginStatusResponse(
                [new Sdk.PluginStatusInfo("broken", "1.0", "invalid", "s", "d", "sdk", [], "compile failed")]));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var statuses = await client.GetPluginStatusesAsync(limit.Token);

        var only = Assert.Single(statuses);
        Assert.Equal("broken", only.PluginId);
        Assert.Equal("invalid", only.Status);
        Assert.Equal("compile failed", only.Error);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Prompt_is_evaluated_with_the_bound_context()
    {
        await using var host = new FakeHost();
        host.Answer(Sdk.ExtensionProtocol.PluginPrompt, new Sdk.PromptOutput("dynamic-text"));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var contributor = await RegisterPromptAsync(host, client, "alpha", "tone");
        var directory = BoundRoots.AbsoluteDir("prompts");

        var text = await client.GetPluginPromptAsync(
            contributor, new ToolContext("sess-p", directory, 3), limit.Token);

        Assert.Equal("dynamic-text", text);
        var sent = Assert.Single(host.Requests(Sdk.ExtensionProtocol.PluginPrompt)).Read<Sdk.PromptExecutionRequest>();
        Assert.Equal("alpha", sent.PluginId);
        Assert.Equal("tone", sent.ContributorId);
        var context = sent.Context;
        Assert.False(string.IsNullOrWhiteSpace(context.RunId));
        Assert.Equal("sess-p", context.SessionId);
        Assert.Equal(directory, context.WorkingDirectory);
        Assert.Equal(3, context.Depth);
        Assert.Empty(context.PluginChain);
    }

    [Fact]
    public async Task Unregistered_prompt_returns_null_and_sends_nothing()
    {
        await using var harness = await HostClientHarness.StartAsync();
        await RegisterPromptAsync(harness.Host, harness.Client, "alpha", "tone");

        var missing = await harness.Client.GetPluginPromptAsync(
            new PluginPromptReference("alpha", "ghost"),
            new ToolContext("sess", BoundRoots.AbsoluteDir("prompts")),
            harness.Limit.Token);
        var blank = await harness.Client.GetPluginPromptAsync(
            new PluginPromptReference("", ""),
            new ToolContext("sess", BoundRoots.AbsoluteDir("prompts")),
            harness.Limit.Token);

        Assert.Null(missing);
        Assert.Null(blank);
        Assert.Empty(harness.Host.Requests("plugin.prompt"));
        Assert.NotEmpty(harness.Client.Diagnostics);
    }

    [Fact]
    public async Task Prompt_callbacks_run_against_the_prompt_root()
    {
        var tool = new StubTool("echo");
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var contributor = await RegisterPromptAsync(host, client, "alpha", "tone");
        var directory = BoundRoots.AbsoluteDir("promptbound");

        var hold = new InFlightRoot();
        host.On(Sdk.ExtensionProtocol.PluginPrompt, async (message, ct) =>
        {
            hold.Arrived.TrySetResult(message);
            await hold.Gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return new Sdk.PromptOutput("done");
        });
        var evaluation = client.GetPluginPromptAsync(
            contributor, new ToolContext("sess-pr", directory), limit.Token);
        var request = await hold.Arrived.Task.Bounded();
        var runId = BoundRoots.RunIdOf(request);

        var callback = await host.CallClientAsync(
            Sdk.ExtensionProtocol.HostToolCall, BoundRoots.BoundPayload(runId, "alpha", "echo", "{}"), limit.Token);
        Assert.Equal(("ran echo", false), BoundRoots.OutputOf(callback));
        hold.Gate.TrySetResult();
        Assert.Equal("done", await evaluation.Bounded());

        Assert.Equal(new ToolContext("sess-pr", directory, 1), Assert.Single(tool.Invocations).Context);
    }

    [Fact]
    public async Task Prompt_host_error_returns_null_with_a_diagnostic()
    {
        await using var host = new FakeHost();
        host.On("plugin.prompt", (_, _) => throw new FakeHostError("gone", "worker is gone"));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var contributor = await RegisterPromptAsync(host, client, "alpha", "tone");

        var text = await client.GetPluginPromptAsync(
            contributor,
            new ToolContext("sess", BoundRoots.AbsoluteDir("prompts")),
            limit.Token);

        Assert.Null(text);
        Assert.NotEmpty(client.Diagnostics);
    }

    /// <summary>A complete "active" row that a malformed sibling must poison.</summary>
    private static Sdk.PluginStatusInfo OkRow { get; } = new("ok", "1", "active", "s", "d", "sdk", [], null);
}
