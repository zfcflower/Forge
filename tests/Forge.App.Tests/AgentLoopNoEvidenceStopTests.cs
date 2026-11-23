using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The loop's circuit breaker: a run that keeps asking the same question ends with what it produced instead of spending the
/// whole turn budget and then throwing it away. Each case drives the real loop through a scripted provider, so the assertions
/// are about turns, tool executions and the text the run hands back — not about the predicate in isolation.
/// </summary>
public sealed class AgentLoopNoEvidenceStopTests : IDisposable
{
    private readonly string workspace = Directory.CreateTempSubdirectory("forge-no-evidence-").FullName;

    public void Dispose() => Directory.Delete(workspace, recursive: true);

    /// <summary>A stand-in for a read-only search tool: counts executions and answers with the text the real one answers.</summary>
    private sealed class ProbeTool(string name, string answer) : ITool
    {
        public int Calls { get; private set; }

        public ToolDefinition Definition { get; } = new(name, "probe", "{}");

        public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(new ToolResult(answer));
        }
    }

    /// <summary>Answers turn <c>n</c> with the message the script builds, the way a provider does: text first, then the end.</summary>
    private sealed class ScriptedProvider(Func<int, ChatMessage> script) : IChatProvider
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var index = Interlocked.Increment(ref calls);
            var message = script(index);
            foreach (var block in message.Content)
            {
                if (block is TextBlock text)
                {
                    yield return new TextDelta(text.Text);
                }
            }

            yield return new MessageCompleted(message, TokenUsage.Empty, StopReason.ToolUse);
        }
    }

    private static ChatMessage CallsTool(params ToolCallBlock[] calls) => new(MessageRole.Assistant, calls);

    private static ChatMessage Says(string text) => new(MessageRole.Assistant, [new TextBlock(text)]);

    private async Task<(ScriptedProvider Provider, List<AgentEvent> Events)> RunAsync(
        ScriptedProvider provider,
        IEnumerable<ITool> tools,
        int noEvidenceTurnLimit)
    {
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = tools,
            Layers = new PromptLayers("sys", null, null),
            Options = new AgentOptions { NoEvidenceTurnLimit = noEvidenceTurnLimit },
        });
        var root = host.CreateRoot("session-no-evidence", workspace);

        var events = new List<AgentEvent>();
        await foreach (var evt in host.RunAsync(root, "find it"))
        {
            events.Add(evt);
        }

        return (provider, events);
    }

    [Fact]
    public async Task Three_turns_of_the_same_empty_search_end_the_run_with_what_it_produced()
    {
        var search = new ProbeTool("grep", "no matches\nscanned 1 file(s)");
        var provider = new ScriptedProvider(index => index == 1
            ? CallsTool(new ToolCallBlock("c1", "grep", """{"pattern":"codemodel"}"""))
            : CallsTool(new ToolCallBlock($"c{index}", "grep", """{"pattern":"codemodel"}""")));

        var (_, events) = await RunAsync(provider, [search], noEvidenceTurnLimit: 3);

        Assert.Equal(3, provider.Calls);
        Assert.Equal(3, search.Calls);
        Assert.Single(events.OfType<AgentTurnCompleted>());
        var text = string.Concat(events.OfType<AgentTextDelta>().Select(delta => delta.Text));
        Assert.Contains("stopped: 3 consecutive turns returned no new evidence", text, StringComparison.Ordinal);
        Assert.Empty(events.OfType<AgentFailed>());
    }

    [Fact]
    public async Task A_search_that_finds_something_clears_the_streak()
    {
        var empty = new ProbeTool("grep", "no matches\nscanned 1 file(s)");
        var hit = new ProbeTool("find", "src/hit.cs");
        var provider = new ScriptedProvider(index => index switch
        {
            1 => CallsTool(new ToolCallBlock("c1", "grep", """{"pattern":"x"}""")),
            2 => CallsTool(new ToolCallBlock("c2", "grep", """{"pattern":"x"}""")),
            3 => CallsTool(new ToolCallBlock("c3", "find", """{"pattern":"x"}""")),
            4 => CallsTool(new ToolCallBlock("c4", "grep", """{"pattern":"x"}""")),
            5 => CallsTool(new ToolCallBlock("c5", "grep", """{"pattern":"x"}""")),
            _ => Says("done"),
        });

        var (_, events) = await RunAsync(provider, [empty, hit], noEvidenceTurnLimit: 3);

        Assert.Equal(6, provider.Calls);
        Assert.Equal(4, empty.Calls);
        var text = string.Concat(events.OfType<AgentTextDelta>().Select(delta => delta.Text));
        Assert.DoesNotContain("no new evidence", text, StringComparison.Ordinal);
        Assert.Contains("done", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_batch_repeated_call_for_call_counts_as_no_evidence_whatever_the_tool_is()
    {
        var shell = new ProbeTool("probe", "ok");
        var provider = new ScriptedProvider(index =>
            CallsTool(new ToolCallBlock($"c{index}", "probe", """{"command":"ls"}""")));

        var (_, events) = await RunAsync(provider, [shell], noEvidenceTurnLimit: 3);

        Assert.Equal(4, provider.Calls);
        Assert.Equal(4, shell.Calls);
        var text = string.Concat(events.OfType<AgentTextDelta>().Select(delta => delta.Text));
        Assert.Contains("stopped: 3 consecutive turns returned no new evidence", text, StringComparison.Ordinal);
        Assert.Contains("No result was produced.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_limit_of_zero_leaves_the_streak_alone()
    {
        var search = new ProbeTool("grep", "no matches\nscanned 1 file(s)");
        var provider = new ScriptedProvider(index =>
            index == 5
                ? Says("done")
                : CallsTool(new ToolCallBlock($"c{index}", "grep", """{"pattern":"x"}""")));

        var (_, events) = await RunAsync(provider, [search], noEvidenceTurnLimit: 0);

        Assert.Equal(5, provider.Calls);
        Assert.Equal(4, search.Calls);
        Assert.DoesNotContain(events.OfType<AgentTextDelta>(), delta => delta.Text.Contains("no new evidence", StringComparison.Ordinal));
    }
}
