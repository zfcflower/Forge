#if DEBUG
using Forge.Approval;
using Forge.ExtensionHost;
using Forge.HostClient;
using Forge.Scripting;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>
/// Active invalidation of the plugin compilation cache (F-SCRIPT-45): a dependency change - a refresh that replaces or
/// removes a plugin, a revoke, a disable, an uninstall or a commit that swaps the live bytes - evicts the stale
/// in-memory compilation result instead of leaving it to the LRU. Eviction is memory hygiene only, so every test also
/// pins what must <em>not</em> happen: an unchanged refresh keeps the warm entry, and the versioned output on disk is
/// what a returning plugin reloads from. The host runs in process (the pattern of <c>HostShutdownFenceTests</c>)
/// because the cache has no cross-process observation point; the internal view of it is a Debug-only
/// <c>InternalsVisibleTo</c> seam, so this whole file compiles only in Debug, like the internals-dependent cases in
/// <c>Forge.Scripting.Tests</c>.
/// </summary>
public sealed class CompilationCacheEvictionTests
{
    [Fact]
    public async Task Refresh_after_a_source_change_evicts_the_previous_compilation_and_keeps_the_new_one()
    {
        await using var fx = await EvictionHarness.StartAsync("changed");
        var ct = fx.Ct;
        var first = PluginSource("first");
        var second = PluginSource("second");
        fx.WritePlugin("alpha", first);
        await fx.Client.RefreshPluginsAsync(ct);

        var firstKey = ExpectedCacheKey("alpha", first);
        Assert.Equal(1, fx.Cache.Count);
        Assert.True(fx.Cache.TryGet(firstKey, out _));

        fx.WritePlugin("alpha", second);
        await fx.Client.RefreshPluginsAsync(ct);

        var secondKey = ExpectedCacheKey("alpha", second);
        Assert.NotEqual(firstKey, secondKey);
        Assert.Equal(1, fx.Cache.Count);
        Assert.False(fx.Cache.TryGet(firstKey, out _));
        Assert.True(fx.Cache.TryGet(secondKey, out _));

        // A refresh that changes nothing must not drop the warm entry: the replacement compiled to the same key, so
        // there is nothing stale to release.
        await fx.Client.RefreshPluginsAsync(ct);
        Assert.Equal(1, fx.Cache.Count);
        Assert.True(fx.Cache.TryGet(secondKey, out _));
    }

    [Fact]
    public async Task Refresh_that_removes_a_plugin_evicts_its_compilation()
    {
        await using var fx = await EvictionHarness.StartAsync("removed");
        var ct = fx.Ct;
        var source = PluginSource("only");
        fx.WritePlugin("alpha", source);
        await fx.Client.RefreshPluginsAsync(ct);
        Assert.Equal(1, fx.Cache.Count);

        Directory.Delete(fx.PluginDirectory("alpha"), recursive: true);
        await fx.Client.RefreshPluginsAsync(ct);

        Assert.Equal(0, fx.Cache.Count);
        Assert.False(fx.Cache.TryGet(ExpectedCacheKey("alpha", source), out _));
    }

    [Fact]
    public async Task Revoke_evicts_the_revoked_plugins_compilation()
    {
        await using var fx = await EvictionHarness.StartAsync("revoke");
        var ct = fx.Ct;
        var source = PluginSource("only");
        fx.WritePlugin("alpha", source);
        await fx.Client.RefreshPluginsAsync(ct);
        Assert.Equal(1, fx.Cache.Count);

        await fx.Client.RevokePluginAsync("alpha", "1.0.0", ct);

        Assert.Equal(0, fx.Cache.Count);
        Assert.False(fx.Cache.TryGet(ExpectedCacheKey("alpha", source), out _));
    }

    [Fact]
    public async Task Disable_evicts_the_disabled_plugins_compilation()
    {
        await using var fx = await EvictionHarness.StartAsync("disable");
        var ct = fx.Ct;
        var source = PluginSource("only");
        fx.WritePlugin("alpha", source);
        await fx.Client.RefreshPluginsAsync(ct);
        Assert.Equal(1, fx.Cache.Count);

        await fx.Client.DisablePluginAsync("alpha", "1.0.0", ct);

        Assert.Equal(0, fx.Cache.Count);
        Assert.False(fx.Cache.TryGet(ExpectedCacheKey("alpha", source), out _));
    }

    [Fact]
    public async Task Delete_evicts_the_uninstalled_plugins_compilation()
    {
        await using var fx = await EvictionHarness.StartAsync("delete");
        var ct = fx.Ct;
        var source = PluginSource("only");
        fx.WritePlugin("alpha", source);
        await fx.Client.RefreshPluginsAsync(ct);
        Assert.Equal(1, fx.Cache.Count);

        await fx.Client.DeletePluginAsync("alpha", "1.0.0", ct);

        Assert.Equal(0, fx.Cache.Count);
        Assert.False(fx.Cache.TryGet(ExpectedCacheKey("alpha", source), out _));
        Assert.False(Directory.Exists(fx.PluginDirectory("alpha")));
    }

    [Fact]
    public async Task Commit_evicts_the_replaced_live_compilation_and_keeps_the_committed_one()
    {
        await using var fx = await EvictionHarness.StartAsync("commit");
        var ct = fx.Ct;
        var live = PluginSource("live");
        var committed = PluginSource("committed");
        fx.WritePlugin("alpha", live);
        await fx.Client.RefreshPluginsAsync(ct);
        var liveKey = ExpectedCacheKey("alpha", live);
        Assert.Equal(1, fx.Cache.Count);

        var staged = await fx.Client.StageExtensionAsync(
            new Sdk.ExtensionStageRequest(
                "alpha",
                "1.0.0",
                Sdk.ExtensionKinds.Plugin,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["Plugin.cs"] = committed },
                [],
                []),
            ct);
        Assert.Empty(staged.Diagnostics);

        // Staging runs the same prepare path, so the draft's compilation is warm before the commit swaps it in.
        var committedKey = ExpectedCacheKey("alpha", committed);
        Assert.True(fx.Cache.TryGet(committedKey, out _));

        await ExtensionAuthoringSupport.CommitAsync(fx.Client, ct, staged);

        Assert.Equal(1, fx.Cache.Count);
        Assert.False(fx.Cache.TryGet(liveKey, out _));
        Assert.True(fx.Cache.TryGet(committedKey, out _));
    }

    /// <summary>A plugin that compiles fast and never runs in these tests; <paramref name="marker"/> is the only
    /// difference between two contents of the same id and version, which is what makes the cache keys differ.</summary>
    private static string PluginSource(string marker) =>
        $$"""
        public sealed class TestPlugin : IPlugin
        {
            public PluginInfo Info => new("alpha", "1.0.0", "{{marker}}");

            public void Register(IRegistrationContext context)
            {
            }
        }
        """;

    /// <summary>The key the catalog must have compiled <paramref name="source"/> under: the same
    /// <see cref="CompilationRequest"/> <c>PluginCatalog.CompileAsync</c> builds for a source plugin whose file is
    /// <c>Plugin.cs</c>. Derived independently here, so the assertions compare against the cache-key contract instead
    /// of reading the implementation's own bookkeeping.</summary>
    private static string ExpectedCacheKey(string id, string source) =>
        new CompilationRequest
        {
            Source = source,
            AssemblyName = PluginIds.AssemblyName(id),
            ExpectedSourceHash = Hashing.SourceHash(source),
            ContractVersion = ScriptCompiler.ContractVersion,
            FilePath = "Plugin.cs",
        }.CacheKey;

    /// <summary>One in-process host on a private pipe over temp plugins/data directories, connected but never
    /// launched as a child: the tests drive it through the public client and observe the catalog cache through the
    /// Debug-only internal seam. Every assertion runs right after the awaited operation returned, because eviction is
    /// synchronous inside the request handler - no polling and no timers anywhere.</summary>
    private sealed class EvictionHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource budget = new(TestBudgets.Fixture);
        private readonly Forge.ExtensionHost.ExtensionHost host;
        private readonly Task<int> runTask;

        private EvictionHarness(
            string root,
            Forge.ExtensionHost.ExtensionHost host,
            ExtensionHostClient client,
            Task<int> runTask)
        {
            Root = root;
            PluginsDirectory = Path.Combine(root, "plugins");
            this.host = host;
            Client = client;
            this.runTask = runTask;
        }

        public string Root { get; }

        public string PluginsDirectory { get; }

        public ExtensionHostClient Client { get; }

        public CancellationToken Ct => budget.Token;

        /// <summary>The catalog's plugin compilation cache - the thing under test.</summary>
        public CompilationCache Cache => host.Catalog.Cache;

        public static async Task<EvictionHarness> StartAsync(string name)
        {
            var root = Path.Combine(Path.GetTempPath(), "forge-evict-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
            var plugins = Path.Combine(root, "plugins");
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(plugins);
            Directory.CreateDirectory(data);
            var pipe = "forge-evict-" + Guid.NewGuid().ToString("N")[..16];

            var host = new Forge.ExtensionHost.ExtensionHost(new HostOptions
            {
                PipeName = pipe,
                PluginsDirectory = plugins,
                DataDirectory = data,
                RuntimePath = "dotnet",
                ConnectTimeout = TimeSpan.FromSeconds(15),
            });
            var runTask = host.RunAsync();
            var client = new ExtensionHostClient(
                new HostConnection
                {
                    PipeName = pipe,
                    HostExecutablePath = null,
                    StartTimeout = TestBudgets.HostStart,
                },
                null,
                null,
                new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromSeconds(60) });

            var harness = new EvictionHarness(root, host, client, runTask);
            try
            {
                await client.StartAsync(harness.Ct);
            }
            catch
            {
                await harness.DisposeAsync();
                throw;
            }

            return harness;
        }

        public string PluginDirectory(string id) => Path.Combine(PluginsDirectory, id);

        /// <summary>Writes <c>&lt;plugins&gt;/&lt;id&gt;/manifest.json</c> and its <c>Plugin.cs</c>, the same package
        /// shape <c>E2eFixture.WritePlugin</c> lays out for the out-of-process suites.</summary>
        public void WritePlugin(string id, string source, string version = "1.0.0")
        {
            var directory = PluginDirectory(id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "manifest.json"),
                System.Text.Json.JsonSerializer.Serialize(new { id, version, source = "Plugin.cs" }));
            File.WriteAllText(Path.Combine(directory, "Plugin.cs"), source);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Client.DisposeAsync();
                await host.DisposeAsync();
                await runTask.WaitAsync(budget.Token);
            }
            finally
            {
                TempRoots.Delete(Root);
                budget.Dispose();
            }
        }
    }
}
#endif
