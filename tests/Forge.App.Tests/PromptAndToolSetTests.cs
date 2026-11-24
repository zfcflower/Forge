using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Two shared texts that must not drift from what the run actually offers: the system prompt (whose two identity variants are
/// documented to differ only in the first sentence, and which must not promise tools an agent kind does not hold) and the error
/// an agent gets for a tool it does not have.
/// </summary>
public sealed class PromptAndToolSetTests
{
    private sealed class OneCallProvider(ToolCallBlock call) : IChatProvider
    {
        private int calls;

        public ChatRequest? Last { get; private set; }

        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            Last = request;
            var index = Interlocked.Increment(ref calls);
            yield return new MessageCompleted(
                index == 1
                    ? new ChatMessage(MessageRole.Assistant, [call])
                    : new ChatMessage(MessageRole.Assistant, [new TextBlock("done")]),
                TokenUsage.Empty,
                StopReason.ToolUse);
        }
    }

    private sealed class NoopTool : ITool
    {
        public ToolDefinition Definition { get; } = new("grep", "probe", "{}");

        public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            ValueTask.FromResult(new ToolResult("no matches"));
    }

    [Fact]
    public void The_two_system_prompt_variants_differ_only_in_their_first_sentence()
    {
        var named = BuiltInPrompts.SystemNamed.Split('\n');
        var unnamed = BuiltInPrompts.System.Split('\n');

        Assert.Equal("You are Forge, a coding agent working inside an isolated Git worktree.", named[0]);
        Assert.Equal("You are a coding agent working inside an isolated Git worktree.", unnamed[0]);
        Assert.Equal(named[1..], unnamed[1..]);
    }

    [Fact]
    public void The_shared_prompt_does_not_promise_tools_an_agent_kind_lacks()
    {
        // The scout holds read/grep/find/ls only; a prompt telling it to reach for bash (which it once did) cost a production
        // run three turns and its first collapse. The shared layer is composed before the kind is known, so it must stay
        // generic: it may name a shell only conditionally.
        Assert.DoesNotContain("use bash only", BuiltInPrompts.System, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("read/ls/find/grep", BuiltInPrompts.System, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("when a shell tool is declared", BuiltInPrompts.System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_tool_error_names_the_tools_the_agent_does_hold()
    {
        var provider = new OneCallProvider(new ToolCallBlock("c1", "bash", """{"command":"ls"}"""));
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [new NoopTool()],
            Layers = new PromptLayers("sys", null, null),
        });
        var root = host.CreateRoot("session-unknown-tool", Directory.GetCurrentDirectory());

        await foreach (var _ in host.RunAsync(root, "go", TestContext.Current.CancellationToken))
        {
        }

        // The error is what the next request carries, which is what the model acts on.
        var result = Assert.Single(
            Assert.Single(provider.Last!.Messages, message => message.Role == MessageRole.Tool).Content.OfType<ToolResultBlock>());
        Assert.True(result.IsError);
        var text = Assert.IsType<TextBlock>(Assert.Single(result.Blocks)).Text;
        Assert.StartsWith("Unknown tool 'bash'. Available: ", text, StringComparison.Ordinal);
        const string marker = "Available: ";
        var offered = text[(text.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].TrimEnd('.').Split(", ");
        Assert.Contains("grep", offered, StringComparer.Ordinal);
        Assert.DoesNotContain("bash", offered, StringComparer.Ordinal);
    }
}
