using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>Section 8.3: what the loop adds to each request for prompt caching, and what it records about it.</summary>
public class PromptCacheLoopTests
{
    private const string Workspace = "D:/work/cache";

    private static AgentHost Host(
        IChatProvider provider,
        ISessionStore? store,
        AgentOptions? options = null,
        IEnumerable<ITool>? tools = null,
        Action<FirstRequestCacheReport>? diagnostics = null) =>
        new(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = tools ?? [],
            Layers = new PromptLayers("test system", null, null),
            Store = store,
            Options = options ?? new AgentOptions(),
            CacheDiagnostics = diagnostics ?? (_ => { }),
        });

    private static async Task RunAsync(AgentHost host, AgentInstance root, string text)
    {
        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, text));
        Assert.DoesNotContain(events, e => e is AgentFailed);
    }

    [Fact]
    public async Task Cache_routing_key_is_the_lineage_root_for_an_inheriting_session()
    {
        var store = new LineageTestStore();
        var provider = new FakeProvider((_, _) => Turn.Say("ok"));
        var host = Host(provider, store);
        var parent = await store.CreateSessionAsync(Workspace, "parent", CancellationToken.None);
        var parentRoot = host.CreateRoot(parent.Id, Workspace);
        await RunAsync(host, parentRoot, "hi");
        var child = await store.InheritAsync(parent);
        var childRoot = await host.ResumeRootAsync(child.Id, Workspace, CancellationToken.None);
        await RunAsync(host, childRoot, "again");

        Assert.Equal(parent.Id, provider.Requests[0].CacheRoutingKey);
        Assert.Equal(parent.Id, provider.Requests[1].CacheRoutingKey);
        Assert.NotEqual(child.Id, provider.Requests[1].CacheRoutingKey);
    }

    [Fact]
    public async Task Cache_routing_key_falls_back_to_the_session_id()
    {
        var provider = new FakeProvider((_, _) => Turn.Say("ok"));
        var withoutStore = Host(provider, store: null);
        await RunAsync(withoutStore, withoutStore.CreateRoot("no-store", Workspace), "hi");

        var unknown = Host(provider, new InMemorySessionStore());
        await RunAsync(unknown, unknown.CreateRoot("not-in-store", Workspace), "hi");

        var broken = new LineageTestStore { FailGetSession = true };
        var failing = Host(provider, broken);
        await RunAsync(failing, failing.CreateRoot("broken-row", Workspace), "hi");

        Assert.Equal(["no-store", "not-in-store", "broken-row"], provider.Requests.Select(r => r.CacheRoutingKey));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("5m", null)]
    [InlineData("1h", "1h")]
    [InlineData("2h", null)]
    public async Task Message_cache_ttl_option_reaches_the_cache_hint(string? option, string? expected)
    {
        var provider = new FakeProvider((_, _) => Turn.Say("ok"));
        var host = Host(provider, null, new AgentOptions { MessageCacheTtl = option });
        var root = host.CreateRoot("s", Workspace);
        await RunAsync(host, root, "one");
        await RunAsync(host, root, "two");

        Assert.All(provider.Requests, request => Assert.Equal(expected, request.Cache!.MessageTtl));
        Assert.True(provider.Requests[1].Cache!.CacheSystemAndTools);
        Assert.Equal(provider.Requests[1].Messages.Count - 2, provider.Requests[1].Cache!.CacheThroughMessageIndex);
    }

    [Fact]
    public async Task Fingerprint_is_written_once_and_matches_the_request_prefix()
    {
        var store = new LineageTestStore();
        var provider = new FakeProvider((_, _) => Turn.Say("ok"));
        var host = Host(provider, store, tools: [FakeTool.Echo("alpha")]);
        var session = await store.CreateSessionAsync(Workspace, "s", CancellationToken.None);
        var root = host.CreateRoot(session.Id, Workspace);

        await RunAsync(host, root, "one");
        await RunAsync(host, root, "two");
        await RunAsync(host, root, "three");

        var call = Assert.Single(store.FingerprintCalls);
        Assert.Equal(session.Id, call.SessionId);
        var request = provider.Requests[^1];
        Assert.Equal(PromptFingerprint.Compute(request.SystemPrompt, request.Tools), call.Fingerprint);
        Assert.Equal(call.Fingerprint, host.PrefixFingerprint(root));
        Assert.Equal(call.Fingerprint, (await store.GetSessionAsync(session.Id, CancellationToken.None))!.PrefixFingerprint);
    }

    [Fact]
    public async Task Fingerprint_is_not_rewritten_when_a_new_registry_has_the_same_definitions()
    {
        var store = new LineageTestStore();
        var provider = new FakeProvider((_, _) => Turn.Say("ok"));
        var host = Host(provider, store, tools: [FakeTool.Echo("alpha")]);
        var session = await store.CreateSessionAsync(Workspace, "s", CancellationToken.None);
        var root = host.CreateRoot(session.Id, Workspace);

        await RunAsync(host, root, "one");
        host.ReplaceExternalTools([FakeTool.Echo("alpha")]);
        await RunAsync(host, root, "two");
        host.ReplaceExternalTools([FakeTool.Echo("alpha"), FakeTool.Echo("beta")]);
        await RunAsync(host, root, "three");
        host.ReplaceExternalTools([FakeTool.Echo("alpha")]);
        await RunAsync(host, root, "four");

        var calls = store.FingerprintCalls.Select(c => c.Fingerprint).ToArray();
        Assert.Equal(3, calls.Length);
        Assert.NotEqual(calls[0], calls[1]);
        Assert.Equal(calls[0], calls[2]);
    }

    [Fact]
    public async Task Catalog_without_fingerprint_support_does_not_fail_the_turn()
    {
        var store = new LineageTestStore { FingerprintNotSupported = true };
        var provider = new FakeProvider((_, _) => Turn.Say("ok"));
        var host = Host(provider, store);
        var session = await store.CreateSessionAsync(Workspace, "s", CancellationToken.None);
        var root = host.CreateRoot(session.Id, Workspace);

        await RunAsync(host, root, "one");
        await RunAsync(host, root, "two");

        Assert.Equal(2, provider.Requests.Count);
        Assert.Empty(store.FingerprintCalls);
    }

    [Fact]
    public async Task First_request_report_is_written_once_per_instance_with_origin_provider_and_model()
    {
        var store = new LineageTestStore();
        var usages = new[] { new TokenUsage(1000, 20, 900, 50), new TokenUsage(1200, 30, 1100, 0), new TokenUsage(5, 5) };
        var provider = new FakeProvider((_, index) => Turn.Say("ok", usages[Math.Min(index, usages.Length - 1)]));
        var reports = new List<FirstRequestCacheReport>();
        var host = Host(
            provider,
            store,
            new AgentOptions { DefaultModel = new ModelSettings("claude-test") },
            diagnostics: report => reports.Add(report));
        var parent = await store.CreateSessionAsync(Workspace, "parent", CancellationToken.None);
        await store.AppendAsync(parent.Id, ChatMessage.User("old"), null, CancellationToken.None);
        await store.AppendAsync(parent.Id, ChatMessage.Assistant("old answer"), null, CancellationToken.None);
        var child = await store.InheritAsync(parent);
        var root = await host.ResumeRootAsync(child.Id, Workspace, CancellationToken.None);

        await RunAsync(host, root, "one");
        await RunAsync(host, root, "two");

        var report = Assert.Single(reports);
        Assert.Equal(child.Id, report.SessionId);
        Assert.Equal(SessionOrigin.Full, report.Origin);
        Assert.Equal("fake", report.ProviderId);
        Assert.Equal("claude-test", report.Model);
        Assert.Equal(usages[0], report.Usage);
        Assert.Equal(
            $"[cache] first-request session={child.Id} agent=root origin=Full provider=fake model=claude-test input=1000 cache_read=900 cache_write=50 output=20",
            report.ToString());
    }

    [Fact]
    public async Task Failing_diagnostics_sink_does_not_fail_the_turn()
    {
        var provider = new FakeProvider((_, _) => Turn.Say("ok"));
        var host = Host(provider, null, diagnostics: _ => throw new InvalidOperationException("sink down"));
        await RunAsync(host, host.CreateRoot("s", Workspace), "hi");
        Assert.Single(provider.Requests);
    }
}
