using Forge.Abstractions;
using Forge.App.Prompts;
using Forge.App.Settings.Prompts;
using Xunit;

namespace Forge.App.Tests.Settings;

/// <summary>
/// The prompts settings section against a real <see cref="PromptTemplateService"/> over a temp user directory and a temp
/// workspace: listing, badges, filter, copy / save / delete and the new-template rules. No Avalonia is involved; the view
/// model awaits the thread pool and applies results on the awaiting continuation.
/// </summary>
public sealed class PromptsSettingsViewModelTests : IDisposable
{
    private const string ValidCommand = "---\ndescription: mine\n---\nDo the thing.\n";

    private readonly string root = TestTempFiles.NewDirectory("forge-prompts-settings-");
    private readonly string user;
    private readonly string workspace;
    private readonly PromptTemplateService service;

    public PromptsSettingsViewModelTests()
    {
        user = Path.Combine(root, "user");
        workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(workspace);
        service = new PromptTemplateService(user);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private PromptsSettingsViewModel Create(string? ws) => new(service, () => ws, TimeSpan.Zero);

    private void Write(string scopeRoot, string kindDirectory, string id, string text)
    {
        var directory = Path.Combine(scopeRoot, ".forge", kindDirectory);
        if (scopeRoot == user)
        {
            directory = Path.Combine(user, kindDirectory);
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".md"), text);
    }

    private static IEnumerable<PromptRowViewModel> Rows(PromptsSettingsViewModel vm) => vm.Groups.SelectMany(group => group.Rows);

    private static PromptRowViewModel Row(PromptsSettingsViewModel vm, PromptTemplateKind kind, PromptTemplateScope scope, string id) =>
        Rows(vm).Single(row => row.Ref == new PromptTemplateRef(kind, scope, id));

    private string GlobalPath(string id) => Path.Combine(user, "commands", id + ".md");

    [Fact]
    public async Task The_list_groups_commands_agents_and_prompts_in_that_order()
    {
        var vm = Create(workspace);
        await vm.ReloadAsync();

        Assert.Equal([PromptTemplateKind.Command, PromptTemplateKind.Agent, PromptTemplateKind.Slot], vm.Groups.Select(group => group.Kind).ToArray());
        Assert.All(vm.Groups, group => Assert.All(group.Rows, row => Assert.Equal(group.Kind, row.Kind)));
        Assert.True(vm.HasRows);
        Assert.False(vm.IsEmpty);
        Assert.True(Row(vm, PromptTemplateKind.Command, PromptTemplateScope.BuiltIn, "review").IsBuiltIn);
        Assert.True(Row(vm, PromptTemplateKind.Agent, PromptTemplateScope.BuiltIn, "scout").IsAgentKind);
        Assert.True(Row(vm, PromptTemplateKind.Slot, PromptTemplateScope.BuiltIn, "system").IsSlotKind);
    }

    [Fact]
    public async Task Rows_carry_source_and_extra_badges()
    {
        // Global override of a built-in, a project file shadowing that global file, an append file, an invalid file, a plain custom one.
        Write(user, "commands", "review", ValidCommand);
        Write(workspace, "commands", "review", ValidCommand);
        Write(user, "commands", "extra", "---\nmode: append\n---\nMore.\n");
        Write(user, "commands", "broken", "---\ndescription: never closed\nBody\n");
        Write(user, "commands", "plain", ValidCommand);

        var vm = Create(workspace);
        await vm.ReloadAsync();

        var builtIn = Row(vm, PromptTemplateKind.Command, PromptTemplateScope.BuiltIn, "review");
        var global = Row(vm, PromptTemplateKind.Command, PromptTemplateScope.Global, "review");
        var project = Row(vm, PromptTemplateKind.Command, PromptTemplateScope.Project, "review");
        Assert.True(builtIn.IsBuiltIn);
        Assert.False(builtIn.Overrides);
        Assert.True(builtIn.IsShadowed);
        Assert.True(builtIn.RowOpacity < 1.0);

        Assert.True(global.IsGlobal);
        Assert.True(global.Overrides);
        Assert.True(global.IsShadowed);
        Assert.False(global.IsBuiltIn);
        Assert.Equal(GlobalPath("review"), global.ToolTipText);

        Assert.True(project.IsProject);
        Assert.True(project.Overrides);
        Assert.False(project.IsShadowed);
        Assert.Equal(1.0, project.RowOpacity);

        var append = Row(vm, PromptTemplateKind.Command, PromptTemplateScope.Global, "extra");
        Assert.True(append.IsAppend);
        Assert.False(append.Overrides);

        var broken = Row(vm, PromptTemplateKind.Command, PromptTemplateScope.Global, "broken");
        Assert.True(broken.IsInvalid);
        Assert.NotEmpty(broken.ProblemsText);

        var plain = Row(vm, PromptTemplateKind.Command, PromptTemplateScope.Global, "plain");
        Assert.False(plain.IsInvalid);
        Assert.False(plain.IsAppend);
        Assert.False(plain.Overrides);
        Assert.False(plain.IsShadowed);
    }

    [Fact]
    public async Task The_filter_keeps_built_ins_or_custom_files_and_drops_groups_that_become_empty()
    {
        Write(user, "commands", "plain", ValidCommand);
        var vm = Create(workspace);
        await vm.ReloadAsync();

        vm.IsFilterCustom = true;
        Assert.Equal(PromptFilter.Custom, vm.Filter);
        Assert.All(Rows(vm), row => Assert.False(row.IsBuiltIn));
        Assert.Equal([PromptTemplateKind.Command], vm.Groups.Select(group => group.Kind).ToArray());
        Assert.Single(Rows(vm));

        vm.IsFilterBuiltIn = true;
        Assert.All(Rows(vm), row => Assert.True(row.IsBuiltIn));
        Assert.Equal(3, vm.Groups.Count);
        Assert.False(vm.IsFilterCustom);

        vm.IsFilterAll = true;
        Assert.Contains(Rows(vm), row => !row.IsBuiltIn);
        Assert.Contains(Rows(vm), row => row.IsBuiltIn);
    }

    [Fact]
    public async Task The_custom_filter_is_empty_without_any_file()
    {
        var vm = Create(workspace);
        await vm.ReloadAsync();

        vm.IsFilterCustom = true;

        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Groups);
    }

    [Fact]
    public async Task Selecting_a_built_in_opens_it_read_only_with_its_text()
    {
        var vm = Create(workspace);
        await vm.ReloadAsync();

        await vm.SelectAsync(Row(vm, PromptTemplateKind.Command, PromptTemplateScope.BuiltIn, "review"));

        var editor = Assert.IsType<PromptEditorViewModel>(vm.Editor);
        Assert.True(editor.IsBuiltIn);
        Assert.True(editor.IsReadOnly);
        Assert.NotEmpty(editor.Text);
        Assert.Null(editor.Revision);
        var original = editor.Text;
        editor.Text = "changed";
        Assert.Equal(original, editor.Text);
        Assert.False(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Copying_a_built_in_to_global_creates_the_file_opens_it_and_shadows_the_built_in()
    {
        var vm = Create(workspace);
        await vm.ReloadAsync();
        var builtIn = Row(vm, PromptTemplateKind.Command, PromptTemplateScope.BuiltIn, "review");
        await vm.SelectAsync(builtIn);
        var builtInText = vm.Editor!.Text;

        await vm.Editor.CopyAsync(PromptTemplateScope.Global);

        Assert.True(File.Exists(GlobalPath("review")));
        Assert.Equal(builtInText.ReplaceLineEndings("\n"), File.ReadAllText(GlobalPath("review")).ReplaceLineEndings("\n"));
        var editor = Assert.IsType<PromptEditorViewModel>(vm.Editor);
        Assert.True(editor.IsCustom);
        Assert.Equal(new PromptTemplateRef(PromptTemplateKind.Command, PromptTemplateScope.Global, "review"), editor.Ref);
        Assert.NotNull(editor.Revision);
        Assert.True(editor.HasStatus);
        Assert.False(editor.StatusIsError);
        Assert.True(Row(vm, PromptTemplateKind.Command, PromptTemplateScope.BuiltIn, "review").IsShadowed);
        Assert.True(Row(vm, PromptTemplateKind.Command, PromptTemplateScope.Global, "review").IsSelected);
    }

    [Fact]
    public async Task Copying_to_global_twice_reports_that_the_scope_already_has_it()
    {
        Write(user, "commands", "review", ValidCommand);
        var vm = Create(workspace);
        await vm.ReloadAsync();
        await vm.SelectAsync(Row(vm, PromptTemplateKind.Command, PromptTemplateScope.BuiltIn, "review"));

        await vm.Editor!.CopyAsync(PromptTemplateScope.Global);

        Assert.True(vm.Editor.IsBuiltIn);
        Assert.True(vm.Editor.StatusIsError);
        Assert.Equal(ValidCommand, File.ReadAllText(GlobalPath("review")).ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Copying_a_slot_built_in_is_allowed_because_that_is_how_it_is_overridden()
    {
        var vm = Create(workspace);
        await vm.ReloadAsync();
        await vm.SelectAsync(Row(vm, PromptTemplateKind.Slot, PromptTemplateScope.BuiltIn, "system"));

        await vm.Editor!.CopyAsync(PromptTemplateScope.Global);

        Assert.True(File.Exists(Path.Combine(user, "prompts", "system.md")));
        Assert.True(vm.Editor.IsCustom);
        Assert.True(Row(vm, PromptTemplateKind.Slot, PromptTemplateScope.BuiltIn, "system").IsShadowed);
    }

    [Fact]
    public async Task Copying_to_project_needs_a_workspace()
    {
        var withoutWorkspace = Create(null);
        await withoutWorkspace.ReloadAsync();
        await withoutWorkspace.SelectAsync(Row(withoutWorkspace, PromptTemplateKind.Command, PromptTemplateScope.BuiltIn, "review"));
        Assert.False(withoutWorkspace.Editor!.HasWorkspace);
        Assert.False(withoutWorkspace.Editor.CopyProjectCommand.CanExecute(null));
        Assert.True(withoutWorkspace.Editor.CopyGlobalCommand.CanExecute(null));

        var withWorkspace = Create(workspace);
        await withWorkspace.ReloadAsync();
        await withWorkspace.SelectAsync(Row(withWorkspace, PromptTemplateKind.Command, PromptTemplateScope.BuiltIn, "review"));
        Assert.True(withWorkspace.Editor!.CopyProjectCommand.CanExecute(null));

        await withWorkspace.Editor.CopyAsync(PromptTemplateScope.Project);

        Assert.True(File.Exists(Path.Combine(workspace, ".forge", "commands", "review.md")));
        Assert.True(Row(withWorkspace, PromptTemplateKind.Command, PromptTemplateScope.Project, "review").IsSelected);
    }

    [Fact]
    public async Task Saving_writes_the_file_and_the_next_save_uses_the_new_revision()
    {
        Write(user, "commands", "plain", ValidCommand);
        var vm = Create(workspace);
        await vm.ReloadAsync();
        await vm.SelectAsync(Row(vm, PromptTemplateKind.Command, PromptTemplateScope.Global, "plain"));
        var editor = vm.Editor!;
        var first = editor.Revision;

        editor.Text = "---\ndescription: edited\n---\nNew body.\n";
        Assert.True(editor.IsDirty);
        await editor.SaveAsync();

        Assert.False(editor.IsDirty);
        Assert.False(editor.StatusIsError);
        Assert.NotEqual(first, editor.Revision);
        Assert.Contains("New body.", File.ReadAllText(GlobalPath("plain")));

        editor.Text = "---\ndescription: edited again\n---\nThird body.\n";
        await editor.SaveAsync();

        Assert.False(editor.StatusIsError);
        Assert.Contains("Third body.", File.ReadAllText(GlobalPath("plain")));
    }

    [Fact]
    public async Task A_save_after_the_file_changed_underneath_is_a_conflict_and_does_not_overwrite()
    {
        Write(user, "commands", "plain", ValidCommand);
        var vm = Create(workspace);
        await vm.ReloadAsync();
        await vm.SelectAsync(Row(vm, PromptTemplateKind.Command, PromptTemplateScope.Global, "plain"));
        var editor = vm.Editor!;

        const string external = "---\ndescription: external\n---\nChanged elsewhere.\n";
        File.WriteAllText(GlobalPath("plain"), external);
        editor.Text = "---\ndescription: mine\n---\nMy edit.\n";
        await editor.SaveAsync();

        Assert.True(editor.StatusIsError);
        Assert.True(editor.HasStatus);
        Assert.True(editor.IsDirty);
        Assert.Equal(external, File.ReadAllText(GlobalPath("plain")));

        // Reverting re-reads the file and clears the conflict, after which a save goes through.
        await editor.RevertAsync();
        Assert.Equal(external, editor.Text);
        Assert.False(editor.IsDirty);
        editor.Text = "---\ndescription: mine\n---\nMy edit.\n";
        await editor.SaveAsync();
        Assert.False(editor.StatusIsError);
        Assert.Contains("My edit.", File.ReadAllText(GlobalPath("plain")));
    }

    [Fact]
    public async Task An_invalid_text_is_refused_with_localized_problems_and_nothing_is_written()
    {
        Write(user, "commands", "plain", ValidCommand);
        var vm = Create(workspace);
        await vm.ReloadAsync();
        await vm.SelectAsync(Row(vm, PromptTemplateKind.Command, PromptTemplateScope.Global, "plain"));
        var editor = vm.Editor!;

        editor.Text = "---\ndescription: never closed\nBody\n";
        await editor.ValidateAsync();
        Assert.True(editor.HasProblems);
        Assert.All(editor.Problems, problem => Assert.DoesNotContain("[prompts.", problem));

        await editor.SaveAsync();

        Assert.True(editor.StatusIsError);
        Assert.Equal(ValidCommand, File.ReadAllText(GlobalPath("plain")).ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Deleting_needs_a_confirmation_and_restores_the_built_in()
    {
        var vm = Create(workspace);
        await vm.ReloadAsync();
        await vm.SelectAsync(Row(vm, PromptTemplateKind.Command, PromptTemplateScope.BuiltIn, "review"));
        await vm.Editor!.CopyAsync(PromptTemplateScope.Global);
        var editor = vm.Editor;
        Assert.True(editor.IsCustom);

        // Asking only arms the confirmation.
        editor.DeleteCommand.Execute(null);
        Assert.True(editor.ConfirmingDelete);
        Assert.True(File.Exists(GlobalPath("review")));
        editor.CancelDeleteCommand.Execute(null);
        Assert.False(editor.ConfirmingDelete);
        Assert.True(File.Exists(GlobalPath("review")));

        editor.DeleteCommand.Execute(null);
        await editor.DeleteAsync();

        Assert.False(File.Exists(GlobalPath("review")));
        Assert.True(vm.Editor!.IsBuiltIn);
        Assert.False(Row(vm, PromptTemplateKind.Command, PromptTemplateScope.BuiltIn, "review").IsShadowed);
        Assert.DoesNotContain(Rows(vm), row => row.Scope == PromptTemplateScope.Global && row.Id == "review");
    }

    [Fact]
    public async Task Deleting_a_file_changed_underneath_is_a_conflict_and_keeps_the_file()
    {
        Write(user, "commands", "plain", ValidCommand);
        var vm = Create(workspace);
        await vm.ReloadAsync();
        await vm.SelectAsync(Row(vm, PromptTemplateKind.Command, PromptTemplateScope.Global, "plain"));
        var editor = vm.Editor!;

        File.WriteAllText(GlobalPath("plain"), "---\ndescription: external\n---\nElsewhere.\n");
        await editor.DeleteAsync();

        Assert.True(File.Exists(GlobalPath("plain")));
        Assert.True(editor.StatusIsError);
    }

    [Fact]
    public async Task Deleting_a_custom_file_with_no_built_in_closes_the_editor()
    {
        Write(user, "commands", "plain", ValidCommand);
        var vm = Create(workspace);
        await vm.ReloadAsync();
        await vm.SelectAsync(Row(vm, PromptTemplateKind.Command, PromptTemplateScope.Global, "plain"));

        await vm.Editor!.DeleteAsync();

        Assert.False(File.Exists(GlobalPath("plain")));
        Assert.Null(vm.Editor);
        Assert.True(vm.ShowsSelectHint);
    }

    [Fact]
    public async Task New_offers_commands_and_agents_only_and_project_only_with_a_workspace()
    {
        var vm = Create(workspace);
        await vm.ReloadAsync();
        vm.NewCommand.Execute(null);
        var editor = vm.Editor!;
        Assert.True(editor.IsNew);
        Assert.Equal([PromptTemplateKind.Command, PromptTemplateKind.Agent], editor.KindChoices.Select(choice => choice.Value).ToArray());
        Assert.DoesNotContain(editor.KindChoices, choice => choice.Value == PromptTemplateKind.Slot);
        Assert.Equal([PromptTemplateScope.Global, PromptTemplateScope.Project], editor.ScopeChoices.Select(choice => choice.Value).ToArray());

        var noWorkspace = Create(null);
        noWorkspace.NewCommand.Execute(null);
        Assert.Equal([PromptTemplateScope.Global], noWorkspace.Editor!.ScopeChoices.Select(choice => choice.Value).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Has Space")]
    [InlineData("UPPER")]
    [InlineData(".hidden")]
    [InlineData("nul")]
    public async Task New_refuses_an_invalid_id_without_writing(string id)
    {
        var vm = Create(workspace);
        await vm.ReloadAsync();
        vm.NewCommand.Execute(null);
        var editor = vm.Editor!;
        editor.Id = id;
        editor.Text = ValidCommand;

        await editor.SaveAsync();

        Assert.True(editor.StatusIsError);
        Assert.True(editor.IsNew);
        Assert.False(Directory.Exists(Path.Combine(user, "commands")) && Directory.EnumerateFiles(Path.Combine(user, "commands")).Any());
    }

    [Fact]
    public async Task New_refuses_an_id_that_exists_in_that_scope_but_allows_it_in_another()
    {
        Write(user, "commands", "taken", ValidCommand);
        var vm = Create(workspace);
        await vm.ReloadAsync();
        vm.NewCommand.Execute(null);
        var editor = vm.Editor!;
        editor.Id = "taken";
        editor.Text = "---\ndescription: second\n---\nSecond.\n";

        await editor.SaveAsync();

        Assert.True(editor.StatusIsError);
        Assert.True(editor.IsNew);
        Assert.Equal(ValidCommand, File.ReadAllText(GlobalPath("taken")).ReplaceLineEndings("\n"));

        editor.SelectedScope = editor.ScopeChoices.Single(choice => choice.Value == PromptTemplateScope.Project);
        await editor.SaveAsync();

        Assert.True(vm.Editor!.IsCustom);
        Assert.True(File.Exists(Path.Combine(workspace, ".forge", "commands", "taken.md")));
    }

    [Fact]
    public async Task New_creates_the_file_and_opens_it_selected()
    {
        var vm = Create(workspace);
        await vm.ReloadAsync();
        vm.NewCommand.Execute(null);
        var editor = vm.Editor!;
        editor.SelectedKind = editor.KindChoices.Single(choice => choice.Value == PromptTemplateKind.Agent);
        editor.Id = "my-agent";
        editor.Text = "---\nname: My agent\ndescription: helper\n---\nYou help.\n";

        await editor.SaveAsync();

        var path = Path.Combine(user, "agents", "my-agent.md");
        Assert.True(File.Exists(path));
        var opened = Assert.IsType<PromptEditorViewModel>(vm.Editor);
        Assert.True(opened.IsCustom);
        Assert.Equal(new PromptTemplateRef(PromptTemplateKind.Agent, PromptTemplateScope.Global, "my-agent"), opened.Ref);
        Assert.True(Row(vm, PromptTemplateKind.Agent, PromptTemplateScope.Global, "my-agent").IsSelected);
    }

    [Fact]
    public async Task New_cannot_create_a_slot_even_when_asked_to()
    {
        var vm = Create(workspace);
        await vm.ReloadAsync();
        vm.NewCommand.Execute(null);
        var editor = vm.Editor!;
        editor.SelectedKind = new PromptKindChoice("slot", PromptTemplateKind.Slot);
        editor.Id = "system";
        editor.Text = ValidCommand;

        await editor.SaveAsync();

        Assert.True(editor.StatusIsError);
        Assert.True(editor.IsNew);
        Assert.False(Directory.Exists(Path.Combine(user, "prompts")));
    }

    [Fact]
    public async Task A_stale_reload_does_not_replace_a_newer_one()
    {
        var vm = Create(workspace);
        var first = vm.ReloadAsync();
        Write(user, "commands", "late", ValidCommand);
        var second = vm.ReloadAsync();
        await Task.WhenAll(first, second);

        Assert.Contains(Rows(vm), row => row.Id == "late");
    }

    [Fact]
    public async Task A_file_deleted_outside_closes_the_editor_on_the_next_reload()
    {
        Write(user, "commands", "plain", ValidCommand);
        var vm = Create(workspace);
        await vm.ReloadAsync();
        await vm.SelectAsync(Row(vm, PromptTemplateKind.Command, PromptTemplateScope.Global, "plain"));
        Assert.NotNull(vm.Editor);

        File.Delete(GlobalPath("plain"));
        await vm.ReloadAsync();

        Assert.Null(vm.Editor);
        Assert.DoesNotContain(Rows(vm), row => row.Id == "plain");
    }

    [Fact]
    public async Task Saving_through_the_service_raises_Changed()
    {
        var changed = 0;
        service.Changed += () => changed++;
        var vm = Create(workspace);
        await vm.ReloadAsync();
        await vm.SelectAsync(Row(vm, PromptTemplateKind.Command, PromptTemplateScope.BuiltIn, "review"));

        await vm.Editor!.CopyAsync(PromptTemplateScope.Global);

        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Unsaved_edits_survive_the_first_click_elsewhere_and_go_on_the_second()
    {
        Write(user, "commands", "plain", ValidCommand);
        var vm = Create(workspace);
        await vm.ReloadAsync();
        var plain = Row(vm, PromptTemplateKind.Command, PromptTemplateScope.Global, "plain");
        var review = Row(vm, PromptTemplateKind.Command, PromptTemplateScope.BuiltIn, "review");
        await vm.SelectAsync(plain);
        var editor = vm.Editor!;
        editor.Text = "---\ndescription: mine\n---\nUnsaved.\n";

        // Re-clicking the row being edited keeps everything.
        await vm.SelectAsync(plain);
        Assert.Same(editor, vm.Editor);

        await vm.SelectAsync(review);
        Assert.Same(editor, vm.Editor);
        Assert.Equal("---\ndescription: mine\n---\nUnsaved.\n", editor.Text);
        Assert.True(editor.StatusIsError);
        Assert.False(string.IsNullOrEmpty(editor.Status));

        // "New" is a different target, so it only warns again.
        vm.NewCommand.Execute(null);
        Assert.Same(editor, vm.Editor);

        vm.NewCommand.Execute(null);
        Assert.NotSame(editor, vm.Editor);
        Assert.True(vm.Editor!.IsNew);
    }

    [Fact]
    public async Task A_clean_editor_switches_rows_on_the_first_click()
    {
        Write(user, "commands", "plain", ValidCommand);
        var vm = Create(workspace);
        await vm.ReloadAsync();
        await vm.SelectAsync(Row(vm, PromptTemplateKind.Command, PromptTemplateScope.Global, "plain"));

        await vm.SelectAsync(Row(vm, PromptTemplateKind.Command, PromptTemplateScope.BuiltIn, "review"));

        Assert.True(vm.Editor!.IsBuiltIn);
    }
}
