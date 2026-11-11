using Forge.Abstractions;
using Forge.App.Automations.Settings;
using Xunit;

namespace Forge.App.Tests.Automations.Settings;

public sealed class AutomationEditorViewModelTests
{
    private static readonly DateTimeOffset Base = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    private static AutomationEditorViewModel Create(
        IAutomationEngine? engine = null,
        AutomationDefinition? original = null,
        Func<AutomationTrigger, int, AutomationPreview>? preview = null,
        IReadOnlyList<AutomationSessionChoice>? sessions = null) =>
        new(
            engine ?? new FakeAutomationEngine(),
            original,
            preview ?? AutomationTestData.NoPreview,
            [AutomationTestData.RepoA, AutomationTestData.Plain],
            sessions ?? [new AutomationSessionChoice("s1", "Main", "Alpha")]);

    private static AutomationEditorViewModel Valid()
    {
        var vm = Create();
        vm.Name = "Daily note";
        vm.Prompt = "write the note";
        return vm;
    }

    [Fact]
    public void Empty_name_blocks_the_build()
    {
        var vm = Create();
        vm.Prompt = "x";

        Assert.Null(vm.TryBuild());
        Assert.True(vm.HasError);
    }

    [Fact]
    public void Empty_prompt_blocks_the_build()
    {
        var vm = Create();
        vm.Name = "n";

        Assert.Null(vm.TryBuild());
        Assert.True(vm.HasError);
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("9:60")]
    [InlineData("noon")]
    [InlineData("09:5")]
    [InlineData("")]
    public void A_bad_or_missing_time_blocks_the_build(string times)
    {
        var vm = Valid();
        vm.AddTrigger();
        vm.Triggers[0].Kind = AutomationTriggerKind.Daily;
        vm.Triggers[0].TimesText = times;

        Assert.Null(vm.TryBuild());
        Assert.True(vm.HasError);
    }

    [Theory]
    [InlineData("0", "0", false)]
    [InlineData("13", "0", false)]
    [InlineData("abc", "0", false)]
    [InlineData("1", "60", false)]
    [InlineData("1", "-1", false)]
    [InlineData("1", "0", true)]
    [InlineData("12", "59", true)]
    public void Hourly_interval_and_minute_are_range_checked(string interval, string minute, bool valid)
    {
        var vm = Valid();
        vm.AddTrigger();
        vm.Triggers[0].Kind = AutomationTriggerKind.Hourly;
        vm.Triggers[0].IntervalText = interval;
        vm.Triggers[0].MinuteText = minute;

        var built = vm.TryBuild();

        Assert.Equal(valid, built is not null);
        if (built is not null)
        {
            var trigger = Assert.Single(built.Triggers);
            Assert.Equal(int.Parse(interval), trigger.IntervalHours);
            Assert.Equal(int.Parse(minute), trigger.Minute);
        }
    }

    [Fact]
    public void A_weekly_trigger_needs_at_least_one_day()
    {
        var vm = Valid();
        vm.AddTrigger();
        vm.Triggers[0].Kind = AutomationTriggerKind.Weekly;
        vm.Triggers[0].TimesText = "18:30";

        Assert.Null(vm.TryBuild());

        vm.Triggers[0].Days.Single(d => d.Day == DayOfWeek.Monday).IsChecked = true;
        vm.Triggers[0].Days.Single(d => d.Day == DayOfWeek.Wednesday).IsChecked = true;
        var built = vm.TryBuild();

        Assert.NotNull(built);
        var trigger = Assert.Single(built.Triggers);
        Assert.Equal(AutomationTriggerKind.Weekly, trigger.Kind);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Wednesday], trigger.Days);
        Assert.Equal([new AutomationTime(18, 30)], trigger.Times);
    }

    [Fact]
    public void Daily_times_are_sorted_and_distinct()
    {
        var vm = Valid();
        vm.AddTrigger();
        vm.Triggers[0].TimesText = "18:30, 9:00;09:00 07:15";

        var built = vm.TryBuild();

        Assert.NotNull(built);
        Assert.Equal([new AutomationTime(7, 15), new AutomationTime(9, 0), new AutomationTime(18, 30)], built.Triggers[0].Times);
    }

    [Fact]
    public void A_cron_trigger_needs_text_and_a_valid_preview()
    {
        var vm = Create(preview: (t, _) => t.Cron == "bad" ? new AutomationPreview([], "invalid cron") : new AutomationPreview([Base], null));
        vm.Name = "n";
        vm.Prompt = "p";
        vm.AddTrigger();
        vm.Triggers[0].Kind = AutomationTriggerKind.Cron;

        Assert.Null(vm.TryBuild());

        vm.Triggers[0].CronText = "bad";
        Assert.Null(vm.TryBuild());
        Assert.Contains("invalid cron", vm.Error);
        Assert.Equal("invalid cron", vm.PreviewError);

        vm.Triggers[0].CronText = "*/5 * * * *";
        var built = vm.TryBuild();
        Assert.NotNull(built);
        Assert.Equal("*/5 * * * *", built.Triggers[0].Cron);
        Assert.False(vm.HasPreviewError);
    }

    [Fact]
    public void Fixed_fork_needs_a_repository_project_and_scout_needs_a_session()
    {
        var vm = Valid();
        Assert.DoesNotContain(vm.Projects, p => !p.IsRepository);

        vm.PlacementIndex = 1;
        Assert.Null(vm.TryBuild());
        vm.SelectedProject = AutomationTestData.RepoA;
        var fork = vm.TryBuild();
        Assert.Equal(AutomationPlacement.FixedFork, fork!.Placement);
        Assert.Equal("p1", fork.ProjectId);
        Assert.Null(fork.TargetSessionId);

        vm.PlacementIndex = 2;
        Assert.Null(vm.TryBuild());
        vm.SelectedSession = vm.Sessions[0];
        var scout = vm.TryBuild();
        Assert.Equal(AutomationPlacement.Scout, scout!.Placement);
        Assert.Equal("s1", scout.TargetSessionId);
        Assert.Null(scout.ProjectId);
    }

    [Fact]
    public void Scratch_keeps_the_read_only_flag_other_placements_drop_it()
    {
        var vm = Valid();
        vm.ReadOnlyTools = true;
        Assert.True(vm.TryBuild()!.ReadOnlyTools);

        vm.PlacementIndex = 1;
        vm.SelectedProject = AutomationTestData.RepoA;
        Assert.False(vm.TryBuild()!.ReadOnlyTools);
    }

    [Fact]
    public void Preview_merges_all_triggers_sorted_distinct_and_capped_at_five()
    {
        var hour = TimeSpan.FromHours(1);
        var vm = Create(preview: (t, count) => t.Kind == AutomationTriggerKind.Hourly
            ? new AutomationPreview([Base + (hour * 1), Base + (hour * 3), Base + (hour * 5), Base + (hour * 7)], null)
            : new AutomationPreview([Base + (hour * 2), Base + (hour * 3), Base + (hour * 4), Base + (hour * 6)], null));
        vm.AddTrigger();
        vm.Triggers[0].Kind = AutomationTriggerKind.Hourly;
        vm.AddTrigger();
        vm.Triggers[1].Kind = AutomationTriggerKind.Daily;

        var expected = new[] { 1, 2, 3, 4, 5 }.Select(h => AutomationText.Moment(Base + (hour * h))).ToList();
        Assert.Equal(expected, vm.PreviewLines);
        Assert.True(vm.HasPreviewLines);
    }

    [Fact]
    public void Preview_updates_when_a_trigger_changes_or_is_removed()
    {
        var calls = 0;
        var vm = Create(preview: (t, _) =>
        {
            calls++;
            return new AutomationPreview([Base], null);
        });
        vm.AddTrigger();
        var afterAdd = calls;

        vm.Triggers[0].TimesText = "10:00";
        Assert.True(calls > afterAdd);

        vm.Triggers[0].RemoveCommand.Execute(null);
        Assert.Empty(vm.Triggers);
        Assert.Empty(vm.PreviewLines);
        Assert.True(vm.HasNoTriggers);
    }

    [Fact]
    public void An_invalid_trigger_contributes_no_preview_lines()
    {
        var vm = Create();
        vm.AddTrigger();
        vm.Triggers[0].TimesText = "nope";

        Assert.Empty(vm.PreviewLines);
    }

    [Fact]
    public void The_built_definition_carries_all_fields_and_a_user_id()
    {
        var vm = Valid();
        vm.Enabled = false;
        vm.CatchUp = true;
        vm.ModelText = " model-x ";
        vm.ProviderText = "prov";
        vm.AddTrigger();
        vm.Triggers[0].TimesText = "09:00";

        var built = vm.TryBuild()!;

        Assert.StartsWith("user.", built.Id);
        Assert.False(built.IsBuiltIn);
        Assert.Equal("Daily note", built.Name);
        Assert.False(built.Enabled);
        Assert.True(built.CatchUp);
        Assert.Equal(AutomationAction.Prompt, built.Action);
        Assert.Equal("write the note", built.Prompt);
        Assert.Equal(new ModelSettings("model-x", ProviderId: "prov"), built.Model);
        Assert.Equal([new AutomationTime(9, 0)], built.Triggers[0].Times);
    }

    [Fact]
    public void An_empty_model_means_the_global_default()
    {
        var vm = Valid();
        Assert.Null(vm.TryBuild()!.Model);
    }

    [Fact]
    public void Remind_ignores_placement_and_model()
    {
        var vm = Valid();
        vm.ActionIndex = 1;
        vm.PlacementIndex = 1; // would need a project if it applied
        vm.ModelText = "m";

        var built = vm.TryBuild();

        Assert.NotNull(built);
        Assert.Equal(AutomationAction.Remind, built.Action);
        Assert.Equal(AutomationPlacement.Scratch, built.Placement);
        Assert.Null(built.Model);
        Assert.False(vm.ShowsPlacement);
    }

    [Fact]
    public async Task Save_appends_a_new_definition_through_update_config()
    {
        var engine = new FakeAutomationEngine { Config = new AutomationConfig { Definitions = [new AutomationDefinition("user.keep", "keep")] } };
        var vm = Create(engine);
        vm.Name = "n";
        vm.Prompt = "p";
        AutomationDefinition? saved = null;
        vm.Saved += (_, d) => saved = d;

        Assert.True(await vm.SaveAsync());

        Assert.Equal(2, engine.Config.Definitions.Count);
        Assert.Equal("user.keep", engine.Config.Definitions[0].Id);
        Assert.Equal(saved!.Id, engine.Config.Definitions[1].Id);
    }

    [Fact]
    public async Task Save_replaces_the_edited_definition_and_keeps_its_id()
    {
        var original = new AutomationDefinition("user.a", "old") { Prompt = "p", Model = new ModelSettings("m1", ThinkingEffort.High, 100) };
        var engine = new FakeAutomationEngine { Config = new AutomationConfig { Definitions = [original, new AutomationDefinition("user.b", "b")] } };
        var vm = Create(engine, original);
        Assert.False(vm.IsNew);
        vm.Name = "new name";
        vm.ModelText = "m2";

        Assert.True(await vm.SaveAsync());

        Assert.Equal(2, engine.Config.Definitions.Count);
        var edited = engine.Config.Definitions[0];
        Assert.Equal("user.a", edited.Id);
        Assert.Equal("new name", edited.Name);
        // Thinking and output limits of the existing model are kept when only the id changes.
        Assert.Equal(new ModelSettings("m2", ThinkingEffort.High, 100), edited.Model);
    }

    [Fact]
    public async Task A_validation_error_blocks_save_and_writes_nothing()
    {
        var engine = new FakeAutomationEngine();
        var vm = Create(engine);

        Assert.False(await vm.SaveAsync());

        Assert.Equal(0, engine.UpdateCount);
        Assert.True(vm.HasError);
    }

    [Fact]
    public void Editing_populates_the_form_from_the_definition()
    {
        var original = new AutomationDefinition("user.a", "Weekly report")
        {
            Prompt = "report",
            Placement = AutomationPlacement.Scout,
            TargetSessionId = "gone",
            CatchUp = true,
            Triggers =
            [
                new AutomationTrigger(AutomationTriggerKind.Weekly) { Days = [DayOfWeek.Friday], Times = [new AutomationTime(17, 0)] },
                new AutomationTrigger(AutomationTriggerKind.Hourly) { IntervalHours = 3, Minute = 20 },
            ],
        };

        var vm = Create(original: original);

        Assert.Equal("Weekly report", vm.Name);
        Assert.Equal(2, vm.PlacementIndex);
        Assert.Equal("gone", vm.SelectedSession?.Id); // an inactive target stays selectable
        Assert.True(vm.CatchUp);
        Assert.Equal(2, vm.Triggers.Count);
        Assert.Equal("17:00", vm.Triggers[0].TimesText);
        Assert.True(vm.Triggers[0].Days.Single(d => d.Day == DayOfWeek.Friday).IsChecked);
        Assert.Equal("3", vm.Triggers[1].IntervalText);

        var rebuilt = vm.TryBuild()!;
        Assert.Equal(original.Triggers[0].Days, rebuilt.Triggers[0].Days);
        Assert.Equal(original.Triggers[1].IntervalHours, rebuilt.Triggers[1].IntervalHours);
        Assert.Equal("gone", rebuilt.TargetSessionId);
    }

    [Fact]
    public async Task A_failing_write_is_shown_and_does_not_raise_saved()
    {
        var vm = Create(new ThrowingEngine());
        vm.Name = "n";
        vm.Prompt = "p";
        var raised = false;
        vm.Saved += (_, _) => raised = true;

        Assert.False(await vm.SaveAsync());

        Assert.False(raised);
        Assert.Contains("disk full", vm.Error);
    }

    private sealed class ThrowingEngine : IAutomationEngine
    {
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067

        public AutomationConfig Config => new();

        public IReadOnlyList<AutomationDefinitionError> ConfigErrors => [];

        public IReadOnlyList<AutomationDefinition> Definitions => [];

        public IReadOnlyList<AutomationRun> ActiveRuns => [];

        public DateTimeOffset? NextFireTime(string automationId) => null;

        public Task UpdateConfigAsync(Func<AutomationConfig, AutomationConfig> change, CancellationToken ct) =>
            throw new IOException("disk full");

        public Task<AutomationStartResult> StartAsync(AutomationRunRequest request, CancellationToken ct) => throw new NotSupportedException();

        public bool Cancel(string runId, string reason) => false;

        public Task ResetTargetBudgetAsync(string targetKey, CancellationToken ct) => Task.CompletedTask;
    }
}
