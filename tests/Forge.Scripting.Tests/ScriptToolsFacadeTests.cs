using Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Scripting.Tests;

/// <summary>
/// The tool surface a script reads about itself (wave W14): <c>Tools.List()</c> reports the callable names of this run
/// and <c>Tools.Describe(name)</c> reports one tool's side effect, full description and argument schema — read from the
/// run's declarations only, never by calling the host. Exercised through the public runner path (a compiled script whose
/// <c>Tools</c> global is the facade), because that is the only way script code ever reaches it.
/// </summary>
public sealed class ScriptToolsFacadeTests
{
    /// <summary>Runs one script and returns its result; the host records every inner call it receives.</summary>
    private static Task<CodeExecutionResult> Run(
        string code,
        IReadOnlyList<ToolDeclaration>? tools = null,
        CodeExecutionOptions? options = null,
        RecordingHostFacade? host = null) =>
        ScriptCodeSupport.ExecuteAsync(
            new ScriptCodeRunner(),
            ScriptCodeSupport.Request(code, tools, options),
            host ?? new RecordingHostFacade(),
            CancellationToken.None);

    private static ToolDeclaration Declared(
        string name,
        string description,
        string? schema,
        ToolSideEffect sideEffect = ToolSideEffect.ReadOnly) =>
        new(name, description, schema!, sideEffect);

    [Fact]
    public async Task List_reports_the_declared_names_ordinal_sorted_and_without_run_code()
    {
        // run_code is declared on purpose: the facade must drop it even when a caller passes it in.
        var tools = new[]
        {
            ScriptCodeSupport.Tool("zulu"),
            ScriptCodeSupport.Tool("alpha"),
            ScriptCodeSupport.Tool("mid"),
            ScriptCodeSupport.Tool("run_code"),
        };

        var result = await Run("return string.Join(\"|\", Tools.List());", tools);

        Assert.False(result.IsError, result.Content);
        Assert.Equal("alpha|mid|zulu", result.Content);
    }

    [Fact]
    public async Task With_no_declarations_list_is_empty_and_describe_reports_none()
    {
        var result = await Run(
            """
            var listed = Tools.List();
            try
            {
                Tools.Describe("x");
                return "described";
            }
            catch (Exception error)
            {
                return listed.Count + "|" + error.GetType().Name + "|" + error.Message;
            }
            """);

        Assert.False(result.IsError, result.Content);
        Assert.StartsWith("0|ToolCallException|", result.Content, StringComparison.Ordinal);
        Assert.Contains("(none)", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Describe_reports_the_name_side_effect_full_description_and_raw_schema()
    {
        var tools = new[]
        {
            Declared(
                "scanner",
                "Reads the workspace.\nSecond line of detail.",
                """{"type":"object","properties":{"path":{"type":"string"}}}""",
                ToolSideEffect.ReadOnly),
            Declared("blank", "  ", "   ", ToolSideEffect.Write),
            Declared("nullschema", "d", null, ToolSideEffect.Execute),
        };

        var result = await Run(
            """
            return Tools.Describe("scanner")
                + "\n---\n" + Tools.Describe("blank")
                + "\n---\n" + Tools.Describe("nullschema");
            """,
            tools);

        Assert.False(result.IsError, result.Content);
        var sections = result.Content.Split("\n---\n");
        Assert.Equal(3, sections.Length);

        // Name, side-effect token, and the description in full — the second line proves it is not cut to the first line.
        Assert.Contains("scanner [read-only] Reads the workspace.", sections[0], StringComparison.Ordinal);
        Assert.Contains("Second line of detail.", sections[0], StringComparison.Ordinal);
        Assert.Contains(
            "args=" + """{"type":"object","properties":{"path":{"type":"string"}}}""",
            sections[0],
            StringComparison.Ordinal);

        // A whitespace-only description and schema collapse to the placeholders.
        Assert.Contains("blank [write] (no description)", sections[1], StringComparison.Ordinal);
        Assert.EndsWith("args={}", sections[1], StringComparison.Ordinal);

        // A null schema prints the empty object too, under the execute token.
        Assert.Contains("nullschema [execute] d", sections[2], StringComparison.Ordinal);
        Assert.EndsWith("args={}", sections[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Describe_of_an_unknown_name_throws_ToolCallException_and_never_reaches_the_host()
    {
        var host = new RecordingHostFacade();
        var tools = new[] { ScriptCodeSupport.Tool("known") };

        var result = await Run(
            """
            try
            {
                Tools.Describe("missing");
                return "no-throw";
            }
            catch (Exception error)
            {
                return error.GetType().Name + "|" + error.Message;
            }
            """,
            tools,
            host: host);

        Assert.False(result.IsError, result.Content);
        Assert.StartsWith("ToolCallException|", result.Content, StringComparison.Ordinal);
        Assert.Contains("Unknown tool 'missing'", result.Content, StringComparison.Ordinal);
        Assert.Contains("known", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, host.CallCount);
    }

    [Fact]
    public async Task Describe_and_List_answer_while_the_only_concurrency_slot_is_held_by_an_in_flight_call()
    {
        // One slot in total and it is taken: a Describe or List that needed a slot would wait on the call it runs beside,
        // which only finishes after the script's own describes, so this run would stall instead of completing.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new RecordingHostFacade
        {
            Handler = async (_, _, _) =>
            {
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return new HostToolResult("ran!");
            },
        };
        var tools = new[] { ScriptCodeSupport.Tool("worker") };
        var options = new CodeExecutionOptions { MaxConcurrentToolCalls = 1 };

        var running = Run(
            """
            var pending = Tools.Call("worker");
            var described = 0;
            for (var i = 0; i < 10; i++)
            {
                Tools.Describe("worker");
                described += Tools.List().Count;
            }

            return described + "|" + await pending;
            """,
            tools,
            options,
            host);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        release.TrySetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.IsError, result.Content);
        Assert.Equal("10|ran!", result.Content);
        // Only the call reached the host; the describes and lists were answered from the declarations.
        Assert.Equal(1, host.CallCount);
    }
}
