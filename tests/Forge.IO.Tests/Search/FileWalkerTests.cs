using Xunit;
using static Forge.IO.Tests.TestSupport;

namespace Forge.IO.Tests;

/// <summary>
/// The traversal: depth counts levels below the start directory (1 is the direct children), the version control
/// directory is never entered, the filter sees workspace-relative forward-slash paths, the walk stops when its token is
/// cancelled, and a link is reported but never walked into.
/// </summary>
public class FileWalkerTests
{
    [Fact]
    public void Depth_of_one_visits_the_direct_children_only()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("top.txt", "t");
        workspace.Write("sub/nested.txt", "n");
        workspace.Write("sub/deeper/bottom.txt", "b");

        var entries = Walk(workspace.Root, workspace.Root, 1);

        Assert.Equal(["sub", "top.txt"], entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal));
        Assert.Contains(entries, entry => entry is { RelativePath: "sub", IsDirectory: true });
        Assert.Contains(entries, entry => entry is { RelativePath: "top.txt", IsDirectory: false });
    }

    [Fact]
    public void Depth_of_two_adds_the_grandchildren()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("top.txt", "t");
        workspace.Write("sub/nested.txt", "n");
        workspace.Write("sub/deeper/bottom.txt", "b");

        var entries = Walk(workspace.Root, workspace.Root, 2);

        Assert.Equal(
            ["sub", "sub/deeper", "sub/nested.txt", "top.txt"],
            entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_depth_below_one_yields_nothing()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("top.txt", "t");

        Assert.Empty(Walk(workspace.Root, workspace.Root, 0));
        Assert.Empty(Walk(workspace.Root, workspace.Root, -3));
    }

    [Fact]
    public void A_walk_from_a_subdirectory_reports_paths_relative_to_the_root()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("sub/nested.txt", "n");
        var start = workspace.Full("sub");

        var entries = Walk(workspace.Root, start, 1);

        Assert.Equal(["sub/nested.txt"], entries.Select(entry => entry.RelativePath));
        Assert.Equal(workspace.Full("sub/nested.txt"), entries[0].FullPath);
    }

    [Fact]
    public void The_version_control_directory_is_never_entered()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write(".git/config", "[core]");
        workspace.Write(".git/objects/ab/cdef", "object");
        workspace.Write("sub/.git/HEAD", "ref");
        workspace.Write("sub/tracked.txt", "t");

        var entries = Walk(workspace.Root, workspace.Root, 64);

        Assert.Equal(["sub", "sub/tracked.txt"], entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.RelativePath.Contains(".git", StringComparison.Ordinal));
    }

    [Fact]
    public void The_filter_sees_workspace_relative_forward_slash_paths()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("sub/deeper/file.txt", "f");
        workspace.Write("sub/other.txt", "o");
        var seen = new List<(string Path, bool IsDirectory)>();

        var entries = Walk(
            workspace.Root,
            workspace.Root,
            64,
            new RecordingFilter((path, isDirectory) =>
            {
                seen.Add((path, isDirectory));
                return false;
            }));

        Assert.Equal(
            ["sub", "sub/deeper", "sub/deeper/file.txt", "sub/other.txt"],
            seen.Select(entry => entry.Path).Order(StringComparer.Ordinal));
        Assert.Contains(seen, entry => entry is { Path: "sub/deeper", IsDirectory: true });
        Assert.Contains(seen, entry => entry is { Path: "sub/other.txt", IsDirectory: false });
        Assert.DoesNotContain(seen, entry => entry.Path.Contains('\\', StringComparison.Ordinal));
        Assert.DoesNotContain(seen, entry => entry.Path.StartsWith(workspace.Root, StringComparison.Ordinal));
        Assert.Equal(4, entries.Count);
    }

    [Fact]
    public void An_ignored_directory_is_not_entered_so_its_contents_are_never_offered()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write(".gitignore", "build/\n");
        workspace.Write("build/generated.txt", "g");
        workspace.Write("src/kept.txt", "k");
        var ignore = new GitIgnore(workspace.Root);

        var entries = Walk(workspace.Root, workspace.Root, 64, ignore);

        Assert.Equal([".gitignore", "src", "src/kept.txt"], entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Cancelling_the_walk_stops_it_with_OperationCanceledException()
    {
        using var workspace = new TemporaryWorkspace();
        for (var index = 0; index < 5; index++)
        {
            workspace.Write($"file-{index}.txt", "x");
        }

        using var cancellation = new CancellationTokenSource();
        var enumerator = FileWalker.Walk(workspace.Root, workspace.Root, 64, null, cancellation.Token).GetEnumerator();
        Assert.True(enumerator.MoveNext(), "the walk yielded nothing to cancel after");
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
        {
            while (enumerator.MoveNext())
            {
            }
        });
    }

    [Fact]
    public void An_already_cancelled_token_stops_the_walk_before_the_first_entry()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("file.txt", "x");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => FileWalker.Walk(workspace.Root, workspace.Root, 64, null, cancellation.Token).ToList());
    }

    [Fact]
    public void A_directory_link_is_reported_but_not_walked_into()
    {
        using var workspace = new TemporaryWorkspace();
        var target = workspace.CreateDirectory("real");
        workspace.Write("real/inside.txt", "i");
        var link = Path.Combine(workspace.Root, "linked");
        if (!DirectoryLinks.TryCreate(link, target))
        {
            Assert.False(Directory.Exists(link), "DirectoryLinks reported failure but created a link");
            return;
        }

        var entries = Walk(workspace.Root, workspace.Root, 64);

        Assert.Contains(entries, entry => entry is { RelativePath: "linked", IsLink: true });
        Assert.Contains(entries, entry => entry is { RelativePath: "real/inside.txt", IsLink: false });
        Assert.DoesNotContain(entries, entry => entry.RelativePath.StartsWith("linked/", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_link_is_reported_with_the_link_bit_and_stays_a_file()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("plain.txt", "p");
        var link = Path.Combine(workspace.Root, "alias.txt");
        if (!DirectoryLinks.TryCreateFileLink(link, workspace.Full("plain.txt")))
        {
            return;
        }

        var entries = Walk(workspace.Root, workspace.Root, 64);

        Assert.Contains(entries, entry => entry is { RelativePath: "alias.txt", IsDirectory: false, IsLink: true });
        Assert.Contains(entries, entry => entry is { RelativePath: "plain.txt", IsDirectory: false, IsLink: false });
    }

    private static List<WalkEntry> Walk(string root, string start, int depth, IPathFilter? filter = null) =>
        [.. FileWalker.Walk(root, start, depth, filter, CancellationToken.None)];

    private sealed class RecordingFilter(Func<string, bool, bool> decide) : IPathFilter
    {
        public bool IsIgnored(string relativePath, bool isDirectory) => decide(relativePath, isDirectory);
    }
}
