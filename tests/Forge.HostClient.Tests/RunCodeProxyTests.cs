using Forge.Abstractions;
using Forge.Ipc;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary><c>run_code</c> proxy: stable description, local argument validation, timeout budgets and the
/// <c>code.execute</c> wire shape (camelCase context/tools/options, per-call expiry and recovery).</summary>
public sealed class RunCodeProxyTests
{
    private const string CodeExecute = Sdk.ExtensionProtocol.CodeExecute;

    [Fact]
    public async Task Description_is_stable_across_tool_registration_order()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var first = new TestRegistry([new StubTool("b"), new StubTool("a")]);
        var second = new TestRegistry([new StubTool("a"), new StubTool("b")]);

        var one = client.CreateRunCodeTool(first).Definition.Description;
        var two = client.CreateRunCodeTool(second).Definition.Description;

        Assert.Equal(one, two);
    }

    [Fact]
    public async Task Description_updates_after_the_registry_changes()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var registry = new TestRegistry([new StubTool("solo")]);
        var proxy = client.CreateRunCodeTool(registry);
        var before = proxy.Definition.Description;
        Assert.Contains("solo", before);

        registry.Add(new StubTool("fresh"));
        var after = proxy.Definition.Description;

        Assert.NotEqual(before, after);
        Assert.Contains("fresh", after);
        Assert.Contains("solo", after);
    }

    [Fact]
    public async Task Own_run_code_tool_is_excluded_from_the_description_and_declarations()
    {
        await using var harness = await HostClientHarness.StartAsync();
        var registry = new TestRegistry([new StubTool("run_code"), new StubTool("real")]);
        var proxy = harness.Client.CreateRunCodeTool(registry);

        Assert.DoesNotContain("\n  run_code ", proxy.Definition.Description, StringComparison.Ordinal);
        Assert.Contains("\n  real ", proxy.Definition.Description, StringComparison.Ordinal);

        var hold = new InFlightRoot { Content = "ok" };
        BoundRoots.ParkRoot(harness.Host, CodeExecute, hold);
        var invocation = proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1+1\"}"),
            new ToolContext("sess", BoundRoots.AbsoluteDir("self")),
            harness.Limit.Token).AsTask();
        hold.Release();
        var result = await invocation.Bounded();

        Assert.False(result.IsError);
        var request = Assert.Single(harness.Host.Requests(CodeExecute)).Read<Sdk.CodeExecutionRequest>();
        Assert.Equal(["real"], request.Tools.Select(tool => tool.Name).ToArray());
    }

    [Theory]
    [InlineData("{}", "requires a 'code' string")]
    [InlineData("{\"code\":5}", "requires a 'code' string")]
    [InlineData("{\"code\":null}", "requires a 'code' string")]
    [InlineData("{\"code\":\"\"}", "non-empty")]
    [InlineData("{\"code\":\"   \"}", "non-empty")]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("\"text\"", "must be a JSON object")]
    [InlineData("not json", "not valid JSON")]
    [InlineData("{\"code\":\"x\",\"timeout_seconds\":\"5\"}", "'timeout_seconds' must be a positive number")]
    [InlineData("{\"code\":\"x\",\"timeout_seconds\":true}", "'timeout_seconds' must be a positive number")]
    [InlineData("{\"code\":\"x\",\"timeout_seconds\":{}}", "'timeout_seconds' must be a positive number")]
    [InlineData("{\"code\":\"x\",\"timeout_seconds\":[]}", "'timeout_seconds' must be a positive number")]
    [InlineData("{\"code\":\"x\",\"timeout_seconds\":0}", "'timeout_seconds' must be a positive number")]
    [InlineData("{\"code\":\"x\",\"timeout_seconds\":-5}", "'timeout_seconds' must be a positive number")]
    public async Task Invalid_arguments_are_rejected_locally_without_any_request(string arguments, string fragment)
    {
        await using var harness = await HostClientHarness.StartAsync();
        var proxy = harness.Client.CreateRunCodeTool(new TestRegistry());

        var result = await proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", arguments),
            new ToolContext("sess", BoundRoots.AbsoluteDir("args")),
            harness.Limit.Token);

        Assert.True(result.IsError);
        Assert.Contains(fragment, result.Content);
        Assert.Empty(harness.Host.Requests(CodeExecute));
    }

    [Fact]
    public async Task Oversize_script_is_rejected_locally()
    {
        await using var harness = await HostClientHarness.StartAsync();
        var execution = new Sdk.CodeExecutionOptions { MaxScriptChars = 8 };
        var proxy = harness.Client.CreateRunCodeTool(new TestRegistry(), new RunCodeProxyOptions { Execution = execution });

        var result = await proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"123456789\"}"),
            new ToolContext("sess", BoundRoots.AbsoluteDir("args")),
            harness.Limit.Token);

        Assert.True(result.IsError);
        Assert.Contains("9 chars", result.Content);
        Assert.Contains("limit is 8", result.Content);
        Assert.Empty(harness.Host.Requests(CodeExecute));
    }

    [Fact]
    public async Task Finite_huge_timeout_clamps_to_the_maximum_without_overflow()
    {
        await using var harness = await HostClientHarness.StartAsync();
        var hold = new InFlightRoot { Content = "ok" };
        BoundRoots.ParkRoot(harness.Host, CodeExecute, hold);
        var proxy = harness.Client.CreateRunCodeTool(new TestRegistry());

        var invocation = proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1+1\",\"timeout_seconds\":1e300}"),
            new ToolContext("sess", BoundRoots.AbsoluteDir("huge")),
            harness.Limit.Token).AsTask();
        hold.Release();
        var result = await invocation.Bounded();

        Assert.False(result.IsError);
        var request = Assert.Single(harness.Host.Requests(CodeExecute));
        Assert.Equal(600_000, request.Read<Sdk.CodeExecutionRequest>().TimeoutMilliseconds);
    }

    [Fact]
    public async Task Default_timeout_is_60s_and_values_above_600s_clamp()
    {
        await using var harness = await HostClientHarness.StartAsync();
        var hold = new InFlightRoot();
        BoundRoots.ParkRoot(harness.Host, CodeExecute, hold);
        var proxy = harness.Client.CreateRunCodeTool(new TestRegistry());
        var context = new ToolContext("sess", BoundRoots.AbsoluteDir("budgets"));
        hold.Release();

        await proxy.InvokeAsync(new ToolCall("a", "run_code", "{\"code\":\"1\"}"), context, harness.Limit.Token);
        await proxy.InvokeAsync(
            new ToolCall("b", "run_code", "{\"code\":\"1\",\"timeout_seconds\":10000}"), context, harness.Limit.Token);
        await proxy.InvokeAsync(
            new ToolCall("c", "run_code", "{\"code\":\"1\",\"timeout_seconds\":30}"), context, harness.Limit.Token);

        var budgets = harness.Host.Requests(CodeExecute)
            .Select(message => message.Read<Sdk.CodeExecutionRequest>().TimeoutMilliseconds)
            .ToArray();
        Assert.Equal([60_000, 600_000, 30_000], budgets);
    }

    [Fact]
    public async Task Null_timeout_uses_the_default_budget()
    {
        await using var harness = await HostClientHarness.StartAsync();
        var hold = new InFlightRoot();
        BoundRoots.ParkRoot(harness.Host, CodeExecute, hold);
        var proxy = harness.Client.CreateRunCodeTool(new TestRegistry());
        hold.Release();

        await proxy.InvokeAsync(
            new ToolCall("a", "run_code", "{\"code\":\"1\",\"timeout_seconds\":null}"),
            new ToolContext("sess", BoundRoots.AbsoluteDir("budgets")),
            harness.Limit.Token);

        Assert.Equal(60_000, Assert.Single(harness.Host.Requests(CodeExecute))
            .Read<Sdk.CodeExecutionRequest>().TimeoutMilliseconds);
    }

    [Fact]
    public async Task Per_call_script_timeout_may_exceed_a_small_default_RequestTimeout()
    {
        await using var host = new FakeHost();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On(CodeExecute, async (message, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
            return new Sdk.CodeExecutionResult("slow-ok");
        });
        var options = new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(30) };
        await using var client = await ClientFactory.StartAsync(host, options: options);
        using var limit = TestLimit.Create();
        var proxy = client.CreateRunCodeTool(new TestRegistry());

        var result = await proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1\",\"timeout_seconds\":1}"),
            new ToolContext("sess", BoundRoots.AbsoluteDir("small-default")),
            limit.Token);

        await entered.Task.Bounded();
        Assert.Equal(new ToolResult("slow-ok", false), result);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Code_execute_carries_the_context_tools_and_options()
    {
        await using var harness = await HostClientHarness.StartAsync();
        var execution = new Sdk.CodeExecutionOptions { MaxConcurrentToolCalls = 3, MaxResultChars = 1234 };
        var registry = new TestRegistry(
        [
            new StubTool("zeta", ToolSideEffect.Write),
            new StubTool("alpha", ToolSideEffect.ReadOnly),
        ]);
        var hold = new InFlightRoot { Content = "ok" };
        BoundRoots.ParkRoot(harness.Host, CodeExecute, hold);
        var proxy = harness.Client.CreateRunCodeTool(
            registry, new RunCodeProxyOptions { Execution = execution });
        var directory = BoundRoots.AbsoluteDir("wire");

        var invocation = proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1+1\",\"timeout_seconds\":45}"),
            new ToolContext("sess-wire", directory, 2),
            harness.Limit.Token).AsTask();
        hold.Release();
        await invocation.Bounded();

        var message = Assert.Single(harness.Host.Requests(CodeExecute));
        // Exactly the five documented fields: a fixarray of five, nothing appended.
        Assert.Equal(0x95, message.Payload.Span[0]);
        var args = message.Read<Sdk.CodeExecutionRequest>();
        Assert.Equal("1+1", args.Code);
        Assert.Equal(45_000, args.TimeoutMilliseconds);
        var context = args.Context;
        Assert.False(string.IsNullOrWhiteSpace(context.RunId));
        Assert.Equal(context.RunId, context.RootRunId);
        Assert.Equal("sess-wire", context.SessionId);
        Assert.Equal(directory, context.WorkingDirectory);
        Assert.Equal(2, context.Depth);
        Assert.Empty(context.PluginChain);
        var tools = args.Tools.ToArray();
        Assert.Equal(["alpha", "zeta"], tools.Select(tool => tool.Name).ToArray());
        Assert.All(tools, tool =>
        {
            Assert.NotNull(tool.Description);
            Assert.False(string.IsNullOrWhiteSpace(tool.InputSchemaJson));
        });
        Assert.Equal(Sdk.ToolSideEffect.ReadOnly, tools[0].SideEffect);
        Assert.Equal(Sdk.ToolSideEffect.Write, tools[1].SideEffect);
        Assert.Equal(3, args.Options.MaxConcurrentToolCalls);
        Assert.Equal(1234, args.Options.MaxResultChars);
    }

    [Fact]
    public async Task Expired_script_call_fails_and_the_next_call_recovers()
    {
        await using var harness = await HostClientHarness.StartAsync();
        harness.Host.On(CodeExecute, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new Sdk.CodeExecutionResult("never");
        });
        var proxy = harness.Client.CreateRunCodeTool(new TestRegistry());
        var context = new ToolContext("sess", BoundRoots.AbsoluteDir("expiry"));

        var expired = await proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1\",\"timeout_seconds\":0.3}"), context, harness.Limit.Token);

        Assert.True(expired.IsError);
        Assert.Contains("script was stopped", expired.Content);
        Assert.Contains("run_code timeout", expired.Content);

        harness.Host.Answer(CodeExecute, new Sdk.CodeExecutionResult("recovered"));
        var recovered = await proxy.InvokeAsync(
            new ToolCall("rc-2", "run_code", "{\"code\":\"2\"}"), context, harness.Limit.Token);

        Assert.Equal(new ToolResult("recovered", false), recovered);
        Assert.Equal(HostStatus.Running, harness.Client.Status);
    }
}
