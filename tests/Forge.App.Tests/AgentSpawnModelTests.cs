using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// <c>spawn_agent</c>'s per-spawn override end to end: the model the user named reaches the child's request, a refusal
/// comes back as a tool error without spawning anything, and a resumed child is never silently switched to another
/// model. The provider is scripted, so the test asserts what the wire saw rather than what the code intended.
/// </summary>
public sealed class AgentSpawnModelTests
{
    private sealed record Reply(string? Text = null, string? ToolName = null, string? Arguments = null);

    /// <summary>Answers one script entry per request and records the model each request asked for.</summary>
    private sealed class ScriptedProvider(params Reply[] script) : IChatProvider
    {
        private int calls;

        public string Id => "scripted";

        public List<ModelSettings> Requests { get; } = [];

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            Requests.Add(request.Model);
            var reply = script[calls];
            calls++;
            if (reply.ToolName is { } name)
            {
                yield return new MessageCompleted(
                    new ChatMessage(MessageRole.Assistant, [new ToolCallBlock($"c{calls}", name, reply.Arguments ?? "{}")]),
                    TokenUsage.Empty,
                    StopReason.ToolUse);
            }
            else
            {
                yield return new MessageCompleted(
                    ChatMessage.Assistant(reply.Text ?? "done"),
                    TokenUsage.Empty,
                    StopReason.EndTurn);
            }
        }
    }

    private static AgentDefinition Reviewer { get; } = new(
        "Reviewer",
        "reviews a change",
        "prompt",
        new ModelSettings("default-model", ThinkingEffort.Low),
        ["read"]);

    private static AgentHost Host(ScriptedProvider provider, Func<SpawnModelRequest, SpawnModelChoice>? spawnModels) =>
        new(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [],
            Layers = new PromptLayers("sys", null, null),
            Agents = [Reviewer],
            Options = new AgentOptions { DefaultModel = new ModelSettings("root-model") },
            SpawnModels = spawnModels,
        });

    private static async Task<List<AgentEvent>> RunAsync(AgentHost host, AgentInstance root)
    {
        var events = new List<AgentEvent>();
        await foreach (var value in host.RunAsync(root, "go"))
        {
            events.Add(value);
        }

        return events;
    }

    [Fact]
    public async Task A_model_override_reaches_the_child_and_the_receipt_says_it_was_for_this_spawn_only()
    {
        var provider = new ScriptedProvider(
            new Reply(
                ToolName: "spawn_agent",
                Arguments: """{"kind":"Reviewer","task":"review this","model":"kimi-k2","thinking":"high"}"""),
            new Reply(Text: "review ok"),
            new Reply(Text: "done"));
        var requests = new List<SpawnModelRequest>();
        var host = Host(provider, request =>
        {
            requests.Add(request);
            return SpawnModelChoice.Resolved(new ModelSettings("kimi-k2", ThinkingEffort.High, null, "probe"), "clamped to high");
        });
        var root = host.CreateRoot("session-spawn-model", Directory.GetCurrentDirectory());

        var events = await RunAsync(host, root);

        var seen = Assert.Single(requests);
        Assert.Equal("kimi-k2", seen.Model);
        Assert.Null(seen.Provider);
        Assert.Equal("high", seen.Thinking);
        Assert.Equal(new ModelSettings("default-model", ThinkingEffort.Low), seen.Current);

        // The spawn never waits: the root's tool result says it started, and the override is on that answer because the model asked for
        // it here. The child's own request happens in the background, so neither its count nor its order is this case's business.
        Assert.Equal(new ModelSettings("root-model"), provider.Requests[0]);

        var started = Assert.Single(events.OfType<AgentSubAgentStarted>());
        Assert.Equal("kimi-k2", started.Model);
        Assert.Equal("probe", started.ProviderId);

        var result = Assert.Single(events.OfType<AgentToolFinished>()).Result;
        Assert.False(result.IsError);
        Assert.Contains("Started Reviewer", result.Text);
        Assert.Contains("still running, so there is no report yet", result.Text);
        Assert.Contains("[model: kimi-k2 on probe @ high; this spawn only; clamped to high]", result.Text);
    }

    [Fact]
    public async Task A_refused_override_is_a_tool_error_and_nothing_is_spawned()
    {
        var provider = new ScriptedProvider(
            new Reply(
                ToolName: "spawn_agent",
                Arguments: """{"kind":"Reviewer","task":"review this","model":"gpt-9"}"""),
            new Reply(Text: "done"));
        var host = Host(provider, _ => SpawnModelChoice.Refused("unknown model 'gpt-9'; call models."));
        var root = host.CreateRoot("session-spawn-refused", Directory.GetCurrentDirectory());

        var events = await RunAsync(host, root);

        // The refusal goes back to the model as an error result, so the turn makes a second request but starts no child.
        Assert.Equal(2, provider.Requests.Count);
        Assert.Empty(events.OfType<AgentSubAgentStarted>());
        var result = Assert.Single(events.OfType<AgentToolFinished>()).Result;
        Assert.True(result.IsError);
        Assert.Equal("unknown model 'gpt-9'; call models.", result.Text);
    }

    [Fact]
    public async Task A_resume_with_a_model_is_refused_before_the_resolver_runs()
    {
        var provider = new ScriptedProvider(
            new Reply(
                ToolName: "spawn_agent",
                Arguments: """{"kind":"Reviewer","task":"continue","resume_id":"reviewer-1","model":"kimi-k2"}"""),
            new Reply(Text: "done"));
        var calls = 0;
        var host = Host(provider, _ =>
        {
            calls++;
            return SpawnModelChoice.Refused("unreachable");
        });
        var root = host.CreateRoot("session-spawn-resume", Directory.GetCurrentDirectory());

        var events = await RunAsync(host, root);

        Assert.Equal(0, calls);
        var result = Assert.Single(events.OfType<AgentToolFinished>()).Result;
        Assert.True(result.IsError);
        Assert.Contains("resumed sub-agent keeps the model it started with", result.Text);
    }

    [Fact]
    public async Task A_refusal_before_a_child_is_registered_carries_no_model_receipt()
    {
        var provider = new ScriptedProvider(
            new Reply(
                ToolName: "spawn_agent",
                Arguments: """{"kind":"Loose","task":"review this","model":"kimi-k2"}"""),
            new Reply(Text: "done"));
        var loose = new AgentDefinition("Loose", "a kind nobody may spawn", "p", new ModelSettings("default-model"), null);
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [],
            Layers = new PromptLayers("sys", null, null),
            Agents = [loose],
            Options = new AgentOptions { DefaultModel = new ModelSettings("root-model") },
            SpawnModels = _ => SpawnModelChoice.Resolved(new ModelSettings("kimi-k2", ThinkingEffort.High, null, "probe")),
        });
        var root = host.CreateRoot("session-spawn-loose", Directory.GetCurrentDirectory());

        var events = await RunAsync(host, root);

        // The tools-list gate refuses before any instance exists, so the receipt must not claim a child ran on that model.
        var result = Assert.Single(events.OfType<AgentToolFinished>()).Result;
        Assert.True(result.IsError);
        Assert.Contains("no usable `tools` list", result.Text);
        Assert.DoesNotContain("[model:", result.Text);
        Assert.Empty(events.OfType<AgentSubAgentStarted>());
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public void The_override_parameters_exist_only_where_a_resolver_does()
    {
        var provider = new ScriptedProvider();

        var with = Host(provider, _ => SpawnModelChoice.Refused("unused"));
        var without = Host(provider, null);

        Assert.Contains("\"model\"", with.Tools.Definitions.Single(d => d.Name == "spawn_agent").InputSchemaJson);
        Assert.Contains("Model override", with.Tools.Definitions.Single(d => d.Name == "spawn_agent").Description);
        Assert.DoesNotContain("\"model\"", without.Tools.Definitions.Single(d => d.Name == "spawn_agent").InputSchemaJson);
        Assert.DoesNotContain("Model override", without.Tools.Definitions.Single(d => d.Name == "spawn_agent").Description);
    }
}
