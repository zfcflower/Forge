using Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Scripting.Tests;

/// <summary>Core <see cref="ScriptCodeRunner"/> behavior through its public API only:
/// values, logs, JSON, inner tools, errors, diagnostics, truncation and request validation.</summary>
public sealed class ScriptCodeRunnerBasicsTests
{
    private static Task<CodeExecutionResult> Run(
        string code,
        RecordingHostFacade? host = null,
        IReadOnlyList<ToolDeclaration>? tools = null,
        CodeExecutionOptions? options = null,
        int timeoutMilliseconds = 10_000,
        CancellationToken ct = default) =>
        ScriptCodeSupport.ExecuteAsync(
            new ScriptCodeRunner(),
            ScriptCodeSupport.Request(code, tools, options, timeoutMilliseconds),
            host ?? new RecordingHostFacade(),
            ct);

    [Fact]
    public async Task Top_level_expression_returns_its_value()
    {
        // A bare expression statement is CS0201; the value comes back via `return` or a trailing expression.
        var result = await Run("return 1 + 2 * 3;");

        Assert.False(result.IsError);
        Assert.False(result.RequiresTermination);
        Assert.Equal("7", result.Content);
    }

    [Fact]
    public async Task Return_keyword_returns_value()
    {
        var result = await Run("var x = 40; return x + 2;");

        Assert.Equal("42", result.Content);
    }

    [Fact]
    public async Task Top_level_await_is_supported()
    {
        var result = await Run("await System.Threading.Tasks.Task.Delay(1); return 41 + 1;");

        Assert.Equal("42", result.Content);
    }

    [Fact]
    public async Task Returned_task_is_awaited_and_unwrapped()
    {
        var result = await Run("return System.Threading.Tasks.Task.FromResult(7);");

        Assert.False(result.IsError);
        Assert.Equal("7", result.Content);
    }

    [Fact]
    public async Task Log_is_captured_and_appended_after_result()
    {
        var result = await Run("Log(\"hello\"); Log(42); return \"done\";");

        Assert.False(result.IsError);
        Assert.Equal("done\n[log]\nhello\n42", result.Content);
    }

    [Fact]
    public async Task Log_null_writes_null()
    {
        var result = await Run("Log(null); return \"r\";");

        Assert.Equal("r\n[log]\nnull", result.Content);
    }

    [Fact]
    public async Task Anonymous_object_is_serialized_as_json()
    {
        var result = await Run("return new { a = 1, b = \"x\" };");

        Assert.Equal("{\"a\":1,\"b\":\"x\"}", result.Content);
    }

    [Fact]
    public async Task Typed_collection_is_serialized_as_json()
    {
        var result = await Run("return new System.Collections.Generic.List<int> { 1, 2, 3 };");

        Assert.Equal("[1,2,3]", result.Content);
    }

    [Theory]
    [InlineData("return 42;", "42")]
    [InlineData("return true;", "true")]
    [InlineData("return 'q';", "q")]
    [InlineData("return (string)null;", "null")]
    public async Task Primitives_format_as_expected(string code, string expected)
    {
        var result = await Run(code);

        Assert.False(result.IsError);
        Assert.Equal(expected, result.Content);
    }

    [Fact]
    public async Task String_result_is_returned_verbatim()
    {
        var result = await Run("return \"{\\\"a\\\":1}\";");

        Assert.Equal("{\"a\":1}", result.Content);
    }

    [Fact]
    public async Task Call_returns_tool_text_with_default_empty_arguments()
    {
        var host = new RecordingHostFacade { Handler = (_, _, _) => Task.FromResult(new HostToolResult("tool-says-hi")) };
        var tools = new[] { ScriptCodeSupport.Tool("greeter") };

        var result = await Run("return await Tools.Call(\"greeter\");", host, tools);

        Assert.Equal("tool-says-hi", result.Content);
        Assert.Equal(1, host.CallCount);
        Assert.Equal("greeter", Assert.Single(host.Names));
        Assert.Equal("{}", Assert.Single(host.ArgumentsJson));
    }

    [Fact]
    public async Task Call_with_object_arguments_serializes_to_json()
    {
        var host = new RecordingHostFacade
        {
            Handler = (_, args, _) => Task.FromResult(new HostToolResult(args.GetRawText())),
        };
        var tools = new[] { ScriptCodeSupport.Tool("echo") };

        var result = await Run("return await Tools.Call(\"echo\", new { a = 1 });", host, tools);

        Assert.Contains("\"a\":1", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallJson_accepts_handwritten_json()
    {
        var host = new RecordingHostFacade
        {
            Handler = (_, args, _) => Task.FromResult(new HostToolResult(args.GetRawText())),
        };
        var tools = new[] { ScriptCodeSupport.Tool("echo") };

        var result = await Run("return await Tools.CallJson(\"echo\", \"{\\\"a\\\":2}\");", host, tools);

        Assert.Contains("\"a\":2", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallJson_generic_deserializes_tool_result()
    {
        var host = new RecordingHostFacade
        {
            Handler = (_, _, _) => Task.FromResult(new HostToolResult("{\"a\":5}")),
        };
        var tools = new[] { ScriptCodeSupport.Tool("get") };

        var result = await Run(
            "var d = await Tools.CallJson<System.Collections.Generic.Dictionary<string, int>>(\"get\", \"{}\"); return d[\"a\"];", host, tools);

        Assert.Equal("5", result.Content);
    }

    [Fact]
    public async Task Call_generic_deserializes_tool_result()
    {
        var host = new RecordingHostFacade
        {
            Handler = (_, _, _) => Task.FromResult(new HostToolResult("{\"a\":6}")),
        };
        var tools = new[] { ScriptCodeSupport.Tool("get") };

        var result = await Run(
            "var d = await Tools.Call<System.Collections.Generic.Dictionary<string, int>>(\"get\", new { q = 1 }); return d[\"a\"];", host, tools);

        Assert.Equal("6", result.Content);
    }

    [Fact]
    public async Task Json_helper_parses_without_touching_the_host()
    {
        var host = new RecordingHostFacade();

        var result = await Run(
            "var d = Tools.Json<System.Collections.Generic.Dictionary<string, int>>(\"{\\\"a\\\":9}\"); return d[\"a\"];", host);

        Assert.Equal("9", result.Content);
        Assert.Equal(0, host.CallCount);
    }

    [Fact]
    public async Task Json_element_result_is_returned_as_raw_json()
    {
        var result = await Run("return Tools.Json(\"{\\\"a\\\":1}\");");

        Assert.Equal("{\"a\":1}", result.Content);
    }

    [Fact]
    public async Task Json_helper_rejects_invalid_json_as_catchable_tool_error()
    {
        var result = await Run(
            "try { return Tools.Json<System.Collections.Generic.Dictionary<string, int>>(\"nope\"); } catch (ToolCallException ex) { return \"tool=\" + ex.ToolName + \" msg=\" + ex.Message; }");

        Assert.False(result.IsError);
        Assert.Contains("tool=json", result.Content, StringComparison.Ordinal);
        Assert.Contains("is not JSON", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refused_tool_result_is_catchable_without_denied_flag()
    {
        var host = new RecordingHostFacade
        {
            Handler = (_, _, _) => Task.FromResult(new HostToolResult("needs-approval", IsError: true)),
        };
        var tools = new[] { ScriptCodeSupport.Tool("guarded") };

        var result = await Run(
            "try { await Tools.Call(\"guarded\"); return \"no-throw\"; } catch (ToolCallException ex) { return \"caught denied=\" + ex.Denied + \" tool=\" + ex.ToolName + \" msg=\" + ex.Message; }",
            host, tools);

        Assert.False(result.IsError);
        Assert.Equal("caught denied=False tool=guarded msg=needs-approval", result.Content);
    }

    [Fact]
    public async Task Host_failure_is_wrapped_as_catchable_tool_error()
    {
        var host = new RecordingHostFacade
        {
            Handler = (_, _, _) => throw new InvalidOperationException("db-down"),
        };
        var tools = new[] { ScriptCodeSupport.Tool("broken") };

        var result = await Run(
            "try { await Tools.Call(\"broken\"); return \"no-throw\"; } catch (ToolCallException ex) { return \"denied=\" + ex.Denied + \" msg=\" + ex.Message; }",
            host, tools);

        Assert.False(result.IsError);
        Assert.Contains("denied=False", result.Content, StringComparison.Ordinal);
        Assert.Contains("failed in the host", result.Content, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", result.Content, StringComparison.Ordinal);
        Assert.Contains("db-down", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Self_call_is_refused_with_denied_flag_even_when_declared()
    {
        var host = new RecordingHostFacade();
        var tools = new[]
        {
            new ToolDeclaration("run_code", "self", "{}", ToolSideEffect.Execute),
            ScriptCodeSupport.Tool("real"),
        };

        var result = await Run(
            "string a; string b;" +
            "try { await Tools.Call(\"run_code\"); a = \"no-throw\"; } catch (ToolCallException ex) { a = \"call denied=\" + ex.Denied; }" +
            "try { await Tools.CallJson(\"run_code\", \"{}\"); b = \"no-throw\"; } catch (ToolCallException ex) { b = \"calljson denied=\" + ex.Denied; }" +
            "return a + \";\" + b;", host, tools);

        Assert.False(result.IsError);
        Assert.Equal("call denied=True;calljson denied=True", result.Content);
        Assert.Equal(0, host.CallCount);
    }

    [Fact]
    public async Task Unknown_tool_error_lists_available_names_sorted_without_run_code()
    {
        var host = new RecordingHostFacade();
        var tools = new[]
        {
            ScriptCodeSupport.Tool("beta"),
            ScriptCodeSupport.Tool("alpha"),
            new ToolDeclaration("run_code", "self", "{}", ToolSideEffect.Execute),
        };

        var result = await Run(
            "try { await Tools.Call(\"zzz\"); return \"no-throw\"; } catch (ToolCallException ex) { return \"denied=\" + ex.Denied + \" msg=\" + ex.Message; }",
            host, tools);

        Assert.False(result.IsError);
        Assert.Contains("Unknown tool 'zzz'. Available tools: alpha, beta.", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("run_code", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_tool_with_no_tools_lists_none()
    {
        var result = await Run(
            "try { await Tools.Call(\"zzz\"); return \"no-throw\"; } catch (ToolCallException ex) { return ex.Message; }");

        Assert.False(result.IsError);
        Assert.Contains("Available tools: (none)", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Syntax_error_reports_line_column_and_code()
    {
        var result = await Run("return Missing;");

        Assert.True(result.IsError);
        Assert.False(result.RequiresTermination);
        Assert.Contains("does not compile (1 error(s))", result.Content, StringComparison.Ordinal);
        Assert.Contains("line 1, column 8", result.Content, StringComparison.Ordinal);
        Assert.Contains("CS0103", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostic_count_limit_is_observed_with_more_note()
    {
        var code = "var a1 = Missing1;\nvar a2 = Missing2;\nvar a3 = Missing3;\nvar a4 = Missing4;\nvar a5 = Missing5;\nreturn 1;";
        var options = new CodeExecutionOptions { MaxDiagnosticCount = 2 };

        var result = await Run(code, options: options);

        Assert.True(result.IsError);
        Assert.Contains("(5 error(s))", result.Content, StringComparison.Ordinal);
        Assert.Contains("... 3 more", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostic_chars_limit_truncates_with_note()
    {
        var code = "var a1 = Missing1;\nvar a2 = Missing2;\nvar a3 = Missing3;\nvar a4 = Missing4;\nvar a5 = Missing5;\nreturn 1;";
        var options = new CodeExecutionOptions { MaxDiagnosticChars = 150 };

        var result = await Run(code, options: options);

        Assert.True(result.IsError);
        Assert.Contains("diagnostics truncated", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_exception_names_type_and_script_location()
    {
        var result = await Run("throw new System.InvalidOperationException(\"boom-marker\");");

        Assert.True(result.IsError);
        Assert.False(result.RequiresTermination);
        Assert.Contains("InvalidOperationException", result.Content, StringComparison.Ordinal);
        Assert.Contains("boom-marker", result.Content, StringComparison.Ordinal);
        Assert.Contains("in script.csx:line 1", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_result_is_truncated_at_max_result_chars()
    {
        var options = new CodeExecutionOptions { MaxResultChars = 20 };

        var result = await Run("return new string('x', 100);", options: options);

        Assert.Contains("[... result truncated: kept 20 of 100 chars]", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_inner_result_is_truncated_at_max_inner_chars()
    {
        var host = new RecordingHostFacade
        {
            Handler = (_, _, _) => Task.FromResult(new HostToolResult(new string('y', 100))),
        };
        var tools = new[] { ScriptCodeSupport.Tool("big") };
        var options = new CodeExecutionOptions { MaxInnerResultChars = 10 };

        var result = await Run("return await Tools.Call(\"big\");", host, tools, options);

        Assert.Contains("[... tool result truncated: kept 10 of 100 chars]", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_log_is_truncated_at_max_log_chars()
    {
        var options = new CodeExecutionOptions { MaxLogChars = 10 };

        var result = await Run("Log(new string('y', 50)); return \"ok\";", options: options);

        Assert.Contains("[log truncated at 10 chars]", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_compile_is_keyed_by_options_not_just_source()
    {
        const string BadCode = "var a1 = Missing1;\nvar a2 = Missing2;\nvar a3 = Missing3;\nvar a4 = Missing4;\nvar a5 = Missing5;\nreturn 1;";
        var narrow = new CodeExecutionOptions { MaxDiagnosticCount = 2 };
        var wide = new CodeExecutionOptions { MaxDiagnosticCount = 12 };

        var firstNarrow = await Run(BadCode, options: narrow);
        var thenWide = await Run(BadCode, options: wide);
        var firstWide = await Run(BadCode, options: wide);
        var thenNarrow = await Run(BadCode, options: narrow);

        Assert.Contains("... 3 more", firstNarrow.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("more", thenWide.Content, StringComparison.Ordinal);
        Assert.Contains("(5 error(s))", thenWide.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("more", firstWide.Content, StringComparison.Ordinal);
        Assert.Contains("... 3 more", thenNarrow.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Zero_cache_capacity_disables_caching_but_still_runs()
    {
        var options = new CodeExecutionOptions { CompilationCacheCapacity = 0 };

        var first = await Run("return 1 + 1;", options: options);
        var second = await Run("return 1 + 1;", options: options);

        Assert.Equal("2", first.Content);
        Assert.Equal("2", second.Content);
    }

    [Fact]
    public async Task Null_request_host_options_or_tools_are_rejected()
    {
        var runner = new ScriptCodeRunner();
        var request = ScriptCodeSupport.Request("return 1;");

        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.ExecuteAsync(null!, new RecordingHostFacade(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.ExecuteAsync(request, null!, CancellationToken.None));
        var nullOptions = new CodeExecutionRequest("return 1;", request.Context, 1000, [], null!);
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.ExecuteAsync(nullOptions, new RecordingHostFacade(), CancellationToken.None));
        var nullTools = new CodeExecutionRequest("return 1;", request.Context, 1000, null!, new CodeExecutionOptions());
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.ExecuteAsync(nullTools, new RecordingHostFacade(), CancellationToken.None));
    }

    [Fact]
    public async Task Null_tool_entry_blank_name_or_undefined_side_effect_are_rejected()
    {
        var runner = new ScriptCodeRunner();
        var context = ScriptCodeSupport.Context();

        var nullEntry = new CodeExecutionRequest("return 1;", context, 1000, new ToolDeclaration?[] { null! }!, new CodeExecutionOptions());
        await Assert.ThrowsAsync<ArgumentException>(() => runner.ExecuteAsync(nullEntry, new RecordingHostFacade(), CancellationToken.None));

        var blankName = new CodeExecutionRequest("return 1;", context, 1000, new[] { ScriptCodeSupport.Tool(" ") }, new CodeExecutionOptions());
        await Assert.ThrowsAsync<ArgumentException>(() => runner.ExecuteAsync(blankName, new RecordingHostFacade(), CancellationToken.None));

        var undefinedSideEffect = new CodeExecutionRequest(
            "return 1;", context, 1000,
            new[] { new ToolDeclaration("x", "d", "{}", (ToolSideEffect)999) }, new CodeExecutionOptions());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.ExecuteAsync(undefinedSideEffect, new RecordingHostFacade(), CancellationToken.None));
    }

    [Fact]
    public async Task Non_positive_timeout_or_context_is_rejected()
    {
        var runner = new ScriptCodeRunner();
        var context = ScriptCodeSupport.Context();

        foreach (var timeout in new[] { 0, -5 })
        {
            var request = new CodeExecutionRequest("return 1;", context, timeout, [], new CodeExecutionOptions());
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.ExecuteAsync(request, new RecordingHostFacade(), CancellationToken.None));
        }

        var nullContext = new CodeExecutionRequest("return 1;", null!, 1000, [], new CodeExecutionOptions());
        await Assert.ThrowsAsync<ArgumentException>(() => runner.ExecuteAsync(nullContext, new RecordingHostFacade(), CancellationToken.None));
    }

    [Fact]
    public async Task Non_positive_limits_are_rejected()
    {
        var runner = new ScriptCodeRunner();
        var context = ScriptCodeSupport.Context();

        var cases = new (Func<CodeExecutionOptions> Mutate, string _)[]
        {
            (() => new CodeExecutionOptions() with { MaxConcurrentToolCalls = 0 }, "concurrency"),
            (() => new CodeExecutionOptions() with { MaxScriptChars = 0 }, "script"),
            (() => new CodeExecutionOptions() with { MaxResultChars = 0 }, "result"),
            (() => new CodeExecutionOptions() with { MaxInnerResultChars = 0 }, "inner"),
            (() => new CodeExecutionOptions() with { MaxLogChars = 0 }, "log"),
            (() => new CodeExecutionOptions() with { MaxDiagnosticChars = 0 }, "diagnosticChars"),
            (() => new CodeExecutionOptions() with { MaxDiagnosticCount = 0 }, "diagnosticCount"),
            (() => new CodeExecutionOptions() with { CompilationCacheCapacity = -1 }, "cache"),
        };

        foreach (var (mutate, _) in cases)
        {
            var request = new CodeExecutionRequest("return 1;", context, 1000, [], mutate());
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.ExecuteAsync(request, new RecordingHostFacade(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task Duplicate_tool_names_are_accepted()
    {
        var host = new RecordingHostFacade { Handler = (_, _, _) => Task.FromResult(new HostToolResult("D")) };
        var tools = new[] { ScriptCodeSupport.Tool("dup"), ScriptCodeSupport.Tool("dup") };

        var result = await Run("return await Tools.Call(\"dup\");", host, tools);

        Assert.False(result.IsError);
        Assert.Equal("D", result.Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_script_returns_an_error_not_an_exception(string code)
    {
        var result = await Run(code);

        Assert.True(result.IsError);
        Assert.False(result.RequiresTermination);
        Assert.Contains("non-empty", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overlong_script_returns_an_error_naming_both_lengths()
    {
        var code = "return " + new string('1', 20) + ";";
        var options = new CodeExecutionOptions { MaxScriptChars = 10 };

        var result = await Run(code, options: options);

        Assert.True(result.IsError);
        Assert.Contains($"The script is {code.Length} chars; the limit is 10.", result.Content, StringComparison.Ordinal);
    }
}
