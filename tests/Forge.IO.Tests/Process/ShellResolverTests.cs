using Xunit;

namespace Forge.IO.Tests;

/// <summary>
/// Shell resolution: a configured shell must exist as written or the error names it, the platform default must name an
/// executable that is really there, and each dialect's name and script arguments are fixed so a command reaches the
/// shell it was written for.
/// </summary>
public class ShellResolverTests
{
    [Fact]
    public void A_configured_shell_that_does_not_exist_fails_with_the_path_it_was_given()
    {
        using var workspace = new TemporaryWorkspace();
        var missing = Path.Combine(workspace.Root, "no-such-shell.exe");

        var exception = Assert.Throws<FileNotFoundException>(() => ShellResolver.Resolve(missing));

        Assert.Equal(missing, exception.FileName);
        Assert.Contains(missing, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configured_shell_looked_up_by_name_that_is_not_on_path_fails()
    {
        var exception = Assert.Throws<FileNotFoundException>(() => ShellResolver.Resolve("forge-no-such-shell-xyz"));

        Assert.Equal("forge-no-such-shell-xyz", exception.FileName);
    }

    [Fact]
    public void The_platform_default_is_an_existing_executable_of_the_expected_dialect()
    {
        var shell = ShellResolver.Resolve(null);

        Assert.True(File.Exists(shell.FileName), $"the resolved shell '{shell.FileName}' does not exist");
        Assert.True(Path.IsPathFullyQualified(shell.FileName), $"'{shell.FileName}' is not an absolute path");
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(shell.DialectName, new[] { "pwsh", "cmd" });
        }
        else
        {
            Assert.Contains(shell.DialectName, new[] { "bash", "sh" });
        }
    }

    [Fact]
    public void A_configured_existing_shell_keeps_its_path_and_dialect()
    {
        var platformDefault = ShellResolver.Resolve(null);

        var configured = ShellResolver.Resolve(platformDefault.FileName);

        Assert.Equal(platformDefault.FileName, configured.FileName);
        Assert.Equal(platformDefault.DialectName, configured.DialectName);
    }

    [Fact]
    public void A_bare_configured_name_is_looked_up_on_path()
    {
        // The platform's own interpreter is always on PATH, so this resolves without depending on an installed pwsh.
        var name = OperatingSystem.IsWindows() ? "cmd" : "sh";

        var shell = ShellResolver.Resolve(name);

        Assert.True(File.Exists(shell.FileName), $"'{shell.FileName}' does not exist");
        Assert.Equal(name, shell.DialectName);
    }

    [Theory]
    [InlineData(ShellDialect.Pwsh, "pwsh", new[] { "-NoProfile", "-NonInteractive", "-Command" })]
    [InlineData(ShellDialect.Cmd, "cmd", new[] { "/d", "/s", "/c" })]
    [InlineData(ShellDialect.Bash, "bash", new[] { "-c" })]
    [InlineData(ShellDialect.Sh, "sh", new[] { "-c" })]
    public void Each_dialect_has_a_fixed_name_and_script_argument_shape(ShellDialect dialect, string name, string[] prefix)
    {
        var shell = new Shell(Path.Combine(Path.GetTempPath(), name), dialect);

        Assert.Equal(name, shell.DialectName);
        var expected = new List<string>(prefix) { "echo hi" };
        Assert.Equal(expected, shell.ArgumentsForScript("echo hi"));
    }
}
