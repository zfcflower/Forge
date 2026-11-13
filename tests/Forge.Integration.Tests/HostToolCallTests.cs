using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Abstractions;
using Xunit;

namespace Forge.Integration.Tests;

public sealed class HostToolCallTests
{
    [Fact]
    public async Task Plugin_calls_a_host_tool_through_the_resolver_after_approval()
    {
        var hostTool = new RecordingHostTool();
        var requests = new ConcurrentQueue<ApprovalRequest>();
        await using var fx = new E2eFixture(
            toolResolver: name => name == hostTool.Definition.Name ? hostTool : null,
            approvalPath: (request, _) =>
            {
                requests.Enqueue(request);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        await fx.StartWithApprovedPluginAsync("alpha", "a", PluginSources.AllTools.Length);

        await fx.AssertToolContentAsync(
            "alpha", "callHost", """{"target":"host-echo","args":{"text":"from-plugin"}}""", "host-echo:from-plugin");
        Assert.Equal(1, hostTool.Invocations);

        var request = Assert.Single(requests);
        Assert.Equal("host-echo", request.ToolName);
        Assert.Equal(ToolSideEffect.Write, request.SideEffect);
        Assert.Contains("from-plugin", request.ArgumentsJson, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(request.WorkingDirectory));
    }

    [Fact]
    public async Task Denied_approval_yields_an_error_result_and_the_host_tool_does_not_run()
    {
        var hostTool = new RecordingHostTool();
        var requests = new ConcurrentQueue<ApprovalRequest>();
        await using var fx = new E2eFixture(
            toolResolver: name => name == hostTool.Definition.Name ? hostTool : null,
            approvalPath: (request, _) =>
            {
                requests.Enqueue(request);
                return ValueTask.FromResult(ApprovalDecision.Deny);
            });
        await fx.StartWithApprovedPluginAsync("alpha", "a", PluginSources.AllTools.Length);

        var result = await fx.InvokeAsync(
            "alpha", "callHost", """{"target":"host-echo","args":{"text":"nope"}}""");

        Assert.True(result.IsError);
        Assert.Contains("denied", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, hostTool.Invocations);
        Assert.Single(requests);

        var echo = await fx.InvokeAsync("alpha", "echo", """{"text":"still-alive"}""");
        Assert.False(echo.IsError, echo.Content);
    }

    [Fact]
    public async Task Unknown_host_tool_yields_an_error_result()
    {
        var requests = new ConcurrentQueue<ApprovalRequest>();
        await using var fx = new E2eFixture(
            toolResolver: _ => null,
            approvalPath: (request, _) =>
            {
                requests.Enqueue(request);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        await fx.StartWithApprovedPluginAsync("alpha", "a", PluginSources.AllTools.Length);

        var result = await fx.InvokeAsync(
            "alpha", "callHost", """{"target":"no-such-tool","args":{}}""");

        Assert.True(result.IsError);
        Assert.Contains("Unknown tool 'no-such-tool'", result.Content, StringComparison.Ordinal);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task Host_tool_without_an_approval_path_is_refused()
    {
        var hostTool = new RecordingHostTool();
        await using var fx = new E2eFixture(toolResolver: name => name == hostTool.Definition.Name ? hostTool : null);
        await fx.StartWithApprovedPluginAsync("alpha", "a", PluginSources.AllTools.Length);

        var result = await fx.InvokeAsync(
            "alpha", "callHost", """{"target":"host-echo","args":{"text":"x"}}""");

        Assert.True(result.IsError);
        Assert.Contains("no approval path", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, hostTool.Invocations);
    }

    private sealed class RecordingHostTool : ITool
    {
        private int invocations;

        public ToolDefinition Definition { get; } =
            new("host-echo", "Echoes text from the main program", "{\"type\":\"object\"}");

        public ToolSideEffect SideEffect => ToolSideEffect.Write;

        public int Invocations => Volatile.Read(ref invocations);

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref invocations);
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var text = document.RootElement.TryGetProperty("text", out var value) ? value.GetString() : "";
            return ValueTask.FromResult(new ToolResult("host-echo:" + text));
        }
    }
}
