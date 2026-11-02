using System.Text.Json;
using Forge.Abstractions;
using Xunit;

namespace Forge.Workspace.Tests.Specs;

public sealed class SpecToolsTests
{
    private sealed class Sandbox : IDisposable
    {
        private readonly TempDirectory temp = new();

        public Sandbox()
        {
            SpecFixture.Write(Root, "docs/arch.md", SpecFixture.Spec("arch", "architecture-design", "status: active\n"));
            SpecFixture.Write(Root, "docs/module-a.md", SpecFixture.Spec("module-a", "module-design", "parent: arch\ndepends-on: [module-b]\ntags: [storage]\n"));
            SpecFixture.Write(Root, "docs/module-b.md", SpecFixture.Spec("module-b", "module-design", "parent: arch\nreferences: [module-a]\n"));
        }

        public string Root => temp.Path;

        public SpecTools Tools() => new(directory => new SpecGraph(directory));

        public void Dispose() => temp.Dispose();
    }

    private static ToolContext Context(string directory) => new("session-1", directory);

    private static async Task<ToolResult> Invoke(ITool tool, string json, string directory)
    {
        var result = await tool.InvokeAsync(new ToolCall("call-1", tool.Definition.Name, json), Context(directory), CancellationToken.None);
        return result;
    }

    private static JsonElement Json(ToolResult result)
    {
        Assert.False(result.IsError, result.Content);
        return JsonDocument.Parse(result.Content).RootElement.Clone();
    }

    [Fact]
    public void Exposes_seven_tools_in_fixed_order_with_expected_names()
    {
        using var sandbox = new Sandbox();
        var tools = sandbox.Tools();

        Assert.Equal(
            ["spec_grep", "spec_get", "spec_graph", "spec_validate", "spec_create", "spec_update", "spec_delete"],
            tools.All.Select(static tool => tool.Definition.Name));
        Assert.Same(tools.Grep, tools.All[0]);
        Assert.Same(tools.Delete, tools.All[6]);
    }

    [Fact]
    public void Read_tools_are_read_only_and_write_tools_are_writes()
    {
        using var sandbox = new Sandbox();
        var tools = sandbox.Tools();

        Assert.Equal(ToolSideEffect.ReadOnly, tools.Grep.SideEffect);
        Assert.Equal(ToolSideEffect.ReadOnly, tools.Get.SideEffect);
        Assert.Equal(ToolSideEffect.ReadOnly, tools.Graph.SideEffect);
        Assert.Equal(ToolSideEffect.ReadOnly, tools.Validate.SideEffect);
        Assert.Equal(ToolSideEffect.Write, tools.Create.SideEffect);
        Assert.Equal(ToolSideEffect.Write, tools.Update.SideEffect);
        Assert.Equal(ToolSideEffect.Write, tools.Delete.SideEffect);
    }

    [Theory]
    [InlineData("spec_grep", new[] { "pattern" })]
    [InlineData("spec_get", new[] { "id" })]
    [InlineData("spec_graph", new[] { "id" })]
    [InlineData("spec_validate", new string[0])]
    [InlineData("spec_create", new[] { "path", "id", "type", "title" })]
    [InlineData("spec_update", new[] { "id" })]
    [InlineData("spec_delete", new[] { "id" })]
    public void Schemas_parse_as_closed_objects_with_the_required_fields(string name, string[] required)
    {
        using var sandbox = new Sandbox();
        var tool = sandbox.Tools().All.Single(candidate => candidate.Definition.Name == name);

        using var schema = JsonDocument.Parse(tool.Definition.InputSchemaJson);

        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        var actualRequired = schema.RootElement.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(static item => item.GetString()!).ToArray()
            : Array.Empty<string>();
        Assert.Equal(required, actualRequired);
        Assert.False(string.IsNullOrWhiteSpace(tool.Definition.Description));
    }

    [Fact]
    public async Task Grep_returns_paged_matches_in_json_with_total_and_next_offset()
    {
        using var sandbox = new Sandbox();
        var tools = sandbox.Tools();

        var result = await Invoke(tools.Grep, """{"pattern":"Title of","limit":2}""", sandbox.Root);
        var json = Json(result);

        Assert.Equal(2, json.GetProperty("matches").GetArrayLength());
        var first = json.GetProperty("matches")[0];
        Assert.Equal("arch", first.GetProperty("id").GetString());
        Assert.Equal("docs/arch.md", first.GetProperty("path").GetString());
        Assert.Equal(4, first.GetProperty("line").GetInt32());
        Assert.Equal("title: Title of arch", first.GetProperty("text").GetString());
        Assert.Equal(3, json.GetProperty("total").GetInt32());
        Assert.Equal(2, json.GetProperty("next_offset").GetInt32());
    }

    [Fact]
    public async Task Grep_honours_regex_case_flags_and_metadata_filters()
    {
        using var sandbox = new Sandbox();
        var tools = sandbox.Tools();

        var regex = Json(await Invoke(tools.Grep, """{"pattern":"^TITLE: title of module","regex":true,"ignore_case":true,"parent":"arch"}""", sandbox.Root));
        var exact = Json(await Invoke(tools.Grep, """{"pattern":"TITLE: TITLE","ignore_case":false}""", sandbox.Root));
        var tagged = Json(await Invoke(tools.Grep, """{"pattern":"Title of","tag":"storage"}""", sandbox.Root));
        var dependent = Json(await Invoke(tools.Grep, """{"pattern":"Title of","depends_on":"module-b"}""", sandbox.Root));

        Assert.Equal(2, regex.GetProperty("total").GetInt32());
        Assert.Equal(0, exact.GetProperty("total").GetInt32());
        Assert.Equal(1, tagged.GetProperty("total").GetInt32());
        Assert.Equal("module-a", tagged.GetProperty("matches")[0].GetProperty("id").GetString());
        Assert.Equal("module-a", dependent.GetProperty("matches")[0].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, Json(await Invoke(tools.Grep, """{"pattern":"Title of"}""", sandbox.Root)).GetProperty("next_offset").ValueKind);
    }

    [Fact]
    public async Task Grep_reports_invalid_json_missing_pattern_and_bad_regex_as_errors()
    {
        using var sandbox = new Sandbox();
        var tools = sandbox.Tools();

        var malformed = await Invoke(tools.Grep, "{not json", sandbox.Root);
        var missing = await Invoke(tools.Grep, "{}", sandbox.Root);
        var blank = await Invoke(tools.Grep, """{"pattern":"   "}""", sandbox.Root);
        var badRegex = await Invoke(tools.Grep, """{"pattern":"([","regex":true}""", sandbox.Root);

        Assert.True(malformed.IsError);
        Assert.Contains("not valid JSON", malformed.Content, StringComparison.Ordinal);
        Assert.True(missing.IsError);
        Assert.Contains("pattern", missing.Content, StringComparison.Ordinal);
        Assert.True(blank.IsError);
        Assert.True(badRegex.IsError);
        Assert.StartsWith("spec_grep failed:", badRegex.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_arguments_are_treated_as_an_empty_object()
    {
        using var sandbox = new Sandbox();

        var result = await Invoke(new SpecTools(dir => new SpecGraph(dir)).Validate, string.Empty, sandbox.Root);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task Get_returns_frontmatter_forward_and_reverse_links()
    {
        using var sandbox = new Sandbox();

        var json = Json(await Invoke(new SpecTools(dir => new SpecGraph(dir)).Get, """{"id":"module-a"}""", sandbox.Root));

        Assert.Equal("module-a", json.GetProperty("id").GetString());
        Assert.Equal("module-design", json.GetProperty("type").GetString());
        Assert.Equal("docs/module-a.md", json.GetProperty("path").GetString());
        Assert.Equal("arch", json.GetProperty("frontmatter").GetProperty("parent").GetString());
        var forward = json.GetProperty("forward_links");
        Assert.Contains(forward.EnumerateArray(), link => link.GetProperty("kind").GetString() == "depends-on" && link.GetProperty("target").GetString() == "module-b" && link.GetProperty("exists").GetBoolean());
        var reverse = json.GetProperty("reverse_links");
        Assert.Contains(reverse.EnumerateArray(), link => link.GetProperty("kind").GetString() == "references" && link.GetProperty("source").GetString() == "module-b");
    }


    [Fact]
    public async Task Get_renders_list_fields_as_json_arrays_regardless_of_length()
    {
        using var sandbox = new Sandbox();

        var json = Json(await Invoke(new SpecTools(dir => new SpecGraph(dir)).Get, """{"id":"module-a"}""", sandbox.Root));

        Assert.Equal(JsonValueKind.Array, json.GetProperty("frontmatter").GetProperty("depends-on").ValueKind);
    }
    [Fact]
    public async Task Get_unknown_id_is_an_error_result()
    {
        using var sandbox = new Sandbox();

        var result = await Invoke(new SpecTools(dir => new SpecGraph(dir)).Get, """{"id":"nope"}""", sandbox.Root);

        Assert.True(result.IsError);
        Assert.Contains("No spec with id 'nope'", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Graph_defaults_to_subtree_depth_one_and_reports_shape()
    {
        using var sandbox = new Sandbox();

        var json = Json(await Invoke(new SpecTools(dir => new SpecGraph(dir)).Graph, """{"id":"arch"}""", sandbox.Root));

        Assert.Equal("arch", json.GetProperty("root").GetString());
        Assert.Equal("subtree", json.GetProperty("direction").GetString());
        Assert.Equal(1, json.GetProperty("depth").GetInt32());
        Assert.Equal(3, json.GetProperty("nodes").GetArrayLength());
        var edge = json.GetProperty("edges")[0];
        Assert.Equal("parent", edge.GetProperty("kind").GetString());
        Assert.True(edge.GetProperty("reverse").GetBoolean());
    }

    [Fact]
    public async Task Graph_ancestors_and_neighbors_with_edge_names()
    {
        using var sandbox = new Sandbox();
        var tools = new SpecTools(dir => new SpecGraph(dir));

        var ancestors = Json(await Invoke(tools.Graph, """{"id":"module-a","direction":"ancestors","depth":3}""", sandbox.Root));
        var neighbors = Json(await Invoke(tools.Graph, """{"id":"module-a","direction":"neighbors","edge":"depends-on"}""", sandbox.Root));
        var references = Json(await Invoke(tools.Graph, """{"id":"module-a","direction":"neighbors","edge":"references"}""", sandbox.Root));

        Assert.Equal(["module-a", "arch"], ancestors.GetProperty("nodes").EnumerateArray().Select(static node => node.GetProperty("id").GetString()));
        Assert.Equal("ancestors", ancestors.GetProperty("direction").GetString());
        Assert.Equal(2, neighbors.GetProperty("nodes").GetArrayLength());
        Assert.Equal("depends-on", neighbors.GetProperty("edges")[0].GetProperty("kind").GetString());
        Assert.Equal(2, references.GetProperty("nodes").GetArrayLength());
        Assert.Contains(references.GetProperty("edges").EnumerateArray(), static edge => edge.GetProperty("reverse").GetBoolean());
    }

    [Fact]
    public async Task Graph_accepts_underscored_depends_on_and_rejects_unknown_direction_or_edge()
    {
        using var sandbox = new Sandbox();
        var tools = new SpecTools(dir => new SpecGraph(dir));

        var accepted = await Invoke(tools.Graph, """{"id":"module-a","direction":"neighbors","edge":"depends_on"}""", sandbox.Root);
        var badDirection = await Invoke(tools.Graph, """{"id":"module-a","direction":"sideways"}""", sandbox.Root);
        var badEdge = await Invoke(tools.Graph, """{"id":"module-a","direction":"neighbors","edge":"friends"}""", sandbox.Root);

        Assert.False(accepted.IsError);
        Assert.True(badDirection.IsError);
        Assert.Contains("Unknown direction 'sideways'", badDirection.Content, StringComparison.Ordinal);
        Assert.True(badEdge.IsError);
        Assert.Contains("Unknown edge 'friends'", badEdge.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Graph_unknown_id_is_an_error_result()
    {
        using var sandbox = new Sandbox();

        var result = await Invoke(new SpecTools(dir => new SpecGraph(dir)).Graph, """{"id":"ghost"}""", sandbox.Root);

        Assert.True(result.IsError);
        Assert.Contains("No spec with id 'ghost'", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_reports_clean_tree_and_then_dangling_link_codes()
    {
        using var sandbox = new Sandbox();
        var tools = new SpecTools(dir => new SpecGraph(dir));

        var clean = Json(await Invoke(tools.Validate, "{}", sandbox.Root));
        SpecFixture.Write(sandbox.Root, "docs/orphan.md", SpecFixture.Spec("orphan", "module-design", "parent: missing\ndepends-on: [ghost]\n"));
        var broken = Json(await Invoke(tools.Validate, "{}", sandbox.Root));

        Assert.True(clean.GetProperty("valid").GetBoolean());
        Assert.Empty(clean.GetProperty("issues").EnumerateArray());
        Assert.False(broken.GetProperty("valid").GetBoolean());
        var codes = broken.GetProperty("issues").EnumerateArray().Select(static issue => issue.GetProperty("code").GetString()!).OrderBy(static code => code, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "dangling-link", "dangling-parent" }, codes);
        var parentIssue = broken.GetProperty("issues").EnumerateArray().Single(static issue => issue.GetProperty("code").GetString() == "dangling-parent");
        Assert.Equal("orphan", parentIssue.GetProperty("id").GetString());
        Assert.Equal("docs/orphan.md", parentIssue.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Tools_refresh_before_reading_so_new_files_are_visible_immediately()
    {
        using var sandbox = new Sandbox();
        var tools = new SpecTools(dir => new SpecGraph(dir));
        await Invoke(tools.Grep, """{"pattern":"Title of"}""", sandbox.Root);
        SpecFixture.Write(sandbox.Root, "docs/fresh.md", SpecFixture.Spec("fresh", "module-design"));

        var json = Json(await Invoke(tools.Get, """{"id":"fresh"}""", sandbox.Root));

        Assert.Equal("fresh", json.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Create_writes_the_file_and_returns_the_new_view()
    {
        using var sandbox = new Sandbox();
        var tools = new SpecTools(dir => new SpecGraph(dir));

        var json = Json(await Invoke(tools.Create, """
            {"path":"docs/new/task.md","id":"task-new","type":"task-spec","title":"New task","parent":"module-a","depends_on":["module-b"],"tags":["fresh"],"status":"draft"}
            """, sandbox.Root));

        Assert.Equal("task-new", json.GetProperty("id").GetString());
        Assert.Equal("draft", json.GetProperty("status").GetString());
        Assert.Equal("docs/new/task.md", json.GetProperty("path").GetString());
        Assert.True(File.Exists(Path.Combine(sandbox.Root, "docs", "new", "task.md")));
    }


    [Fact]
    public async Task Create_without_status_defaults_to_draft()
    {
        using var sandbox = new Sandbox();

        var json = Json(await Invoke(new SpecTools(dir => new SpecGraph(dir)).Create, """{"path":"docs/draft.md","id":"draft-default","type":"task-spec","title":"Default status"}""", sandbox.Root));

        Assert.Equal("draft", json.GetProperty("status").GetString());
    }
    [Fact]
    public async Task Create_rejects_duplicate_ids_and_paths_outside_the_workspace()
    {
        using var sandbox = new Sandbox();
        var tools = new SpecTools(dir => new SpecGraph(dir));

        var duplicate = await Invoke(tools.Create, """{"path":"docs/dup.md","id":"arch","type":"t","title":"Dup"}""", sandbox.Root);
        var escaping = await Invoke(tools.Create, """{"path":"../outside.md","id":"outside","type":"t","title":"Out"}""", sandbox.Root);

        Assert.True(duplicate.IsError);
        Assert.Contains("already exists", duplicate.Content, StringComparison.Ordinal);
        Assert.True(escaping.IsError);
        Assert.Contains("outside the workspace", escaping.Content, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(sandbox.Root)!, "outside.md")));
    }

    [Fact]
    public async Task Create_requires_non_empty_strings()
    {
        using var sandbox = new Sandbox();

        var result = await Invoke(new SpecTools(dir => new SpecGraph(dir)).Create, """{"path":"x.md","id":"","type":"t","title":"T"}""", sandbox.Root);

        Assert.True(result.IsError);
        Assert.Contains("'id' must be a non-empty string", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_applies_set_add_remove_and_keeps_the_body()
    {
        using var sandbox = new Sandbox();
        var tools = new SpecTools(dir => new SpecGraph(dir));

        var json = Json(await Invoke(tools.Update, """
            {"id":"module-a","set":{"status":"done","title":null},"add":{"tags":["cache"]},"remove":{"depends_on":["module-b"]}}
            """, sandbox.Root));

        Assert.Equal("done", json.GetProperty("status").GetString());
        Assert.Null(json.GetProperty("title").GetString());
        var tags = json.GetProperty("frontmatter").GetProperty("tags").EnumerateArray().Select(static tag => tag.GetString()!).ToArray();
        Assert.Equal(new[] { "storage", "cache" }, tags);
        Assert.EndsWith("\n# Title\n", await File.ReadAllTextAsync(Path.Combine(sandbox.Root, "docs", "module-a.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_rejects_non_string_scalar_values_and_non_array_lists()
    {
        using var sandbox = new Sandbox();
        var tools = new SpecTools(dir => new SpecGraph(dir));

        var numeric = await Invoke(tools.Update, """{"id":"module-a","set":{"priority":3}}""", sandbox.Root);
        var notArray = await Invoke(tools.Update, """{"id":"module-a","add":{"tags":"cache"}}""", sandbox.Root);
        var mixedArray = await Invoke(tools.Update, """{"id":"module-a","add":{"tags":["a",1]}}""", sandbox.Root);

        Assert.True(numeric.IsError);
        Assert.Contains("must be a string or null", numeric.Content, StringComparison.Ordinal);
        Assert.True(notArray.IsError);
        Assert.True(mixedArray.IsError);
    }

    [Fact]
    public async Task Update_unknown_id_is_an_error_result()
    {
        using var sandbox = new Sandbox();

        var result = await Invoke(new SpecTools(dir => new SpecGraph(dir)).Update, """{"id":"ghost","set":{"status":"x"}}""", sandbox.Root);

        Assert.True(result.IsError);
        Assert.Contains("No spec with id 'ghost'", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_removes_the_file_and_reports_the_id()
    {
        using var sandbox = new Sandbox();
        var tools = new SpecTools(dir => new SpecGraph(dir));

        var json = Json(await Invoke(tools.Delete, """{"id":"module-b"}""", sandbox.Root));

        Assert.Equal("module-b", json.GetProperty("deleted").GetString());
        Assert.False(File.Exists(Path.Combine(sandbox.Root, "docs", "module-b.md")));
        var missing = await Invoke(tools.Delete, """{"id":"module-b"}""", sandbox.Root);
        Assert.True(missing.IsError);
    }

    [Fact]
    public async Task Context_working_directory_selects_the_workspace_graph()
    {
        using var first = new Sandbox();
        using var second = new TempDirectory();
        SpecFixture.Write(second.Path, "solo.md", SpecFixture.Spec("solo", "module-design"));
        var tools = first.Tools();

        var inFirst = Json(await Invoke(tools.Grep, """{"pattern":"Title of"}""", first.Root));
        var inSecond = Json(await Invoke(tools.Grep, """{"pattern":"Title of"}""", second.Path));

        Assert.Equal(3, inFirst.GetProperty("total").GetInt32());
        Assert.Equal(1, inSecond.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Fixed_graph_constructor_ignores_the_working_directory()
    {
        using var sandbox = new Sandbox();
        var graph = new SpecGraph(sandbox.Root);
        var tools = new SpecTools(graph);

        var json = Json(await Invoke(tools.Get, """{"id":"arch"}""", "/definitely/not/a/workspace"));

        Assert.Equal("arch", json.GetProperty("id").GetString());
    }

    [Fact]
    public void Null_graph_and_null_factory_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new SpecTools((SpecGraph)null!));
        Assert.Throws<ArgumentNullException>(() => new SpecTools((Func<string, SpecGraph>)null!));
    }

    [Fact]
    public async Task Null_call_or_context_is_rejected()
    {
        using var sandbox = new Sandbox();
        var tool = new SpecTools(dir => new SpecGraph(dir)).Validate;

        await Assert.ThrowsAsync<ArgumentNullException>(() => tool.InvokeAsync(null!, Context(sandbox.Root), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => tool.InvokeAsync(new ToolCall("c", "spec_validate", "{}"), null!, CancellationToken.None).AsTask());
    }
}
