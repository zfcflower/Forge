using Xunit;

namespace Forge.IO.Tests;

/// <summary>
/// The direct/script decision: a command line becomes an argument vector only when that vector means exactly what a
/// shell would do with the text. The table is the contract, including the cases where a shell would keep a character
/// literal but the check stays conservative.
/// </summary>
public class CommandLineTests
{
    [Theory]
    [InlineData("git status", new[] { "git", "status" })]
    [InlineData("  ls   -la  ", new[] { "ls", "-la" })]
    [InlineData("echo \"hello world\"", new[] { "echo", "hello world" })]
    [InlineData("echo 'it is literal'", new[] { "echo", "it is literal" })]
    [InlineData("echo \"a\\\"b\"", new[] { "echo", "a\"b" })]
    [InlineData("echo \"a\\\\b\"", new[] { "echo", "a\\b" })]
    [InlineData("my\\ file", new[] { "my file" })]
    // A Windows path must keep its separators: consuming the backslash would turn `src\a.csproj` into `srca.csproj`,
    // which is what a shell does for the characters it treats as special but not for a path separator.
    [InlineData(@"dotnet build src\Forge.IO\Forge.IO.csproj", new[] { "dotnet", "build", @"src\Forge.IO\Forge.IO.csproj" })]
    [InlineData(@"type C:\Users\me\log.txt", new[] { "type", @"C:\Users\me\log.txt" })]
    [InlineData("dir 'C:\\Program Files'", new[] { "dir", "C:\\Program Files" })]
    [InlineData("echo \"\"", new[] { "echo", "" })]
    [InlineData("echo a=b", new[] { "echo", "a=b" })]
    [InlineData("1FOO=bar", new[] { "1FOO=bar" })]
    [InlineData("printf '%s\\n' x", new[] { "printf", "%s\\n", "x" })]
    public void Simple_command_lines_parse_into_a_vector(string command, string[] expected)
    {
        var parsed = CommandLine.TryParseSimple(command, out var arguments);

        Assert.True(parsed, $"'{command}' was rejected as shell syntax");
        Assert.Equal(expected, arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("a && b")]
    [InlineData("a | b")]
    [InlineData("a > out.txt")]
    [InlineData("a < in.txt")]
    [InlineData("a; b")]
    [InlineData("echo *")]
    [InlineData("echo ?")]
    [InlineData("echo [abc]")]
    [InlineData("echo {a,b}")]
    [InlineData("echo $HOME")]
    [InlineData("echo `date`")]
    [InlineData("echo ~")]
    [InlineData("echo #comment")]
    [InlineData("echo !")]
    [InlineData("echo a\nb")]
    [InlineData("FOO=bar ls")]
    [InlineData("_PRIVATE=1 ls")]
    [InlineData("FOO2=bar ls")]
    [InlineData("echo 'unclosed")]
    [InlineData("echo \"unclosed")]
    [InlineData("ls \\")]
    [InlineData("(echo a)")]
    [InlineData("echo a&b")]
    public void Shell_syntax_is_rejected_and_yields_no_vector(string command)
    {
        var parsed = CommandLine.TryParseSimple(command, out var arguments);

        Assert.False(parsed, $"'{command}' was accepted but a shell would interpret it");
        Assert.Empty(arguments);
    }

    [Fact]
    public void A_rejected_command_line_never_reports_a_partial_vector()
    {
        // A metacharacter after a complete token must not leave that token behind for a caller that ignores the answer.
        var parsed = CommandLine.TryParseSimple("git log | more", out var arguments);

        Assert.False(parsed);
        Assert.Empty(arguments);
    }

    [Fact]
    public void A_backslash_inside_single_quotes_stays_literal()
    {
        var parsed = CommandLine.TryParseSimple("echo 'a\\b'", out var arguments);

        Assert.True(parsed);
        Assert.Equal(["echo", "a\\b"], arguments);
    }

    [Fact]
    public void Upper_case_assignments_below_the_first_token_parse_as_arguments()
    {
        var parsed = CommandLine.TryParseSimple("env FOO=bar", out var arguments);

        Assert.True(parsed);
        Assert.Equal(["env", "FOO=bar"], arguments);
    }
}
