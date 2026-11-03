using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Workflows.Tests.Support;
using Xunit;

namespace Forge.Workflows.Tests;

public sealed class WorkflowBindingTests
{
    private const string SourceContent = """
        {"items":[{"name":"a"},{"name":"b"},{"name":"c"}],"odd key":["x","y"],"it's":"q","a.b":42,"nested":{"deep":[{"name":"d"}]},"flag":true,"nothing":null}
        """;

    [Theory]
    [InlineData("src", """$.steps.src.json.items[2].name""", "\"c\"")]
    [InlineData("src", """$.steps.src.json.items[0]["name"]""", "\"a\"")]
    [InlineData("src", """$.steps.src.json.items[1]""", """{"name":"b"}""")]
    [InlineData("src", """$.steps.src.json['odd key'][1]""", "\"y\"")]
    [InlineData("src", """$.steps.src.json["odd key"][0]""", "\"x\"")]
    [InlineData("src", """$.steps.src.json['it\'s']""", "\"q\"")]
    [InlineData("src", """$.steps.src.json['a.b']""", "42")]
    [InlineData("src", """$.steps.src.json.nested.deep[0].name""", "\"d\"")]
    [InlineData("src", """$.steps.src.json.flag""", "true")]
    [InlineData("src", """$.steps.src.json.nothing""", "null")]
    [InlineData("src", """$.steps.src.json.items[2].name?""", "\"c\"")]
    [InlineData("src", """$.steps.src.status""", "\"succeeded\"")]
    [InlineData("src", """$.steps.src.isError""", "false")]
    [InlineData("my step", """$.steps['my step'].json.items[0].name""", "\"a\"")]
    [InlineData("src", """$.steps.src.json""", SourceContent)]
    public async Task Path_resolves_to_the_expected_json_value(string producerId, string path, string expectedJson)
    {
        var host = NewHost();

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(ConsumerDocument(producerId, path), host));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        AssertJsonEquals(expectedJson, EchoValue(host));
    }

    [Fact]
    public async Task Content_path_yields_the_raw_text_of_the_producer()
    {
        var host = NewHost();

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(ConsumerDocument("src", "$.steps.src.content"), host));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(SourceContent, EchoValue(host).GetString());
    }

    [Fact]
    public async Task Optional_property_that_is_missing_is_omitted_from_the_object()
    {
        var host = NewHost();
        const string json = """
            {"steps":[
              {"id":"src","tool":"src"},
              {"id":"use","tool":"echo","args":{"v":{"keep":1,"gone":{"$ref":"$.steps.src.json.missing?"}}}}
            ]}
            """;

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        var value = EchoValue(host);
        Assert.Equal(1, value.GetProperty("keep").GetInt32());
        Assert.False(value.TryGetProperty("gone", out _));
    }

    [Fact]
    public async Task Optional_array_element_that_is_missing_becomes_null()
    {
        var host = NewHost();
        const string json = """
            {"steps":[
              {"id":"src","tool":"src"},
              {"id":"use","tool":"echo","args":{"v":[{"$ref":"$.steps.src.json.items[9].name?"},"x"]}}
            ]}
            """;

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        AssertJsonEquals("""[null,"x"]""", EchoValue(host));
    }

    [Fact]
    public async Task Bindings_nested_inside_arrays_and_objects_are_resolved()
    {
        var host = NewHost();
        const string json = """
            {"steps":[
              {"id":"src","tool":"src"},
              {"id":"use","tool":"echo","args":{"v":{"outer":[{"$ref":"$.steps.src.json.items[1].name"},"lit",[{"$ref":"$.steps.src.json.flag"}]]}}}
            ]}
            """;

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        AssertJsonEquals("""{"outer":["b","lit",[true]]}""", EchoValue(host));
    }

    [Fact]
    public async Task Literal_directive_passes_a_binding_shaped_object_through_unchanged()
    {
        var host = NewHost();
        const string json = """
            {"steps":[
              {"id":"src","tool":"src"},
              {"id":"use","tool":"echo","args":{"v":{"$literal":{"$ref":"$.steps.src.content"}}}}
            ]}
            """;

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        AssertJsonEquals("""{"$ref":"$.steps.src.content"}""", EchoValue(host));
    }

    [Fact]
    public async Task Literal_directive_keeps_a_plain_string_that_looks_like_a_path()
    {
        var host = NewHost();
        const string json = """
            {"steps":[
              {"id":"src","tool":"src"},
              {"id":"use","tool":"echo","args":{"v":{"$literal":"$.steps.src.json"}}}
            ]}
            """;

        await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal("$.steps.src.json", EchoValue(host).GetString());
    }

    [Fact]
    public async Task Multi_key_object_is_literal_but_its_nested_bindings_still_resolve()
    {
        var host = NewHost();
        const string json = """
            {"steps":[
              {"id":"src","tool":"src"},
              {"id":"use","tool":"echo","args":{"v":{"$ref":"not-a-path","y":{"$ref":"$.steps.src.json.items[0].name"}}}}
            ]}
            """;

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        AssertJsonEquals("""{"$ref":"not-a-path","y":"a"}""", EchoValue(host));
    }

    [Fact]
    public async Task Args_without_directives_reach_the_tool_as_the_original_json_text()
    {
        var host = NewHost();
        const string json = """
            {"steps":[{"id":"a","tool":"echo","args":{ "a" : "\u00e9" , "b":1.50 , "c":[ true, null ] }}]}
            """;

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal("""{ "a" : "\u00e9" , "b":1.50 , "c":[ true, null ] }""", Assert.Single(host.Calls).ArgumentsText);
    }

    [Fact]
    public async Task Missing_args_reach_the_tool_as_an_empty_object()
    {
        var host = NewHost();

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync("""{"steps":[{"id":"a","tool":"echo"}]}""", host));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal("{}", Assert.Single(host.Calls).ArgumentsText);
    }

    [Theory]
    [InlineData(SourceContent, "$.steps.src.json.items[5].name", "index 5 is out of range")]
    [InlineData(SourceContent, "$.steps.src.json.absent", "property 'absent' not found")]
    [InlineData(SourceContent, "$.steps.src.json.items.name", "cannot read property 'name'")]
    [InlineData(SourceContent, "$.steps.src.json.flag[0]", "cannot index into a boolean")]
    [InlineData(SourceContent, "$.steps.src.content.x", "cannot be navigated")]
    [InlineData("plain text", "$.steps.src.json.x", "is not JSON")]
    [InlineData("", "$.steps.src.json.x", "produced no content")]
    public async Task Unresolvable_binding_fails_the_step_before_the_tool_is_called(string content, string path, string reason)
    {
        var host = new RecordingHost().Respond("src", content).Respond("echo", "never");

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(ConsumerDocument("src", path), host));

        var use = WorkflowKit.Step(result, "use");
        Assert.Equal(StepStatus.Failed, use.Status);
        Assert.Equal(0, host.CallCount("echo"));
        Assert.StartsWith($"step 'use': binding '{path}' could not be resolved:", use.Error);
        Assert.Contains(reason, use.Error);
        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.Equal(use.Error, result.Error);
    }

    [Fact]
    public async Task Out_of_range_error_locates_the_failing_segment_as_documented_in_readme()
    {
        var host = new RecordingHost().Respond("src", """{"items":[{"name":"a"},{"name":"b"}]}""").Respond("echo", "never");
        const string json = """{"steps":[{"id":"src","tool":"src"},{"id":"use","tool":"echo","args":{"v":{"$ref":"$.steps.src.json.items[5]"}}}]}""";

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(
            "step 'use': binding '$.steps.src.json.items[5]' could not be resolved: index 5 is out of range at '$.steps.src.json.items[5]' (length 2)",
            WorkflowKit.Step(result, "use").Error);
    }

    [Fact]
    public async Task Failed_binding_skips_the_steps_that_depend_on_it()
    {
        var host = new RecordingHost().Respond("src", SourceContent).Respond("echo", "never").Respond("after", "x");
        const string json = """
            {"steps":[
              {"id":"src","tool":"src","after":[]},
              {"id":"use","tool":"echo","args":{"v":{"$ref":"$.steps.src.json.items[7].name"}}},
              {"id":"next","tool":"after"}
            ]}
            """;

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(StepStatus.Failed, WorkflowKit.Step(result, "use").Status);
        Assert.Equal(StepStatus.Skipped, WorkflowKit.Step(result, "next").Status);
        Assert.Equal(0, host.CallCount("after"));
        Assert.Equal(new[] { "next" }, result.NeverRanStepIds);
    }

    [Fact]
    public async Task Optional_top_level_binding_that_cannot_resolve_omits_the_argument()
    {
        var host = NewHost();

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(ConsumerDocument("src", "$.steps.src.json.items[9].name?"), host));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal("{}", Assert.Single(host.Calls, static call => call.Tool == "echo").ArgumentsText);
    }

    private static RecordingHost NewHost() => new RecordingHost()
        .Respond("src", SourceContent)
        .Respond("echo", "ok");

    private static string ConsumerDocument(string producerId, string path)
    {
        var steps = new JsonArray(
            new JsonObject { ["id"] = producerId, ["tool"] = "src" },
            new JsonObject
            {
                ["id"] = "use",
                ["tool"] = "echo",
                ["args"] = new JsonObject { ["v"] = new JsonObject { ["$ref"] = path } },
            });
        return new JsonObject { ["steps"] = steps }.ToJsonString();
    }

    private static JsonElement EchoValue(RecordingHost host)
    {
        var call = Assert.Single(host.Calls, static call => call.Tool == "echo");
        return call.Arguments.GetProperty("v");
    }

    private static void AssertJsonEquals(string expectedJson, JsonElement actual)
    {
        var expected = JsonNode.Parse(expectedJson);
        var actualNode = JsonNode.Parse(actual.GetRawText());
        Assert.True(
            JsonNode.DeepEquals(expected, actualNode),
            $"expected {expected?.ToJsonString() ?? "null"} but was {actualNode?.ToJsonString() ?? "null"}");
    }
}
