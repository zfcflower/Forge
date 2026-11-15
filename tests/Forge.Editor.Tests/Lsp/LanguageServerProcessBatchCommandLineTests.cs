using Forge.Editor.Lsp;
using Xunit;

namespace Forge.Editor.Tests.Lsp;

/// <summary>
/// The .cmd/.bat launch path: npm shims and other batch servers run as <c>cmd /d /s /c "…"</c>, which quotes the
/// script and its arguments far more sharply than <see cref="System.Diagnostics.Process"/> alone. These tests pin
/// the command line character for character, including the escapes.
/// </summary>
public class LanguageServerProcessBatchCommandLineTests
{
    [Fact]
    public void The_batch_command_line_quotes_the_script_after_cmd_d_s_c()
    {
        var line = LanguageServerProcess.BuildBatchCommandLine(@"C:\npm\typescript-language-server.cmd", ["--stdio"]);

        // /s makes cmd strip exactly the first and the last quote of the wrapped command, leaving a quoted script
        // path followed by its arguments - the shape npm shims expect.
        Assert.Equal("/e:ON /v:OFF /d /s /c \"\"C:\\npm\\typescript-language-server.cmd\" --stdio\"", line);
    }

    [Fact]
    public void A_script_without_arguments_is_still_wrapped()
    {
        var line = LanguageServerProcess.BuildBatchCommandLine(@"C:\npm\server.cmd", []);

        Assert.Equal("/e:ON /v:OFF /d /s /c \"\"C:\\npm\\server.cmd\"\"", line);
    }

    [Fact]
    public void Arguments_with_spaces_are_quoted_and_plain_ones_are_left_alone()
    {
        var line = LanguageServerProcess.BuildBatchCommandLine(@"C:\npm\server.cmd", ["--stdio", "a b"]);

        Assert.Equal("/e:ON /v:OFF /d /s /c \"\"C:\\npm\\server.cmd\" --stdio \"a b\"\"", line);
    }

    [Fact]
    public void A_percent_in_an_argument_is_escaped_so_cmd_leaves_it_alone()
    {
        var line = LanguageServerProcess.BuildBatchCommandLine(@"C:\npm\server.cmd", ["50%"]);

        Assert.Equal("/e:ON /v:OFF /d /s /c \"\"C:\\npm\\server.cmd\" \"50%%cd:~,%\"\"", line);
    }

    [Fact]
    public void An_embedded_quote_is_doubled_and_the_argument_is_quoted()
    {
        var line = LanguageServerProcess.BuildBatchCommandLine(@"C:\npm\server.cmd", ["a\"b"]);

        Assert.Equal("/e:ON /v:OFF /d /s /c \"\"C:\\npm\\server.cmd\" \"a\"\"b\"\"", line);
    }

    [Fact]
    public void Trailing_backslashes_are_doubled_so_they_do_not_merge_with_the_closing_quote()
    {
        var line = LanguageServerProcess.BuildBatchCommandLine(@"C:\npm\server.cmd", [@"C:\dir\"]);

        Assert.Equal("/e:ON /v:OFF /d /s /c \"\"C:\\npm\\server.cmd\" \"C:\\dir\\\\\"\"", line);
    }

    [Theory]
    [InlineData("C:\\npm\\qu\"ote.cmd")]
    [InlineData("C:\\npm\\ca^ret.cmd")]
    [InlineData("C:\\npm\\pe%rcent.cmd")]
    [InlineData("C:\\npm\\trailing\\")]
    public void Scripts_that_cannot_be_quoted_safely_are_rejected(string script)
    {
        var exception = Assert.Throws<LanguageServerCrashedException>(
            () => LanguageServerProcess.BuildBatchCommandLine(script, ["--stdio"]));

        Assert.Contains("cannot be launched", exception.Message);
    }

    [Theory]
    [InlineData("first\nsecond")]
    [InlineData("first\rsecond")]
    public void Arguments_with_line_breaks_are_rejected(string argument)
    {
        var exception = Assert.Throws<LanguageServerCrashedException>(
            () => LanguageServerProcess.BuildBatchCommandLine(@"C:\npm\server.cmd", [argument]));

        Assert.Contains("line break", exception.Message);
    }

    /// <summary>
    /// The npm-shim launch path against a real cmd.exe: a shim that echoes <c>%*</c> must see the arguments exactly
    /// as they were quoted, spaces and all, or servers like typescript-language-server.cmd would mis-parse their
    /// command line.
    /// </summary>
    [Fact]
    public async Task A_real_cmd_shim_receives_its_arguments_intact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "forge-batch-shim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var script = Path.Combine(directory, "shim.cmd");
            await File.WriteAllTextAsync(script, "@echo off\r\necho [%*]\r\n");

            await using var process = new LanguageServerProcess(
                new LanguageServerLaunch(script, ["--stdio", "a b"]));
            var streams = process.Start();

            using var reader = new StreamReader(streams.Output);
            var output = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("[--stdio \"a b\"]" + Environment.NewLine, output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
