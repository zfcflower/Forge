using Xunit;
using static Forge.IO.Tests.TestSupport;

namespace Forge.IO.Tests;

/// <summary>
/// The workspace path contract: one spelling of a path, a containment answer that follows the links of the components
/// that exist, and a resolution that keeps the tail of a path that does not exist yet.
/// </summary>
public class WorkspacePathTests
{
    [Fact]
    public void Resolve_accepts_a_relative_path_inside_the_root_and_returns_the_file()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.Write("src/app.cs", "class App {}");

        var resolved = WorkspacePath.Resolve(workspace.Root, "src/app.cs");

        Assert.Equal(WorkspacePath.ResolveRealPath(file), resolved);
    }

    [Fact]
    public void Resolve_accepts_an_absolute_path_inside_the_root()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.Write("src/app.cs", "class App {}");

        var resolved = WorkspacePath.Resolve(workspace.Root, file);

        Assert.Equal(WorkspacePath.ResolveRealPath(file), resolved);
    }

    [Fact]
    public void Resolve_rejects_a_relative_path_that_walks_out_of_the_root()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("src/app.cs", "class App {}");

        var exception = Assert.Throws<WorkspacePathException>(() => WorkspacePath.Resolve(workspace.Root, "../outside.txt"));

        Assert.Contains("../outside.txt", exception.Message, StringComparison.Ordinal);
        Assert.Contains("outside the workspace", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_rejects_an_absolute_path_outside_the_root()
    {
        using var workspace = new TemporaryWorkspace();
        using var outside = new TestDirectory();
        var foreign = Path.Combine(outside.Path, "secret.txt");
        File.WriteAllText(foreign, "secret");

        var exception = Assert.Throws<WorkspacePathException>(() => WorkspacePath.Resolve(workspace.Root, foreign));

        Assert.Contains("outside the workspace", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_accepts_the_root_itself()
    {
        using var workspace = new TemporaryWorkspace();

        var resolved = WorkspacePath.Resolve(workspace.Root, ".");

        Assert.Equal(WorkspacePath.ResolveRealPath(workspace.Root), resolved);
    }

    [Fact]
    public void Resolve_rejects_a_link_that_points_outside_the_root()
    {
        // A junction or a symbolic link inside the workspace is a reparse point like any other, so resolution has to
        // follow it before it answers. Neither can be created by an unprivileged Windows account without Developer Mode,
        // so the test says so and stops rather than passing an assertion it never made.
        using var workspace = new TemporaryWorkspace();
        using var outside = new TestDirectory();
        File.WriteAllText(Path.Combine(outside.Path, "secret.txt"), "secret");
        var link = Path.Combine(workspace.Root, "escape");
        if (!DirectoryLinks.TryCreate(link, outside.Path))
        {
            Assert.False(Directory.Exists(link), "DirectoryLinks reported failure but created a link");
            return;
        }

        var fileException = Assert.Throws<WorkspacePathException>(
            () => WorkspacePath.Resolve(workspace.Root, "escape/secret.txt"));
        var directoryException = Assert.Throws<WorkspacePathException>(
            () => WorkspacePath.Resolve(workspace.Root, "escape"));

        Assert.Contains("outside the workspace", fileException.Message, StringComparison.Ordinal);
        Assert.Contains("outside the workspace", directoryException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_accepts_a_link_that_points_inside_the_root()
    {
        using var workspace = new TemporaryWorkspace();
        var target = workspace.CreateDirectory("target");
        var link = Path.Combine(workspace.Root, "alias");
        if (!DirectoryLinks.TryCreate(link, target))
        {
            Assert.False(Directory.Exists(link), "DirectoryLinks reported failure but created a link");
            return;
        }

        var resolved = WorkspacePath.Resolve(workspace.Root, "alias/file.txt");

        Assert.Equal(WorkspacePath.ResolveRealPath(Path.Combine(target, "file.txt")), resolved);
    }

    [Fact]
    public void ResolveRealPath_keeps_the_whole_path_when_no_component_exists()
    {
        // The regression this pins: resolution used to stop at the deepest existing ancestor, so a path whose parents do
        // not exist yet resolved to a directory instead of to the file the caller asked about.
        using var workspace = new TemporaryWorkspace();
        var missing = Path.Combine(workspace.Root, "a", "b", "c.txt");

        var real = WorkspacePath.ResolveRealPath(missing);

        Assert.Equal(ExpectedBelowRoot(workspace.Root, "a", "b", "c.txt"), real);
    }

    [Fact]
    public void ResolveRealPath_keeps_the_tail_below_a_partly_existing_path()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.CreateDirectory("a");

        var real = WorkspacePath.ResolveRealPath(Path.Combine(workspace.Root, "a", "b", "c.txt"));

        Assert.Equal(ExpectedBelowRoot(workspace.Root, "a", "b", "c.txt"), real);
    }

    [Fact]
    public void Resolve_returns_the_requested_file_name_for_a_path_whose_directories_do_not_exist()
    {
        using var workspace = new TemporaryWorkspace();

        var resolved = WorkspacePath.Resolve(workspace.Root, "a/b/c.txt");

        Assert.EndsWith(Path.Combine("a", "b", "c.txt"), resolved, StringComparison.Ordinal);
        Assert.Equal(ExpectedBelowRoot(workspace.Root, "a", "b", "c.txt"), resolved);
    }

    /// <summary>
    /// The full path of a not-yet-existing entry below <paramref name="root"/>. The root itself is resolved first because
    /// the temporary directory is reached through links on some platforms (macOS <c>/var</c> is <c>/private/var</c>), and a
    /// real-path answer that kept the raw spelling of the root would fail for a reason the test is not about.
    /// </summary>
    private static string ExpectedBelowRoot(string root, params string[] segments) =>
        Path.Combine([WorkspacePath.ResolveRealPath(root), .. segments]);

    [Fact]
    public void ResolveRealPath_is_idempotent()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.Write("one/two.txt", "x");

        Assert.Equal(WorkspacePath.ResolveRealPath(file), WorkspacePath.ResolveRealPath(WorkspacePath.ResolveRealPath(file)));
    }

    [Fact]
    public void RelativeTo_returns_forward_slashes_and_empty_for_the_root()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.Write("sub/dir/file.txt", "x");

        Assert.Equal("sub/dir/file.txt", WorkspacePath.RelativeTo(workspace.Root, file));
        Assert.Equal(string.Empty, WorkspacePath.RelativeTo(workspace.Root, workspace.Root));
        Assert.DoesNotContain("\\", WorkspacePath.RelativeTo(workspace.Root, file), StringComparison.Ordinal);
    }

    [Fact]
    public void RelativeTo_tolerates_a_trailing_separator()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.Write("sub/file.txt", "x");

        var withSeparator = file + Path.DirectorySeparatorChar;

        Assert.Equal("sub/file.txt", WorkspacePath.RelativeTo(workspace.Root, withSeparator));
    }

    [Fact]
    public void RelativeTo_rejects_a_path_outside_the_root()
    {
        using var workspace = new TemporaryWorkspace();
        using var outside = new TestDirectory();

        var exception = Assert.Throws<WorkspacePathException>(
            () => WorkspacePath.RelativeTo(workspace.Root, Path.Combine(outside.Path, "x.txt")));

        Assert.Contains("outside the workspace", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("./a/b", "a/b")]
    [InlineData(".\\a\\b", "a/b")]
    [InlineData("././a", "a")]
    [InlineData("a\\b\\c.txt", "a/b/c.txt")]
    [InlineData("a/b/c.txt", "a/b/c.txt")]
    [InlineData("", "")]
    [InlineData("./", "")]
    internal void NormalizeRelative_strips_leading_dot_segments_and_backslashes(string input, string expected) =>
        Assert.Equal(expected, WorkspacePath.NormalizeRelative(input));

    [Fact]
    public void Normalize_removes_the_trailing_separator_and_is_absolute()
    {
        using var workspace = new TemporaryWorkspace();

        var normalized = WorkspacePath.Normalize(workspace.Root + Path.DirectorySeparatorChar);

        Assert.Equal(workspace.Root, normalized);
        Assert.True(Path.IsPathFullyQualified(normalized), $"'{normalized}' is not absolute");
    }

    [Fact]
    public void Normalize_rejects_a_blank_path() =>
        Assert.Throws<ArgumentException>(() => WorkspacePath.Normalize(" "));

    [Fact]
    public void IsUnder_accepts_the_root_and_its_children_but_not_a_sibling_with_the_same_prefix()
    {
        using var workspace = new TemporaryWorkspace();
        var child = workspace.Write("sub/file.txt", "x");

        Assert.True(WorkspacePath.IsUnder(workspace.Root, workspace.Root));
        Assert.True(WorkspacePath.IsUnder(child, workspace.Root));
        Assert.True(WorkspacePath.IsUnder(workspace.Root + Path.DirectorySeparatorChar, workspace.Root));
        Assert.False(WorkspacePath.IsUnder(workspace.Root + "-sibling", workspace.Root));
    }

    [Fact]
    public void IsUnder_folds_case_only_where_the_platform_does()
    {
        using var workspace = new TemporaryWorkspace();
        var expected = WorkspacePath.Comparison == StringComparison.OrdinalIgnoreCase;

        Assert.Equal(expected, WorkspacePath.IsUnder(workspace.Root.ToUpperInvariant(), workspace.Root.ToLowerInvariant()));
    }

    [Fact]
    public void SamePath_ignores_spelling_but_not_a_different_directory()
    {
        using var workspace = new TemporaryWorkspace();

        Assert.True(WorkspacePath.SamePath(workspace.Root, workspace.Root + Path.DirectorySeparatorChar));
        Assert.True(WorkspacePath.SamePath(workspace.Root, Path.Combine(workspace.Root, "sub", "..")));
        Assert.False(WorkspacePath.SamePath(workspace.Root, workspace.Root + "-sibling"));
    }

    [Fact]
    public void Comparison_and_Comparer_agree_with_the_platform()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(StringComparison.OrdinalIgnoreCase, WorkspacePath.Comparison);
            Assert.Equal(StringComparer.OrdinalIgnoreCase, WorkspacePath.Comparer);
        }
        else
        {
            Assert.Equal(StringComparison.Ordinal, WorkspacePath.Comparison);
            Assert.Equal(StringComparer.Ordinal, WorkspacePath.Comparer);
        }
    }
}
