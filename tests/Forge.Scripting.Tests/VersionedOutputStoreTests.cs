using Forge.Scripting.Tests.Support;
using Xunit;

namespace Forge.Scripting.Tests;

public class VersionedOutputStoreTests : IDisposable
{
    private readonly TempDirectory root = new();

    public void Dispose() => root.Dispose();

    [Fact]
    public void Save_writes_plugin_dll_under_plugin_and_version_directories()
    {
        var store = new VersionedOutputStore(root.Root);
        byte[] bytes = [1, 2, 3, 4];

        var path = store.Save("acme.tools", "1.2.0", bytes);

        var expected = Path.GetFullPath(Path.Combine(root.Root, "acme.tools", "1.2.0", "plugin.dll"));
        Assert.Equal(expected, path);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.True(store.Exists("acme.tools", "1.2.0"));
        Assert.Equal(path, store.GetOutputPath("acme.tools", "1.2.0"));
    }

    [Fact]
    public void Save_leaves_only_the_output_file_in_the_version_directory()
    {
        var store = new VersionedOutputStore(root.Root);

        var path = store.Save("acme.tools", "1.0.0", [9]);

        Assert.Equal(["plugin.dll"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(file => Path.GetFileName(file)));
    }

    [Fact]
    public void Save_is_idempotent_and_never_overwrites_an_existing_version()
    {
        var store = new VersionedOutputStore(root.Root);
        byte[] original = [1, 1, 1];
        byte[] replacement = [2, 2];

        var first = store.Save("acme.tools", "1.0.0", original, out var firstExisting);
        var second = store.Save("acme.tools", "1.0.0", replacement, out var secondExisting);

        Assert.False(firstExisting);
        Assert.True(secondExisting);
        Assert.Equal(first, second);
        Assert.Equal(original, File.ReadAllBytes(first));
    }

    [Fact]
    public async Task SaveAsync_returns_the_same_path_and_keeps_the_first_bytes()
    {
        var store = new VersionedOutputStore(root.Root);

        var first = await store.SaveAsync("acme.tools", "1.0.0", new byte[] { 5, 6 });
        var second = await store.SaveAsync("acme.tools", "1.0.0", new byte[] { 7 });

        Assert.Equal(first, second);
        Assert.Equal(new byte[] { 5, 6 }, File.ReadAllBytes(first));
    }

    [Fact]
    public async Task Concurrent_saves_of_one_version_leave_a_single_intact_file()
    {
        var store = new VersionedOutputStore(root.Root);
        var writers = Enumerable.Range(0, 8)
            .Select(index => Task.Run(() => store.Save("acme.tools", "1.0.0", new byte[] { (byte)index, (byte)index })))
            .ToArray();

        var paths = await Task.WhenAll(writers);

        Assert.All(paths, path => Assert.Equal(paths[0], path));
        var stored = File.ReadAllBytes(paths[0]);
        Assert.Equal(2, stored.Length);
        Assert.Equal(stored[0], stored[1]);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(paths[0])!));
    }

    [Fact]
    public void Explicit_version_is_used_verbatim()
    {
        var store = new VersionedOutputStore(root.Root);

        var path = store.Save("acme.tools", "2.0.0-beta.1", [1]);

        Assert.Equal("2.0.0-beta.1", Path.GetFileName(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void Custom_file_name_replaces_plugin_dll()
    {
        var store = new VersionedOutputStore(root.Root);

        var path = store.Save("acme.tools", "1.0.0", [1], fileName: "acme.dll");

        Assert.Equal("acme.dll", Path.GetFileName(path));
        Assert.False(store.Exists("acme.tools", "1.0.0"));
        Assert.True(store.Exists("acme.tools", "1.0.0", "acme.dll"));
    }

    [Fact]
    public void Save_of_a_compilation_defaults_the_version_to_the_cache_key_prefix()
    {
        var store = new VersionedOutputStore(root.Root);
        var result = new ScriptCompiler().CompileDirect(Build.Request(PluginSources.Echo));

        var path = store.Save("acme.tools", result);

        Assert.Equal(VersionedOutputStore.VersionFor(result.CacheKey), Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.Equal(result.CacheKey[..16], Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.Equal(result.AssemblyBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task SaveAsync_of_a_compilation_stores_the_image()
    {
        var store = new VersionedOutputStore(root.Root);
        var result = new ScriptCompiler().CompileDirect(Build.Request(PluginSources.Echo));

        var path = await store.SaveAsync("acme.tools", result, version: "9.9.9");

        Assert.Equal("9.9.9", Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.Equal(result.AssemblyBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Failed_compilation_cannot_be_stored()
    {
        var store = new VersionedOutputStore(root.Root);
        var failed = new ScriptCompiler().CompileDirect(Build.Request(PluginSources.Broken));

        Assert.Throws<InvalidOperationException>(() => store.Save("acme.tools", failed));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync("acme.tools", failed));
        Assert.False(store.Exists("acme.tools", VersionedOutputStore.VersionFor(failed.CacheKey)));
    }

    [Fact]
    public void VersionFor_takes_the_hash_prefix_and_keeps_short_hashes_whole()
    {
        const string hash = "0123456789abcdef0123456789abcdef";

        Assert.Equal("0123456789abcdef", VersionedOutputStore.VersionFor(hash));
        Assert.Equal("0123", VersionedOutputStore.VersionFor(hash, prefixLength: 4));
        Assert.Equal("abc", VersionedOutputStore.VersionFor("abc"));
        Assert.Throws<ArgumentOutOfRangeException>(() => VersionedOutputStore.VersionFor(hash, prefixLength: 0));
    }

    [Fact]
    public void ListVersions_returns_ordinal_sorted_version_directories()
    {
        var store = new VersionedOutputStore(root.Root);
        store.Save("acme.tools", "2.0.0", [1]);
        store.Save("acme.tools", "10.0.0", [1]);
        store.Save("acme.tools", "1.0.0", [1]);

        Assert.Equal(["1.0.0", "10.0.0", "2.0.0"], store.ListVersions("acme.tools"));
    }

    [Fact]
    public void ListVersions_is_empty_for_an_unknown_plugin()
    {
        var store = new VersionedOutputStore(root.Root);

        Assert.Empty(store.ListVersions("nobody"));
    }

    [Fact]
    public void TryRead_returns_stored_bytes_and_false_for_missing_versions()
    {
        var store = new VersionedOutputStore(root.Root);
        store.Save("acme.tools", "1.0.0", [4, 2]);

        Assert.True(store.TryRead("acme.tools", "1.0.0", out var bytes));
        Assert.Equal(new byte[] { 4, 2 }, bytes);
        Assert.False(store.TryRead("acme.tools", "9.9.9", out var missing));
        Assert.Empty(missing);
    }

    [Fact]
    public void GetSize_sums_every_stored_version_of_one_plugin()
    {
        var store = new VersionedOutputStore(root.Root);
        store.Save("acme.tools", "1.0.0", [1, 2, 3]);
        store.Save("acme.tools", "2.0.0", [4, 5]);
        store.Save("other.plugin", "1.0.0", new byte[100]);

        Assert.Equal(5, store.GetSize("acme.tools"));
        Assert.Equal(0, store.GetSize("nobody"));
    }

    [Fact]
    public void Missing_store_directory_reports_nothing_stored()
    {
        var store = new VersionedOutputStore(Path.Combine(root.Root, "not-created"));

        Assert.Empty(store.ListVersions("acme.tools"));
        Assert.Equal(0, store.GetSize("acme.tools"));
        Assert.False(store.Exists("acme.tools", "1.0.0"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("nested/../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(" padded")]
    public void Path_segments_that_escape_or_nest_are_rejected(string value)
    {
        var store = new VersionedOutputStore(root.Root);

        Assert.ThrowsAny<ArgumentException>(() => store.GetOutputPath(value, "1.0.0"));
        Assert.ThrowsAny<ArgumentException>(() => store.GetOutputPath("acme.tools", value));
        Assert.ThrowsAny<ArgumentException>(() => store.GetOutputPath("acme.tools", "1.0.0", value));
        Assert.ThrowsAny<ArgumentException>(() => store.Save(value, "1.0.0", [1]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_path_segments_are_rejected(string value)
    {
        var store = new VersionedOutputStore(root.Root);

        Assert.ThrowsAny<ArgumentException>(() => store.GetVersionDirectory(value, "1.0.0"));
        Assert.ThrowsAny<ArgumentException>(() => store.GetVersionDirectory("acme.tools", value));
    }

    [Fact]
    public void Base_directory_is_normalized_to_a_full_path()
    {
        var store = new VersionedOutputStore(Path.Combine(root.Root, "sub", "..", "store"));

        Assert.Equal(Path.GetFullPath(Path.Combine(root.Root, "store")), store.BaseDirectory);
    }

    [Fact]
    public void Blank_base_directory_is_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => new VersionedOutputStore(" "));
    }
}
