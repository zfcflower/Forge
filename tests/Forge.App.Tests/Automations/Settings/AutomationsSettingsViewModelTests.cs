using Forge.Abstractions;
using Forge.App.Automations.Settings;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Xunit;

namespace Forge.App.Tests.Automations.Settings;

public sealed class AutomationsSettingsViewModelTests
{
    private sealed class Harness
    {
        public FakeAutomationEngine Engine { get; } = new();

        public FakeRunStore Store { get; } = new();

        public List<string> Opened { get; } = [];

        public List<AutomationEditorViewModel> Editors { get; } = [];

        public bool ConfirmResult { get; set; } = true;

        public string? ConfirmedName { get; private set; }

        public IReadOnlyList<AutomationProjectChoice> ProjectChoices { get; set; } =
            [AutomationTestData.RepoA, AutomationTestData.Plain, AutomationTestData.RepoB];

        public AutomationsSettingsViewModel Create() => new(
            Engine,
            Store,
            AutomationTestData.NoPreview,
            _ => Task.FromResult(ProjectChoices),
            _ => Task.FromResult<IReadOnlyList<AutomationSessionChoice>>([new AutomationSessionChoice("s1", "Main", "Alpha")]),
            Opened.Add,
            editor =>
            {
                Editors.Add(editor);
                return Task.FromResult(false);
            },
            action => action(),
            name =>
            {
                ConfirmedName = name;
                return Task.FromResult(ConfirmResult);
            });
    }

    private static AutomationDefinition User(string id, bool enabled = true) =>
        new(id, "Job " + id) { Enabled = enabled, Triggers = [new AutomationTrigger(AutomationTriggerKind.Daily) { Times = [new AutomationTime(9, 0)] }] };

    [Fact]
    public async Task Rows_list_built_ins_first_then_user_definitions()
    {
        var h = new Harness();
        h.Engine.Config = new AutomationConfig { Definitions = [User("user.a"), User("user.b")] };
        using var vm = h.Create();

        await vm.LoadAsync();

        Assert.Equal(
            [.. BuiltInAutomations.All, "user.a", "user.b"],
            vm.Rows.Select(r => r.Id));
        Assert.All(vm.Rows.Take(4), r => Assert.True(r.IsBuiltIn));
        Assert.All(vm.Rows.Skip(4), r => Assert.False(r.IsBuiltIn));
    }

    [Fact]
    public async Task Built_in_rows_have_localized_names_and_summaries()
    {
        using var scope = new LanguageScope(UiLanguage.English);
        var h = new Harness();
        using var vm = h.Create();
        await vm.LoadAsync();

        Assert.Equal("Quota resume", vm.Rows.Single(r => r.Id == BuiltInAutomations.QuotaResume).Name);
        Assert.Equal("Event-driven", vm.Rows.Single(r => r.Id == BuiltInAutomations.CiChain).TriggerSummary);
        Assert.Equal("Daily 03:00", vm.Rows.Single(r => r.Id == BuiltInAutomations.Housekeeping).TriggerSummary);
    }

    [Fact]
    public void Trigger_summaries_read_as_human_text()
    {
        using var scope = new LanguageScope(UiLanguage.English);

        Assert.Equal("Daily 09:00, 18:30", AutomationText.Summarize(new AutomationTrigger(AutomationTriggerKind.Daily) { Times = [new(9, 0), new(18, 30)] }));
        Assert.Equal(
            "Weekly Mon, Wed 18:30",
            AutomationText.Summarize(new AutomationTrigger(AutomationTriggerKind.Weekly) { Days = [DayOfWeek.Wednesday, DayOfWeek.Monday], Times = [new(18, 30)] }));
        Assert.Equal("Every 2 hours at minute 15", AutomationText.Summarize(new AutomationTrigger(AutomationTriggerKind.Hourly) { IntervalHours = 2, Minute = 15 }));
        Assert.Equal("Hourly at minute 5", AutomationText.Summarize(new AutomationTrigger(AutomationTriggerKind.Hourly) { IntervalHours = 1, Minute = 5 }));
        Assert.Equal("Cron */5 * * * *", AutomationText.Summarize(new AutomationTrigger(AutomationTriggerKind.Cron) { Cron = "*/5 * * * *" }));
        Assert.Equal("Manual only", AutomationText.Summarize(new AutomationDefinition("user.x", "x")));
    }

    [Fact]
    public async Task Next_fire_time_and_last_status_are_shown()
    {
        var h = new Harness();
        var next = new DateTimeOffset(2026, 11, 12, 3, 0, 0, TimeSpan.Zero);
        h.Engine.NextTimes[BuiltInAutomations.Housekeeping] = next;
        h.Store.Runs.Add(AutomationTestData.Run("r2", BuiltInAutomations.Housekeeping, AutomationRunStatus.Failed));
        h.Store.Runs.Add(AutomationTestData.Run("r1", BuiltInAutomations.Housekeeping, AutomationRunStatus.Completed));
        using var vm = h.Create();

        await vm.LoadAsync();

        var row = vm.Rows.Single(r => r.Id == BuiltInAutomations.Housekeeping);
        Assert.Equal(AutomationText.Moment(next), row.NextText);
        Assert.Contains(AutomationText.Status(AutomationRunStatus.Failed), row.LastStatusText);
        Assert.NotEqual(row.LastStatusText, vm.Rows.Single(r => r.Id == BuiltInAutomations.CiChain).LastStatusText);
    }

    [Fact]
    public async Task Pause_toggle_writes_config_paused()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.LoadAsync();
        Assert.True(vm.IsRunning);

        vm.IsRunning = false;

        Assert.True(h.Engine.Config.Paused);
        await vm.RefreshAsync();
        Assert.False(vm.IsRunning);

        vm.IsRunning = true;
        Assert.False(h.Engine.Config.Paused);
    }

    [Fact]
    public async Task Built_in_enabled_toggle_writes_disabled_built_ins()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.LoadAsync();
        var row = vm.Rows.Single(r => r.Id == BuiltInAutomations.Housekeeping);

        row.IsEnabled = false;
        Assert.Equal([BuiltInAutomations.Housekeeping], h.Engine.Config.DisabledBuiltIns);
        Assert.Empty(h.Engine.Config.Definitions);

        row.IsEnabled = true;
        Assert.Empty(h.Engine.Config.DisabledBuiltIns);
    }

    [Fact]
    public async Task User_enabled_toggle_writes_only_that_definition()
    {
        var h = new Harness();
        h.Engine.Config = new AutomationConfig { Definitions = [User("user.a"), User("user.b")] };
        using var vm = h.Create();
        await vm.LoadAsync();

        vm.Rows.Single(r => r.Id == "user.b").IsEnabled = false;

        Assert.True(h.Engine.Config.Definitions[0].Enabled);
        Assert.False(h.Engine.Config.Definitions[1].Enabled);
        Assert.Empty(h.Engine.Config.DisabledBuiltIns);
    }

    [Fact]
    public async Task Run_now_starts_a_manual_run_of_that_automation()
    {
        var h = new Harness();
        h.Engine.Config = new AutomationConfig { Definitions = [User("user.a")] };
        using var vm = h.Create();
        await vm.LoadAsync();

        await vm.RunNowAsync(vm.Rows.Single(r => r.Id == "user.a"));

        var request = Assert.Single(h.Engine.Started);
        Assert.Equal("user.a", request.AutomationId);
        Assert.Equal(AutomationTriggerSource.Manual, request.Source);
        Assert.Null(vm.Problem);
    }

    [Fact]
    public async Task Run_now_is_unavailable_for_event_only_built_ins()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.LoadAsync();

        foreach (var id in new[] { BuiltInAutomations.QuotaResume, BuiltInAutomations.CiChain, BuiltInAutomations.SessionWatch })
        {
            Assert.False(vm.Rows.Single(r => r.Id == id).RunNowCommand.CanExecute(null));
        }

        Assert.True(vm.Rows.Single(r => r.Id == BuiltInAutomations.Housekeeping).RunNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_refused_start_is_reported_as_a_problem()
    {
        var h = new Harness();
        h.Engine.StartStatus = AutomationStartStatus.Busy;
        using var vm = h.Create();
        await vm.LoadAsync();

        await vm.RunNowAsync(vm.Rows.Single(r => r.Id == BuiltInAutomations.Housekeeping));

        Assert.True(vm.HasProblem);
    }

    [Fact]
    public async Task Delete_asks_first_and_removes_only_user_definitions()
    {
        var h = new Harness();
        h.Engine.Config = new AutomationConfig { Definitions = [User("user.a"), User("user.b")] };
        using var vm = h.Create();
        await vm.LoadAsync();

        h.ConfirmResult = false;
        await vm.DeleteAsync(vm.Rows.Single(r => r.Id == "user.a"));
        Assert.Equal(2, h.Engine.Config.Definitions.Count);

        h.ConfirmResult = true;
        await vm.DeleteAsync(vm.Rows.Single(r => r.Id == "user.a"));
        Assert.Equal(["user.b"], h.Engine.Config.Definitions.Select(d => d.Id));
        Assert.DoesNotContain(vm.Rows, r => r.Id == "user.a");

        var before = h.Engine.UpdateCount;
        var builtIn = vm.Rows.First(r => r.IsBuiltIn);
        Assert.False(builtIn.CanDelete);
        await vm.DeleteAsync(builtIn);
        Assert.Equal(before, h.Engine.UpdateCount);
    }

    [Fact]
    public async Task Ci_auto_push_toggles_write_the_off_list_per_project()
    {
        var h = new Harness();
        h.Engine.Config = new AutomationConfig { CiAutoPushOffProjects = ["p3"] };
        using var vm = h.Create();
        await vm.LoadAsync();
        var ci = vm.Rows.Single(r => r.IsCi);

        // Only repository projects are listed; the state mirrors the config.
        Assert.Equal(["p1", "p3"], ci.CiProjects.Select(p => p.Id));
        Assert.True(ci.CiProjects[0].AutoPush);
        Assert.False(ci.CiProjects[1].AutoPush);

        ci.CiProjects[0].AutoPush = false;
        Assert.Equal(["p3", "p1"], h.Engine.Config.CiAutoPushOffProjects);

        ci.CiProjects[1].AutoPush = true;
        Assert.Equal(["p1"], h.Engine.Config.CiAutoPushOffProjects);
        Assert.All(vm.Rows.Where(r => !r.IsCi), r => Assert.Empty(r.CiProjects));
    }

    [Fact]
    public async Task Config_errors_become_warning_lines()
    {
        var h = new Harness();
        h.Engine.ConfigErrors = [new AutomationDefinitionError(2, "user.bad", "no name")];
        using var vm = h.Create();
        await vm.LoadAsync();

        var line = Assert.Single(vm.ConfigErrors);
        Assert.Contains("no name", line);
        Assert.True(vm.HasConfigErrors);
    }

    [Fact]
    public async Task Engine_changes_refresh_the_list_through_post()
    {
        var h = new Harness();
        using var vm = h.Create();
        await vm.LoadAsync();
        Assert.Equal(4, vm.Rows.Count);

        h.Engine.Config = new AutomationConfig { Definitions = [User("user.new")] };
        h.Engine.RaiseChanged();

        Assert.Contains(vm.Rows, r => r.Id == "user.new");
    }

    [Fact]
    public async Task Selecting_a_row_loads_its_runs_and_opens_the_right_session()
    {
        var h = new Harness();
        h.Store.Runs.Add(AutomationTestData.Run("create", "user.a") with { SessionId = "sess-new" });
        h.Store.Runs.Add(AutomationTestData.Run("attach", "user.a") with { TargetSessionId = "sess-target", Usage = new TokenUsage(1200, 30) });
        h.Store.Runs.Add(AutomationTestData.Run("none", "user.a") with { Message = "skipped: busy" });
        h.Store.Runs.Add(AutomationTestData.Run("other", "user.z"));
        h.Engine.Config = new AutomationConfig { Definitions = [User("user.a")] };
        using var vm = h.Create();
        await vm.LoadAsync();

        await vm.SelectRowAsync(vm.Rows.Single(r => r.Id == "user.a"));

        Assert.Equal(["create", "attach", "none"], vm.Runs.Select(r => r.Run.Id));
        var query = h.Store.Queries.Last();
        Assert.Equal("user.a", query.AutomationId);
        Assert.Equal(100, query.Limit);

        vm.Runs[0].OpenCommand.Execute(null);
        vm.Runs[1].OpenCommand.Execute(null);
        vm.Runs[2].OpenCommand.Execute(null);
        Assert.Equal(["sess-new", "sess-target"], h.Opened);
        Assert.True(vm.Runs[1].HasUsage);
        Assert.Contains("1,200", vm.Runs[1].UsageText);
        Assert.Equal("skipped: busy", vm.Runs[2].MessageText);
        Assert.False(vm.Runs[2].CanOpen);
    }

    [Fact]
    public async Task Setting_selected_row_loads_runs_and_clearing_it_empties_them()
    {
        var h = new Harness();
        h.Store.Runs.Add(AutomationTestData.Run("r1", BuiltInAutomations.Housekeeping));
        using var vm = h.Create();
        await vm.LoadAsync();

        vm.SelectedRow = vm.Rows.Single(r => r.Id == BuiltInAutomations.Housekeeping);
        Assert.Single(vm.Runs);
        Assert.True(vm.HasRuns);

        vm.SelectedRow = null;
        Assert.Empty(vm.Runs);
    }

    [Fact]
    public async Task New_opens_an_editor_for_a_new_definition_and_edit_for_the_existing_one()
    {
        var h = new Harness();
        h.Engine.Config = new AutomationConfig { Definitions = [User("user.a")] };
        using var vm = h.Create();
        await vm.LoadAsync();

        await vm.NewCommand.ExecuteForTestAsync();
        Assert.True(h.Editors[^1].IsNew);

        await vm.EditAsync(vm.Rows.Single(r => r.Id == "user.a"));
        Assert.False(h.Editors[^1].IsNew);
        Assert.Equal("Job user.a", h.Editors[^1].Name);

        var count = h.Editors.Count;
        await vm.EditAsync(vm.Rows.First(r => r.IsBuiltIn));
        Assert.Equal(count, h.Editors.Count);
    }
}

internal static class AsyncCommandTestExtensions
{
    /// <summary>Runs an async command's body and waits for it (the command itself is fire-and-forget).</summary>
    public static async Task ExecuteForTestAsync(this Forge.App.ViewModels.AsyncRelayCommand command)
    {
        command.Execute(null);
        for (var i = 0; i < 200 && !command.CanExecute(null); i++)
        {
            await Task.Delay(5);
        }
    }
}
