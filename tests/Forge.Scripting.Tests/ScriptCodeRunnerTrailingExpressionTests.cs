using Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Scripting.Tests;

/// <summary>The script's last line is the result when it produces a value: the semantic model decides by type, so a
/// trailing call, <c>new</c>, assignment or increment is returned and only an expression that is genuinely <c>void</c> or
/// ref-like (which cannot be boxed) stays a statement. Every case here goes through the public
/// <see cref="ScriptCodeRunner"/> API.</summary>
public sealed class ScriptCodeRunnerTrailingExpressionTests
{
    private static Task<CodeExecutionResult> Run(
        string code,
        RecordingHostFacade? host = null,
        IReadOnlyList<ToolDeclaration>? tools = null) =>
        ScriptCodeSupport.ExecuteAsync(
            new ScriptCodeRunner(),
            ScriptCodeSupport.Request(code, tools),
            host ?? new RecordingHostFacade(),
            CancellationToken.None);

    private static RecordingHostFacade ToolHost() =>
        new() { Handler = (_, _, _) => Task.FromResult(new HostToolResult("tool-says-hi")) };

    [Fact]
    public async Task Trailing_tool_call_returns_its_text()
    {
        var result = await Run("await Tools.Call(\"greeter\");", ToolHost(), [ScriptCodeSupport.Tool("greeter")]);

        Assert.False(result.IsError);
        Assert.Equal("tool-says-hi", result.Content);
    }

    [Fact]
    public async Task Trailing_unawaited_tool_call_is_awaited_and_unwrapped()
    {
        var result = await Run("Tools.CallJson(\"greeter\", \"{}\");", ToolHost(), [ScriptCodeSupport.Tool("greeter")]);

        Assert.False(result.IsError);
        Assert.Equal("tool-says-hi", result.Content);
    }

    [Fact]
    public async Task Trailing_object_creation_returns_the_instance()
    {
        var result = await Run("var list = new System.Collections.Generic.List<int>();\nlist.Add(9);\nnew System.Collections.Generic.List<int> { 1, 2, 3 };");

        Assert.False(result.IsError);
        Assert.Equal("[1,2,3]", result.Content);
    }

    [Fact]
    public async Task Trailing_assignment_returns_the_assigned_value()
    {
        var result = await Run("var x = 0;\nx = 5;");

        Assert.False(result.IsError);
        Assert.Equal("5", result.Content);
    }

    [Theory]
    [InlineData("var x = 1;\n++x;", "2")]
    [InlineData("var x = 1;\nx++;", "1")]
    public async Task Trailing_increment_returns_the_expression_value(string code, string expected)
    {
        var result = await Run(code);

        Assert.False(result.IsError);
        Assert.Equal(expected, result.Content);
    }

    [Fact]
    public async Task Trailing_void_call_stays_a_statement()
    {
        var result = await Run("var value = 3;\nLog(value);");

        Assert.False(result.IsError);
        Assert.Equal("null\n[log]\n3", result.Content);
    }

    [Fact]
    public async Task Trailing_ref_struct_call_stays_a_statement()
    {
        var result = await Run("var text = \"abc\";\ntext.AsSpan();");

        Assert.False(result.IsError);
        Assert.Equal("null", result.Content);
    }

    [Fact]
    public async Task Trailing_await_of_a_non_generic_task_returns_null()
    {
        var result = await Run("await System.Threading.Tasks.Task.Delay(1);");

        Assert.False(result.IsError);
        Assert.Equal("null", result.Content);
    }

    [Fact]
    public async Task Trailing_statement_expression_diagnostic_keeps_the_script_line()
    {
        var result = await Run("var x = 1;\nx.NoSuchMember();");

        Assert.True(result.IsError);
        Assert.Contains("does not compile (1 error(s))", result.Content, StringComparison.Ordinal);
        Assert.Contains("line 2", result.Content, StringComparison.Ordinal);
        Assert.Contains("CS1061", result.Content, StringComparison.Ordinal);
    }
}
