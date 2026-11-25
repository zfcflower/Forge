using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A blank optional string argument means "not given", so the schema's own default applies — the reading every tool here
/// promises and the one a model makes when it fills an unused optional field with <c>""</c> instead of omitting it. These
/// pin the sites that used to answer with an empty name instead: <c>todo add</c> refused a blank <c>after_id</c> as an
/// unknown anchor, <c>todo update</c> handed a blank <c>title</c> to the store's guard, and <c>spec_graph</c> called a
/// blank <c>direction</c>/<c>edge</c> unknown.
/// </summary>
public sealed class BlankOptionalArgumentTests : IDisposable
{
    private const string Session = "session-blank-optionals";

    private readonly string root = Directory.CreateTempSubdirectory("forge-blank-optionals-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    private ToolContext Context => new(Session, root);

    private async Task<ToolResult> CallAsync(ITool tool, string arguments) =>
        await tool.InvokeAsync(new ToolCall("call", tool.Definition.Name, arguments), Context, CancellationToken.None);

    private static string IdOf(ToolResult added)
    {
        Assert.False(added.IsError, added.Content);
        var content = added.Content;
        return content[(content.IndexOf('#') + 1)..content.IndexOf(':')];
    }

    [Fact]
    public async Task Todo_add_with_a_blank_after_id_appends_to_the_end_of_the_queue()
    {
        await using var store = new SqliteTodoStore(Path.Combine(root, "todos"));
        var tool = new TodoTool(store);

        var first = IdOf(await CallAsync(tool, """{"operation":"add","title":"first"}"""));
        var second = IdOf(await CallAsync(tool, """{"operation":"add","title":"second","after_id":""}"""));

        var listing = await CallAsync(tool, """{"operation":"list"}""");
        Assert.False(listing.IsError, listing.Content);
        Assert.Equal(
            [
                "0 groups, 2 items:",
                "Loose (the queue you may promote):",
                $"  1. [pending] {first} first",
                $"  2. [pending] {second} second",
            ],
            listing.Content.Split('\n'));
    }

    [Fact]
    public async Task Todo_update_with_a_blank_title_changes_the_other_fields_and_keeps_the_title()
    {
        await using var store = new SqliteTodoStore(Path.Combine(root, "todos"));
        var tool = new TodoTool(store);
        var id = IdOf(await CallAsync(tool, """{"operation":"add","title":"third"}"""));

        var updated = await CallAsync(tool, $$"""{"operation":"update","id":"{{id}}","title":"","note":"changed"}""");

        Assert.False(updated.IsError, updated.Content);
        Assert.Equal($"Updated #{id}: [ ] #{id} third — changed", updated.Content);
        Assert.Equal("third", (await store.ListAsync(Session, CancellationToken.None)).Single().Title);
    }

    [Fact]
    public async Task Spec_graph_with_a_blank_direction_walks_the_subtree()
    {
        var directory = WriteSpecs();
        var tool = new SpecTools(new SpecGraph(directory)).Graph;

        var blank = await CallAsync(tool, """{"id":"a","direction":""}""");
        var omitted = await CallAsync(tool, """{"id":"a"}""");

        Assert.False(blank.IsError, blank.Content);
        Assert.Equal(omitted.Content, blank.Content);
        Assert.Contains("\"direction\":\"subtree\"", blank.Content);
    }

    [Fact]
    public async Task Spec_graph_with_a_blank_edge_walks_depends_on()
    {
        var directory = WriteSpecs();
        var tool = new SpecTools(new SpecGraph(directory)).Graph;

        var blank = await CallAsync(tool, """{"id":"c","direction":"neighbors","edge":""}""");

        Assert.False(blank.IsError, blank.Content);
        Assert.Contains("\"kind\":\"depends-on\"", blank.Content);
    }

    /// <summary>Three specs on disk: <c>a</c> with child <c>b</c>, and <c>b</c> depending on <c>c</c>.</summary>
    private string WriteSpecs()
    {
        var directory = Path.Combine(root, "specs");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.md"), "---\nid: a\ntype: module-design\ntitle: A\n---\n\n# A\n");
        File.WriteAllText(
            Path.Combine(directory, "b.md"),
            "---\nid: b\ntype: submodule-design\ntitle: B\nparent: a\ndepends-on:\n  - c\n---\n\n# B\n");
        File.WriteAllText(Path.Combine(directory, "c.md"), "---\nid: c\ntype: module-design\ntitle: C\n---\n\n# C\n");
        return directory;
    }
}
