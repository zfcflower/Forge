using Forge.Workflows.Tests.Support;
using Xunit;

namespace Forge.Workflows.Tests;

public sealed class WorkflowValidationTests
{
    [Fact]
    public void Valid_definition_has_no_diagnostics()
    {
        const string json = """
            {"steps":[
              {"id":"fetch","tool":"read","after":[]},
              {"id":"use","tool":"write","args":{"v":{"$ref":"$.steps.fetch.json.items[0].name"}}}
            ]}
            """;

        var diagnostics = WorkflowValidator.Validate(WorkflowJson.Parse(json));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Empty_definition_is_valid()
    {
        Assert.Empty(WorkflowValidator.Validate(new WorkflowDefinition()));
        Assert.Empty(WorkflowValidator.Validate(WorkflowJson.Parse("""{"steps":[]}""")));
    }

    [Fact]
    public void Duplicate_step_id_is_reported_against_the_second_occurrence()
    {
        const string json = """{"steps":[{"id":"a","tool":"t","after":[]},{"id":"a","tool":"t","after":[]}]}""";

        var diagnostic = Assert.Single(WorkflowValidator.Validate(WorkflowJson.Parse(json)));

        Assert.Equal(WorkflowDiagnosticCode.DuplicateStepId, diagnostic.Code);
        Assert.Equal(WorkflowDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(1, diagnostic.StepIndex);
        Assert.Equal("a", diagnostic.StepId);
        Assert.Equal("step id 'a' is already used by steps[0]", diagnostic.Message);
    }

    [Fact]
    public void Missing_tool_names_the_step()
    {
        var diagnostic = Assert.Single(WorkflowValidator.Validate(WorkflowJson.Parse("""{"steps":[{"id":"a","after":[]}]}""")));

        Assert.Equal(WorkflowDiagnosticCode.MissingTool, diagnostic.Code);
        Assert.Equal(0, diagnostic.StepIndex);
        Assert.Equal("a", diagnostic.StepId);
        Assert.Equal("step 'a' has no tool", diagnostic.Message);
    }

    [Fact]
    public void Missing_step_id_is_reported_with_its_index()
    {
        var diagnostics = WorkflowValidator.Validate(WorkflowJson.Parse("""{"steps":[{"tool":"t","after":[]}]}"""));

        var diagnostic = Assert.Single(diagnostics, static d => d.Code == WorkflowDiagnosticCode.MissingStepId);
        Assert.Equal(0, diagnostic.StepIndex);
        Assert.Null(diagnostic.StepId);
        Assert.Equal("steps[0] has no id", diagnostic.Message);
    }

    [Fact]
    public void Unknown_after_entry_names_the_missing_step()
    {
        const string json = """{"steps":[{"id":"a","tool":"t","after":["ghost"]}]}""";

        var diagnostic = Assert.Single(WorkflowValidator.Validate(WorkflowJson.Parse(json)));

        Assert.Equal(WorkflowDiagnosticCode.UnknownDependency, diagnostic.Code);
        Assert.Equal("a", diagnostic.StepId);
        Assert.Equal("step 'a' depends on unknown step 'ghost'", diagnostic.Message);
    }

    [Fact]
    public void Empty_after_entry_is_invalid()
    {
        const string json = """{"steps":[{"id":"a","tool":"t","after":[""]}]}""";

        var diagnostic = Assert.Single(WorkflowValidator.Validate(WorkflowJson.Parse(json)));

        Assert.Equal(WorkflowDiagnosticCode.InvalidAfterEntry, diagnostic.Code);
    }

    [Fact]
    public void Step_listing_itself_in_after_is_a_self_dependency()
    {
        const string json = """{"steps":[{"id":"a","tool":"t","after":["a"]}]}""";

        var diagnostic = Assert.Single(WorkflowValidator.Validate(WorkflowJson.Parse(json)));

        Assert.Equal(WorkflowDiagnosticCode.SelfDependency, diagnostic.Code);
        Assert.Equal("step 'a' lists itself in after", diagnostic.Message);
    }

    [Fact]
    public void Two_step_cycle_is_reported_once_with_its_path()
    {
        const string json = """
            {"steps":[
              {"id":"a","tool":"t","after":["b"]},
              {"id":"b","tool":"t","after":["a"]}
            ]}
            """;

        var diagnostic = Assert.Single(WorkflowValidator.Validate(WorkflowJson.Parse(json)));

        Assert.Equal(WorkflowDiagnosticCode.DependencyCycle, diagnostic.Code);
        Assert.Equal("dependency cycle: a -> b -> a", diagnostic.Message);
        Assert.Equal(0, diagnostic.StepIndex);
    }

    [Fact]
    public void Three_step_cycle_renders_the_dependency_path()
    {
        const string json = """
            {"steps":[
              {"id":"a","tool":"t","after":["c"]},
              {"id":"b","tool":"t","after":["a"]},
              {"id":"c","tool":"t","after":["b"]}
            ]}
            """;

        var diagnostic = Assert.Single(WorkflowValidator.Validate(WorkflowJson.Parse(json)));

        Assert.Equal("dependency cycle: a -> c -> b -> a", diagnostic.Message);
    }

    [Fact]
    public void Cycle_created_by_the_implicit_previous_step_edge_is_detected()
    {
        const string json = """
            {"steps":[
              {"id":"x","tool":"t","after":[]},
              {"id":"second","tool":"t","after":["first"]},
              {"id":"first","tool":"t"}
            ]}
            """;

        var diagnostic = Assert.Single(WorkflowValidator.Validate(WorkflowJson.Parse(json)));

        Assert.Equal(WorkflowDiagnosticCode.DependencyCycle, diagnostic.Code);
        Assert.Equal("dependency cycle: second -> first -> second", diagnostic.Message);
        Assert.Equal(1, diagnostic.StepIndex);
    }

    [Theory]
    [InlineData("$.steps.a")]
    [InlineData("$.foo.a.json")]
    [InlineData("$.steps.a.bogus")]
    [InlineData("$.steps.a.json.")]
    [InlineData("$.steps.a.json[-1]")]
    [InlineData("$.steps.a.json[")]
    [InlineData("$.steps.a.json[99999999999]")]
    [InlineData("$.steps.a.json?x")]
    [InlineData("$.steps..json")]
    public void Malformed_binding_path_is_reported_on_the_consumer(string path)
    {
        var definition = WorkflowJson.Parse(Consumer("""{"$ref":"__PATH__"}""".Replace("__PATH__", path, StringComparison.Ordinal)));

        var diagnostic = Assert.Single(WorkflowValidator.Validate(definition));

        Assert.Equal(WorkflowDiagnosticCode.InvalidBindingPath, diagnostic.Code);
        Assert.Equal("use", diagnostic.StepId);
        Assert.Equal(1, diagnostic.StepIndex);
    }

    [Theory]
    [InlineData("""{"$foo":1}""")]
    [InlineData("""{"$ref":5}""")]
    public void Unknown_or_malformed_directive_is_reported(string argument)
    {
        var definition = WorkflowJson.Parse(Consumer(argument));

        var diagnostic = Assert.Single(WorkflowValidator.Validate(definition));

        Assert.Equal(WorkflowDiagnosticCode.InvalidBindingDirective, diagnostic.Code);
        Assert.Equal("use", diagnostic.StepId);
    }

    [Fact]
    public void Binding_to_an_unknown_step_is_reported()
    {
        var definition = WorkflowJson.Parse(Consumer("""{"$ref":"$.steps.ghost.json"}"""));

        var diagnostic = Assert.Single(WorkflowValidator.Validate(definition));

        Assert.Equal(WorkflowDiagnosticCode.UnknownBindingStep, diagnostic.Code);
        Assert.Equal("step 'use': binding '$.steps.ghost.json' names unknown step 'ghost'", diagnostic.Message);
    }

    [Fact]
    public void Binding_to_a_parallel_step_is_rejected_and_the_message_suggests_after()
    {
        const string json = """
            {"steps":[
              {"id":"src","tool":"t","after":[]},
              {"id":"use","tool":"t","after":[],"args":{"v":{"$ref":"$.steps.src.content"}}}
            ]}
            """;

        var diagnostic = Assert.Single(WorkflowValidator.Validate(WorkflowJson.Parse(json)));

        Assert.Equal(WorkflowDiagnosticCode.BindingOnNonDependency, diagnostic.Code);
        Assert.Equal("use", diagnostic.StepId);
        Assert.Contains("add 'src' to its after list", diagnostic.Message);
    }

    [Fact]
    public void Binding_to_a_transitive_dependency_is_allowed()
    {
        const string json = """
            {"steps":[
              {"id":"a","tool":"t","after":[]},
              {"id":"b","tool":"t","after":["a"]},
              {"id":"c","tool":"t","after":["b"],"args":{"v":{"$ref":"$.steps.a.json.x?"}}}
            ]}
            """;

        Assert.Empty(WorkflowValidator.Validate(WorkflowJson.Parse(json)));
    }

    [Fact]
    public void Binding_to_the_step_itself_is_rejected()
    {
        const string json = """{"steps":[{"id":"a","tool":"t","after":[],"args":{"v":{"$ref":"$.steps.a.content"}}}]}""";

        var diagnostic = Assert.Single(WorkflowValidator.Validate(WorkflowJson.Parse(json)));

        Assert.Equal(WorkflowDiagnosticCode.BindingOnNonDependency, diagnostic.Code);
        Assert.Contains("reads its own output", diagnostic.Message);
    }

    [Fact]
    public void Literal_directive_is_not_validated_as_a_binding()
    {
        const string json = """{"steps":[{"id":"a","tool":"t","after":[],"args":{"v":{"$literal":{"$ref":"$.steps.ghost.json"}}}}]}""";

        Assert.Empty(WorkflowValidator.Validate(WorkflowJson.Parse(json)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void Unsupported_version_is_a_document_level_error(int version)
    {
        var definition = new WorkflowDefinition
        {
            Version = version,
            Steps = [new WorkflowStep { Id = "a", Tool = "t", After = [] }],
        };

        var diagnostics = WorkflowValidator.Validate(definition);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(WorkflowDiagnosticCode.UnsupportedVersion, diagnostic.Code);
        Assert.Equal(-1, diagnostic.StepIndex);
        Assert.Null(diagnostic.StepId);
    }

    [Fact]
    public async Task Unsupported_version_makes_the_run_invalid_and_calls_no_tool()
    {
        const string json = """{"version":2,"steps":[{"id":"a","tool":"t"},{"id":"b","tool":"t"}]}""";
        var host = new RecordingHost().Respond("t", "never");

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(WorkflowRunStatus.Invalid, result.Status);
        Assert.False(result.Succeeded);
        Assert.Empty(host.Calls);
        Assert.Contains(result.Diagnostics, static d => d.Code == WorkflowDiagnosticCode.UnsupportedVersion);
        Assert.Equal(new[] { "a", "b" }, result.NeverRanStepIds);
        Assert.All(result.Steps, static step => Assert.Equal(StepStatus.NotRun, step.Status));
        Assert.StartsWith("the workflow definition is invalid: workflow version 2 is not supported", result.Error);
    }

    [Fact]
    public void Document_level_diagnostics_come_before_step_diagnostics()
    {
        var definition = new WorkflowDefinition
        {
            Version = 2,
            Steps = [new WorkflowStep { Id = "a" }],
        };

        var diagnostics = WorkflowValidator.Validate(definition);

        Assert.Equal(WorkflowDiagnosticCode.UnsupportedVersion, diagnostics[0].Code);
        Assert.Equal(WorkflowDiagnosticCode.MissingTool, diagnostics[1].Code);
    }

    [Fact]
    public void Repeated_validation_returns_identical_diagnostics()
    {
        const string json = """
            {"steps":[
              {"id":"a","tool":"t","after":["ghost","a"]},
              {"id":"a","after":["b"]},
              {"id":"b","tool":"t","after":["c"],"args":{"v":{"$ref":"$.steps.zz.json"}}},
              {"id":"c","tool":"t","after":["b"]}
            ]}
            """;
        var definition = WorkflowJson.Parse(json);

        var first = WorkflowValidator.Validate(definition);
        var second = WorkflowValidator.Validate(definition);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Diagnostics_are_grouped_per_step_in_document_order()
    {
        const string json = """
            {"steps":[
              {"id":"a"},
              {"id":"a","tool":"t"},
              {"tool":"t"}
            ]}
            """;

        var diagnostics = WorkflowValidator.Validate(WorkflowJson.Parse(json));

        Assert.Equal(
            new[]
            {
                WorkflowDiagnosticCode.MissingTool,
                WorkflowDiagnosticCode.DuplicateStepId,
                WorkflowDiagnosticCode.MissingStepId,
            },
            diagnostics.Select(static d => d.Code).ToArray());
    }

    [Fact]
    public void Validate_never_throws_for_an_in_memory_definition_full_of_errors()
    {
        var definition = new WorkflowDefinition
        {
            Steps =
            [
                null!,
                new WorkflowStep { Id = "a", After = ["a", "ghost", ""] },
                new WorkflowStep { Id = "a", Tool = "t" },
            ],
        };

        var diagnostics = WorkflowValidator.Validate(definition);

        var codes = diagnostics.Select(static d => d.Code).ToHashSet();
        Assert.Contains(WorkflowDiagnosticCode.MissingStepId, codes);
        Assert.Contains(WorkflowDiagnosticCode.MissingTool, codes);
        Assert.Contains(WorkflowDiagnosticCode.DuplicateStepId, codes);
        Assert.Contains(WorkflowDiagnosticCode.SelfDependency, codes);
        Assert.Contains(WorkflowDiagnosticCode.UnknownDependency, codes);
        Assert.Contains(WorkflowDiagnosticCode.InvalidAfterEntry, codes);
        Assert.All(diagnostics, static d => Assert.Equal(WorkflowDiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public async Task Run_of_an_invalid_in_memory_definition_reports_invalid_without_calling_tools()
    {
        var definition = new WorkflowDefinition
        {
            Steps = [new WorkflowStep { Id = "a", Tool = "t", After = [] }, new WorkflowStep { Id = "a", After = [] }],
        };
        var host = new RecordingHost().Respond("t", "never");

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(definition, host));

        Assert.Equal(WorkflowRunStatus.Invalid, result.Status);
        Assert.Empty(host.Calls);
        Assert.Contains(result.Diagnostics, static d => d.Code == WorkflowDiagnosticCode.MissingTool);
        Assert.Equal(new[] { "a", "a" }, result.Steps.Select(static step => step.Id).ToArray());
    }

    private static string Consumer(string argument) =>
        """{"steps":[{"id":"src","tool":"t","after":[]},{"id":"use","tool":"t","args":{"v":ARG}}]}""".Replace("ARG", argument, StringComparison.Ordinal);
}
