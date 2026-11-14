using Forge.App.Localization;
using Forge.App.ViewModels.Settings;
using Forge.Mcp;
using Xunit;

namespace Forge.App.Tests.Mcp;

/// <summary>
/// The editor's shadow hint: saving a name the other file already lists is legitimate (a project entry wins by name) but has to
/// be said, or the edit looks applied while the other file keeps winning. Plain view model, no UI and no file.
/// </summary>
public sealed class McpServerEditorShadowTests
{
    private const string Global = "/tmp/forge-global/mcp.json";
    private const string Project = "/tmp/forge-work/.forge/mcp.json";

    private static McpServerEntry Entry(string name, string? sourcePath = null) =>
        new() { Name = name, Command = "cmd", SourcePath = sourcePath };

    private static McpServerEditorViewModel Editor(string name, string? sourcePath, IReadOnlyCollection<string> other, bool isNew = false) =>
        new(Entry(name, sourcePath), Global, Project, isNew, other);

    [Fact]
    public void An_unchanged_name_never_warns_although_the_other_file_lists_it()
    {
        var editor = Editor("shared", Project, ["shared"]);

        Assert.False(editor.HasShadowWarning);
        Assert.Empty(editor.ShadowWarningText);
    }

    [Fact]
    public void Renaming_to_a_name_the_other_file_lists_warns_with_the_side_that_wins()
    {
        // The entry lives in the global file, so the target is global and the other file — the project one — would shadow it.
        var editor = Editor("fresh", Global, ["shared"]);

        editor.Name = "shared";

        Assert.True(editor.HasShadowWarning);
        Assert.Equal(Loc.Instance[McpSettingsStrings.EditorShadowedByGlobalKey], editor.ShadowWarningText);
    }

    [Fact]
    public void Saving_into_the_project_file_under_a_name_the_global_file_has_says_this_entry_shadows_it()
    {
        var editor = Editor("fresh", Global, ["shared"]);
        editor.SaveToProject = true;

        editor.Name = "shared";

        Assert.True(editor.HasShadowWarning);
        Assert.Equal(Loc.Instance[McpSettingsStrings.EditorShadowedByProjectKey], editor.ShadowWarningText);
    }

    [Fact]
    public void A_name_the_other_file_does_not_list_never_warns()
    {
        var editor = Editor("fresh", Global, ["other"]);

        editor.Name = "brand-new";

        Assert.False(editor.HasShadowWarning);
    }
}
