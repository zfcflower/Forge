using System.Text;
using System.Text.Json;
using Forge.Workflows;
using Xunit;

namespace Forge.Workflows.Tests;

public sealed class WorkflowParsingTests
{
    [Fact]
    public void Valid_document_maps_name_version_steps_args_and_after()
    {
        const string json = """
            {
              "name": "sync",
              "version": 1,
              "steps": [
                { "id": "fetch", "tool": "read_file", "args": { "path": "a.txt" } },
                { "id": "store", "tool": "write_file", "args": { "from": { "$ref": "$.steps.fetch.content" } }, "after": ["fetch"] },
                { "id": "notify", "tool": "send", "after": [] }
              ]
            }
            """;

        var definition = WorkflowJson.Parse(json);

        Assert.Equal("sync", definition.Name);
        Assert.Equal(1, definition.Version);
        Assert.Equal(3, definition.Steps.Count);
        Assert.Equal("fetch", definition.Steps[0].Id);
        Assert.Equal("read_file", definition.Steps[0].Tool);
        Assert.Equal("a.txt", definition.Steps[0].Args.GetProperty("path").GetString());
        Assert.Null(definition.Steps[0].After);
        Assert.Equal(new[] { "fetch" }, definition.Steps[1].After);
        Assert.Empty(definition.Steps[2].After!);
        Assert.Equal(StepErrorMode.Fail, definition.Steps[2].OnError);
    }

    [Fact]
    public void Omitted_version_is_normalized_to_supported_version()
    {
        var definition = WorkflowJson.Parse("""{"steps":[]}""");

        Assert.Equal(WorkflowDefinition.SupportedVersion, definition.Version);
    }

    [Fact]
    public void Version_above_one_parses_so_the_validator_can_report_it()
    {
        var definition = WorkflowJson.Parse("""{"version":2,"steps":[]}""");

        Assert.Equal(2, definition.Version);
    }

    [Fact]
    public void Malformed_json_throws_format_exception_with_line_and_position()
    {
        var ex = Assert.Throws<WorkflowFormatException>(() => WorkflowJson.Parse("""{"steps": [ x ]}"""));

        Assert.StartsWith("invalid workflow JSON", ex.Message);
        Assert.Equal("$.steps[0]", ex.JsonPath);
        Assert.Equal(12, ex.BytePositionInLine);
        Assert.IsType<JsonException>(ex.InnerException);
    }

    [Fact]
    public void Malformed_json_reports_a_one_based_line_number()
    {
        var ex = Assert.Throws<WorkflowFormatException>(() => WorkflowJson.Parse("{\n\"steps\": [ x ]}"));

        Assert.Equal(2, ex.LineNumber);
    }

    [Fact]
    public void Truncated_json_throws_format_exception()
    {
        Assert.Throws<WorkflowFormatException>(() => WorkflowJson.Parse("""{"steps": [ """));
    }

    [Theory]
    [InlineData("""{"steps":"x"}""", "$.steps")]
    [InlineData("""{"steps":[1]}""", "$.steps[0]")]
    [InlineData("""{"steps":[{"id":"a","tool":5}]}""", "$.steps[0].tool")]
    [InlineData("""{"steps":[{"id":"a","tool":"t","after":"a"}]}""", "$.steps[0].after")]
    [InlineData("""{"steps":[{"id":"a","tool":"t","after":[1]}]}""", "$.steps[0].after[0]")]
    [InlineData("""{"steps":[{"id":"a","tool":"t","after":[null]}]}""", "$.steps[0].after[0]")]
    [InlineData("""{"steps":[{"id":"a","tool":"t","onError":"retry"}]}""", "$.steps[0].onError")]
    [InlineData("""{"steps":[],"name":5}""", "$.name")]
    [InlineData("""{"version":"one","steps":[]}""", "$.version")]
    [InlineData("""{"steps":[null]}""", "$.steps[0]")]
    [InlineData("""{}""", "$.steps")]
    [InlineData("""[]""", "$")]
    public void Wrong_shape_throws_format_exception_naming_the_json_path(string json, string expectedPath)
    {
        var ex = Assert.Throws<WorkflowFormatException>(() => WorkflowJson.Parse(json));

        Assert.Equal(expectedPath, ex.JsonPath);
        Assert.StartsWith("invalid workflow JSON", ex.Message);
    }

    [Fact]
    public void A_commented_document_without_steps_is_still_a_format_error()
    {
        // The presence scan reads the document a second time; it must accept the same comments and
        // trailing commas WorkflowJsonContext does, or a documented-as-invalid document would surface
        // as a JSON reader error instead of a missing-steps format error.
        const string json = """
            {
              // steps would go here
              "name": "sync",
            }
            """;

        var ex = Assert.Throws<WorkflowFormatException>(() => WorkflowJson.Parse(json));

        Assert.Equal("$.steps", ex.JsonPath);
        Assert.StartsWith("invalid workflow JSON", ex.Message);
    }

    [Fact]
    public void Json_null_document_throws_format_exception()
    {
        var ex = Assert.Throws<WorkflowFormatException>(() => WorkflowJson.Parse("null"));

        Assert.Contains("must be a JSON object", ex.Message);
    }

    [Fact]
    public void Unknown_properties_are_ignored_at_every_level()
    {
        const string json = """
            {
              "futureFlag": { "anything": [1, 2, 3] },
              "steps": [
                { "id": "a", "tool": "t", "retries": 3, "meta": { "x": null } }
              ]
            }
            """;

        var definition = WorkflowJson.Parse(json);

        var step = Assert.Single(definition.Steps);
        Assert.Equal("a", step.Id);
        Assert.Equal("t", step.Tool);
    }

    [Fact]
    public void Line_comments_and_trailing_commas_are_accepted()
    {
        const string json = """
            {
              // the only step
              "steps": [
                { "id": "a", "tool": "t", },
              ],
            }
            """;

        var definition = WorkflowJson.Parse(json);

        Assert.Equal("a", Assert.Single(definition.Steps).Id);
    }

    [Fact]
    public void Utf8_overload_produces_the_same_definition_as_the_string_overload()
    {
        const string json = """{"name":"n","steps":[{"id":"a","tool":"t","args":{"k":"v"}},{"id":"b","tool":"u"}]}""";

        var fromString = WorkflowJson.Parse(json);
        var fromBytes = WorkflowJson.Parse(Encoding.UTF8.GetBytes(json).AsSpan());

        Assert.Equal(fromString.Name, fromBytes.Name);
        Assert.Equal(fromString.Version, fromBytes.Version);
        Assert.Equal(
            fromString.Steps.Select(static step => (step.Id, step.Tool, step.After)),
            fromBytes.Steps.Select(static step => (step.Id, step.Tool, step.After)));
    }

    [Fact]
    public void Args_are_absent_when_omitted_and_null_when_declared_null()
    {
        var definition = WorkflowJson.Parse("""{"steps":[{"id":"a","tool":"t"},{"id":"b","tool":"t","args":null}]}""");

        Assert.Equal(JsonValueKind.Undefined, definition.Steps[0].Args.ValueKind);
        Assert.Equal(JsonValueKind.Null, definition.Steps[1].Args.ValueKind);
    }

    [Fact]
    public void Empty_steps_array_parses_to_empty_definition()
    {
        var definition = WorkflowJson.Parse("""{"steps":[]}""");

        Assert.Empty(definition.Steps);
    }
}
