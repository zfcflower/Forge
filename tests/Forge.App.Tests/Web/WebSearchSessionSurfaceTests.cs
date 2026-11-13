using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Providers;
using Forge.App.Web;
using Forge.Core.Storage;
using Forge.Core.Tools.Web;
using Xunit;

namespace Forge.App.Tests.Web;

/// <summary>
/// A real, already open <see cref="SessionRuntime"/> over a scripted model: <c>web_search</c> is offered exactly while a
/// usable backend exists, a settings change reaches the next turn of the open session, and a change that does not move the
/// set publishes no new tool set (the prompt cache stays).
/// </summary>
[Collection("forge-app-ui")]
public sealed class WebSearchSessionSurfaceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-web-session-" + Guid.NewGuid().ToString("N"));

    public WebSearchSessionSurfaceTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is harmless.
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly SqliteSessionStore store;
        private readonly SqliteTodoStore todos;

        public Rig(string root, Func<IReadOnlyList<ITool>> webTools)
        {
            var data = Path.Combine(root, "data");
            var workspace = Path.Combine(root, "ws");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(workspace);
            store = new SqliteSessionStore(data);
            todos = new SqliteTodoStore(data);
            var services = new AgentRuntimeServices(_ => Provider, HostClient: null, store, store, todos, new ModelSettings("main-model"))
            {
                WebTools = webTools,
            };
            Runtime = new SessionRuntime(services, new SessionInfo("s1", workspace, "t", DateTimeOffset.UnixEpoch));
        }

        public ScriptedProvider Provider { get; } = new();

        public SessionRuntime Runtime { get; }

        /// <summary>Runs one turn and returns the tool names its (single) request offered.</summary>
        public async Task<string[]> TurnAsync()
        {
            var before = Provider.Queue.Count;
            await Runtime.RunTurnAsync("hello", _ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
            var requests = Provider.Requests;
            Assert.Equal(before + 1, requests.Count);
            return [.. requests[^1].Tools.Select(tool => tool.Name)];
        }

        public async ValueTask DisposeAsync()
        {
            Runtime.Dispose();
            await store.DisposeAsync();
            await todos.DisposeAsync();
        }
    }

    private WebServices NewServices() =>
        new(new WebSettingsStore(Path.Combine(root, "settings.json")), new ProviderSecretStore(root), new HttpClient());

    [Fact]
    public async Task Web_search_follows_the_settings_from_the_next_turn_of_an_open_session()
    {
        var web = NewServices();
        await using var rig = new Rig(root, web.CreateTools);

        // No backend: only web_fetch, so the model never sees a tool that would ask and then fail.
        var first = await rig.TurnAsync();
        Assert.Contains(WebFetchTool.Name, first);
        Assert.DoesNotContain(WebSearchTool.Name, first);
        Assert.DoesNotContain(WebSearchTool.Name, rig.Runtime.ToolNames());
        var unconfigured = rig.Runtime.PublishedTools;
        Assert.NotNull(unconfigured);

        // Configuring a backend reaches the already open session on its next turn.
        web.Store.Save(new WebSettings(WebSettings.Searxng, "http://searx.local", false));
        var second = await rig.TurnAsync();
        Assert.Contains(WebSearchTool.Name, second);
        Assert.Contains(WebFetchTool.Name, second);
        var configured = rig.Runtime.PublishedTools;
        Assert.NotSame(unconfigured, configured);

        // A change that keeps a usable backend (another URL, another backend with a key) publishes nothing new.
        web.Store.Save(new WebSettings(WebSettings.Searxng, "http://other.local", true));
        Assert.Contains(WebSearchTool.Name, await rig.TurnAsync());
        Assert.Same(configured, rig.Runtime.PublishedTools);

        web.Secrets.Set(WebServices.TavilyKeyReference, "k");
        web.Store.Save(new WebSettings(WebSettings.Tavily, string.Empty, false));
        Assert.Contains(WebSearchTool.Name, await rig.TurnAsync());
        Assert.Same(configured, rig.Runtime.PublishedTools);

        // Removing the key takes web_search away again on the next turn.
        web.Secrets.Remove(WebServices.TavilyKeyReference);
        var removed = await rig.TurnAsync();
        Assert.DoesNotContain(WebSearchTool.Name, removed);
        Assert.Contains(WebFetchTool.Name, removed);
        var withoutSearch = rig.Runtime.PublishedTools;
        Assert.NotSame(configured, withoutSearch);

        // Still unusable (a backend without its key): nothing to republish.
        web.Store.Save(new WebSettings(WebSettings.Brave, string.Empty, false));
        Assert.DoesNotContain(WebSearchTool.Name, await rig.TurnAsync());
        Assert.Same(withoutSearch, rig.Runtime.PublishedTools);
    }

    [Fact]
    public async Task A_session_opened_with_a_backend_offers_web_search_on_its_first_turn()
    {
        var web = NewServices();
        web.Store.Save(new WebSettings(WebSettings.Searxng, "https://searx.local", false));
        await using var rig = new Rig(root, web.CreateTools);

        Assert.Contains(WebSearchTool.Name, await rig.TurnAsync());

        // The second turn without a change keeps the very same published set.
        var after = rig.Runtime.PublishedTools;
        Assert.Contains(WebSearchTool.Name, await rig.TurnAsync());
        Assert.Same(after, rig.Runtime.PublishedTools);
    }

    [Fact]
    public async Task A_failing_web_tool_factory_keeps_the_last_set_and_the_turn_still_runs()
    {
        var web = NewServices();
        web.Store.Save(new WebSettings(WebSettings.Searxng, "http://searx.local", false));
        var fail = false;
        await using var rig = new Rig(root, () => fail ? throw new InvalidOperationException("boom") : web.CreateTools());

        Assert.Contains(WebSearchTool.Name, await rig.TurnAsync());
        var published = rig.Runtime.PublishedTools;

        fail = true;
        var names = await rig.TurnAsync();
        Assert.Contains(WebSearchTool.Name, names);
        Assert.Same(published, rig.Runtime.PublishedTools);

        // Once the factory recovers, the next turn catches up with the settings.
        fail = false;
        web.Store.Save(WebSettings.Default);
        Assert.DoesNotContain(WebSearchTool.Name, await rig.TurnAsync());
    }

    private sealed class ScriptedProvider : IChatProvider
    {
        public ConcurrentQueue<ChatRequest> Queue { get; } = new();

        public IReadOnlyList<ChatRequest> Requests => [.. Queue];

        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Queue.Enqueue(request);
            await Task.Yield();
            yield return new TextDelta("done");
            yield return new MessageCompleted(ChatMessage.Assistant("done"), new TokenUsage(3, 2), StopReason.EndTurn);
        }
    }
}
