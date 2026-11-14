using Forge.App.Localization;
using Forge.App.ViewModels.Settings;
using Forge.Mcp;
using Xunit;

namespace Forge.App.Tests.Mcp;

/// <summary>
/// The editor's shadow hint: saving a name the other file already lists is legitimate (a project entry wins by name) but has to
/// be said, or the edit looks applied while the other file keeps winning. The list that matters is the one in the file this save
/// does <em>not</em> write, so it flips with the target. Plain view model: no UI and no file.
/// </summary>
public sealed class McpServerEditorShadowTests
{
    private const string Global = "/tmp/forge-global/mcp.json";
    private const string Project = "/tmp/forge-work/.forge/mcp.json";

    private static McpServerEntry Entry(string name, string? sourcePath = null) =>
        new() { Name = name, Command = "cmd", SourcePath = sourcePath };

    private static McpServerEditorViewModel Editor(
        string name,
        string? sourcePath,
        IReadOnlyCollection<string> globalNames,
        IReadOnlyCollection<string> projectNames,
        string? projectPath = Project,
        bool isNew = false) =>
        new(Entry(name, sourcePath), Global, projectPath, isNew, globalNames, projectNames);

    [Fact]
    public void An_unchanged_name_never_warns_although_the_other_file_lists_it()
    {
        var editor = Editor("shared", Global, ["shared"], []);

        Assert.False(editor.HasShadowWarning);
        Assert.Empty(editor.ShadowWarningText);
    }

    [Fact]
    public void Saving_into_the_global_file_under_a_name_the_project_file_has_says_the_project_entry_wins()
    {
        var editor = Editor("fresh", Global, [], ["shared"]);

        editor.Name = "shared";

        Assert.True(editor.HasShadowWarning);
        Assert.Equal(Loc.Instance[McpSettingsStrings.EditorShadowedByGlobalKey], editor.ShadowWarningText);
    }

    [Fact]
    public void Saving_into_the_project_file_under_a_name_the_global_file_has_says_this_entry_shadows_it()
    {
        var editor = Editor("fresh", Global, ["shared"], ["shared"]);
        editor.SaveToProject = true;

        editor.Name = "shared";

        Assert.True(editor.HasShadowWarning);
        Assert.Equal(Loc.Instance[McpSettingsStrings.EditorShadowedByProjectKey], editor.ShadowWarningText);
    }

    [Fact]
    public void Moving_to_the_project_file_collides_with_the_global_file_not_with_its_own()
    {
        // The entry lives in the global file under `fresh`; the project file already has `taken`. Renaming while moving into the
        // project file collides with what is left behind in the global file, which has no `taken`.
        var editor = Editor("fresh", Global, ["fresh"], ["taken"]);
        editor.SaveToProject = true;

        editor.Name = "taken";

        Assert.False(editor.HasShadowWarning);
    }

    [Fact]
    public void Without_a_project_file_a_new_entry_never_warns_about_one()
    {
        var editor = Editor("fresh", null, [], [], projectPath: null, isNew: true);

        editor.Name = "anything";

        Assert.False(editor.HasShadowWarning);
    }

    [Fact]
    public void A_name_neither_file_lists_never_warns()
    {
        var editor = Editor("fresh", Global, ["one"], ["two"]);

        editor.Name = "brand-new";

        Assert.False(editor.HasShadowWarning);
    }
}
