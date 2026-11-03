using Forge.Scripting.Tests.Support;
using Xunit;

namespace Forge.Scripting.Tests;

public class CompilationCacheTests
{
    [Fact]
    public void Second_compile_of_the_same_request_is_served_from_the_cache()
    {
        var compiler = new ScriptCompiler();
        var request = Build.Request(PluginSources.Echo);

        var first = compiler.Compile(request);
        var second = compiler.Compile(request);

        Assert.Same(first, second);
        Assert.Equal(1, compiler.Cache.Misses);
        Assert.Equal(1, compiler.Cache.Hits);
        Assert.Equal(1, compiler.Cache.Count);
    }

    [Fact]
    public void Cache_hit_shares_the_stored_image_array()
    {
        var compiler = new ScriptCompiler();
        var request = Build.Request(PluginSources.Echo);
        var first = compiler.Compile(request);

        var cached = compiler.Compile(request);

        Assert.Same(first.AssemblyBytes, cached.AssemblyBytes);
    }

    [Fact]
    public void Different_requests_are_cached_under_different_keys()
    {
        var compiler = new ScriptCompiler();
        var first = compiler.Compile(Build.Request(PluginSources.Echo, "PluginA"));
        var second = compiler.Compile(Build.Request(PluginSources.Echo, "PluginB"));

        Assert.NotSame(first, second);
        Assert.Equal(2, compiler.Cache.Count);
        Assert.Equal(2, compiler.Cache.Misses);
        Assert.Equal(0, compiler.Cache.Hits);
    }

    [Fact]
    public void Capacity_evicts_the_least_recently_used_entry()
    {
        var compiler = new ScriptCompiler(new CompilationCache(2));
        var a = Build.Request(PluginSources.Echo, "PluginA");
        var b = Build.Request(PluginSources.Echo, "PluginB");
        var c = Build.Request(PluginSources.Echo, "PluginC");
        var resultA = compiler.Compile(a);
        compiler.Compile(b);
        Assert.Same(resultA, compiler.Compile(a));

        compiler.Compile(c);

        Assert.Equal(2, compiler.Cache.Count);
        Assert.Same(resultA, compiler.Compile(a));
        Assert.False(compiler.Cache.TryGet(b.CacheKey, out _));
        Assert.True(compiler.Cache.TryGet(c.CacheKey, out _));
    }

    [Fact]
    public void Capacity_property_reports_the_configured_bound()
    {
        Assert.Equal(7, new CompilationCache(7).Capacity);
        Assert.Equal(64, new CompilationCache().Capacity);
    }

    [Fact]
    public void Negative_capacity_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompilationCache(-1));
    }

    [Fact]
    public void Evict_removes_one_present_entry_and_reports_absence_otherwise()
    {
        var compiler = new ScriptCompiler();
        var request = Build.Request(PluginSources.Echo);
        compiler.Compile(request);

        Assert.True(compiler.Cache.Evict(request.CacheKey));
        Assert.False(compiler.Cache.Evict(request.CacheKey));
        Assert.Equal(0, compiler.Cache.Count);
        Assert.False(compiler.Cache.TryGet(request.CacheKey, out _));
    }

    [Fact]
    public void Clear_empties_the_cache_and_resets_the_counters()
    {
        var compiler = new ScriptCompiler();
        var request = Build.Request(PluginSources.Echo);
        compiler.Compile(request);
        compiler.Compile(request);

        compiler.Cache.Clear();

        Assert.Equal(0, compiler.Cache.Count);
        Assert.Equal(0, compiler.Cache.Hits);
        Assert.Equal(0, compiler.Cache.Misses);
    }

    [Fact]
    public void Zero_capacity_disables_caching()
    {
        var compiler = new ScriptCompiler(new ScriptCompilerOptions { CacheCapacity = 0 });
        var request = Build.Request(PluginSources.Echo);

        var first = compiler.Compile(request);
        var second = compiler.Compile(request);

        Assert.NotSame(first, second);
        Assert.Equal(0, compiler.Cache.Count);
        Assert.Equal(0, compiler.Cache.Hits);
        Assert.Equal(2, compiler.Cache.Misses);
        Assert.True(first.Success);
        Assert.Equal(first.AssemblyBytes, second.AssemblyBytes);
    }

    [Fact]
    public void Failed_compilation_is_cached()
    {
        var compiler = new ScriptCompiler();
        var request = Build.Request(PluginSources.Broken);

        var first = compiler.Compile(request);
        var second = compiler.Compile(request);

        Assert.False(first.Success);
        Assert.Same(first, second);
        Assert.Equal(1, compiler.Cache.Hits);
    }

    [Fact]
    public async Task CompileAsync_concurrent_calls_share_one_result()
    {
        var compiler = new ScriptCompiler();
        var request = Build.Request(PluginSources.Echo);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => compiler.CompileAsync(request)));

        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.True(results[0].Success);
        Assert.Equal(1, compiler.Cache.Count);
    }

    [Fact]
    public async Task CompileAsync_result_is_visible_to_synchronous_compile()
    {
        var compiler = new ScriptCompiler();
        var request = Build.Request(PluginSources.Echo);

        var viaAsync = await compiler.CompileAsync(request);
        var viaSync = compiler.Compile(request);

        Assert.Same(viaAsync, viaSync);
        Assert.Equal(1, compiler.Cache.Hits);
    }

    [Fact]
    public async Task WarmUpAsync_compiles_uncached_requests_and_keeps_existing_entries()
    {
        var compiler = new ScriptCompiler();
        var cached = Build.Request(PluginSources.Echo, "PluginA");
        var existing = compiler.Compile(cached);
        var requests = new[]
        {
            cached,
            Build.Request(PluginSources.Echo, "PluginB"),
            Build.Request(PluginSources.Broken, "PluginC"),
        };

        await compiler.Cache.WarmUpAsync(compiler, requests, maxParallelism: 2);

        Assert.Equal(3, compiler.Cache.Count);
        Assert.True(compiler.Cache.TryGet(cached.CacheKey, out var kept));
        Assert.Same(existing, kept);
        Assert.True(compiler.Cache.TryGet(requests[1].CacheKey, out var warmed));
        Assert.True(warmed.Success);
        Assert.True(compiler.Cache.TryGet(requests[2].CacheKey, out var failed));
        Assert.False(failed.Success);
    }

    [Fact]
    public async Task WarmUpAsync_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var compiler = new ScriptCompiler();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            compiler.Cache.WarmUpAsync(compiler, [Build.Request(PluginSources.Echo)], ct: cts.Token));
        Assert.Equal(0, compiler.Cache.Count);
    }

    [Fact]
    public void Blank_key_is_rejected()
    {
        var cache = new CompilationCache();

        Assert.Throws<ArgumentException>(() => cache.TryGet(" ", out _));
        Assert.Throws<ArgumentException>(() => cache.Set(string.Empty, new CompilationResult()));
        Assert.Throws<ArgumentException>(() => cache.Evict(" "));
    }
}
