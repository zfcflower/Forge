using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>The host-side hooks automations use: a sub-agent started outside a turn, and the quota event of a failed turn.</summary>
public class AutomationHostSupportTests
{
    private static string Workspace => Path.GetTempPath();

    private static readonly AgentDefinition Scout =
        new("Scout", "scout kind", "scout role", new ModelSettings("scout-model"), ["read"]);

    private sealed class ListSink : IAgentEventSink
    {
        private readonly List<AgentEvent> events = [];

        public IReadOnlyList<AgentEvent> Events
        {
            get
            {
                lock (events)
                {
                    return [.. events];
                }
            }
        }

        public void Emit(AgentEvent value)
        {
            lock (events)
            {
                events.Add(value);
            }
        }
    }

    [Fact]
    public async Task SpawnChild_runs_the_kind_under_the_root_and_returns_its_text_session_and_usage()
    {
        var usage = new TokenUsage(12, 3);
        var provider = new FakeProvider((request, _) => Turn.Say("analysis: flaky test", usage));
        var store = new InMemorySessionStore();
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], store: store, agents: [Scout]);
        var root = host.CreateRoot("parent-session", Workspace);
        var sink = new ListSink();

        var result = await host.SpawnChildAsync(root, "Scout", "look at the CI log", sink, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("analysis: flaky test", result.Text, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(result.ChildSessionId));
        Assert.NotEqual("parent-session", result.ChildSessionId);
        Assert.Equal(usage, result.Usage);
        var started = Assert.Single(sink.Events.OfType<AgentSubAgentStarted>());
        Assert.Equal(root.Id, started.AgentId);
        Assert.Equal("Scout", started.Kind);
        Assert.Equal(result.ChildSessionId, started.ChildSessionId);
        var finished = Assert.Single(sink.Events.OfType<AgentSubAgentFinished>());
        Assert.Equal(SubAgentStatus.Completed, finished.Status);
        Assert.Contains(provider.Requests, request => request.SystemPrompt.Contains("scout role", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SpawnChild_with_an_unknown_kind_is_an_error_result_not_an_exception()
    {
        var provider = new FakeProvider((_, _) => Turn.Say("unused"));
        var host = AgentTestHost.Create(provider, agents: [Scout]);
        var root = host.CreateRoot("parent-session", Workspace);

        var result = await host.SpawnChild(root, "Nope");

        Assert.True(result.IsError);
        Assert.Equal(string.Empty, result.ChildSessionId);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task SpawnChild_honours_cancellation()
    {
        var provider = new FakeProvider(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Turn.Say("never");
        });
        var host = AgentTestHost.Create(provider, agents: [Scout]);
        var root = host.CreateRoot("parent-session", Workspace);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var outcome = await Record.ExceptionAsync(() => host.SpawnChildAsync(root, "Scout", "task", new ListSink(), cts.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(outcome);
    }

    [Fact]
    public async Task A_turn_failing_on_a_spent_quota_emits_the_quota_event_before_the_failure()
    {
        var provider = new FakeProvider((_, _) => throw new ProviderException(
            "fake returned 429 TooManyRequests: {\"error\":{\"code\":\"insufficient_quota\"}}",
            retryable: true,
            statusCode: 429));
        var host = AgentTestHost.Create(provider);
        var root = host.CreateRoot("session", Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        var quotaIndex = events.FindIndex(static e => e is AgentQuotaExhausted);
        var failedIndex = events.FindIndex(static e => e is AgentFailed);
        Assert.True(quotaIndex >= 0, "no quota event");
        Assert.True(failedIndex > quotaIndex, "the failure must follow the quota event");
        var quota = ((AgentQuotaExhausted)events[quotaIndex]).Quota;
        Assert.Equal(root.Definition.Model.Model, quota.Model);
    }

    [Fact]
    public async Task An_ordinary_provider_failure_emits_no_quota_event()
    {
        var provider = new FakeProvider((_, _) => throw new ProviderException("fake returned 500: boom", retryable: false, statusCode: 500));
        var host = AgentTestHost.Create(provider);
        var root = host.CreateRoot("session", Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.Empty(events.OfType<AgentQuotaExhausted>());
        Assert.Single(events.OfType<AgentFailed>());
    }
}

internal static class AutomationHostTestExtensions
{
    public static Task<ChildAgentResult> SpawnChild(this AgentHost host, AgentInstance root, string kind) =>
        host.SpawnChildAsync(root, kind, "task", new NullSink(), CancellationToken.None);

    private sealed class NullSink : IAgentEventSink
    {
        public void Emit(AgentEvent value)
        {
        }
    }
}
