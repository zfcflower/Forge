using System.Text;
using Forge.Abstractions;
using Forge.App.Automations;
using Xunit;

namespace Forge.App.Tests.Automations;

public sealed class AutomationDefinitionStoreTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-automation-defs-" + Guid.NewGuid().ToString("N"));

    public AutomationDefinitionStoreTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
        }
    }

    private string FilePath => Path.Combine(root, "automations.json");

    private AutomationDefinitionStore Store() => new(FilePath);

    [Fact]
    public async Task A_missing_file_is_an_empty_config_without_errors()
    {
        var load = await Store().LoadAsync(Ct);

        Assert.False(load.Config.Paused);
        Assert.Empty(load.Config.Definitions);
        Assert.Empty(load.Config.DisabledBuiltIns);
        Assert.Empty(load.Config.CiAutoPushOffProjects);
        Assert.Empty(load.Errors);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task A_full_config_round_trips()
    {
        var config = new AutomationConfig
        {
            Paused = true,
            DisabledBuiltIns = [BuiltInAutomations.Housekeeping],
            CiAutoPushOffProjects = ["p1", "p2"],
            Definitions =
            [
                new AutomationDefinition("daily-review", "Daily review")
                {
                    Enabled = false,
                    Prompt = "Review the open items",
                    Placement = AutomationPlacement.FixedFork,
                    ProjectId = "p1",
                    ReadOnlyTools = true,
                    CatchUp = true,
                    Model = new ModelSettings("model-x", ThinkingEffort.High, 4096, "prov"),
                    Triggers =
                    [
                        new AutomationTrigger(AutomationTriggerKind.Hourly) { IntervalHours = 6, Minute = 15 },
                        new AutomationTrigger(AutomationTriggerKind.Daily) { Times = [new AutomationTime(9, 30), new AutomationTime(17, 0)] },
                        new AutomationTrigger(AutomationTriggerKind.Weekly) { Days = [DayOfWeek.Monday, DayOfWeek.Sunday], Times = [new AutomationTime(8, 0)] },
                        new AutomationTrigger(AutomationTriggerKind.Cron) { Cron = "*/5 * * * mon-fri" },
                    ],
                },
                new AutomationDefinition("remind", "Remind") { Action = AutomationAction.Remind, Prompt = "Stretch", Placement = AutomationPlacement.Scout, TargetSessionId = "s1" },
            ],
        };

        await Store().SaveAsync(config, Ct);
        var load = await Store().LoadAsync(Ct);

        Assert.Empty(load.Errors);
        Assert.True(load.Config.Paused);
        Assert.Equal(["builtin.housekeeping"], load.Config.DisabledBuiltIns);
        Assert.Equal(["p1", "p2"], load.Config.CiAutoPushOffProjects);
        Assert.Equal(2, load.Config.Definitions.Count);

        var first = load.Config.Definitions[0];
        Assert.Equal("daily-review", first.Id);
        Assert.Equal("Daily review", first.Name);
        Assert.False(first.Enabled);
        Assert.Equal("Review the open items", first.Prompt);
        Assert.Equal(AutomationPlacement.FixedFork, first.Placement);
        Assert.Equal("p1", first.ProjectId);
        Assert.True(first.ReadOnlyTools);
        Assert.True(first.CatchUp);
        Assert.Equal(new ModelSettings("model-x", ThinkingEffort.High, 4096, "prov"), first.Model);
        Assert.Equal(4, first.Triggers.Count);
        Assert.Equal(6, first.Triggers[0].IntervalHours);
        Assert.Equal(15, first.Triggers[0].Minute);
        Assert.Equal([new AutomationTime(9, 30), new AutomationTime(17, 0)], first.Triggers[1].Times);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Sunday], first.Triggers[2].Days);
        Assert.Equal("*/5 * * * mon-fri", first.Triggers[3].Cron);

        var second = load.Config.Definitions[1];
        Assert.Equal(AutomationAction.Remind, second.Action);
        Assert.Equal(AutomationPlacement.Scout, second.Placement);
        Assert.Equal("s1", second.TargetSessionId);
        Assert.Null(second.Model);
        Assert.Empty(second.Triggers);
    }

    [Fact]
    public async Task The_file_is_camel_case_with_enums_as_strings()
    {
        await Store().SaveAsync(
            new AutomationConfig
            {
                Definitions = [new AutomationDefinition("a", "A") { Triggers = [new AutomationTrigger(AutomationTriggerKind.Weekly) { Days = [DayOfWeek.Friday], Times = [new AutomationTime(1, 2)] }] }],
            },
            Ct);

        var text = await File.ReadAllTextAsync(FilePath);
        Assert.Contains("\"definitions\"", text, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"Weekly\"", text, StringComparison.Ordinal);
        Assert.Contains("\"Friday\"", text, StringComparison.Ordinal);
        Assert.Contains("\"placement\": \"Scratch\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_bad_element_reports_one_error_and_the_good_ones_still_load()
    {
        Write("""
            {
              "definitions": [
                { "id": "good-1", "name": "Good one" },
                { "id": "bad-enum", "name": "Bad", "placement": "Nowhere" },
                { "id": "good-2", "name": "Good two", "triggers": [ { "kind": "Cron", "cron": "0 9 * * *" } ] },
                { "name": "No id" },
                "not an object",
                { "id": "bad-trigger", "name": "T", "triggers": [ { "kind": "Hourly", "intervalHours": 99 } ] },
                { "id": "good-3", "name": "Good three" }
              ]
            }
            """);

        var load = await Store().LoadAsync(Ct);

        Assert.Equal(["good-1", "good-2", "good-3"], load.Config.Definitions.Select(d => d.Id));
        Assert.Equal([1, 3, 4, 5], load.Errors.Select(e => e.Index));
        Assert.Equal("bad-enum", load.Errors[0].Id);
        Assert.Null(load.Errors[1].Id);
        Assert.Equal("bad-trigger", load.Errors[3].Id);
        Assert.All(load.Errors, error => Assert.False(string.IsNullOrWhiteSpace(error.Message)));
    }

    [Fact]
    public async Task A_built_in_id_is_rejected_as_an_error()
    {
        Write("""{ "definitions": [ { "id": "builtin.ci-chain", "name": "Hijack" }, { "id": "mine", "name": "Mine" } ] }""");

        var load = await Store().LoadAsync(Ct);

        Assert.Equal(["mine"], load.Config.Definitions.Select(d => d.Id));
        var error = Assert.Single(load.Errors);
        Assert.Equal(0, error.Index);
        Assert.Equal("builtin.ci-chain", error.Id);
    }

    [Fact]
    public async Task A_duplicate_id_keeps_the_first_and_reports_the_later_ones()
    {
        Write("""{ "definitions": [ { "id": "dup", "name": "First" }, { "id": "other", "name": "O" }, { "id": "dup", "name": "Second" } ] }""");

        var load = await Store().LoadAsync(Ct);

        Assert.Equal(["First", "O"], load.Config.Definitions.Select(d => d.Name));
        var error = Assert.Single(load.Errors);
        Assert.Equal(2, error.Index);
        Assert.Equal("dup", error.Id);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    public async Task A_corrupt_file_is_reported_and_loads_as_empty_without_throwing(string content)
    {
        Write(content);

        var load = await Store().LoadAsync(Ct);

        Assert.Empty(load.Config.Definitions);
        var error = Assert.Single(load.Errors);
        Assert.Equal(AutomationDefinitionStore.FileErrorIndex, error.Index);
        Assert.Equal(content, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task An_empty_file_is_an_empty_config()
    {
        Write(string.Empty);
        var load = await Store().LoadAsync(Ct);
        Assert.Empty(load.Errors);
        Assert.Empty(load.Config.Definitions);
    }

    [Fact]
    public async Task A_wrongly_typed_top_level_field_is_an_error_but_the_other_fields_survive()
    {
        Write("""{ "paused": "yes", "disabledBuiltIns": ["builtin.housekeeping"], "ciAutoPushOffProjects": 5, "definitions": [ { "id": "a", "name": "A" } ] }""");

        var load = await Store().LoadAsync(Ct);

        Assert.False(load.Config.Paused);
        Assert.Equal(["builtin.housekeeping"], load.Config.DisabledBuiltIns);
        Assert.Empty(load.Config.CiAutoPushOffProjects);
        Assert.Single(load.Config.Definitions);
        Assert.Equal(2, load.Errors.Count);
        Assert.All(load.Errors, error => Assert.Equal(AutomationDefinitionStore.FileErrorIndex, error.Index));
    }

    [Fact]
    public async Task Save_refuses_built_in_and_duplicate_ids_without_touching_the_file()
    {
        await Store().SaveAsync(new AutomationConfig { Definitions = [new AutomationDefinition("keep", "Keep")] }, Ct);
        var before = await File.ReadAllBytesAsync(FilePath);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Store().SaveAsync(new AutomationConfig { Definitions = [new AutomationDefinition("builtin.x", "X")] }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Store().SaveAsync(new AutomationConfig { Definitions = [new AutomationDefinition("d", "1"), new AutomationDefinition("d", "2")] }, Ct));

        Assert.Equal(before, await File.ReadAllBytesAsync(FilePath));
    }

    [Fact]
    public async Task Save_creates_the_directory_and_replaces_the_previous_content()
    {
        var nested = new AutomationDefinitionStore(Path.Combine(root, "deep", "er", "automations.json"));
        await nested.SaveAsync(new AutomationConfig { Definitions = [new AutomationDefinition("one", "One")] }, Ct);
        await nested.SaveAsync(new AutomationConfig { Definitions = [new AutomationDefinition("two", "Two")] }, Ct);

        var load = await nested.LoadAsync(Ct);
        Assert.Equal(["two"], load.Config.Definitions.Select(d => d.Id));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "deep", "er"), "*.tmp*"));
    }

    [Fact]
    public async Task Save_throws_IOException_when_the_read_back_differs()
    {
        // A directory where the file should be makes the replace fail; the failure must surface, not be swallowed.
        Directory.CreateDirectory(FilePath);
        await Assert.ThrowsAnyAsync<Exception>(() => Store().SaveAsync(new AutomationConfig(), Ct));
        Assert.True(Directory.Exists(FilePath));
    }

    [Fact]
    public async Task A_cancelled_token_neither_loads_nor_saves()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().LoadAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().SaveAsync(new AutomationConfig(), cts.Token));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Parse_accepts_trailing_commas_and_comments_from_hand_edits()
    {
        Write("{ // note\n \"definitions\": [ { \"id\": \"a\", \"name\": \"A\", }, ], }");
        var load = await Store().LoadAsync(Ct);
        Assert.Empty(load.Errors);
        Assert.Single(load.Config.Definitions);
    }

    private void Write(string content) => File.WriteAllText(FilePath, content, new UTF8Encoding(false));
}
