using Xunit;

namespace Forge.IO.Tests;

/// <summary>
/// The <c>.gitignore</c> matrix, one row per rule shape: comments, escaped <c>#</c>, escaped trailing spaces, negation,
/// anchoring, directory-only patterns, <c>**</c>, per-directory files, parent/child override, the repository exclude file,
/// and CRLF line endings. Each row builds its own workspace with the rules the row names, so a row cannot pass because of
/// a rule another row wrote.
/// </summary>
public class GitIgnoreTests
{
    [Theory]
    // rule text / sub/.gitignore / .git/info/exclude / path / is directory / include ancestors / ignored
    [InlineData("comment lines are skipped", "# build\n", null, null, "build", true, false, false)]
    [InlineData("escaped hash names a file starting with hash", "\\#hash\n", null, null, "#hash", false, false, true)]
    [InlineData("escaped trailing space is part of the name", "foo\\ \n", null, null, "foo ", false, false, true)]
    [InlineData("the escaped space keeps the name different from the plain one", "foo\\ \n", null, null, "foo", false, false, false)]
    [InlineData("negation re-includes a file", "*.log\n!keep.log\n", null, null, "keep.log", false, false, false)]
    [InlineData("negation leaves the other files ignored", "*.log\n!keep.log\n", null, null, "other.log", false, false, true)]
    [InlineData("leading slash anchors to the root", "/anchored\n", null, null, "anchored", true, false, true)]
    [InlineData("leading slash does not match below a directory", "/anchored\n", null, null, "src/anchored", true, false, false)]
    [InlineData("trailing slash matches a directory", "cache/\n", null, null, "cache", true, false, true)]
    [InlineData("trailing slash does not match a file of the same name", "cache/\n", null, null, "cache", false, false, false)]
    [InlineData("a directory pattern takes its contents with it", "cache/\n", null, null, "cache/file.txt", false, true, true)]
    [InlineData("double star matches a name at any depth", "**/temp\n", null, null, "a/b/temp", true, false, true)]
    [InlineData("double star matches the name at the root too", "**/temp\n", null, null, "temp", true, false, true)]
    [InlineData("double star is not a substring match", "**/temp\n", null, null, "atempx", true, false, false)]
    [InlineData("a nested gitignore applies below its own directory", null, "*.txt\n", null, "sub/a.txt", false, false, true)]
    [InlineData("a nested gitignore does not apply to the root", null, "*.txt\n", null, "a.txt", false, false, false)]
    [InlineData("a child rule overrides a parent rule", "*.log\n", "!keep.log\n", null, "sub/keep.log", false, false, false)]
    [InlineData("a parent rule still ignores what the child does not negate", "*.log\n", "!keep.log\n", null, "sub/other.log", false, false, true)]
    [InlineData("the repository exclude file participates", null, null, "secret.txt\n", "secret.txt", false, false, true)]
    [InlineData("a gitignore with CRLF endings is parsed", "*.log\r\n!keep.log\r\n", null, null, "x.log", false, false, true)]
    [InlineData("a CRLF negation still wins", "*.log\r\n!keep.log\r\n", null, null, "keep.log", false, false, false)]
    public void Ignore_rules(
        string name,
        string? rootRules,
        string? nestedRules,
        string? excludeRules,
        string path,
        bool isDirectory,
        bool withAncestors,
        bool expected)
    {
        using var workspace = new TemporaryWorkspace();
        if (rootRules is not null)
        {
            workspace.Write(".gitignore", rootRules);
        }

        if (nestedRules is not null)
        {
            workspace.Write("sub/.gitignore", nestedRules);
        }

        if (excludeRules is not null)
        {
            workspace.Write(".git/info/exclude", excludeRules);
        }

        var ignore = new GitIgnore(workspace.Root);
        var actual = withAncestors
            ? ignore.IsIgnoredWithAncestors(path, isDirectory)
            : ignore.IsIgnored(path, isDirectory);

        Assert.True(actual == expected, $"{name}: '{path}' (directory={isDirectory}) was reported as {actual}");
    }

    [Fact]
    public void An_ignored_directory_hides_every_file_below_it()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write(".gitignore", "build/\n");

        var ignore = new GitIgnore(workspace.Root);

        Assert.True(ignore.IsIgnored("build", isDirectory: true));
        Assert.True(ignore.IsIgnoredWithAncestors("build/gen/out.cs", isDirectory: false));
        Assert.False(ignore.IsIgnored("build/gen/out.cs", isDirectory: false));
    }

    [Fact]
    public void Rules_from_a_gitignore_are_read_lazily_and_only_once_per_directory()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write(".gitignore", "*.log\n");
        workspace.Write("sub/.gitignore", "*.tmp\n");
        var ignore = new GitIgnore(workspace.Root);

        Assert.True(ignore.IsIgnored("sub/x.tmp", isDirectory: false));
        Assert.True(ignore.IsIgnored("sub/x.log", isDirectory: false));
        // A second query for the same directory must answer from the cached rule stack: rewrite the file and the answer
        // for the same directory must not change.
        workspace.Write("sub/.gitignore", "*.cs\n");
        Assert.True(ignore.IsIgnored("sub/y.tmp", isDirectory: false));
        Assert.False(ignore.IsIgnored("sub/y.cs", isDirectory: false));
    }

    [Fact]
    public void The_root_itself_is_never_ignored()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write(".gitignore", "*\n");

        var ignore = new GitIgnore(workspace.Root);

        Assert.False(ignore.IsIgnored(string.Empty, isDirectory: true));
    }

    [Fact]
    public void A_missing_workspace_is_a_construction_error() =>
        Assert.Throws<ArgumentException>(() => new GitIgnore(" "));
}
