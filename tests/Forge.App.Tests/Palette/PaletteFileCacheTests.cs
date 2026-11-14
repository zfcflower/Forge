using Forge.App.Services;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Palette;

/// <summary>
/// Pins the palette's per-workspace file list: nothing is cached before the first refresh, a refresh of a plain
/// directory walks it with the build folders skipped and returns '/'-separated relative paths, a refresh of a repository
/// honours <c>.gitignore</c>, a missing directory refreshes to an empty list, and a cached list never answers for another
/// workspace.
/// </summary>
public sealed class PaletteFileCacheTests : IDisposable
{
    private readonly List<string> roots = [];
    private readonly GitCli git = new();

    public void Dispose()
    {
        foreach (var root in roots)
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public void Nothing_is_cached_before_the_first_refresh()
    {
        var cache = new PaletteFileCache();

        Assert.Empty(cache.Cached(null));
        Assert.Empty(cache.Cached(Path.Combine(Path.GetTempPath(), "forge-palette-never-refreshed")));
    }

    [Fact]
    public async Task Refresh_walks_a_plain_directory_and_skips_the_build_folders()
    {
        var root = NewRoot();
        TestTempFiles.WriteFile(root, "README.md", "x");
        TestTempFiles.WriteFile(root, "src/app/Main.cs", "x");
        TestTempFiles.WriteFile(root, "bin/Debug/tool.dll", "x");
        TestTempFiles.WriteFile(root, "obj/tool.o", "x");
        TestTempFiles.WriteFile(root, "node_modules/pkg/index.js", "x");
        var cache = new PaletteFileCache();

        var files = await cache.RefreshAsync(root, git, CancellationToken.None);

        Assert.Equal(
            ["README.md", "src/app/Main.cs"],
            files.OrderBy(static file => file, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(files, static file => file.Contains('\\'));
        Assert.Equal(files.ToArray(), cache.Cached(root).ToArray());
    }

    [Fact]
    public async Task Refresh_of_a_repository_honours_gitignore()
    {
        var root = NewRoot();
        Assert.True((await git.RunAsync(root, ["init"], CancellationToken.None)).Success);
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "ignored/\n*.log\n");
        TestTempFiles.WriteFile(root, "kept/keep.txt", "x");
        TestTempFiles.WriteFile(root, "ignored/skip.txt", "x");
        TestTempFiles.WriteFile(root, "debug.log", "x");
        TestTempFiles.WriteFile(root, "top.txt", "x");
        var cache = new PaletteFileCache();

        var files = await cache.RefreshAsync(root, git, CancellationToken.None);

        Assert.Contains("kept/keep.txt", files);
        Assert.Contains("top.txt", files);
        Assert.DoesNotContain("ignored/skip.txt", files);
        Assert.DoesNotContain("debug.log", files);
    }

    [Fact]
    public async Task Refresh_of_a_missing_directory_is_empty()
    {
        var missing = Path.Combine(Path.GetTempPath(), "forge-palette-missing-" + Guid.NewGuid().ToString("N"));
        var cache = new PaletteFileCache();

        var files = await cache.RefreshAsync(missing, git, CancellationToken.None);

        Assert.Empty(files);
        Assert.Empty(cache.Cached(missing));
    }

    [Fact]
    public async Task A_cached_list_belongs_to_the_workspace_that_was_refreshed()
    {
        var root = NewRoot();
        TestTempFiles.WriteFile(root, "only.txt", "x");
        var other = NewRoot();
        var cache = new PaletteFileCache();

        await cache.RefreshAsync(root, git, CancellationToken.None);

        Assert.Equal(["only.txt"], cache.Cached(root).ToArray());
        Assert.Empty(cache.Cached(other));
        Assert.Empty(cache.Cached(null));
    }

    private string NewRoot()
    {
        var root = TestTempFiles.NewDirectory("forge-palette-cache-");
        roots.Add(root);
        return root;
    }
}
