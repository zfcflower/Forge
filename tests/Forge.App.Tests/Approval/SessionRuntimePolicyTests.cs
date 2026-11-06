using System.Reflection;
using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Tools;
using Xunit;

namespace Forge.App.Tests.Approval;

/// <summary>
/// The session runtime's approval policy: the extension tools that only stage or switch off go through without a
/// tool card (their real gate is the plugin approval card), deletion still asks, and the checklist stays allowed.
/// Without the approval wiring an ask is a denial on the agent loop's own gate, exactly as before the queue existed.
/// </summary>
public sealed class SessionRuntimePolicyTests
{
    private static IApprovalPolicy SessionPolicy()
    {
        var field = typeof(Forge.App.Agents.SessionRuntime).GetField("SessionPolicy", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return Assert.IsType<DefaultApprovalPolicy>(field.GetValue(null));
    }

    private static ApprovalRequest ToolCall(string tool, ToolSideEffect sideEffect = ToolSideEffect.Execute) =>
        new(tool, "{}", sideEffect, Path.GetTempPath());

    [Fact]
    public void Staging_and_switching_tools_do_not_ask()
    {
        var policy = SessionPolicy();

        Assert.Equal(ApprovalMode.Allow, policy.Evaluate(ToolCall(ExtensionToolNames.List, ToolSideEffect.ReadOnly)));
        Assert.Equal(ApprovalMode.Allow, policy.Evaluate(ToolCall(ExtensionToolNames.Create)));
        Assert.Equal(ApprovalMode.Allow, policy.Evaluate(ToolCall(ExtensionToolNames.Update)));
        Assert.Equal(ApprovalMode.Allow, policy.Evaluate(ToolCall(ExtensionToolNames.Disable)));
    }

    [Fact]
    public void Extension_delete_still_asks()
    {
        var policy = SessionPolicy();

        Assert.Equal(ApprovalMode.Ask, policy.Evaluate(ToolCall(ExtensionToolNames.Delete)));
    }

    [Fact]
    public void Todo_stays_allowed()
    {
        var policy = SessionPolicy();

        Assert.Equal(ApprovalMode.Allow, policy.Evaluate(ToolCall(TodoTool.Name, ToolSideEffect.Write)));
    }

    [Fact]
    public void Unknown_write_or_execute_tool_asks()
    {
        var policy = SessionPolicy();

        Assert.Equal(ApprovalMode.Ask, policy.Evaluate(ToolCall("shell_run")));
        Assert.Equal(ApprovalMode.Ask, policy.Evaluate(ToolCall("write_file", ToolSideEffect.Write)));
    }

    [Fact]
    public async Task Ask_with_no_approval_handler_is_a_denial_on_the_loop_gate()
    {
        // A runtime assembled without the approval wiring (SessionRuntime.Build leaves
        // AgentHostOptions.ApprovalHandler null when services.Approvals is null) keeps the loop's long-standing
        // gate: the ask-mode tool never runs, the loop emits AgentApprovalDenied, and the tool result is the
        // denial sentence. Driven with the real session policy so the ask is the one the app would ask.
        var provider = new ScriptedProvider(static (request, index) => index == 0
            ? ScriptedProvider.Call(("call-1", "probe_write", "{}"))
            : ScriptedProvider.Say("ok"));

        var invoked = 0;
        var probe = new ProbeTool("probe_write", ToolSideEffect.Write, () => Interlocked.Increment(ref invoked));

        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [probe],
            Layers = new PromptLayers("test system", null, null),
            Policy = SessionPolicy(),
            ApprovalHandler = null,
        });

        var dir = ApprovalFixtures.TempDir("forge-loop-gate-");
        try
        {
            var root = host.CreateRoot("session", dir);
            var events = new List<AgentEvent>();
            await foreach (var @event in host.RunAsync(root, "go"))
            {
                events.Add(@event);
            }

            Assert.Equal(0, invoked);
            Assert.Single(events.OfType<AgentApprovalDenied>());
            var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
            Assert.True(result.IsError);
            Assert.Contains("denied", result.Content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    /// <summary>Minimal scripted chat provider: turn 0 calls the probe, turn 1 closes the conversation.</summary>
    private sealed class ScriptedProvider(Func<ChatRequest, int, IReadOnlyList<ChatStreamEvent>> respond) : IChatProvider
    {
        private int calls;

        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            int index;
            lock (this)
            {
                index = calls++;
            }

            foreach (var value in respond(request, index))
            {
                await Task.Yield();
                yield return value;
            }
        }

        public static IReadOnlyList<ChatStreamEvent> Say(string text) =>
        [
            new TextDelta(text),
            new MessageCompleted(ChatMessage.Assistant(text), new TokenUsage(10, 5), StopReason.EndTurn),
        ];

        public static IReadOnlyList<ChatStreamEvent> Call(params (string Id, string Name, string Args)[] toolCalls)
        {
            var content = new List<ContentBlock>(toolCalls.Length + 1) { new TextBlock("calling") };
            foreach (var call in toolCalls)
            {
                content.Add(new ToolCallBlock(call.Id, call.Name, call.Args));
            }

            return [new MessageCompleted(new ChatMessage(MessageRole.Assistant, content), new TokenUsage(10, 5), StopReason.ToolUse)];
        }
    }

    /// <summary>Counts invocations; the loop must never reach an ask-mode tool without approval.</summary>
    private sealed class ProbeTool(string name, ToolSideEffect sideEffect, Action onInvoked) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, $"Probe {name}.", """{"type":"object","properties":{}}""");

        public ToolSideEffect SideEffect { get; } = sideEffect;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
        {
            onInvoked();
            return ValueTask.FromResult(new ToolResult("probe"));
        }
    }
}
