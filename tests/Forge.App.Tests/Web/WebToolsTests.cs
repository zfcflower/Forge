using System.Reflection;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Providers;
using Forge.App.Web;
using Forge.Core.Tools.Web;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Web;

[Collection("forge-app-ui")]
public sealed class WebToolsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "forge-web-" + Guid.NewGuid().ToString("N"));

    public WebToolsTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string SettingsPath => Path.Combine(dir, "settings.json");

    private WebServices NewServices() =>
        new(new WebSettingsStore(SettingsPath), new ProviderSecretStore(dir), new HttpClient());

    [Fact]
    public void Defaults_when_the_file_is_missing()
    {
        var store = new WebSettingsStore(SettingsPath);
        Assert.Null(store.Current.SearchBackend);
        Assert.False(store.Current.AllowPrivateNetwork);
        Assert.Equal(string.Empty, store.Current.SearxngUrl);
    }

    [Fact]
    public void Settings_round_trip_and_keep_other_sections()
    {
        File.WriteAllText(SettingsPath, """{"preferences":{"language":"en"},"web":{"note":"keep"}}""");
        new WebSettingsStore(SettingsPath).Save(new WebSettings("SearXNG", " http://s.local ", true));

        var reloaded = new WebSettingsStore(SettingsPath).Current;
        Assert.Equal("searxng", reloaded.SearchBackend);
        Assert.Equal("http://s.local", reloaded.SearxngUrl);
        Assert.True(reloaded.AllowPrivateNetwork);
        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("\"language\"", text);
        Assert.Contains("\"note\"", text);
    }

    [Fact]
    public void Unknown_backend_token_reads_as_none()
    {
        File.WriteAllText(SettingsPath, """{"web":{"searchBackend":"bing","allowPrivateNetwork":"yes"}}""");
        var store = new WebSettingsStore(SettingsPath);
        Assert.Null(store.Current.SearchBackend);
        Assert.False(store.Current.AllowPrivateNetwork);
    }

    [Fact]
    public void Corrupt_file_reads_as_defaults_and_save_refuses_to_overwrite_it()
    {
        File.WriteAllText(SettingsPath, "{ not json");
        var store = new WebSettingsStore(SettingsPath);
        Assert.Null(store.Current.SearchBackend);
        Assert.Throws<InvalidOperationException>(() => store.Save(new WebSettings("brave", "", false)));
        Assert.Equal("{ not json", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Api_key_goes_to_the_secret_store_and_never_into_settings_json()
    {
        var services = NewServices();
        services.Store.Save(new WebSettings("brave", "", false));
        services.Secrets.Set(WebServices.BraveKeyReference, "SUPER-SECRET-KEY");

        Assert.DoesNotContain("SUPER-SECRET-KEY", File.ReadAllText(SettingsPath));
        foreach (var file in Directory.GetFiles(dir).Where(f => !f.EndsWith("provider-secrets.key", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("SUPER-SECRET-KEY", File.ReadAllText(file));
        }

        Assert.Equal("brave", services.CreateBackend(services.Store.Current)!.Id);
    }

    [Fact]
    public void Both_web_tools_are_always_registered_whatever_the_configuration()
    {
        var services = NewServices();
        Assert.Equal([WebFetchTool.Name, WebSearchTool.Name], services.CreateTools().Select(t => t.Definition.Name));

        // A backend that needs a key but has none, or a SearXNG without a valid URL, keeps the same tool surface.
        services.Store.Save(new WebSettings("tavily", "", false));
        Assert.Equal([WebFetchTool.Name, WebSearchTool.Name], services.CreateTools().Select(t => t.Definition.Name));

        services.Store.Save(new WebSettings("searxng", "not a url", false));
        Assert.Equal([WebFetchTool.Name, WebSearchTool.Name], services.CreateTools().Select(t => t.Definition.Name));
    }

    [Theory]
    [InlineData("brave", "")]
    [InlineData("tavily", "")]
    [InlineData("searxng", "not a url")]
    [InlineData(null, "")]
    public async Task Unconfigured_web_search_reports_it_instead_of_searching(string? backend, string url)
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var services = new WebServices(new WebSettingsStore(SettingsPath), new ProviderSecretStore(dir), http);
        services.Store.Save(new WebSettings(backend, url, false));
        var search = services.CreateTools().Single(t => t.Definition.Name == WebSearchTool.Name);

        var result = await search.InvokeAsync(new ToolCall("1", WebSearchTool.Name, "{\"query\":\"forge\"}"), new ToolContext("s", dir), default);

        Assert.True(result.IsError);
        Assert.Contains(WebSearchTool.NotConfiguredMessage, result.Content);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Web_search_follows_a_settings_change_without_rebuilding_the_tools()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var services = new WebServices(new WebSettingsStore(SettingsPath), new ProviderSecretStore(dir), http);
        var search = services.CreateTools().Single(t => t.Definition.Name == WebSearchTool.Name);
        var call = new ToolCall("1", WebSearchTool.Name, "{\"query\":\"forge\"}");
        var context = new ToolContext("s", dir);

        Assert.True((await search.InvokeAsync(call, context, default)).IsError);

        services.Store.Save(new WebSettings("searxng", "http://first.local", false));
        var first = await search.InvokeAsync(call, context, default);
        Assert.False(first.IsError, first.Content);
        Assert.Equal("first.local", handler.Requests[^1].Host);

        services.Store.Save(new WebSettings("searxng", "http://second.local", false));
        Assert.False((await search.InvokeAsync(call, context, default)).IsError);
        Assert.Equal("second.local", handler.Requests[^1].Host);

        // The key is read per call too: a stored Brave key takes over from SearXNG at once.
        services.Store.Save(new WebSettings("brave", "", false));
        Assert.True((await search.InvokeAsync(call, context, default)).IsError);
        services.Secrets.Set(WebServices.BraveKeyReference, "k");
        await search.InvokeAsync(call, context, default);
        Assert.Equal("api.search.brave.com", handler.Requests[^1].Host);
        Assert.Equal(3, handler.Requests.Count);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"results":[{"title":"T","url":"https://t/","content":"c"}],"web":{"results":[]}}""", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public void Web_search_is_registered_for_searxng_with_a_url()
    {
        var services = NewServices();
        services.Store.Save(new WebSettings("searxng", "http://searx.local", false));
        Assert.Equal([WebFetchTool.Name, WebSearchTool.Name], services.CreateTools().Select(t => t.Definition.Name));
    }

    [Fact]
    public void Session_surface_includes_the_web_tools_and_both_ask_for_approval()
    {
        var services = NewServices();
        services.Store.Save(new WebSettings("searxng", "http://searx.local", false));
        var runtimeServices = new AgentRuntimeServices(
            static _ => throw new InvalidOperationException("unused"),
            null,
            null!,
            null!,
            null!,
            new ModelSettings("test"))
        {
            WebTools = services.CreateTools,
        };
        var runtime = new SessionRuntime(runtimeServices, new SessionInfo("s", dir, "t", DateTimeOffset.UtcNow));

        Assert.Equal([WebFetchTool.Name, WebSearchTool.Name], runtime.BuildTools().Select(t => t.Definition.Name));

        var field = typeof(SessionRuntime).GetField("SessionPolicy", BindingFlags.NonPublic | BindingFlags.Static)!;
        var policy = (IApprovalPolicy)field.GetValue(null)!;
        foreach (var name in new[] { WebFetchTool.Name, WebSearchTool.Name })
        {
            Assert.Equal(ApprovalMode.Ask, policy.Evaluate(new ApprovalRequest(name, "{}", ToolSideEffect.ReadOnly, dir)));
        }
    }

    [Fact]
    public void Session_surface_has_no_web_tools_without_the_factory()
    {
        var runtimeServices = new AgentRuntimeServices(
            static _ => throw new InvalidOperationException("unused"), null, null!, null!, null!, new ModelSettings("test"));
        var runtime = new SessionRuntime(runtimeServices, new SessionInfo("s", dir, "t", DateTimeOffset.UtcNow));
        Assert.Empty(runtime.BuildTools());
    }

    [Fact]
    public async Task Section_view_model_persists_backend_saves_key_securely_and_reports_a_missing_config()
    {
        var services = NewServices();
        using var vm = new WebSettingsSectionViewModel(services);

        vm.SelectedBackend = vm.Backends.Single(o => o.Token == "brave");
        Assert.True(vm.ShowsKey);
        Assert.Equal("brave", new WebSettingsStore(SettingsPath).Current.SearchBackend);

        vm.AllowPrivateNetwork = true;
        Assert.True(new WebSettingsStore(SettingsPath).Current.AllowPrivateNetwork);

        vm.ApiKeyText = "TYPED-KEY";
        vm.SaveCommand.Execute(null);
        await Task.Delay(200);
        for (var i = 0; i < 50 && !vm.HasStoredKey; i++)
        {
            await Task.Delay(50);
        }

        Assert.True(vm.HasStoredKey);
        Assert.Equal(string.Empty, vm.ApiKeyText);
        Assert.DoesNotContain("TYPED-KEY", File.ReadAllText(SettingsPath));
    }
}
