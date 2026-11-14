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
        var first = new TestRegistry([new StubTool("bravo"), new StubTool("alpha")]);
        var second = new TestRegistry([new StubTool("alpha"), new StubTool("bravo")]);

        var one = client.CreateRunCodeTool(first).Definition.Description;
        var two = client.CreateRunCodeTool(second).Definition.Description;

        // Identical bytes, and both really carry the two names: the description is built from the
        // ordinal-sorted name list, never from the registry's enumeration order.
        Assert.Equal(one, two);
        Assert.Contains("alpha", one, StringComparison.Ordinal);
        Assert.Contains("bravo", one, StringComparison.Ordinal);
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
        var added = proxy.Definition.Description;
        Assert.Contains("solo", added);
        Assert.Contains("fresh", added);

        registry.Remove("solo");
        var after = proxy.Definition.Description;

        // The name list tracks the registry in both directions; the dropped name is gone, not hidden.
        Assert.NotEqual(added, after);
        Assert.Contains("fresh", after, StringComparison.Ordinal);
        Assert.DoesNotContain("solo", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Own_run_code_tool_is_excluded_from_the_description_and_declarations()
    {
        await using var harness = await HostClientHarness.StartAsync();
        var registry = new TestRegistry([new StubTool("run_code"), new StubTool("real")]);
        var proxy = harness.Client.CreateRunCodeTool(registry);

        // The name list is the description's tail, from the "Tools (count)" header on; everything before it is the
        // fixed script surface, which mentions run_code by name and must not leak into this check.
        var description = proxy.Definition.Description;
        var names = description[description.IndexOf("Tools (", StringComparison.Ordinal)..];
        Assert.Contains("real", names, StringComparison.Ordinal);
        Assert.DoesNotContain("run_code", names, StringComparison.Ordinal);

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

    [Fact]
    public async Task The_description_carries_names_and_surface_lines_but_not_schemas_or_tool_texts()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var schema = """{"type":"object","properties":{"path":{"type":"string","description":"where to look"}}}""";
        var registry = new TestRegistry(
        [
            new SchemaTool("reader", "Reads one file from the workspace.", schema),
            new SchemaTool("writer", "Writes one file into the workspace.", schema, ToolSideEffect.Write),
        ]);

        var description = client.CreateRunCodeTool(registry).Definition.Description;

        // The names and the two lookup forms the script uses are in; the per-tool payloads are not.
        Assert.Contains("reader", description, StringComparison.Ordinal);
        Assert.Contains("writer", description, StringComparison.Ordinal);
        Assert.Contains("Tools.List()", description, StringComparison.Ordinal);
        Assert.Contains("""Tools.Describe("tool_name")""", description, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"object\"", description, StringComparison.Ordinal);
        Assert.DoesNotContain("Reads one file", description, StringComparison.Ordinal);
        Assert.DoesNotContain("Writes one file", description, StringComparison.Ordinal);

        // run_code is not a member of the name list the description ends with.
        var names = description[description.IndexOf("Tools (", StringComparison.Ordinal)..];
        Assert.DoesNotContain("run_code", names, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_description_stays_small_and_byte_identical_when_a_tool_schema_grows()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var registry = new TestRegistry(SizedTools(6, 400));
        var proxy = client.CreateRunCodeTool(registry);
        var description = proxy.Definition.Description;

        // Six real-size schemas would add kilobytes if they were inlined; names only stay under the gate.
        Assert.True(description.Length < 3000, $"the description was {description.Length} chars");
        foreach (var tool in SizedTools(6, 400))
        {
            Assert.Contains(tool.Definition.Name, description, StringComparison.Ordinal);
        }

        // The same tool with a 500-char bigger schema: the description is rebuilt (its fingerprint moved) but
        // stays byte-identical, because schemas are never inlined.
        registry.Add(new SchemaTool("tool_0", "Tool 0.", Schema(900)));
        var grown = proxy.Definition.Description;

        Assert.Equal(description, grown);
        Assert.True(grown.Length < 3000, $"the grown description was {grown.Length} chars");
    }

    [Fact]
    public async Task The_description_follows_a_changed_execution_provider_on_the_same_tool()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var execution = new Sdk.CodeExecutionOptions { MaxConcurrentToolCalls = 2 };
        var proxy = client.CreateRunCodeTool(
            new TestRegistry(), new RunCodeProxyOptions { ExecutionProvider = () => execution });

        var before = proxy.Definition.Description;
        Assert.Contains("at most 2 inner calls", before, StringComparison.Ordinal);

        // The timeouts do not move; the provider's number alone must rebuild the description.
        execution = new Sdk.CodeExecutionOptions { MaxConcurrentToolCalls = 7 };
        var after = proxy.Definition.Description;

        Assert.Contains("at most 7 inner calls", after, StringComparison.Ordinal);
        Assert.Contains("default timeout 60 s, maximum 600 s", after, StringComparison.Ordinal);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task A_null_execution_provider_throws_instead_of_falling_back()
    {
        await using var harness = await HostClientHarness.StartAsync();
        var proxy = new RunCodeProxyTool(
            harness.Client,
            new TestRegistry(),
            new RunCodeProxyOptions { ExecutionProvider = () => null! });
        var context = new ToolContext("sess", BoundRoots.AbsoluteDir("null-provider"));

        // Reading the definition is as far as a misconfiguration may go; both paths refuse it loudly.
        var fromDefinition = Assert.Throws<InvalidOperationException>(() => proxy.Definition);
        var fromInvoke = Assert.Throws<InvalidOperationException>(
            () => proxy.InvokeAsync(
                new ToolCall("rc-1", "run_code", "{\"code\":\"1\"}"), context, harness.Limit.Token)
                .GetAwaiter().GetResult());

        Assert.Contains("returned null", fromDefinition.Message, StringComparison.Ordinal);
        Assert.Contains("never optional", fromDefinition.Message, StringComparison.Ordinal);
        Assert.Contains("returned null", fromInvoke.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Host.Requests(CodeExecute));
    }

    [Theory]
    [InlineData(0, 100_000)]
    [InlineData(8, 0)]
    public void CreateRunCodeTool_rejects_impossible_provider_budgets(int concurrency, int scriptChars)
    {
        var client = new ExtensionHostClient(new HostConnection { PipeName = "forge-runcode-validate" });
        var execution = new Sdk.CodeExecutionOptions { MaxConcurrentToolCalls = concurrency, MaxScriptChars = scriptChars };
        var options = new RunCodeProxyOptions { ExecutionProvider = () => execution };

        Assert.Throws<ArgumentOutOfRangeException>(() => client.CreateRunCodeTool(new TestRegistry(), options));
    }

    [Fact]
    public async Task The_wire_request_carries_the_provider_budgets_and_an_oversize_script_is_refused_locally()
    {
        await using var harness = await HostClientHarness.StartAsync();
        var execution = new Sdk.CodeExecutionOptions { MaxScriptChars = 10, MaxResultChars = 222, MaxConcurrentToolCalls = 3 };
        var stale = new Sdk.CodeExecutionOptions { MaxScriptChars = 999, MaxResultChars = 111, MaxConcurrentToolCalls = 1 };
        var proxy = harness.Client.CreateRunCodeTool(
            new TestRegistry(),
            new RunCodeProxyOptions { Execution = stale, ExecutionProvider = () => execution });
        var context = new ToolContext("sess", BoundRoots.AbsoluteDir("live-budgets"));

        var refused = await proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"12345678901\"}"), context, harness.Limit.Token);

        // Eleven characters against a live limit of ten: rejected before any request is sent.
        Assert.True(refused.IsError);
        Assert.Contains("limit is 10", refused.Content, StringComparison.Ordinal);
        Assert.Empty(harness.Host.Requests(CodeExecute));

        var hold = new InFlightRoot { Content = "ok" };
        BoundRoots.ParkRoot(harness.Host, CodeExecute, hold);
        var invocation = proxy.InvokeAsync(
            new ToolCall("rc-2", "run_code", "{\"code\":\"1\"}"), context, harness.Limit.Token).AsTask();
        hold.Release();
        await invocation.Bounded();

        // The sent options are the provider's current values, not the stale Execution fallback.
        var sent = Assert.Single(harness.Host.Requests(CodeExecute)).Read<Sdk.CodeExecutionRequest>();
        Assert.Equal(10, sent.Options.MaxScriptChars);
        Assert.Equal(222, sent.Options.MaxResultChars);
        Assert.Equal(3, sent.Options.MaxConcurrentToolCalls);
    }

    [Fact]
    public async Task A_budget_the_description_does_not_state_leaves_it_byte_identical()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var execution = new Sdk.CodeExecutionOptions { MaxScriptChars = 100_000 };
        var proxy = client.CreateRunCodeTool(
            new TestRegistry(), new RunCodeProxyOptions { ExecutionProvider = () => execution });
        var before = proxy.Definition.Description;

        // MaxScriptChars is neither printed nor part of the description's fingerprint.
        execution = new Sdk.CodeExecutionOptions { MaxScriptChars = 99_999 };
        var afterScriptChars = proxy.Definition.Description;
        Assert.Equal(before, afterScriptChars);

        // MaxResultChars is stated in the text, so it must change it.
        execution = new Sdk.CodeExecutionOptions { MaxScriptChars = 99_999, MaxResultChars = 12_345 };
        var afterResultChars = proxy.Definition.Description;

        Assert.NotEqual(afterScriptChars, afterResultChars);
        Assert.Contains("12345", afterResultChars, StringComparison.Ordinal);
    }

    /// <summary>A JSON schema of roughly <paramref name="characters"/> characters, real-shaped.</summary>
    private static string Schema(int characters)
    {
        var filler = new string('x', Math.Max(0, characters - 74));
        return "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\""
            + filler + "\"}}}";
    }

    /// <summary><paramref name="count"/> tools named tool_0.. with schemas of roughly <paramref name="characters"/> chars.</summary>
    private static SchemaTool[] SizedTools(int count, int characters)
    {
        var tools = new SchemaTool[count];
        for (var index = 0; index < count; index++)
        {
            tools[index] = new SchemaTool($"tool_{index}", $"Tool {index}.", Schema(characters));
        }

        return tools;
    }
}
