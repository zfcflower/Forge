using Forge.Abstractions;
using Xunit;
using static Forge.HostClient.Tests.CommandFixtures;
using static Forge.HostClient.Tests.ToolFixtures;

namespace Forge.HostClient.Tests;

/// <summary>
/// Root validation for every public entry point that carries a working directory: plugin tools,
/// <c>run_code</c>, plugin commands and prompt contributors. Null, blank, whitespace and relative
/// paths (including Windows drive-relative form) are rejected locally with an error (or a null prompt)
/// before any execution IPC is sent and before the approval path runs. A parent-provided absolute path
/// is used verbatim: it is checked for being fully qualified, never for existence, and never converted
/// to the process working directory.
/// </summary>
public sealed class HostClientRootContextTests
{
    public static IEnumerable<object?[]> InvalidRoots()
    {
        yield return [null];
        yield return [""];
        yield return [" "];
        yield return ["   "];
        yield return ["relative/root"];
        yield return ["./here"];
        yield return ["../up"];
        yield return ["C:drive-relative"];
    }

    [Theory]
    [MemberData(nameof(InvalidRoots))]
    public async Task Plugin_tool_with_an_invalid_root_is_rejected_without_ipc_or_approval(string? root)
    {
        var approval = new ApprovalSpy();
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(
            host,
            resolver: name => string.Equals(name, "echo", StringComparison.Ordinal) ? new StubTool("echo") : null,
            approval: approval.Approve);
        using var limit = TestLimit.Create();
        var proxy = await BoundRoots.RegisterPluginToolAsync(client, host, "alpha", "entry");

        var result = await proxy.InvokeAsync(
            new ToolCall("root-1", "entry", "{}"),
            new ToolContext("sess", root!),
            limit.Token);

        Assert.True(result.IsError);
        Assert.Contains("fully qualified", result.Content);
        Assert.Empty(host.Requests("plugin.execute"));
        Assert.Equal(0, approval.Calls);
    }

    [Theory]
    [MemberData(nameof(InvalidRoots))]
    public async Task Run_code_with_an_invalid_root_is_rejected_without_ipc_or_approval(string? root)
    {
        var approval = new ApprovalSpy();
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, approval: approval.Approve);
        using var limit = TestLimit.Create();
        var proxy = client.CreateRunCodeTool(new TestRegistry());

        var result = await proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1+1\"}"),
            new ToolContext("sess", root!),
            limit.Token);

        Assert.True(result.IsError);
        Assert.Contains("fully qualified", result.Content);
        Assert.Empty(host.Requests("code.execute"));
        Assert.Equal(0, approval.Calls);
    }

    [Theory]
    [MemberData(nameof(InvalidRoots))]
    public async Task Command_with_an_invalid_root_is_rejected_without_ipc_or_approval(string? root)
    {
        var approval = new ApprovalSpy();
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, approval: approval.Approve);
        using var limit = TestLimit.Create();
        var command = await RegisterCommandAsync(host, client, "alpha", "hi");

        var result = await command.ExecuteAsync("args", new CommandContext("sess", root!), limit.Token);

        Assert.True(result.IsError);
        Assert.Contains("fully qualified", result.Output);
        Assert.Empty(host.Requests("plugin.command"));
        Assert.Equal(0, approval.Calls);
    }

    [Theory]
    [MemberData(nameof(InvalidRoots))]
    public async Task Prompt_with_an_invalid_root_returns_null_without_ipc(string? root)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var contributor = await RegisterPromptAsync(host, client, "alpha", "tone");

        var text = await client.GetPluginPromptAsync(
            contributor,
            new ToolContext("sess", root!),
            limit.Token);

        Assert.Null(text);
        Assert.Empty(host.Requests("plugin.prompt"));
        Assert.NotEmpty(client.Diagnostics);
    }

    [Fact]
    public async Task Plugin_tool_sends_a_nonexistent_absolute_root_verbatim()
    {
        await using var host = new FakeHost();
        host.Answer("plugin.execute", """{"content":"ok","isError":false}""");
        await using var client = await ClientFactory.StartAsync(host, approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var proxy = await BoundRoots.RegisterPluginToolAsync(client, host, "alpha", "entry");
        var directory = BoundRoots.AbsoluteDir("missing-plugin") + "/does-not-exist";

        var result = await proxy.InvokeAsync(
            new ToolCall("root-1", "entry", "{}"),
            new ToolContext("sess", directory),
            limit.Token);

        Assert.False(result.IsError);
        var sent = Assert.Single(host.Requests("plugin.execute")).Arguments!.Value;
        Assert.Equal(directory, sent.GetProperty("context").GetProperty("workingDirectory").GetString());
    }

    [Fact]
    public async Task Run_code_sends_a_nonexistent_absolute_root_verbatim()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var hold = new InFlightRoot { ResultJson = """{"content":"ok","isError":false}""" };
        BoundRoots.ParkRoot(host, "code.execute", hold);
        var proxy = client.CreateRunCodeTool(new TestRegistry());
        var directory = BoundRoots.AbsoluteDir("missing-code") + "/does-not-exist";

        var invocation = proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1+1\"}"),
            new ToolContext("sess", directory),
            limit.Token).AsTask();
        hold.Release();
        var result = await invocation.Bounded();

        Assert.False(result.IsError);
        var sent = Assert.Single(host.Requests("code.execute")).Arguments!.Value;
        Assert.Equal(directory, sent.GetProperty("context").GetProperty("workingDirectory").GetString());
    }

    [Fact]
    public async Task Command_sends_a_nonexistent_absolute_root_verbatim()
    {
        var approval = new ApprovalSpy();
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, approval: approval.Approve);
        using var limit = TestLimit.Create();
        var command = await RegisterCommandAsync(host, client, "alpha", "hi");
        var directory = BoundRoots.AbsoluteDir("missing-command") + "/does-not-exist";

        var result = await command.ExecuteAsync("args", new CommandContext("sess", directory), limit.Token);

        Assert.False(result.IsError);
        Assert.Equal(directory, approval.Last?.WorkingDirectory);
        var sent = Assert.Single(host.Requests("plugin.command")).Arguments!.Value;
        Assert.Equal(directory, sent.GetProperty("context").GetProperty("workingDirectory").GetString());
    }

    [Fact]
    public async Task Prompt_sends_a_nonexistent_absolute_root_verbatim()
    {
        await using var host = new FakeHost();
        host.Answer("plugin.prompt", """{"text":"dynamic-text"}""");
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var contributor = await RegisterPromptAsync(host, client, "alpha", "tone");
        var directory = BoundRoots.AbsoluteDir("missing-prompt") + "/does-not-exist";

        var text = await client.GetPluginPromptAsync(
            contributor,
            new ToolContext("sess", directory),
            limit.Token);

        Assert.Equal("dynamic-text", text);
        var sent = Assert.Single(host.Requests("plugin.prompt")).Arguments!.Value;
        Assert.Equal(directory, sent.GetProperty("context").GetProperty("workingDirectory").GetString());
    }
}
