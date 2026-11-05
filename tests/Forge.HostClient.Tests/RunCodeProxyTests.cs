using System.Text.Json;
using Forge.Abstractions;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary><c>run_code</c> proxy: stable description, local argument validation, timeout budgets and the
/// <c>code.execute</c> wire shape (camelCase context/tools/options, per-call expiry and recovery).</summary>
public sealed class RunCodeProxyTests
{
    [Fact]
    public async Task Description_is_stable_across_tool_registration_order()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
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
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
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
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var registry = new TestRegistry([new StubTool("run_code"), new StubTool("real")]);
        var proxy = client.CreateRunCodeTool(registry);

        Assert.DoesNotContain("\n  run_code ", proxy.Definition.Description, StringComparison.Ordinal);
        Assert.Contains("\n  real ", proxy.Definition.Description, StringComparison.Ordinal);

        var hold = new InFlightRoot { ResultJson = """{"content":"ok","isError":false}""" };
        BoundRoots.ParkRoot(host, "code.execute", hold);
        var invocation = proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1+1\"}"),
            new ToolContext("sess", BoundRoots.AbsoluteDir("self")),
            limit.Token).AsTask();
        hold.Release();
        var result = await invocation.Bounded();

        Assert.False(result.IsError);
        var request = Assert.Single(host.Requests("code.execute"));
        var tools = request.Arguments!.Value.GetProperty("tools").EnumerateArray()
            .Select(element => element.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(["real"], tools);
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
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var proxy = client.CreateRunCodeTool(new TestRegistry());

        var result = await proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", arguments),
            new ToolContext("sess", BoundRoots.AbsoluteDir("args")),
            limit.Token);

        Assert.True(result.IsError);
        Assert.Contains(fragment, result.Content);
        Assert.Empty(host.Requests("code.execute"));
    }

    [Fact]
    public async Task Oversize_script_is_rejected_locally()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var execution = new Sdk.CodeExecutionOptions { MaxScriptChars = 8 };
        var proxy = client.CreateRunCodeTool(new TestRegistry(), new RunCodeProxyOptions { Execution = execution });

        var result = await proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"123456789\"}"),
            new ToolContext("sess", BoundRoots.AbsoluteDir("args")),
            limit.Token);

        Assert.True(result.IsError);
        Assert.Contains("9 chars", result.Content);
        Assert.Contains("limit is 8", result.Content);
        Assert.Empty(host.Requests("code.execute"));
    }

    [Fact]
    public async Task Finite_huge_timeout_clamps_to_the_maximum_without_overflow()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var hold = new InFlightRoot { ResultJson = """{"content":"ok","isError":false}""" };
        BoundRoots.ParkRoot(host, "code.execute", hold);
        var proxy = client.CreateRunCodeTool(new TestRegistry());

        var invocation = proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1+1\",\"timeout_seconds\":1e300}"),
            new ToolContext("sess", BoundRoots.AbsoluteDir("huge")),
            limit.Token).AsTask();
        hold.Release();
        var result = await invocation.Bounded();

        Assert.False(result.IsError);
        var request = Assert.Single(host.Requests("code.execute"));
        Assert.Equal(600_000, request.Arguments!.Value.GetProperty("timeoutMilliseconds").GetInt32());
    }

    [Fact]
    public async Task Default_timeout_is_60s_and_values_above_600s_clamp()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var hold = new InFlightRoot();
        BoundRoots.ParkRoot(host, "code.execute", hold);
        var proxy = client.CreateRunCodeTool(new TestRegistry());
        var context = new ToolContext("sess", BoundRoots.AbsoluteDir("budgets"));
        hold.Release();

        await proxy.InvokeAsync(new ToolCall("a", "run_code", "{\"code\":\"1\"}"), context, limit.Token);
        await proxy.InvokeAsync(
            new ToolCall("b", "run_code", "{\"code\":\"1\",\"timeout_seconds\":10000}"), context, limit.Token);
        await proxy.InvokeAsync(
            new ToolCall("c", "run_code", "{\"code\":\"1\",\"timeout_seconds\":30}"), context, limit.Token);

        var budgets = host.Requests("code.execute")
            .Select(message => message.Arguments!.Value.GetProperty("timeoutMilliseconds").GetInt32())
            .ToArray();
        Assert.Equal([60_000, 600_000, 30_000], budgets);
    }

    [Fact]
    public async Task Null_timeout_uses_the_default_budget()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var hold = new InFlightRoot();
        BoundRoots.ParkRoot(host, "code.execute", hold);
        var proxy = client.CreateRunCodeTool(new TestRegistry());
        hold.Release();

        await proxy.InvokeAsync(
            new ToolCall("a", "run_code", "{\"code\":\"1\",\"timeout_seconds\":null}"),
            new ToolContext("sess", BoundRoots.AbsoluteDir("budgets")),
            limit.Token);

        Assert.Equal(60_000, Assert.Single(host.Requests("code.execute"))
            .Arguments!.Value.GetProperty("timeoutMilliseconds").GetInt32());
    }

    [Fact]
    public async Task Per_call_script_timeout_may_exceed_a_small_default_RequestTimeout()
    {
        await using var host = new FakeHost();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On("code.execute", async (message, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
            return Json.Element("""{"content":"slow-ok","isError":false}""");
        });
        var options = new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(30) };
        await using var client = ClientFactory.Create(host, options: options);
        await client.StartConnectedAsync(host);
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
    public async Task Code_execute_carries_camelCase_context_tools_and_options()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var execution = new Sdk.CodeExecutionOptions { MaxConcurrentToolCalls = 3, MaxResultChars = 1234 };
        var registry = new TestRegistry(
        [
            new StubTool("zeta", ToolSideEffect.Write),
            new StubTool("alpha", ToolSideEffect.ReadOnly),
        ]);
        var hold = new InFlightRoot { ResultJson = """{"content":"ok","isError":false}""" };
        BoundRoots.ParkRoot(host, "code.execute", hold);
        var proxy = client.CreateRunCodeTool(
            registry, new RunCodeProxyOptions { Execution = execution });
        var directory = BoundRoots.AbsoluteDir("wire");

        var invocation = proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1+1\",\"timeout_seconds\":45}"),
            new ToolContext("sess-wire", directory, 2),
            limit.Token).AsTask();
        hold.Release();
        await invocation.Bounded();

        var args = Assert.Single(host.Requests("code.execute")).Arguments!.Value;
        Assert.Equal(
            ["code", "context", "timeoutMilliseconds", "tools", "options"],
            args.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("1+1", args.GetProperty("code").GetString());
        Assert.Equal(45_000, args.GetProperty("timeoutMilliseconds").GetInt32());
        var context = args.GetProperty("context");
        Assert.Equal(
            ["runId", "sessionId", "workingDirectory", "depth", "pluginChain", "rootRunId"],
            context.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.False(string.IsNullOrWhiteSpace(context.GetProperty("runId").GetString()));
        Assert.Equal("sess-wire", context.GetProperty("sessionId").GetString());
        Assert.Equal(directory, context.GetProperty("workingDirectory").GetString());
        Assert.Equal(2, context.GetProperty("depth").GetInt32());
        Assert.Empty(context.GetProperty("pluginChain").EnumerateArray());
        var tools = args.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(["alpha", "zeta"], tools.Select(element => element.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(
            ["description", "inputSchemaJson", "name", "sideEffect"],
            tools[0].EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(0, tools[0].GetProperty("sideEffect").GetInt32());
        Assert.Equal(1, tools[1].GetProperty("sideEffect").GetInt32());
        var wireOptions = args.GetProperty("options");
        Assert.Equal(3, wireOptions.GetProperty("maxConcurrentToolCalls").GetInt32());
        Assert.Equal(1234, wireOptions.GetProperty("maxResultChars").GetInt32());
    }

    [Fact]
    public async Task Expired_script_call_fails_and_the_next_call_recovers()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        host.On("code.execute", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return Json.Element("""{"content":"never","isError":false}""");
        });
        var proxy = client.CreateRunCodeTool(new TestRegistry());
        var context = new ToolContext("sess", BoundRoots.AbsoluteDir("expiry"));

        var expired = await proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1\",\"timeout_seconds\":0.3}"), context, limit.Token);

        Assert.True(expired.IsError);
        Assert.Contains("script was stopped", expired.Content);
        Assert.Contains("run_code timeout", expired.Content);

        host.On("code.execute", (_, _) => Task.FromResult(Json.Element("""{"content":"recovered","isError":false}""")));
        var recovered = await proxy.InvokeAsync(
            new ToolCall("rc-2", "run_code", "{\"code\":\"2\"}"), context, limit.Token);

        Assert.Equal(new ToolResult("recovered", false), recovered);
        Assert.Equal(HostStatus.Running, client.Status);
    }
}
