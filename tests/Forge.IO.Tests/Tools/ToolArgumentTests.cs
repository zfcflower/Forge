using Forge.Abstractions;
using Forge.IO.Tools;
using Xunit;

namespace Forge.IO.Tests;

/// <summary>
/// Arguments that are missing, of the wrong type, or out of range become error results the model can read, never
/// exceptions; a cancelled token is the one thing that escapes. The table is one row per accessor shape so a change to
/// the argument reader cannot quietly change a message or return a result instead of a throw.
/// </summary>
public class ToolArgumentTests
{
    [Theory]
    // tool / arguments / the part of the message the model has to see
    [InlineData("read", "{}", "'path' is required.")]
    [InlineData("read", "{\"path\":5}", "'path' must be a string.")]
    [InlineData("read", "{\"path\":\"a.txt\",\"limit\":0}", "'limit' must be between 1 and 20000.")]
    [InlineData("read", "{\"path\":\"a.txt\",\"limit\":40000}", "'limit' must be between 1 and 20000.")]
    [InlineData("read", "{\"path\":\"a.txt\",\"max_bytes\":-1}", "'max_bytes' must be between 1 and 1048576.")]
    [InlineData("read", "{\"path\":\"a.txt\",\"offset\":0}", "'offset' must be between 1 and 2147483647.")]
    [InlineData("write", "{\"path\":\"a.txt\"}", "'content' is required.")]
    [InlineData("write", "{\"path\":\"a.txt\",\"content\":1}", "'content' must be a string.")]
    [InlineData("write", "{\"path\":\"a.txt\",\"content\":\"x\",\"append\":\"yes\"}", "'append' must be true or false.")]
    [InlineData("edit", "{\"path\":\"a.txt\",\"new_string\":\"y\"}", "'old_string' must be a non-empty string.")]
    [InlineData("edit", "{\"path\":\"a.txt\",\"old_string\":\"\",\"new_string\":\"y\"}", "'old_string' must be a non-empty string.")]
    [InlineData("edit", "{\"path\":\"a.txt\",\"old_string\":\"x\"}", "'new_string' is required.")]
    [InlineData("ls", "{\"depth\":0}", "'depth' must be between 1 and 5.")]
    [InlineData("ls", "{\"depth\":6}", "'depth' must be between 1 and 5.")]
    [InlineData("find", "{\"limit\":0}", "'limit' must be between 1 and 10000.")]
    [InlineData("find", "{\"depth\":65}", "'depth' must be between 1 and 64.")]
    [InlineData("grep", "{}", "'pattern' is required.")]
    [InlineData("grep", "{\"pattern\":1}", "'pattern' must be a string.")]
    [InlineData("grep", "{\"pattern\":\"x\",\"context\":6}", "'context' must be between 0 and 5.")]
    [InlineData("grep", "{\"pattern\":\"x\",\"regex\":\"yes\"}", "'regex' must be true or false.")]
    [InlineData("bash", "{}", "'command' is required.")]
    [InlineData("bash", "{\"command\":\"echo hi\",\"timeout_seconds\":\"soon\"}", "'timeout_seconds' must be an integer.")]
    public async Task A_bad_argument_is_an_error_result_and_never_an_exception(string name, string arguments, string message)
    {
        using var workspace = new TemporaryWorkspace();
        var tool = Tool(name);

        var result = await workspace.InvokeAsync(tool, arguments);

        Assert.True(result.IsError, $"{name} {arguments} was accepted");
        Assert.Contains(message, result.Content, StringComparison.Ordinal);
        Assert.StartsWith(name + ": ", result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public async Task Arguments_that_are_not_an_object_are_an_error_result(string arguments)
    {
        using var workspace = new TemporaryWorkspace();
        var tool = Tool("read");

        var result = await workspace.InvokeAsync(tool, arguments);

        Assert.True(result.IsError);
        Assert.Contains("must be a JSON object", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arguments_that_are_not_json_are_an_error_result()
    {
        using var workspace = new TemporaryWorkspace();
        var tool = Tool("read");

        var result = await workspace.InvokeAsync(tool, "{\"path\":");

        Assert.True(result.IsError);
        Assert.Contains("not valid JSON", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_arguments_count_as_an_empty_object()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("a.txt", "text\n");

        var result = await workspace.InvokeAsync(Tool("ls"), string.Empty);

        Assert.False(result.IsError);
    }

    [Theory]
    [InlineData("read", "{\"path\":\"a.txt\"}")]
    [InlineData("write", "{\"path\":\"b.txt\",\"content\":\"x\"}")]
    [InlineData("edit", "{\"path\":\"a.txt\",\"old_string\":\"alpha\",\"new_string\":\"ALPHA\"}")]
    [InlineData("ls", "{}")]
    [InlineData("find", "{\"pattern\":\"*\"}")]
    [InlineData("grep", "{\"pattern\":\"alpha\"}")]
    [InlineData("bash", "{\"command\":\"echo hi\"}")]
    public async Task A_cancelled_token_throws_instead_of_returning_an_error_result(string name, string arguments)
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("a.txt", "alpha\n");
        var tool = Tool(name);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Record.ExceptionAsync(async () => await workspace.InvokeAsync(tool, arguments, cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
    }

    private static ITool Tool(string name) =>
        new FileTools().All.First(tool => tool.Definition.Name == name);
}
