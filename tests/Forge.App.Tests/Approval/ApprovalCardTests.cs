using Forge.Abstractions;
using Forge.App.Approval;
using Forge.App.Localization;
using Forge.Editor.Diff;
using Forge.Plugin.Sdk;
using ToolSideEffect = Forge.Abstractions.ToolSideEffect;
using Xunit;

namespace Forge.App.Tests.Approval;

public sealed class ApprovalCardTests : IDisposable
{
    private readonly List<ApprovalCardViewModel> owned = [];

    public void Dispose()
    {
        foreach (var card in owned)
        {
            card.Dispose();
        }
    }

    private ToolApprovalCardViewModel ToolCard(
        string tool = "shell_run",
        string args = "{}",
        string? projectId = "p1") =>
        Track(new ToolApprovalCardViewModel(
            new ToolApprovalCardRequest("s1", "Session s1", projectId, tool, ToolSideEffect.Execute, "/tmp", args),
            InlineDispatcher.Instance));

    private ExtensionApprovalCardViewModel ExtensionCard(StagedExtension staged) =>
        Track(new ExtensionApprovalCardViewModel(
            staged, "s1", "Session s1", fromPreviousRun: false, InlineDispatcher.Instance));

    private T Track<T>(T card)
        where T : ApprovalCardViewModel
    {
        owned.Add(card);
        return card;
    }

    [Fact]
    public void Format_arguments_indents_json()
    {
        var formatted = ToolApprovalCardViewModel.FormatArguments("{\"a\":1,\"b\":[1,2]}");

        Assert.Contains("\"a\": 1", formatted);
        Assert.Contains('\n', formatted);
    }

    [Fact]
    public void Format_arguments_passes_non_json_through()
    {
        Assert.Equal("not json {{{", ToolApprovalCardViewModel.FormatArguments("not json {{{"));
        Assert.Equal(string.Empty, ToolApprovalCardViewModel.FormatArguments(string.Empty));
        Assert.Equal(string.Empty, ToolApprovalCardViewModel.FormatArguments(null));
    }

    [Fact]
    public void Format_arguments_truncates_pathological_input()
    {
        var formatted = ToolApprovalCardViewModel.FormatArguments(new string('x', 200_001));

        Assert.EndsWith("…", formatted);
        Assert.Equal(200_001, formatted.Length);
    }

    [Fact]
    public async Task Long_arguments_start_collapsed_and_toggle_opens_them()
    {
        var card = ToolCard(args: "{\"pad\":\"" + new string('x', 1600) + "\"}");

        await ApprovalFixtures.WaitUntilAsync(() => card.IsArgumentsLong, "the arguments to be measured");
        Assert.False(card.IsArgumentsExpanded);
        Assert.False(card.IsArgumentsVisible);
        Assert.NotEmpty(card.ArgumentsToggleText);

        card.ToggleArgumentsCommand.Execute(null);

        Assert.True(card.IsArgumentsExpanded);
        Assert.True(card.IsArgumentsVisible);
    }

    [Fact]
    public async Task Short_arguments_are_shown_expanded()
    {
        var card = ToolCard(args: "{\"a\":1}");

        await ApprovalFixtures.WaitUntilAsync(() => card.HasArguments, "the arguments to be formatted");
        Assert.False(card.IsArgumentsLong);
        Assert.True(card.IsArgumentsExpanded);
        Assert.True(card.IsArgumentsVisible);
    }

    [Fact]
    public async Task Tool_commands_fire_exactly_once()
    {
        var card = ToolCard();

        card.AllowCommand.Execute(null);

        Assert.True(card.IsDecided);
        card.DenyCommand.Execute(null);
        Assert.Equal(ApprovalCardDecision.Allow, (await card.Decision).Decision);
    }

    [Fact]
    public void Delete_card_has_no_remember_buttons_and_a_confirmation_title()
    {
        var card = ToolCard(tool: "extension_delete");

        Assert.True(card.IsDelete);
        Assert.Equal(Loc.Instance[ExtensionsStrings.ToolDeleteTitle], card.Title);
        Assert.True(card.HasNote);
        Assert.NotEmpty(card.Note);
        Assert.False(card.CanRemember);
        Assert.False(card.CanRememberProject);
        Assert.False(card.RememberSessionCommand.CanExecute(null));
        Assert.False(card.RememberProjectCommand.CanExecute(null));
        Assert.False(card.RememberGlobalCommand.CanExecute(null));
        Assert.True(card.AllowCommand.CanExecute(null));
    }

    [Fact]
    public async Task Remember_commands_carry_their_scope_to_the_waiter()
    {
        var sessionCard = ToolCard();
        sessionCard.RememberSessionCommand.Execute(null);
        Assert.Equal(ApprovalScope.Session, (await sessionCard.Decision).Remember);

        var globalCard = ToolCard();
        globalCard.RememberGlobalCommand.Execute(null);
        Assert.Equal(ApprovalScope.Global, (await globalCard.Decision).Remember);
    }

    [Fact]
    public void New_extension_file_shows_the_full_text()
    {
        var staged = ApprovalFixtures.Staged("plug");

        var files = ExtensionApprovalCardViewModel.BuildFiles(staged);

        var file = Assert.Single(files);
        Assert.True(file.IsNew);
        Assert.Equal("new text", file.FullText);
        Assert.False(file.HasRows);
    }

    [Fact]
    public void Updated_file_shows_hunk_headers_line_numbers_and_marks()
    {
        var staged = new StagedExtension(
            "plug",
            "2.0.0",
            ExtensionKinds.Plugin,
            [new StagedFile("a.cs", "a\nB\nc\nd", "a\nb\nc")],
            [],
            [],
            "s",
            "d",
            "sdk",
            [],
            "1.0.0");

        var files = ExtensionApprovalCardViewModel.BuildFiles(staged);

        var file = Assert.Single(files);
        Assert.False(file.IsNew);
        Assert.True(file.IsChanged);
        Assert.NotEmpty(file.AddedLines);
        Assert.NotEmpty(file.RemovedLines);
        var hunk = Assert.Single(file.Rows, static row => row.IsHunk);
        Assert.StartsWith("@@ -", hunk.Text);
        Assert.Contains(file.Rows, static row => row is { Prefix: "-", Text: "b" });
        Assert.Contains(file.Rows, static row => row is { Prefix: "+", Text: "B" });
        Assert.Contains(file.Rows, static row => row is { Prefix: "+", Text: "d" });
        var context = Assert.Single(file.Rows, static row => row is { Prefix: " ", Text: "a" });
        Assert.Equal("1", context.OldNumber);
        Assert.Equal("1", context.NewNumber);
        var removed = Assert.Single(file.Rows, static row => row is { Prefix: "-", Text: "b" });
        Assert.Equal("2", removed.OldNumber);
        Assert.Empty(removed.NewNumber);
    }

    [Fact]
    public void Unchanged_file_is_badged_and_has_no_rows()
    {
        var staged = new StagedExtension(
            "plug",
            "2.0.0",
            ExtensionKinds.Plugin,
            [new StagedFile("same.cs", "identical", "identical")],
            [],
            [],
            "s",
            "d",
            "sdk",
            [],
            "1.0.0");

        var file = Assert.Single(ExtensionApprovalCardViewModel.BuildFiles(staged));

        Assert.True(file.IsUnchanged);
        Assert.False(file.HasRows);
        Assert.NotEmpty(file.Badge);
    }

    [Fact]
    public void Build_files_sorts_by_path()
    {
        var staged = new StagedExtension(
            "plug",
            "1.0.0",
            ExtensionKinds.Plugin,
            [
                new StagedFile("b.cs", "b", null),
                new StagedFile("a.cs", "a", null),
            ],
            [],
            [],
            "s",
            "d",
            "sdk",
            [],
            null);

        var files = ExtensionApprovalCardViewModel.BuildFiles(staged);

        Assert.Equal(["a.cs", "b.cs"], files.Select(static file => file.Path));
    }

    [Fact]
    public async Task Extension_card_with_diagnostics_cannot_be_approved()
    {
        var card = ExtensionCard(ApprovalFixtures.Staged("plug", diagnostics: ["error CS0001: boom"]));

        Assert.True(card.HasDiagnostics);
        Assert.Contains("boom", card.DiagnosticsText);
        Assert.False(card.CanApprove);
        Assert.False(card.ApproveCommand.CanExecute(null));
        Assert.True(card.RejectCommand.CanExecute(null));

        card.RejectCommand.Execute(null);
        Assert.Equal(ApprovalCardDecision.Deny, (await card.Decision).Decision);
    }

    [Fact]
    public async Task Extension_approve_fires_exactly_once()
    {
        var card = ExtensionCard(ApprovalFixtures.Staged("plug"));

        card.ApproveCommand.Execute(null);

        Assert.True(card.IsDecided);
        card.RejectCommand.Execute(null);
        Assert.Equal(ApprovalCardDecision.Allow, (await card.Decision).Decision);
    }

    [Fact]
    public async Task Identical_update_reports_no_file_changes_once_the_diff_is_ready()
    {
        var staged = new StagedExtension(
            "plug",
            "2.0.0",
            ExtensionKinds.Plugin,
            [new StagedFile("same.cs", "identical", "identical")],
            [],
            [],
            "s",
            "d",
            "sdk",
            [],
            "1.0.0");
        var card = ExtensionCard(staged);

        await ApprovalFixtures.WaitUntilAsync(() => card.IsDiffReady, "the diff to be built");

        Assert.True(card.HasNoFileChanges);
        Assert.True(card.CanApprove);
    }
}
