using System.Collections.Concurrent;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;
using Sdk = Forge.Plugin.Sdk;
using static Forge.HostClient.Tests.ToolFixtures;
using static Forge.HostClient.Tests.CommandFixtures;

namespace Forge.HostClient.Tests;

/// <summary>Plugin command proxies: approval as <see cref="ToolSideEffect.Execute"/>, bound context on the
/// wire and output mapping (<c>Output</c>/<c>PromptToSend</c>/<c>IsError</c>).</summary>
public sealed class ExtensionHostCommandTests
{
    private const string PluginCommand = Sdk.ExtensionProtocol.PluginCommand;

    [Fact]
    public async Task Approval_uses_Execute_and_the_working_directory_then_the_command_runs_bound()
    {
        var approval = new ApprovalSpy();
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, approval: approval.Approve);
        using var limit = TestLimit.Create();
        var command = await RegisterCommandAsync(host, client, "alpha", "hi");
        var directory = BoundRoots.AbsoluteDir("cmds");

        var result = await command.ExecuteAsync(
            "some args", new CommandContext("sess-cmd", directory), limit.Token);

        Assert.Equal(new CommandResult("hello", "next", false), result);
        Assert.Equal(
            new ApprovalRequest("plugin-command:alpha:hi", "\"some args\"", ToolSideEffect.Execute, directory), approval.Last);
        var message = Assert.Single(host.Requests(PluginCommand));
        var sent = message.Read<Sdk.CommandExecutionRequest>();
        Assert.Equal("alpha", sent.PluginId);
        Assert.Equal("hi", sent.Command);
        Assert.Equal("some args", sent.Arguments);
        var context = sent.Context;
        Assert.False(string.IsNullOrWhiteSpace(context.RunId));
        Assert.Equal("sess-cmd", context.SessionId);
        Assert.Equal(directory, context.WorkingDirectory);
        Assert.Equal(0, context.Depth);
        Assert.Empty(context.PluginChain);
        Assert.True(sent.TimeoutMilliseconds > 0);
        // The request is exactly the five documented fields: a fixarray of five elements, nothing appended.
        Assert.Equal(0x95, message.Payload.Span[0]);
    }

    [Fact]
    public async Task A_command_named_like_a_builtin_tool_is_approved_under_a_namespaced_name()
    {
        var approval = new ApprovalSpy(ApprovalDecision.Deny);
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, approval: approval.Approve);
        using var limit = TestLimit.Create();
        var command = await RegisterCommandAsync(host, client, "alpha", "todo");

        await command.ExecuteAsync("x", new CommandContext("sess-cmd", BoundRoots.AbsoluteDir("cmds2")), limit.Token);

        Assert.NotNull(approval.Last);
        Assert.Equal("plugin-command:alpha:todo", approval.Last!.ToolName);
        Assert.NotEqual("todo", approval.Last!.ToolName);
        Assert.Contains(':', approval.Last!.ToolName);
        Assert.StartsWith(ExtensionHostClient.PluginCommandApprovalPrefix, approval.Last!.ToolName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denied_approval_returns_an_error_and_sends_nothing()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(
            host, approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Deny));
        using var limit = TestLimit.Create();
        var command = await RegisterCommandAsync(host, client, "alpha", "hi");

        var result = await command.ExecuteAsync(
            "args", new CommandContext("sess", BoundRoots.AbsoluteDir("cmds")), limit.Token);

        Assert.True(result.IsError);
        Assert.Contains("denied", result.Output);
        Assert.Contains("'hi'", result.Output);
        Assert.Empty(host.Requests(PluginCommand));
    }

    [Fact]
    public async Task Missing_approval_path_fails_closed_without_IPC()
    {
        await using var harness = await HostClientHarness.StartAsync();
        var command = await RegisterCommandAsync(harness.Host, harness.Client, "alpha", "hi");

        var result = await command.ExecuteAsync(
            "args", new CommandContext("sess", BoundRoots.AbsoluteDir("cmds")), harness.Limit.Token);

        Assert.True(result.IsError);
        Assert.Contains("no approval path", result.Output);
        Assert.Empty(harness.Host.Requests(PluginCommand));
    }

    [Fact]
    public async Task Approval_failure_is_an_error_and_sends_nothing()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(
            host,
            approval: (_, _) => throw new InvalidOperationException("policy offline"));
        using var limit = TestLimit.Create();
        var command = await RegisterCommandAsync(host, client, "alpha", "hi");

        var result = await command.ExecuteAsync(
            "args", new CommandContext("sess", BoundRoots.AbsoluteDir("cmds")), limit.Token);

        Assert.True(result.IsError);
        Assert.Contains("approval path failed", result.Output);
        Assert.Contains("policy offline", result.Output);
        Assert.Empty(host.Requests(PluginCommand));
    }

    [Fact]
    public async Task Host_error_envelope_names_the_plugin_and_command()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var command = await RegisterCommandAsync(host, client, "alpha", "hi");
        host.On(PluginCommand, (_, _) => throw new FakeHostError("denied", "not permitted"));

        var result = await command.ExecuteAsync(
            "args", new CommandContext("sess", BoundRoots.AbsoluteDir("cmds")), limit.Token);

        Assert.True(result.IsError);
        Assert.Contains("'hi'", result.Output);
        Assert.Contains("plugin 'alpha'", result.Output);
        Assert.Contains("not permitted", result.Output);
    }

    [Fact]
    public async Task Malformed_command_result_is_an_error_with_a_diagnostic()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var command = await RegisterCommandAsync(host, client, "alpha", "hi");
        host.AnswerRaw(PluginCommand, HostWire.Payload("text"));

        var result = await command.ExecuteAsync(
            "args", new CommandContext("sess", BoundRoots.AbsoluteDir("cmds")), limit.Token);

        Assert.Equal(new CommandResult("The Extension Host returned no command result.", null, true), result);
        BoundRoots.AssertDiagnostic(client, "plugin.command returned unusable data");
    }

    [Fact]
    public async Task Relative_working_directory_is_rejected_locally()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var command = await RegisterCommandAsync(host, client, "alpha", "hi");

        var result = await command.ExecuteAsync("args", new CommandContext("sess", "relative/dir"), limit.Token);

        Assert.True(result.IsError);
        Assert.Contains("not a fully qualified path", result.Output);
        Assert.Empty(host.Requests(PluginCommand));
    }

    [Fact]
    public async Task Executing_after_stop_is_an_error_result_and_sends_nothing()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var command = await RegisterCommandAsync(host, client, "alpha", "hi");
        await client.StopAsync(limit.Token);

        var result = await command.ExecuteAsync(
            "args", new CommandContext("sess", BoundRoots.AbsoluteDir("cmds")), limit.Token).AsTask();

        Assert.True(result.IsError);
        Assert.Empty(host.Requests(PluginCommand));
    }

    [Fact]
    public async Task Command_callbacks_run_against_the_command_root_with_trusted_depth()
    {
        var seen = new ConcurrentQueue<ToolContext>();
        var tool = new StubTool("echo", ToolSideEffect.ReadOnly, (_, context, _) =>
        {
            seen.Enqueue(context);
            return ValueTask.FromResult(new ToolResult("cb-ok"));
        });
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var command = await RegisterCommandAsync(host, client, "alpha", "hi", new Sdk.CommandOutput("started"));
        var directory = BoundRoots.AbsoluteDir("cmdbound");

        var hold = new InFlightRoot();
        host.On(PluginCommand, async (message, ct) =>
        {
            hold.Arrived.TrySetResult(message);
            await hold.Gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return new Sdk.CommandOutput("started");
        });
        var invocation = command.ExecuteAsync("args", new CommandContext("sess-cb", directory), limit.Token).AsTask();
        var request = await hold.Arrived.Task.Bounded();
        var runId = BoundRoots.RunIdOf(request);

        var callback = await host.CallClientAsync(
            Sdk.ExtensionProtocol.HostToolCall, BoundRoots.BoundPayload(runId, "alpha", "echo", "{}"), limit.Token);
        Assert.Equal(("cb-ok", false), BoundRoots.OutputOf(callback));
        hold.Gate.TrySetResult();
        Assert.Equal(new CommandResult("started", null, false), await invocation.Bounded());

        Assert.Equal(new ToolContext("sess-cb", directory, 1), Assert.Single(seen));
    }
}
