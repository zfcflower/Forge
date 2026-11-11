using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Forge.Core.Providers;
using Forge.Core.Tests.Providers;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// Section 8.3.1: what the provider caches ahead of the messages (system + tools on the wire) stays byte-identical
/// unless the tool set really changes, and nothing session-specific such as the worktree path leaks into it.
/// </summary>
public class PrefixStabilityTests
{
    private const string StopOnly = "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    private static (string System, string Tools) Prefix(RecordedRequest request)
    {
        var root = request.Root;
        return (root.GetProperty("system").GetRawText(), root.GetProperty("tools").GetRawText());
    }

    private static (AgentHost Host, FakeHttpMessageHandler Handler, LineageTestStore Store) Create(
        IEnumerable<ITool> tools,
        int turns,
        PromptLayers? layers = null)
    {
        var handler = new FakeHttpMessageHandler();
        for (var index = 0; index < turns; index++)
        {
            handler.EnqueueSse(StopOnly);
        }

        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());
        var store = new LineageTestStore();
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = tools,
            Layers = layers ?? new PromptLayers("test system", "global agents", "project agents"),
            Store = store,
        });
        return (host, handler, store);
    }

    private static async Task RunAsync(AgentHost host, AgentInstance root, string text)
    {
        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, text));
        Assert.DoesNotContain(events, e => e is AgentFailed);
    }

    [Fact]
    public async Task Unchanged_tool_set_sends_byte_identical_system_and_tools()
    {
        var (host, handler, store) = Create([FakeTool.Echo("zeta"), FakeTool.Echo("alpha")], turns: 3);
        var session = await store.CreateSessionAsync("D:/w", "s", CancellationToken.None);
        var root = host.CreateRoot(session.Id, "D:/w");

        for (var turn = 0; turn < 3; turn++)
        {
            await RunAsync(host, root, "turn " + turn);
        }

        var first = Prefix(handler.Requests[0]);
        Assert.All(handler.Requests, request => Assert.Equal(first, Prefix(request)));
        Assert.Single(store.FingerprintCalls);
    }

    [Fact]
    public async Task Republishing_an_unchanged_tool_set_leaves_the_bytes_unchanged()
    {
        var alpha = FakeTool.Echo("alpha");
        var beta = FakeTool.Echo("beta");
        var (host, handler, store) = Create([alpha, beta], turns: 4);
        var session = await store.CreateSessionAsync("D:/w", "s", CancellationToken.None);
        var root = host.CreateRoot(session.Id, "D:/w");

        await RunAsync(host, root, "one");
        host.ReplaceExternalTools([alpha, beta]);
        await RunAsync(host, root, "two");
        host.ReplaceExternalTools([beta, alpha]);
        host.ReplaceExternalTools([FakeTool.Echo("beta"), FakeTool.Echo("alpha")]);
        await RunAsync(host, root, "three");
        await RunAsync(host, root, "four");

        var first = Prefix(handler.Requests[0]);
        Assert.All(handler.Requests, request => Assert.Equal(first, Prefix(request)));
        Assert.Single(store.FingerprintCalls);
    }

    [Fact]
    public async Task Hot_loaded_tool_changes_the_prefix_once_then_it_is_stable_again()
    {
        var (host, handler, store) = Create([FakeTool.Echo("alpha")], turns: 4);
        var session = await store.CreateSessionAsync("D:/w", "s", CancellationToken.None);
        var root = host.CreateRoot(session.Id, "D:/w");

        await RunAsync(host, root, "one");
        await RunAsync(host, root, "two");
        host.ReplaceExternalTools([FakeTool.Echo("alpha"), FakeTool.Echo("added")]);
        await RunAsync(host, root, "three");
        await RunAsync(host, root, "four");

        var before = Prefix(handler.Requests[0]);
        var after = Prefix(handler.Requests[2]);
        Assert.Equal(before, Prefix(handler.Requests[1]));
        Assert.Equal(before.System, after.System);
        Assert.NotEqual(before.Tools, after.Tools);
        Assert.Equal(after, Prefix(handler.Requests[3]));

        var calls = store.FingerprintCalls;
        Assert.Equal(2, calls.Count);
        Assert.NotEqual(calls[0].Fingerprint, calls[1].Fingerprint);
        Assert.Equal(calls[1].Fingerprint, host.PrefixFingerprint(root));
    }

    [Fact]
    public async Task Same_agents_md_content_produces_the_same_prefix_and_fingerprint()
    {
        var project = string.Concat("# Rules", "\n", "Use tabs.");
        var first = Create([FakeTool.Echo("alpha")], turns: 1, new PromptLayers("sys", null, project));
        var second = Create([FakeTool.Echo("alpha")], turns: 1, new PromptLayers("sys", null, new string(project.AsSpan())));
        var other = Create([FakeTool.Echo("alpha")], turns: 1, new PromptLayers("sys", null, "# Rules\nUse spaces."));

        var a = first.Host.CreateRoot("a", "D:/repo");
        var b = second.Host.CreateRoot("b", "D:/repo");
        var c = other.Host.CreateRoot("c", "D:/repo");
        await RunAsync(first.Host, a, "go");
        await RunAsync(second.Host, b, "go");
        await RunAsync(other.Host, c, "go");

        Assert.Equal(Prefix(first.Handler.Last), Prefix(second.Handler.Last));
        Assert.Equal(first.Host.PrefixFingerprint(a), second.Host.PrefixFingerprint(b));
        Assert.NotEqual(Prefix(first.Handler.Last).System, Prefix(other.Handler.Last).System);
        Assert.NotEqual(first.Host.PrefixFingerprint(a), other.Host.PrefixFingerprint(c));
    }

    [Fact]
    public async Task Worktree_path_never_enters_the_prefix()
    {
        var (host, handler, _) = Create([FakeTool.Echo("alpha")], turns: 2);
        var main = host.CreateRoot("main", Path.Combine(Path.GetTempPath(), "repo"));
        var worktree = host.CreateRoot("fork", Path.Combine(Path.GetTempPath(), "repo.worktrees", "feature-x"));

        await RunAsync(host, main, "go");
        await RunAsync(host, worktree, "go");

        Assert.NotEqual(main.WorkingDirectory, worktree.WorkingDirectory);
        Assert.Equal(Prefix(handler.Requests[0]), Prefix(handler.Requests[1]));
        Assert.DoesNotContain("feature-x", handler.Requests[1].Root.GetProperty("system").GetRawText(), StringComparison.Ordinal);
        Assert.Equal(host.PrefixFingerprint(main), host.PrefixFingerprint(worktree));
    }
}
