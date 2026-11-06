using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Workflows.Tests.Support;
using Xunit;

namespace Forge.Workflows.Tests;

/// <summary>
/// <c>$input</c> binding root (<c>RunOptions.Input</c>): whole-document, nested, array, optional, missing,
/// validator treatment, non-confusion with step ids, malformed paths, mixing with step bindings, and
/// null/scalar/array inputs. Documents are built with <c>JsonNode</c> so the tests cannot drift into
/// malformed JSON by miscounting braces.
/// </summary>
public sealed class InputBindingTests
{
    [Fact]
    public async Task Whole_input_bound_as_one_value()
    {
        var host = new RecordingHost().Respond("echo", "ok");
        using var input = JsonDocument.Parse("""{"a":1,"b":[1,2]}""");
        var json = Doc(Step("use", "echo", Args(("v", Ref("$input"))), After()));

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host, new RunOptions { Input = input.RootElement }));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(
            """{"a":1,"b":[1,2]}""",
            host.Calls.Single(call => call.Tool == "echo").Arguments.GetProperty("v").GetRawText());
    }

    [Theory]
    [InlineData("$input.user.name", "\"ann\"")]
    [InlineData("$input.items[0]", """{"name":"a"}""")]
    [InlineData("$input.items[1].name", "\"b\"")]
    [InlineData("$input['odd key']", "\"v\"")]
    [InlineData("$input.nested.deep[0]", "7")]
    public async Task Nested_and_index_paths_resolve(string path, string expectedJson)
    {
        var host = new RecordingHost().Respond("echo", "ok");
        using var input = JsonDocument.Parse(
            """{"user":{"name":"ann"},"items":[{"name":"a"},{"name":"b"}],"odd key":"v","nested":{"deep":[7]}}""");
        var json = Doc(Step("use", "echo", Args(("v", Ref(path))), After()));

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host, new RunOptions { Input = input.RootElement }));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(
            expectedJson,
            host.Calls.Single(call => call.Tool == "echo").Arguments.GetProperty("v").GetRawText());
    }

    [Fact]
    public async Task Optional_missing_property_is_omitted_from_the_object()
    {
        var host = new RecordingHost().Respond("echo", "ok");
        using var input = JsonDocument.Parse("""{"keep":1}""");
        var json = Doc(Step("use", "echo", new JsonObject { ["keep"] = 1, ["gone"] = Ref("$input.missing?") }, After()));

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host, new RunOptions { Input = input.RootElement }));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        var args = host.Calls.Single(call => call.Tool == "echo").Arguments;
        Assert.Equal(1, args.GetProperty("keep").GetInt32());
        Assert.False(args.TryGetProperty("gone", out _));
    }

    [Fact]
    public async Task Optional_missing_element_becomes_null_in_arrays()
    {
        var host = new RecordingHost().Respond("echo", "ok");
        using var input = JsonDocument.Parse("""{"items":[1]}""");
        var json = Doc(Step("use", "echo", new JsonObject { ["v"] = new JsonArray { Ref("$input.items[5]?"), "x" } }, After()));

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host, new RunOptions { Input = input.RootElement }));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(
            """[null,"x"]""",
            host.Calls.Single(call => call.Tool == "echo").Arguments.GetProperty("v").GetRawText());
    }

    [Fact]
    public async Task Missing_path_without_optional_fails_the_step_and_skips_successors()
    {
        var host = new RecordingHost().Respond("echo", "never").Respond("after", "never");
        using var input = JsonDocument.Parse("""{"a":1}""");
        var json = Doc(
            Step("use", "echo", Args(("v", Ref("$input.absent"))), After()),
            Step("next", "after"));

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host, new RunOptions { Input = input.RootElement }));

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        var failed = WorkflowKit.Step(result, "use");
        Assert.Equal(StepStatus.Failed, failed.Status);
        Assert.Contains("step 'use': binding '$input.absent' could not be resolved:", failed.Error);
        Assert.Equal(0, host.CallCount("echo"));
        Assert.Equal(StepStatus.Skipped, WorkflowKit.Step(result, "next").Status);
        Assert.Equal(new[] { "next" }, result.NeverRanStepIds);
    }

    [Fact]
    public async Task No_input_fails_the_binding_with_a_clear_error()
    {
        var host = new RecordingHost().Respond("echo", "never").Respond("after", "never");
        var json = Doc(
            Step("use", "echo", Args(("v", Ref("$input.a"))), After()),
            Step("next", "after"));

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host, new RunOptions()));

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        var failed = WorkflowKit.Step(result, "use");
        Assert.Equal(StepStatus.Failed, failed.Status);
        Assert.Contains("the run has no $input", failed.Error);
        Assert.Equal(0, host.CallCount("echo"));
        Assert.Equal(StepStatus.Skipped, WorkflowKit.Step(result, "next").Status);
    }

    [Fact]
    public void Validator_accepts_input_bindings_without_any_dependency()
    {
        var json = Doc(
            Step("fetch", "read", after: After()),
            Step("use", "write", Args(("v", Ref("$input.label")))));

        Assert.Empty(WorkflowValidator.Validate(WorkflowJson.Parse(json)));
    }

    [Fact]
    public void Validator_accepts_input_next_to_an_unrelated_dependency()
    {
        var json = Doc(
            Step("other", "t", after: After()),
            Step("use", "t", Args(("v", Ref("$input.x")), ("w", Ref("$.steps.other.content"))), After("other")));

        Assert.Empty(WorkflowValidator.Validate(WorkflowJson.Parse(json)));
    }

    [Fact]
    public async Task Dollar_input_is_not_a_step_id_even_when_a_step_is_named_input()
    {
        var host = new RecordingHost().Respond("src", "step-text").Respond("echo", "ok");
        using var input = JsonDocument.Parse("""{"v":"input-value"}""");
        var json = Doc(
            Step("input", "src", after: After()),
            Step("use", "echo", Args(("fromStep", Ref("$.steps.input.content")), ("fromInput", Ref("$input.v")))));

        Assert.Empty(WorkflowValidator.Validate(WorkflowJson.Parse(json)));

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host, new RunOptions { Input = input.RootElement }));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        var args = host.Calls.Single(call => call.Tool == "echo").Arguments;
        Assert.Equal("step-text", args.GetProperty("fromStep").GetString());
        Assert.Equal("input-value", args.GetProperty("fromInput").GetString());
    }

    [Fact]
    public void Step_lookup_without_dollar_names_a_step_that_must_exist()
    {
        var unknown = Assert.Single(WorkflowValidator.Validate(WorkflowJson.Parse(
            Doc(Step("a", "t", Args(("v", Ref("steps.ghost.content"))), After())))));

        Assert.Equal(WorkflowDiagnosticCode.UnknownBindingStep, unknown.Code);
        Assert.Equal("a", unknown.StepId);
        Assert.Contains("names unknown step 'ghost'", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_segment_path_is_malformed_whichever_root_it_meant()
    {
        var malformed = Assert.Single(WorkflowValidator.Validate(WorkflowJson.Parse(
            Doc(Step("a", "t", Args(("v", Ref("input.x"))), After())))));

        Assert.Equal(WorkflowDiagnosticCode.InvalidBindingPath, malformed.Code);
        Assert.Equal("a", malformed.StepId);
    }

    [Theory]
    [InlineData("$input.")]
    [InlineData("$input..a")]
    [InlineData("$input[")]
    [InlineData("$input?extra")]
    public void Empty_or_trailing_segments_are_invalid_binding_paths(string path)
    {
        var diagnostic = Assert.Single(WorkflowValidator.Validate(WorkflowJson.Parse(
            Doc(Step("use", "t", Args(("v", Ref(path))), After())))));

        Assert.Equal(WorkflowDiagnosticCode.InvalidBindingPath, diagnostic.Code);
        Assert.Equal(WorkflowDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("use", diagnostic.StepId);
    }

    [Fact]
    public async Task Step_and_input_bindings_mix_in_one_args_object()
    {
        var host = new RecordingHost().Respond("src", """{"n":3}""").Respond("echo", "ok");
        using var input = JsonDocument.Parse("""{"m":"m-value"}""");
        var json = Doc(
            Step("src", "src", after: After()),
            Step("use", "echo", new JsonObject
            {
                ["s"] = Ref("$.steps.src.json.n"),
                ["i"] = Ref("$input.m"),
                ["lit"] = new JsonArray { 1, Ref("$input.m") },
            }));

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host, new RunOptions { Input = input.RootElement }));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        var args = host.Calls.Single(call => call.Tool == "echo").Arguments;
        Assert.Equal(3, args.GetProperty("s").GetInt32());
        Assert.Equal("m-value", args.GetProperty("i").GetString());
        Assert.Equal("""[1,"m-value"]""", args.GetProperty("lit").GetRawText());
    }

    [Fact]
    public async Task Null_input_binds_whole_but_navigating_it_fails()
    {
        var host = new RecordingHost().Respond("echo", "ok");
        using var input = JsonDocument.Parse("null");

        var whole = await WorkflowKit.Within(WorkflowKit.RunAsync(
            Doc(Step("use", "echo", Args(("v", Ref("$input"))), After())),
            host,
            new RunOptions { Input = input.RootElement }));

        Assert.Equal(WorkflowRunStatus.Completed, whole.Status);
        Assert.Equal(JsonValueKind.Null, host.Calls.Single(call => call.Tool == "echo").Arguments.GetProperty("v").ValueKind);

        var navigatingHost = new RecordingHost().Respond("echo", "never");
        var navigating = await WorkflowKit.Within(WorkflowKit.RunAsync(
            Doc(Step("use", "echo", Args(("v", Ref("$input.a"))), After())),
            navigatingHost,
            new RunOptions { Input = input.RootElement }));

        Assert.Equal(WorkflowRunStatus.Failed, navigating.Status);
        Assert.Equal(StepStatus.Failed, WorkflowKit.Step(navigating, "use").Status);
        Assert.Equal(0, navigatingHost.CallCount("echo"));
    }

    [Fact]
    public async Task Scalar_inputs_bind_whole_but_reject_property_reads()
    {
        var host = new RecordingHost().Respond("echo", "ok");
        using var number = JsonDocument.Parse("42");

        var whole = await WorkflowKit.Within(WorkflowKit.RunAsync(
            Doc(Step("use", "echo", Args(("v", Ref("$input"))), After())),
            host,
            new RunOptions { Input = number.RootElement }));

        Assert.Equal(WorkflowRunStatus.Completed, whole.Status);
        Assert.Equal(42, host.Calls.Single(call => call.Tool == "echo").Arguments.GetProperty("v").GetInt32());

        var navigatingHost = new RecordingHost().Respond("echo", "never");
        var navigating = await WorkflowKit.Within(WorkflowKit.RunAsync(
            Doc(Step("use", "echo", Args(("v", Ref("$input.a"))), After())),
            navigatingHost,
            new RunOptions { Input = number.RootElement }));

        Assert.Equal(WorkflowRunStatus.Failed, navigating.Status);
        Assert.Contains("of a number", WorkflowKit.Step(navigating, "use").Error);
        Assert.Equal(0, navigatingHost.CallCount("echo"));
    }

    [Fact]
    public async Task String_input_binds_whole()
    {
        var host = new RecordingHost().Respond("echo", "ok");
        using var text = JsonDocument.Parse("\"hi\"");

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(
            Doc(Step("use", "echo", Args(("v", Ref("$input"))), After())),
            host,
            new RunOptions { Input = text.RootElement }));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal("hi", host.Calls.Single(call => call.Tool == "echo").Arguments.GetProperty("v").GetString());
    }

    [Fact]
    public async Task Array_inputs_support_indexing_and_whole_binding()
    {
        var host = new RecordingHost().Respond("echo", "ok");
        using var array = JsonDocument.Parse("[10,20]");

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(
            Doc(Step("use", "echo", Args(("second", Ref("$input[1]")), ("all", Ref("$input"))), After())),
            host,
            new RunOptions { Input = array.RootElement }));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        var args = host.Calls.Single(call => call.Tool == "echo").Arguments;
        Assert.Equal(20, args.GetProperty("second").GetInt32());
        Assert.Equal("[10,20]", args.GetProperty("all").GetRawText());
    }

    [Fact]
    public async Task Out_of_range_input_index_fails_with_position()
    {
        var host = new RecordingHost().Respond("echo", "never");
        using var array = JsonDocument.Parse("[10,20]");

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(
            Doc(Step("use", "echo", Args(("v", Ref("$input[5]"))), After())),
            host,
            new RunOptions { Input = array.RootElement }));

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.Contains("index 5 is out of range at '$input[5]' (length 2)", WorkflowKit.Step(result, "use").Error);
        Assert.Equal(0, host.CallCount("echo"));
    }

    private static string Doc(params JsonObject[] steps)
    {
        var array = new JsonArray();
        foreach (var step in steps)
        {
            array.Add(step);
        }

        return new JsonObject { ["steps"] = array }.ToJsonString();
    }

    private static JsonObject Step(string id, string tool, JsonObject? args = null, JsonArray? after = null)
    {
        var step = new JsonObject { ["id"] = id, ["tool"] = tool };
        if (args is not null)
        {
            step["args"] = args;
        }

        if (after is not null)
        {
            step["after"] = after;
        }

        return step;
    }

    private static JsonObject Args(params (string Name, JsonObject Ref)[] entries)
    {
        var args = new JsonObject();
        foreach (var (name, reference) in entries)
        {
            args[name] = reference;
        }

        return args;
    }

    private static JsonObject Ref(string path) => new() { ["$ref"] = path };

    private static JsonArray After(params string[] ids)
    {
        var after = new JsonArray();
        foreach (var id in ids)
        {
            after.Add(id);
        }

        return after;
    }
}
