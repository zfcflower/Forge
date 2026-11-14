using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Settings;

/// <summary>
/// Pins the gap-plan preferences: defaults, per-setter clamping to the Min/Max constants, token and text
/// normalization, ModelsHidden normalization, ApplyLoaded validation and the JSON round trip.
/// </summary>
public sealed class GapPlanPreferencesTests
{
    public static TheoryData<string, int, int, int> IntSettings => new()
    {
        { nameof(AppPreferences.AgentsMaxParallelSubAgents), AppPreferences.MinAgentsMaxParallelSubAgents, AppPreferences.MaxAgentsMaxParallelSubAgents, AppPreferences.DefaultAgentsMaxParallelSubAgents },
        { nameof(AppPreferences.IdleArchiveDays), AppPreferences.MinIdleArchiveDays, AppPreferences.MaxIdleArchiveDays, AppPreferences.DefaultIdleArchiveDays },
        { nameof(AppPreferences.PalettePerSourceLimit), AppPreferences.MinPalettePerSourceLimit, AppPreferences.MaxPalettePerSourceLimit, AppPreferences.DefaultPalettePerSourceLimit },
        { nameof(AppPreferences.ChatToolImageThumbnailPixels), AppPreferences.MinChatToolImageThumbnailPixels, AppPreferences.MaxChatToolImageThumbnailPixels, AppPreferences.DefaultChatToolImageThumbnailPixels },
        { nameof(AppPreferences.FilesCsvMaxRows), AppPreferences.MinFilesCsvMaxRows, AppPreferences.MaxFilesCsvMaxRows, AppPreferences.DefaultFilesCsvMaxRows },
        { nameof(AppPreferences.FilesNotebookOutputKilobytes), AppPreferences.MinFilesNotebookOutputKilobytes, AppPreferences.MaxFilesNotebookOutputKilobytes, AppPreferences.DefaultFilesNotebookOutputKilobytes },
        { nameof(AppPreferences.FilesImagePreviewMaxMegabytes), AppPreferences.MinFilesImagePreviewMaxMegabytes, AppPreferences.MaxFilesImagePreviewMaxMegabytes, AppPreferences.DefaultFilesImagePreviewMaxMegabytes },
        { nameof(AppPreferences.GitChecksNoChecksConfirmations), AppPreferences.MinGitChecksNoChecksConfirmations, AppPreferences.MaxGitChecksNoChecksConfirmations, AppPreferences.DefaultGitChecksNoChecksConfirmations },
        { nameof(AppPreferences.GitChecksFailureLogTailLines), AppPreferences.MinGitChecksFailureLogTailLines, AppPreferences.MaxGitChecksFailureLogTailLines, AppPreferences.DefaultGitChecksFailureLogTailLines },
        { nameof(AppPreferences.McpRequestTimeoutSeconds), AppPreferences.MinMcpRequestTimeoutSeconds, AppPreferences.MaxMcpRequestTimeoutSeconds, AppPreferences.DefaultMcpRequestTimeoutSeconds },
        { nameof(AppPreferences.McpReconnectInitialDelaySeconds), AppPreferences.MinMcpReconnectInitialDelaySeconds, AppPreferences.MaxMcpReconnectInitialDelaySeconds, AppPreferences.DefaultMcpReconnectInitialDelaySeconds },
        { nameof(AppPreferences.McpToolSearchLimit), AppPreferences.MinMcpToolSearchLimit, AppPreferences.MaxMcpToolSearchLimit, AppPreferences.DefaultMcpToolSearchLimit },
        { nameof(AppPreferences.McpStderrTailLines), AppPreferences.MinMcpStderrTailLines, AppPreferences.MaxMcpStderrTailLines, AppPreferences.DefaultMcpStderrTailLines },
        { nameof(AppPreferences.InspectorLogTailLines), AppPreferences.MinInspectorLogTailLines, AppPreferences.MaxInspectorLogTailLines, AppPreferences.DefaultInspectorLogTailLines },
        { nameof(AppPreferences.InspectorRefreshMilliseconds), AppPreferences.MinInspectorRefreshMilliseconds, AppPreferences.MaxInspectorRefreshMilliseconds, AppPreferences.DefaultInspectorRefreshMilliseconds },
        { nameof(AppPreferences.ChatMermaidTimeoutMilliseconds), AppPreferences.MinChatMermaidTimeoutMilliseconds, AppPreferences.MaxChatMermaidTimeoutMilliseconds, AppPreferences.DefaultChatMermaidTimeoutMilliseconds },
        { nameof(AppPreferences.ProjectsCloneTimeoutSeconds), AppPreferences.MinProjectsCloneTimeoutSeconds, AppPreferences.MaxProjectsCloneTimeoutSeconds, AppPreferences.DefaultProjectsCloneTimeoutSeconds },
    };

    private static int Get(AppPreferences p, string name) => name switch
    {
        nameof(AppPreferences.AgentsMaxParallelSubAgents) => p.AgentsMaxParallelSubAgents,
        nameof(AppPreferences.IdleArchiveDays) => p.IdleArchiveDays,
        nameof(AppPreferences.PalettePerSourceLimit) => p.PalettePerSourceLimit,
        nameof(AppPreferences.ChatToolImageThumbnailPixels) => p.ChatToolImageThumbnailPixels,
        nameof(AppPreferences.FilesCsvMaxRows) => p.FilesCsvMaxRows,
        nameof(AppPreferences.FilesNotebookOutputKilobytes) => p.FilesNotebookOutputKilobytes,
        nameof(AppPreferences.FilesImagePreviewMaxMegabytes) => p.FilesImagePreviewMaxMegabytes,
        nameof(AppPreferences.GitChecksNoChecksConfirmations) => p.GitChecksNoChecksConfirmations,
        nameof(AppPreferences.GitChecksFailureLogTailLines) => p.GitChecksFailureLogTailLines,
        nameof(AppPreferences.McpRequestTimeoutSeconds) => p.McpRequestTimeoutSeconds,
        nameof(AppPreferences.McpReconnectInitialDelaySeconds) => p.McpReconnectInitialDelaySeconds,
        nameof(AppPreferences.McpToolSearchLimit) => p.McpToolSearchLimit,
        nameof(AppPreferences.McpStderrTailLines) => p.McpStderrTailLines,
        nameof(AppPreferences.InspectorLogTailLines) => p.InspectorLogTailLines,
        nameof(AppPreferences.InspectorRefreshMilliseconds) => p.InspectorRefreshMilliseconds,
        nameof(AppPreferences.ChatMermaidTimeoutMilliseconds) => p.ChatMermaidTimeoutMilliseconds,
        nameof(AppPreferences.ProjectsCloneTimeoutSeconds) => p.ProjectsCloneTimeoutSeconds,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static void Set(AppPreferences p, string name, int value)
    {
        switch (name)
        {
            case nameof(AppPreferences.AgentsMaxParallelSubAgents): p.AgentsMaxParallelSubAgents = value; break;
            case nameof(AppPreferences.IdleArchiveDays): p.IdleArchiveDays = value; break;
            case nameof(AppPreferences.PalettePerSourceLimit): p.PalettePerSourceLimit = value; break;
            case nameof(AppPreferences.ChatToolImageThumbnailPixels): p.ChatToolImageThumbnailPixels = value; break;
            case nameof(AppPreferences.FilesCsvMaxRows): p.FilesCsvMaxRows = value; break;
            case nameof(AppPreferences.FilesNotebookOutputKilobytes): p.FilesNotebookOutputKilobytes = value; break;
            case nameof(AppPreferences.FilesImagePreviewMaxMegabytes): p.FilesImagePreviewMaxMegabytes = value; break;
            case nameof(AppPreferences.GitChecksNoChecksConfirmations): p.GitChecksNoChecksConfirmations = value; break;
            case nameof(AppPreferences.GitChecksFailureLogTailLines): p.GitChecksFailureLogTailLines = value; break;
            case nameof(AppPreferences.McpRequestTimeoutSeconds): p.McpRequestTimeoutSeconds = value; break;
            case nameof(AppPreferences.McpReconnectInitialDelaySeconds): p.McpReconnectInitialDelaySeconds = value; break;
            case nameof(AppPreferences.McpToolSearchLimit): p.McpToolSearchLimit = value; break;
            case nameof(AppPreferences.McpStderrTailLines): p.McpStderrTailLines = value; break;
            case nameof(AppPreferences.InspectorLogTailLines): p.InspectorLogTailLines = value; break;
            case nameof(AppPreferences.InspectorRefreshMilliseconds): p.InspectorRefreshMilliseconds = value; break;
            case nameof(AppPreferences.ChatMermaidTimeoutMilliseconds): p.ChatMermaidTimeoutMilliseconds = value; break;
            case nameof(AppPreferences.ProjectsCloneTimeoutSeconds): p.ProjectsCloneTimeoutSeconds = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }
    }

    private static PreferencesSection Section(string name, int value)
    {
        var section = new PreferencesSection();
        switch (name)
        {
            case nameof(AppPreferences.AgentsMaxParallelSubAgents): section.AgentsMaxParallelSubAgents = value; break;
            case nameof(AppPreferences.IdleArchiveDays): section.IdleArchiveDays = value; break;
            case nameof(AppPreferences.PalettePerSourceLimit): section.PalettePerSourceLimit = value; break;
            case nameof(AppPreferences.ChatToolImageThumbnailPixels): section.ChatToolImageThumbnailPixels = value; break;
            case nameof(AppPreferences.FilesCsvMaxRows): section.FilesCsvMaxRows = value; break;
            case nameof(AppPreferences.FilesNotebookOutputKilobytes): section.FilesNotebookOutputKilobytes = value; break;
            case nameof(AppPreferences.FilesImagePreviewMaxMegabytes): section.FilesImagePreviewMaxMegabytes = value; break;
            case nameof(AppPreferences.GitChecksNoChecksConfirmations): section.GitChecksNoChecksConfirmations = value; break;
            case nameof(AppPreferences.GitChecksFailureLogTailLines): section.GitChecksFailureLogTailLines = value; break;
            case nameof(AppPreferences.McpRequestTimeoutSeconds): section.McpRequestTimeoutSeconds = value; break;
            case nameof(AppPreferences.McpReconnectInitialDelaySeconds): section.McpReconnectInitialDelaySeconds = value; break;
            case nameof(AppPreferences.McpToolSearchLimit): section.McpToolSearchLimit = value; break;
            case nameof(AppPreferences.McpStderrTailLines): section.McpStderrTailLines = value; break;
            case nameof(AppPreferences.InspectorLogTailLines): section.InspectorLogTailLines = value; break;
            case nameof(AppPreferences.InspectorRefreshMilliseconds): section.InspectorRefreshMilliseconds = value; break;
            case nameof(AppPreferences.ChatMermaidTimeoutMilliseconds): section.ChatMermaidTimeoutMilliseconds = value; break;
            case nameof(AppPreferences.ProjectsCloneTimeoutSeconds): section.ProjectsCloneTimeoutSeconds = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }

        return section;
    }

    // ------------------------------------------------------------ defaults

    [Theory]
    [MemberData(nameof(IntSettings))]
    public void Int_settings_start_at_their_default(string name, int min, int max, int expectedDefault)
    {
        Assert.InRange(expectedDefault, min, max);
        Assert.Equal(expectedDefault, Get(new AppPreferences(), name));
    }

    [Fact]
    public void Non_int_settings_start_at_their_defaults()
    {
        var p = new AppPreferences();

        Assert.True(p.AgentsSubAgentsEnabled);
        Assert.True(p.IdleArchiveEnabled);
        Assert.Equal("automation", p.IdleArchiveScope);
        Assert.True(p.NotificationPermissionReprompt);
        Assert.Equal(string.Empty, p.PaletteExcludeDirectories);
        Assert.True(p.ChatToolImagesToModel);
        Assert.Equal("flowchart,sequence,state,class,er", p.ChatMermaidDiagramTypes);
        Assert.Equal(string.Empty, p.ProjectsCloneDirectory);
        Assert.Equal("daily", p.UpdatesCheckFrequency);
        Assert.Equal("stable", p.UpdatesChannel);
        Assert.Empty(p.ModelsHidden);
    }

    // ------------------------------------------------------------ clamping

    [Theory]
    [MemberData(nameof(IntSettings))]
    public void Int_setters_clamp_to_their_bounds_and_keep_in_range_values(string name, int min, int max, int ignoredDefault)
    {
        Assert.InRange(ignoredDefault, min, max);
        var p = new AppPreferences();

        Set(p, name, min - 1);
        Assert.Equal(min, Get(p, name));

        Set(p, name, int.MinValue);
        Assert.Equal(min, Get(p, name));

        Set(p, name, max + 1);
        Assert.Equal(max, Get(p, name));

        Set(p, name, int.MaxValue);
        Assert.Equal(max, Get(p, name));

        Set(p, name, min);
        Assert.Equal(min, Get(p, name));

        Set(p, name, max);
        Assert.Equal(max, Get(p, name));

        var middle = min + ((max - min) / 2);
        Set(p, name, middle);
        Assert.Equal(middle, Get(p, name));
    }

    [Theory]
    [MemberData(nameof(IntSettings))]
    public void Int_setters_raise_PropertyChanged_only_when_the_clamped_value_changes(string name, int min, int max, int expectedDefault)
    {
        var p = new AppPreferences();
        var raised = PropertyChangeLog.For(p);

        Set(p, name, expectedDefault);
        Assert.Empty(raised);

        var other = expectedDefault == max ? min : max;
        Set(p, name, other);
        Assert.Equal([name], raised);

        // Past the same bound again lands on the same clamped value: no second notification.
        raised.Clear();
        Set(p, name, other == max ? max + 5 : min - 5);
        Assert.Empty(raised);
    }

    // ------------------------------------------------------------ booleans

    [Fact]
    public void Boolean_settings_store_and_notify_once_per_change()
    {
        var p = new AppPreferences();
        var raised = PropertyChangeLog.For(p);

        p.AgentsSubAgentsEnabled = false;
        p.IdleArchiveEnabled = false;
        p.NotificationPermissionReprompt = false;
        p.ChatToolImagesToModel = false;
        p.ChatToolImagesToModel = false;

        Assert.False(p.AgentsSubAgentsEnabled);
        Assert.False(p.IdleArchiveEnabled);
        Assert.False(p.NotificationPermissionReprompt);
        Assert.False(p.ChatToolImagesToModel);
        Assert.Equal(
            [nameof(AppPreferences.AgentsSubAgentsEnabled), nameof(AppPreferences.IdleArchiveEnabled), nameof(AppPreferences.NotificationPermissionReprompt), nameof(AppPreferences.ChatToolImagesToModel)],
            raised);
    }

    // ------------------------------------------------------------ tokens

    [Theory]
    [InlineData("automation", "automation")]
    [InlineData("ALL", "all")]
    [InlineData("  All ", "all")]
    [InlineData("Automation", "automation")]
    [InlineData("bogus", "automation")]
    [InlineData("", "automation")]
    [InlineData(null, "automation")]
    public void IdleArchiveScope_accepts_tokens_case_insensitively(string? input, string expected)
    {
        var p = new AppPreferences { IdleArchiveScope = "all" };
        p.IdleArchiveScope = input!;
        Assert.Equal(expected, p.IdleArchiveScope);
    }

    [Theory]
    [InlineData("daily", "daily")]
    [InlineData("WEEKLY", "weekly")]
    [InlineData(" Never ", "never")]
    [InlineData("hourly", "daily")]
    [InlineData("", "daily")]
    [InlineData(null, "daily")]
    public void UpdatesCheckFrequency_accepts_tokens_case_insensitively(string? input, string expected)
    {
        var p = new AppPreferences { UpdatesCheckFrequency = "never" };
        p.UpdatesCheckFrequency = input!;
        Assert.Equal(expected, p.UpdatesCheckFrequency);
    }

    [Theory]
    [InlineData("stable", "stable")]
    [InlineData("BETA", "beta")]
    [InlineData(" Beta ", "beta")]
    [InlineData("nightly", "stable")]
    [InlineData("", "stable")]
    [InlineData(null, "stable")]
    public void UpdatesChannel_accepts_tokens_case_insensitively(string? input, string expected)
    {
        var p = new AppPreferences { UpdatesChannel = "beta" };
        p.UpdatesChannel = input!;
        Assert.Equal(expected, p.UpdatesChannel);
    }

    // ------------------------------------------------------------ text

    [Fact]
    public void Text_settings_trim_and_fall_back_to_the_default_for_null()
    {
        var p = new AppPreferences
        {
            PaletteExcludeDirectories = "  node_modules, bin  ",
            ChatMermaidDiagramTypes = "  flowchart  ",
            ProjectsCloneDirectory = "  D:\\Clones  ",
        };

        Assert.Equal("node_modules, bin", p.PaletteExcludeDirectories);
        Assert.Equal("flowchart", p.ChatMermaidDiagramTypes);
        Assert.Equal("D:\\Clones", p.ProjectsCloneDirectory);

        p.PaletteExcludeDirectories = null!;
        p.ChatMermaidDiagramTypes = null!;
        p.ProjectsCloneDirectory = null!;

        Assert.Equal(AppPreferences.DefaultPaletteExcludeDirectories, p.PaletteExcludeDirectories);
        Assert.Equal(AppPreferences.DefaultChatMermaidDiagramTypes, p.ChatMermaidDiagramTypes);
        Assert.Equal(AppPreferences.DefaultProjectsCloneDirectory, p.ProjectsCloneDirectory);
    }

    // ------------------------------------------------------------ ModelsHidden

    [Fact]
    public void ModelsHidden_sorts_deduplicates_trims_and_ignores_blanks()
    {
        var p = new AppPreferences { ModelsHidden = ["b", " a ", "b", "", "   ", null!, "c", "a"] };

        Assert.Equal(["a", "b", "c"], p.ModelsHidden);
    }

    [Fact]
    public void ModelsHidden_is_ordinal_and_case_sensitive()
    {
        var p = new AppPreferences { ModelsHidden = ["b", "B", "a"] };

        Assert.Equal(["B", "a", "b"], p.ModelsHidden);
    }

    [Fact]
    public void ModelsHidden_raises_PropertyChanged_only_on_change()
    {
        var p = new AppPreferences();
        var raised = 0;
        p.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(AppPreferences.ModelsHidden) ? 1 : 0;

        p.ModelsHidden = [];
        Assert.Equal(0, raised);

        p.ModelsHidden = ["b", "a"];
        Assert.Equal(1, raised);

        p.ModelsHidden = ["a", "b", "a"]; // normalizes to the same list
        Assert.Equal(1, raised);

        p.ModelsHidden = ["a"];
        Assert.Equal(2, raised);

        p.ModelsHidden = ["", "  "]; // clears
        Assert.Equal(3, raised);
        Assert.Empty(p.ModelsHidden);
    }

    // ------------------------------------------------------------ ApplyLoaded

    [Fact]
    public void ApplyLoaded_of_an_empty_section_gives_the_defaults_and_raises_nothing()
    {
        var p = new AppPreferences
        {
            AgentsMaxParallelSubAgents = 9,
            IdleArchiveScope = "all",
            UpdatesChannel = "beta",
            ProjectsCloneDirectory = "x",
            ModelsHidden = ["m"],
            ChatToolImagesToModel = false,
        };
        var raised = 0;
        p.PropertyChanged += (_, _) => raised++;

        p.ApplyLoaded(new PreferencesSection(), UiLanguage.System);

        Assert.Equal(0, raised);
        AssertAllDefaults(p);
    }

    [Theory]
    [MemberData(nameof(IntSettings))]
    public void ApplyLoaded_clamps_out_of_range_ints(string name, int min, int max, int ignoredDefault)
    {
        Assert.InRange(ignoredDefault, min, max);

        var low = new AppPreferences();
        low.ApplyLoaded(Section(name, min - 1), UiLanguage.System);
        Assert.Equal(min, Get(low, name));

        var high = new AppPreferences();
        high.ApplyLoaded(Section(name, max + 1), UiLanguage.System);
        Assert.Equal(max, Get(high, name));

        var inRange = new AppPreferences();
        inRange.ApplyLoaded(Section(name, max), UiLanguage.System);
        Assert.Equal(max, Get(inRange, name));
    }

    [Fact]
    public void ApplyLoaded_normalizes_tokens_text_and_hidden_models()
    {
        var p = new AppPreferences();
        p.ApplyLoaded(
            new PreferencesSection
            {
                IdleArchiveScope = "ALL",
                UpdatesCheckFrequency = "bogus",
                UpdatesChannel = " Beta ",
                PaletteExcludeDirectories = "  obj  ",
                ChatMermaidDiagramTypes = "  gantt ",
                ProjectsCloneDirectory = "  C:\\c ",
                ModelsHidden = ["z", "a", "z", " ", "a "],
            },
            UiLanguage.System);

        Assert.Equal("all", p.IdleArchiveScope);
        Assert.Equal("daily", p.UpdatesCheckFrequency);
        Assert.Equal("beta", p.UpdatesChannel);
        Assert.Equal("obj", p.PaletteExcludeDirectories);
        Assert.Equal("gantt", p.ChatMermaidDiagramTypes);
        Assert.Equal("C:\\c", p.ProjectsCloneDirectory);
        Assert.Equal(["a", "z"], p.ModelsHidden);
    }

    // ------------------------------------------------------------ JSON

    [Fact]
    public void Every_new_value_survives_ToSection_json_ApplyLoaded()
    {
        var source = new AppPreferences
        {
            AgentsMaxParallelSubAgents = 7,
            AgentsSubAgentsEnabled = false,
            IdleArchiveEnabled = false,
            IdleArchiveDays = 30,
            IdleArchiveScope = "all",
            NotificationPermissionReprompt = false,
            PalettePerSourceLimit = 123,
            PaletteExcludeDirectories = "node_modules,bin",
            ChatToolImageThumbnailPixels = 512,
            ChatToolImagesToModel = false,
            FilesCsvMaxRows = 5000,
            FilesNotebookOutputKilobytes = 77,
            FilesImagePreviewMaxMegabytes = 33,
            GitChecksNoChecksConfirmations = 5,
            GitChecksFailureLogTailLines = 333,
            McpRequestTimeoutSeconds = 90,
            McpReconnectInitialDelaySeconds = 12,
            McpToolSearchLimit = 42,
            McpStderrTailLines = 321,
            InspectorLogTailLines = 150,
            InspectorRefreshMilliseconds = 2500,
            ChatMermaidDiagramTypes = "flowchart,gantt",
            ChatMermaidTimeoutMilliseconds = 4000,
            ProjectsCloneTimeoutSeconds = 600,
            ProjectsCloneDirectory = "D:\\Clones",
            UpdatesCheckFrequency = "weekly",
            UpdatesChannel = "beta",
            ModelsHidden = ["model-b", "model-a"],
        };

        var json = JsonSerializer.Serialize(source.ToSection(), PreferencesJsonContext.Default.PreferencesSection);
        var section = JsonSerializer.Deserialize(json, PreferencesJsonContext.Default.PreferencesSection);
        Assert.NotNull(section);
        var loaded = new AppPreferences();
        loaded.ApplyLoaded(section, UiLanguage.System);

        Assert.Equal(7, loaded.AgentsMaxParallelSubAgents);
        Assert.False(loaded.AgentsSubAgentsEnabled);
        Assert.False(loaded.IdleArchiveEnabled);
        Assert.Equal(30, loaded.IdleArchiveDays);
        Assert.Equal("all", loaded.IdleArchiveScope);
        Assert.False(loaded.NotificationPermissionReprompt);
        Assert.Equal(123, loaded.PalettePerSourceLimit);
        Assert.Equal("node_modules,bin", loaded.PaletteExcludeDirectories);
        Assert.Equal(512, loaded.ChatToolImageThumbnailPixels);
        Assert.False(loaded.ChatToolImagesToModel);
        Assert.Equal(5000, loaded.FilesCsvMaxRows);
        Assert.Equal(77, loaded.FilesNotebookOutputKilobytes);
        Assert.Equal(33, loaded.FilesImagePreviewMaxMegabytes);
        Assert.Equal(5, loaded.GitChecksNoChecksConfirmations);
        Assert.Equal(333, loaded.GitChecksFailureLogTailLines);
        Assert.Equal(90, loaded.McpRequestTimeoutSeconds);
        Assert.Equal(12, loaded.McpReconnectInitialDelaySeconds);
        Assert.Equal(42, loaded.McpToolSearchLimit);
        Assert.Equal(321, loaded.McpStderrTailLines);
        Assert.Equal(150, loaded.InspectorLogTailLines);
        Assert.Equal(2500, loaded.InspectorRefreshMilliseconds);
        Assert.Equal("flowchart,gantt", loaded.ChatMermaidDiagramTypes);
        Assert.Equal(4000, loaded.ChatMermaidTimeoutMilliseconds);
        Assert.Equal(600, loaded.ProjectsCloneTimeoutSeconds);
        Assert.Equal("D:\\Clones", loaded.ProjectsCloneDirectory);
        Assert.Equal("weekly", loaded.UpdatesCheckFrequency);
        Assert.Equal("beta", loaded.UpdatesChannel);
        Assert.Equal(["model-a", "model-b"], loaded.ModelsHidden);
    }

    [Fact]
    public void ModelsHidden_is_omitted_from_json_when_empty_and_written_when_set()
    {
        var empty = JsonSerializer.Serialize(new AppPreferences().ToSection(), PreferencesJsonContext.Default.PreferencesSection);
        var emptyNode = Assert.IsType<JsonObject>(JsonNode.Parse(empty));
        Assert.False(emptyNode.ContainsKey("modelsHidden"));

        var set = JsonSerializer.Serialize(new AppPreferences { ModelsHidden = ["x"] }.ToSection(), PreferencesJsonContext.Default.PreferencesSection);
        var setNode = Assert.IsType<JsonObject>(JsonNode.Parse(set));
        Assert.Equal(["x"], setNode["modelsHidden"]!.AsArray().Select(static n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void An_old_settings_json_without_any_new_key_loads_the_defaults()
    {
        const string old = """{ "uiFontSize": 14, "gitPullStrategy": "rebase", "terminalScrollbackLines": 5000 }""";
        var section = JsonSerializer.Deserialize(old, PreferencesJsonContext.Default.PreferencesSection);
        Assert.NotNull(section);

        var p = new AppPreferences();
        p.ApplyLoaded(section, UiLanguage.System);

        AssertAllDefaults(p);
        Assert.Equal(14, p.UiFontSize);
        Assert.Equal("rebase", p.GitPullStrategy);
    }

    [Fact]
    public void An_out_of_range_json_is_clamped_on_load()
    {
        const string json = """{ "agentsMaxParallelSubAgents": 999, "idleArchiveDays": 0, "mcpToolSearchLimit": -5, "updatesChannel": "weird", "modelsHidden": ["b","a","b"] }""";
        var section = JsonSerializer.Deserialize(json, PreferencesJsonContext.Default.PreferencesSection);
        Assert.NotNull(section);

        var p = new AppPreferences();
        p.ApplyLoaded(section, UiLanguage.System);

        Assert.Equal(AppPreferences.MaxAgentsMaxParallelSubAgents, p.AgentsMaxParallelSubAgents);
        Assert.Equal(AppPreferences.MinIdleArchiveDays, p.IdleArchiveDays);
        Assert.Equal(AppPreferences.MinMcpToolSearchLimit, p.McpToolSearchLimit);
        Assert.Equal("stable", p.UpdatesChannel);
        Assert.Equal(["a", "b"], p.ModelsHidden);
    }

    private static void AssertAllDefaults(AppPreferences p)
    {
        Assert.Equal(AppPreferences.DefaultAgentsMaxParallelSubAgents, p.AgentsMaxParallelSubAgents);
        Assert.True(p.AgentsSubAgentsEnabled);
        Assert.True(p.IdleArchiveEnabled);
        Assert.Equal(AppPreferences.DefaultIdleArchiveDays, p.IdleArchiveDays);
        Assert.Equal("automation", p.IdleArchiveScope);
        Assert.True(p.NotificationPermissionReprompt);
        Assert.Equal(AppPreferences.DefaultPalettePerSourceLimit, p.PalettePerSourceLimit);
        Assert.Equal(AppPreferences.DefaultPaletteExcludeDirectories, p.PaletteExcludeDirectories);
        Assert.Equal(AppPreferences.DefaultChatToolImageThumbnailPixels, p.ChatToolImageThumbnailPixels);
        Assert.True(p.ChatToolImagesToModel);
        Assert.Equal(AppPreferences.DefaultFilesCsvMaxRows, p.FilesCsvMaxRows);
        Assert.Equal(AppPreferences.DefaultFilesNotebookOutputKilobytes, p.FilesNotebookOutputKilobytes);
        Assert.Equal(AppPreferences.DefaultFilesImagePreviewMaxMegabytes, p.FilesImagePreviewMaxMegabytes);
        Assert.Equal(AppPreferences.DefaultGitChecksNoChecksConfirmations, p.GitChecksNoChecksConfirmations);
        Assert.Equal(AppPreferences.DefaultGitChecksFailureLogTailLines, p.GitChecksFailureLogTailLines);
        Assert.Equal(AppPreferences.DefaultMcpRequestTimeoutSeconds, p.McpRequestTimeoutSeconds);
        Assert.Equal(AppPreferences.DefaultMcpReconnectInitialDelaySeconds, p.McpReconnectInitialDelaySeconds);
        Assert.Equal(AppPreferences.DefaultMcpToolSearchLimit, p.McpToolSearchLimit);
        Assert.Equal(AppPreferences.DefaultMcpStderrTailLines, p.McpStderrTailLines);
        Assert.Equal(AppPreferences.DefaultInspectorLogTailLines, p.InspectorLogTailLines);
        Assert.Equal(AppPreferences.DefaultInspectorRefreshMilliseconds, p.InspectorRefreshMilliseconds);
        Assert.Equal(AppPreferences.DefaultChatMermaidDiagramTypes, p.ChatMermaidDiagramTypes);
        Assert.Equal(AppPreferences.DefaultChatMermaidTimeoutMilliseconds, p.ChatMermaidTimeoutMilliseconds);
        Assert.Equal(AppPreferences.DefaultProjectsCloneTimeoutSeconds, p.ProjectsCloneTimeoutSeconds);
        Assert.Equal(AppPreferences.DefaultProjectsCloneDirectory, p.ProjectsCloneDirectory);
        Assert.Equal("daily", p.UpdatesCheckFrequency);
        Assert.Equal("stable", p.UpdatesChannel);
        Assert.Empty(p.ModelsHidden);
    }
}
