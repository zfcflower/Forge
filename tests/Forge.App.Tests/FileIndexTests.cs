using System.Diagnostics;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Pins <see cref="Forge.App.Services.FileIndex"/> behavior: a large flat directory indexes fast with no duplicate
/// siblings, nested paths build the right parent/child relations, and the non-git walk skips the tool
/// folders while a git listing honours <c>.gitignore</c>.
/// </summary>
public sealed class FileIndexTests : IDisposable
{
    private readonly List<string> roots = [];
    private readonly GitCli git = new();

    public void Dispose()
    {
        foreach (var root in roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task Large_flat_directory_indexes_quickly_with_no_duplicate_siblings()
    {
        var root = NewRoot();
        const int files = 5000;
        for (var i = 0; i < files; i++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, $"file-{i:00000}.txt"),
                "x");
        }

        var stopwatch = Stopwatch.StartNew();
        var index = await Forge.App.Services.FileIndex.BuildAsync(root, git, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(files, index.FileCount);
        var entries = index.List(string.Empty);
        Assert.Equal(files, entries.Count);
        Assert.Equal(files, entries.Select(static entry => entry.RelativePath).Distinct(StringComparer.Ordinal).Count());
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Indexing {files} files took {stopwatch.Elapsed}, expected under 5 s.");
    }

    [Fact]
    public async Task Nested_paths_build_parent_child_relations()
    {
        var root = NewRoot();
        Write(root, "a/b/c.txt");
        Write(root, "a/d.txt");
        Write(root, "e.txt");

        var index = await Forge.App.Services.FileIndex.BuildAsync(root, git, CancellationToken.None);

        Assert.Equal(3, index.FileCount);
        var roots = index.List(string.Empty);
        Assert.Equal(["a", "e.txt"], roots.Select(static entry => entry.Name).OrderBy(static name => name, StringComparer.Ordinal).ToArray());
        Assert.True(roots.Single(static entry => entry.Name == "a").IsDirectory);
        Assert.False(roots.Single(static entry => entry.Name == "e.txt").IsDirectory);

        var underA = index.List("a");
        Assert.Equal(["b", "d.txt"], underA.Select(static entry => entry.Name).OrderBy(static name => name, StringComparer.Ordinal).ToArray());
        Assert.True(underA.Single(static entry => entry.Name == "b").IsDirectory);

        var underB = index.List("a/b");
        var leaf = Assert.Single(underB);
        Assert.Equal("c.txt", leaf.Name);
        Assert.Equal("a/b/c.txt", leaf.RelativePath);
        Assert.False(leaf.IsDirectory);
    }

    [Fact]
    public async Task Walk_skips_tool_folders_but_keeps_normal_ones()
    {
        var root = NewRoot();
        foreach (var skipped in new[] { "bin", "obj", ".git", ".vs", "node_modules", ".idea", ".forge" })
        {
            Write(root, skipped + "/tool.dat");
        }

        Write(root, "src/ok.txt");
        Write(root, "README.md");

        var index = await Forge.App.Services.FileIndex.BuildAsync(root, git, CancellationToken.None);

        Assert.Equal(2, index.FileCount);
        var roots = index.List(string.Empty).Select(static entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("bin", roots);
        Assert.DoesNotContain("obj", roots);
        Assert.DoesNotContain(".git", roots);
        Assert.DoesNotContain(".vs", roots);
        Assert.DoesNotContain("node_modules", roots);
        Assert.DoesNotContain(".idea", roots);
        Assert.DoesNotContain(".forge", roots);
        Assert.Contains("src", roots);
        Assert.Contains("README.md", roots);
        Assert.Equal("src/ok.txt", Assert.Single(index.List("src")).RelativePath);
    }

    [Fact]
    public async Task Git_repo_listing_honours_gitignore()
    {
        var root = NewRoot();
        Assert.True((await git.RunAsync(root, ["init"], CancellationToken.None)).Success);
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "ignored/\n*.log\n");
        Write(root, "kept/keep.txt");
        Write(root, "ignored/skip.txt");
        Write(root, "debug.log");
        Write(root, "top.txt");

        var index = await Forge.App.Services.FileIndex.BuildAsync(root, git, CancellationToken.None);

        var all = CollectAll(index);
        Assert.Contains("kept/keep.txt", all);
        Assert.Contains("top.txt", all);
        Assert.DoesNotContain("ignored/skip.txt", all);
        Assert.DoesNotContain("debug.log", all);
    }

    private static HashSet<string> CollectAll(Forge.App.Services.FileIndex index)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(string.Empty);
        while (pending.Count > 0)
        {
            foreach (var entry in index.List(pending.Pop()))
            {
                if (entry.IsDirectory)
                {
                    pending.Push(entry.RelativePath);
                }
                else
                {
                    found.Add(entry.RelativePath);
                }
            }
        }

        return found;
    }

    private string NewRoot()
    {
        var root = TestTempFiles.NewDirectory("forge-fileindex-");
        roots.Add(root);
        return root;
    }

    private static void Write(string root, string relativePath) => TestTempFiles.WriteFile(root, relativePath, "x");
}
